"""Agent swarm demo: four agents hand work off to each other via tool calls.

Coordinator -> Researcher -> Writer -> Critic -> (Writer again if revisions are
needed) -> Coordinator, which delivers the final answer. All agents share one
conversation history; a handoff just switches which agent (system prompt and
tools) answers next.
"""
import argparse
import json
import os
import sqlite3
import sys
import time
import uuid
from dataclasses import dataclass, field
from typing import Callable

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
MAX_TURNS = 12
MAX_REVISIONS = 1
DEFAULT_DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "swarm_state.db")

SCHEMA = """
CREATE TABLE IF NOT EXISTS runs (
    run_id TEXT PRIMARY KEY,
    task TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'running',  -- running | completed
    current_agent TEXT NOT NULL,
    turns INTEGER NOT NULL DEFAULT 0,
    revisions INTEGER NOT NULL DEFAULT 0,
    last_text TEXT NOT NULL DEFAULT '',
    result TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS messages (
    run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
    seq INTEGER NOT NULL,
    role TEXT NOT NULL,
    content TEXT NOT NULL,
    PRIMARY KEY (run_id, seq)
);
"""


class RunStore:
    """SQLite persistence: one row per run plus its ordered conversation/handoff history.

    Each swarm turn is checkpointed in a single transaction, so a crash never leaves a
    half-written turn and a run can be resumed from its last completed turn.
    """

    def __init__(self, path: str = None):
        path = path or os.getenv("SWARM_DB_PATH") or DEFAULT_DB_PATH
        if path != ":memory:":
            os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        self.conn = sqlite3.connect(path)
        self.conn.row_factory = sqlite3.Row
        self.conn.execute("PRAGMA foreign_keys = ON")
        if path != ":memory:":
            self.conn.execute("PRAGMA journal_mode = WAL")
        with self.conn:
            self.conn.executescript(SCHEMA)

    def create_run(self, task: str, run_id: str = None) -> str:
        run_id = run_id or uuid.uuid4().hex[:8]
        with self.conn:
            self.conn.execute(
                "INSERT INTO runs (run_id, task, current_agent) VALUES (?, ?, 'Coordinator')",
                (run_id, task),
            )
            self.conn.execute(
                "INSERT INTO messages (run_id, seq, role, content) VALUES (?, 0, 'user', ?)",
                (run_id, task),
            )
        return run_id

    def get_run(self, run_id: str):
        return self.conn.execute("SELECT * FROM runs WHERE run_id = ?", (run_id,)).fetchone()

    def history(self, run_id: str) -> list:
        rows = self.conn.execute(
            "SELECT role, content FROM messages WHERE run_id = ? ORDER BY seq", (run_id,)
        )
        return [{"role": r["role"], "content": r["content"]} for r in rows]

    def checkpoint(self, run_id: str, new_messages: list, agent: str, turns: int,
                   revisions: int, last_text: str, result: str = None):
        """Atomically append messages and update run state; result set => completed."""
        with self.conn:
            start = self.conn.execute(
                "SELECT COALESCE(MAX(seq), -1) + 1 FROM messages WHERE run_id = ?", (run_id,)
            ).fetchone()[0]
            self.conn.executemany(
                "INSERT INTO messages (run_id, seq, role, content) VALUES (?, ?, ?, ?)",
                [(run_id, start + i, m["role"], m["content"]) for i, m in enumerate(new_messages)],
            )
            self.conn.execute(
                "UPDATE runs SET current_agent = ?, turns = ?, revisions = ?, last_text = ?, "
                "result = ?, status = ?, updated_at = CURRENT_TIMESTAMP WHERE run_id = ?",
                (agent, turns, revisions, last_text, result,
                 "running" if result is None else "completed", run_id),
            )

    def list_runs(self) -> list:
        return self.conn.execute(
            "SELECT run_id, status, current_agent, turns, updated_at, task "
            "FROM runs ORDER BY updated_at DESC, rowid DESC"
        ).fetchall()

    def close(self):
        self.conn.close()


@dataclass
class Agent:
    name: str
    instructions: str
    # Handoff tools: tool name -> (description, target agent name).
    handoffs: dict = field(default_factory=dict)


AGENTS = {
    "Coordinator": Agent(
        "Coordinator",
        "You are the swarm coordinator. When the user gives a topic, immediately hand off to "
        "the Researcher. When the Critic hands back an approved piece, present the final "
        "text to the user verbatim, with a one-line note on who contributed.",
        {"transfer_to_researcher": ("Send the topic to the Researcher.", "Researcher")},
    ),
    "Researcher": Agent(
        "Researcher",
        "You are a researcher. List 4-5 concise, factual bullet points about the topic, "
        "then hand off to the Writer, putting the bullets in the message argument.",
        {"transfer_to_writer": ("Pass research notes to the Writer.", "Writer")},
    ),
    "Writer": Agent(
        "Writer",
        "You are a writer. Using the research notes (and any Critic feedback), write a short "
        "engaging paragraph of at most 90 words, then hand off to the Critic, putting the draft in the message argument.",
        {"transfer_to_critic": ("Send the draft to the Critic.", "Critic")},
    ),
    "Critic": Agent(
        "Critic",
        "You are a strict editor. Review the latest draft for accuracy and clarity. If it needs "
        "changes, call request_revision with brief feedback in the message. Otherwise call "
        "approve with the draft copied verbatim (no commentary) in the message.",
        {
            "request_revision": ("Send the draft back to the Writer with feedback.", "Writer"),
            "approve": ("Approve the draft and return to the Coordinator.", "Coordinator"),
        },
    ),
}


