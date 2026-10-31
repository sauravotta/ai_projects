"""Agent swarm demo: four agents hand work off to each other via tool calls.

Coordinator -> Researcher -> Writer -> Critic -> (Writer again if revisions are
needed) -> Coordinator, which delivers the final answer. All agents share one
conversation history; a handoff just switches which agent (system prompt and
tools) answers next.
"""
import json
import os
import sys
import time
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


def run_swarm(task: str, log: Callable[[str], None] = print) -> str:
    history = [{"role": "user", "content": task}]
    agent = AGENTS["Coordinator"]
    revisions = 0
    last_text = ""
    for _ in range(MAX_TURNS):
        log(f"[{agent.name}] thinking...")
        msg = chat([{"role": "system", "content": agent.instructions}] + history,
                   schemas(agent))
        text = (msg.content or "").strip()
        if text:
            last_text = text
            log(f"[{agent.name}] {text}\n")
        history.append({"role": "assistant", "content": text or f"({agent.name} hands off)"})
        valid = [c for c in (msg.tool_calls or []) if c.function.name in agent.handoffs]
        if not valid:
            if agent.name == "Coordinator":
                return last_text
            # The agent forgot to hand off: send the work back to the Coordinator.
            agent = AGENTS["Coordinator"]
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
        history.append({"role": "user", "content":
                        f"[{agent.name} handed off to {target}]\n{payload or text}"})
        agent = AGENTS[target]
    return last_text or "Stopped: max turns reached."


def main():
    topic = " ".join(sys.argv[1:]) or input("topic> ").strip()
    try:
        print("=== FINAL ===\n" + run_swarm(topic))
    except RuntimeError as e:
        print("error>", e)


if __name__ == "__main__":
    main()
