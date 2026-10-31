"""Minimal tool-calling AI agent using OpenRouter (OpenAI-compatible API)."""
import json
import os
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
        history.append(msg)
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
    history = [{"role": "system", "content": "You are a helpful assistant."}]
    print("Agent ready. Type 'exit' to quit.")
    while (text := input("you> ").strip()) not in ("exit", "quit"):
        n = len(history)
        try:
            print("agent>", run_agent(text, history))
        except RuntimeError as e:
            del history[n:]
            print("error>", e)


if __name__ == "__main__":
    main()