def schemas(agent: Agent) -> list:
    return [
        {
            "type": "function",
            "function": {
                "name": name,
                "description": desc,
                "parameters": {
                    "type": "object",
                    "properties": {
                        "message": {
                            "type": "string",
                            "description": "Your full output (notes, draft, or feedback) for the next agent.",
                        }
                    },
                    "required": ["message"],
                },
            },
        }
        for name, (desc, _) in agent.handoffs.items()
    ]


def chat(messages: list, tools: list):
    """Call the API, retrying and falling back across free models on rate limits."""
    for model in dict.fromkeys(MODELS):
        for attempt in range(2):
            try:
                return client.chat.completions.create(
                    model=model, messages=messages, tools=tools or None
                ).choices[0].message
            except (RateLimitError, PermissionDeniedError, NotFoundError):
                time.sleep(2 * (attempt + 1))
    raise RuntimeError("All free models are rate-limited right now. Try again shortly.")


def run_swarm(task: str = None, log: Callable[[str], None] = print,
              store: RunStore = None, run_id: str = None) -> str:
    """Run a swarm. Pass an existing run_id (and no task) to resume an interrupted run.

    Without a store the run is kept in a throwaway in-memory database.
    """
    store = store or RunStore(":memory:")
    if task is None:
        run = store.get_run(run_id) if run_id else None
        if run is None:
            raise RuntimeError(f"No such run: {run_id}")
        if run["status"] == "completed":
            return run["result"]
        if run["turns"]:
            log(f"Resuming run {run_id} at {run['current_agent']} (turn {run['turns']}).\n")
    else:
        run_id = store.create_run(task, run_id)
        run = store.get_run(run_id)
    history = store.history(run_id)
    agent = AGENTS[run["current_agent"]]
    revisions, turns, last_text = run["revisions"], run["turns"], run["last_text"]
    while turns < MAX_TURNS:
        turns += 1
        new = []
        log(f"[{agent.name}] thinking...")
        msg = chat([{"role": "system", "content": agent.instructions}] + history,
                   schemas(agent))
        text = (msg.content or "").strip()
        if text:
            last_text = text
            log(f"[{agent.name}] {text}\n")
        new.append({"role": "assistant", "content": text or f"({agent.name} hands off)"})
        valid = [c for c in (msg.tool_calls or []) if c.function.name in agent.handoffs]
        if not valid:
            if agent.name == "Coordinator":
                store.checkpoint(run_id, new, agent.name, turns, revisions, last_text, last_text)
                return last_text
            # The agent forgot to hand off: send the work back to the Coordinator.
            history += new
            agent = AGENTS["Coordinator"]
            store.checkpoint(run_id, new, agent.name, turns, revisions, last_text)
            continue
        tool = valid[0].function.name
        target = agent.handoffs[tool][1]
        if tool == "request_revision":
            revisions += 1
            if revisions > MAX_REVISIONS:
                target = "Coordinator"
        try:
            payload = json.loads(valid[0].function.arguments or "{}").get("message", "")
        except (json.JSONDecodeError, AttributeError):
            payload = ""
        log(f"  -> handoff: {agent.name} -> {target}\n")
        if payload and payload != text:
            last_text = payload
            log(f"[{agent.name}] {payload.strip()}\n")
        new.append({"role": "user", "content":
                    f"[{agent.name} handed off to {target}]\n{payload or text}"})
        history += new
        agent = AGENTS[target]
        store.checkpoint(run_id, new, agent.name, turns, revisions, last_text)
    result = last_text or "Stopped: max turns reached."
    store.checkpoint(run_id, [], agent.name, turns, revisions, last_text, result)
    return result


def main():
    ap = argparse.ArgumentParser(description="Agent swarm demo with durable SQLite state.")
    ap.add_argument("topic", nargs="*", help="topic to write about (starts a new run)")
    ap.add_argument("--resume", metavar="RUN_ID", help="resume an interrupted run")
    ap.add_argument("--list", action="store_true", help="list saved runs and exit")
    ap.add_argument("--db", help="SQLite file (default: $SWARM_DB_PATH or ./swarm_state.db next to swarm.py)")
    args = ap.parse_args()
    store = RunStore(args.db)
    try:
        if args.list:
            for r in store.list_runs():
                print(f"{r['run_id']}  {r['status']:<9} {r['current_agent']:<10} "
                      f"turns={r['turns']:<2} {r['updated_at']}  {r['task'][:50]}")
            return
        try:
            if args.resume:
                result = run_swarm(run_id=args.resume, store=store)
            else:
                topic = " ".join(args.topic) or input("topic> ").strip()
                run_id = store.create_run(topic)
                print(f"Run id: {run_id} (resume with --resume {run_id})\n")
                result = run_swarm(run_id=run_id, store=store)
            print("=== FINAL ===\n" + result)
        except RuntimeError as e:
            print("error>", e)
    finally:
        store.close()


if __name__ == "__main__":
    main()
