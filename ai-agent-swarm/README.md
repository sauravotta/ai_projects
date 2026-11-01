# AI Agent Swarm

A small demo of the **swarm** pattern: several specialised agents that hand work off to each other
until the task is done. Uses [OpenRouter](https://openrouter.ai) free models.

## Agents
| Agent | Role | Hands off to |
|-------|------|--------------|
| Coordinator | Receives the topic, delivers the final answer | Researcher |
| Researcher | Gathers 4-5 key facts | Writer |
| Writer | Drafts a short paragraph | Critic |
| Critic | Approves or requests one revision | Coordinator (approve) / Writer (revise) |

## How it works
- A handoff is a tool call (e.g. `transfer_to_writer`). Each agent only has the handoff tools it is allowed to use.
- All agents share one conversation history. A handoff switches the active agent's system prompt and tools.
- Guards: at most 12 turns and 1 revision round, so the loop always ends.

## Run
```bash
cd ai-projects/ai-agent-swarm
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env    # then put your OpenRouter key in .env
python swarm.py "why the sky is blue"
```
Output shows each agent's turn and every handoff, then `=== FINAL ===` with the result.
