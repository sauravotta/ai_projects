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
Output starts with a run id; each agent's turn and every handoff, then `=== FINAL ===` with the result.

## Durable state (SQLite)
Every run is stored in a local SQLite database (standard-library `sqlite3`, no extra dependency).
- Tables: `runs` (task, status, current agent, turn/revision counters, result) and `messages` (ordered conversation and handoff history, keyed by `run_id`). Runs never see each other's history.
- Each turn is checkpointed in one transaction, so an interrupted run (crash, Ctrl-C, rate-limit error) resumes from its last completed turn. The `MAX_TURNS` limit counts turns across resumes.
- Database path: `--db PATH`, else `SWARM_DB_PATH`, else `swarm_state.db` next to `swarm.py` (git-ignored).

```bash
python swarm.py "why the sky is blue"      # prints: Run id: 1a2b3c4d
python swarm.py --list                     # saved runs and status
python swarm.py --resume 1a2b3c4d          # continue an interrupted run (completed runs just print their result)
```

## Tests
```bash
python -m unittest -v    # uses a stubbed model; no API key needed
```
