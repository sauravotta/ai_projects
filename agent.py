"""Minimal tool-calling AI agent using the OpenAI API."""
import json
import os
from datetime import datetime

from dotenv import load_dotenv
from openai import OpenAI

load_dotenv()
client = OpenAI()
MODEL = os.getenv("OPENAI_MODEL", "gpt-4o-mini")


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
        msg = client.chat.completions.create(
            model=MODEL, messages=history, tools=TOOL_SCHEMAS
        ).choices[0].message
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
        print("agent>", run_agent(text, history))


if __name__ == "__main__":
    main()
