"""Minimal tool-calling AI agent using OpenRouter (OpenAI-compatible API)."""
import argparse
import json
import os
import sqlite3
import time
from datetime import datetime

from dotenv import load_dotenv
from openai import NotFoundError, OpenAI, PermissionDeniedError, RateLimitError

load_dotenv()
client = OpenAI(
    base_url="https://openrouter.ai/api/v1",
    api_key=os.getenv("OPENROUTER_API_KEY"),
)
MODELS = [
    os.getenv("OPENROUTER_MODEL", "nvidia/nemotron-3-ultra-550b-a55b:free"),
    "nvidia/nemotron-3.5-lightning:free",
    "google/gemma-4-31b-it:free",
    "google/gemma-4-26b-a4b-it:free",
]
DEFAULT_DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "agent_state.db")
SYSTEM_PROMPT = "You are a helpful assistant."


class HistoryStore:
    """SQLite-backed conversation history, isolated per named session."""

    def __init__(self, path: str = None):
        path = path or os.getenv("AGENT_DB_PATH") or DEFAULT_DB_PATH
        if path != ":memory:":
            os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        self.conn = sqlite3.connect(path)
        if path != ":memory:":
            self.conn.execute("PRAGMA journal_mode = WAL")
        with self.conn:
            self.conn.execute(
                "CREATE TABLE IF NOT EXISTS messages ("
                " session_id TEXT NOT NULL, seq INTEGER NOT NULL, payload TEXT NOT NULL,"
                " created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,"
                " PRIMARY KEY (session_id, seq))"
            )

    def load(self, session_id: str) -> list:
        rows = self.conn.execute(
            "SELECT payload FROM messages WHERE session_id = ? ORDER BY seq", (session_id,)
        )
        return [json.loads(r[0]) for r in rows]

    def append(self, session_id: str, messages: list):
        """Append messages in one transaction (all or nothing)."""
        with self.conn:
            start = self.conn.execute(
                "SELECT COALESCE(MAX(seq), -1) + 1 FROM messages WHERE session_id = ?",
                (session_id,),
            ).fetchone()[0]
            self.conn.executemany(
                "INSERT INTO messages (session_id, seq, payload) VALUES (?, ?, ?)",
                [(session_id, start + i, json.dumps(m)) for i, m in enumerate(messages)],
            )

    def clear(self, session_id: str):
        with self.conn:
            self.conn.execute("DELETE FROM messages WHERE session_id = ?", (session_id,))

    def close(self):
        self.conn.close()


def to_dict(msg) -> dict:
    """Convert an SDK assistant message into a JSON-serialisable chat message."""
    if isinstance(msg, dict):
        return msg
    out = {"role": "assistant", "content": msg.content}
    if msg.tool_calls:
        out["tool_calls"] = [
            {"id": c.id, "type": "function",
             "function": {"name": c.function.name, "arguments": c.function.arguments}}
            for c in msg.tool_calls
        ]
    return out


def chat(history: list):
    """Call the API, retrying and falling back across free models on rate limits."""
    for model in dict.fromkeys(MODELS):
        for attempt in range(2):
            try:
                return client.chat.completions.create(
                    model=model, messages=history, tools=TOOL_SCHEMAS
                ).choices[0].message
            except (RateLimitError, PermissionDeniedError, NotFoundError):
                time.sleep(2 * (attempt + 1))
    raise RuntimeError("All free models are rate-limited right now. Try again shortly.")


def get_time() -> str:
    return datetime.now().isoformat()


def add(a: float, b: float) -> float:
    return a + b


TOOLS = {"get_time": get_time, "add": add}
TOOL_SCHEMAS = [
    {
        "type": "function",
        "function": {
            "name": "get_time",
            "description": "Get the current local date and time.",
            "parameters": {"type": "object", "properties": {}},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "add",
            "description": "Add two numbers.",
            "parameters": {
                "type": "object",
                "properties": {"a": {"type": "number"}, "b": {"type": "number"}},
                "required": ["a", "b"],
            },
        },
    },
]


def run_agent(user_input: str, history: list, max_steps: int = 5) -> str:
    history.append({"role": "user", "content": user_input})
    for _ in range(max_steps):
        msg = chat(history)
        history.append(to_dict(msg))
        if not msg.tool_calls:
            return msg.content
        for call in msg.tool_calls:
            args = json.loads(call.function.arguments or "{}")
            result = TOOLS[call.function.name](**args)
            history.append(
                {"role": "tool", "tool_call_id": call.id, "content": str(result)}
            )
    return "Stopped: max steps reached."


def main():
    ap = argparse.ArgumentParser(description="Tool-calling CLI agent with persistent history.")
    ap.add_argument("--session", default="default", help="conversation name (default: default)")
    ap.add_argument("--db", help="SQLite file (default: $AGENT_DB_PATH or ./agent_state.db next to agent.py)")
    ap.add_argument("--reset", action="store_true", help="clear this session's saved history first")
    args = ap.parse_args()
    store = HistoryStore(args.db)
    if args.reset:
        store.clear(args.session)
    history = store.load(args.session)
    if not history:
        history = [{"role": "system", "content": SYSTEM_PROMPT}]
        store.append(args.session, history)
    else:
        print(f"Resumed session '{args.session}' ({len(history) - 1} saved messages).")
    print("Agent ready. Type 'exit' to quit.")
    try:
        while (text := input("you> ").strip()) not in ("exit", "quit"):
            n = len(history)
            try:
                print("agent>", run_agent(text, history))
            except RuntimeError as e:
                del history[n:]
                print("error>", e)
                continue
            # Persist the whole turn only once it succeeded.
            store.append(args.session, history[n:])
    except (EOFError, KeyboardInterrupt):
        print()
    finally:
        store.close()


if __name__ == "__main__":
    main()
