# AI Agent Team (.NET)

A .NET demo of the **team** pattern: a team lead and several specialist teammates coordinate on one goal
through a **shared task list**.

How the patterns differ:
- [ai-agent-swarm](../ai-agent-swarm): different agents hand one task to each other.
- [ai-agent-fleet](../ai-agent-fleet): identical agents process many independent tasks.
- **Team (this project):** specialists work in parallel on pieces of one goal, coordinated through a shared board.

## Components (`src/AgentTeam`)
| File | Role |
|------|------|
| `TeamLead.cs` | Breaks the goal into tasks, starts the team, monitors progress, synthesizes the final brief |
| `Teammate.cs` | A specialist (Researcher, Writer, Reviewer) that claims tasks for its role and posts results |
| `SharedTaskList.cs` | SQLite-backed board: atomic claiming, dependency gating, failure propagation, restart recovery |
| `IModel.cs`, `SimulatedModel.cs`, `OpenRouterModel.cs` | The agents' brain: offline simulation or a real LLM |

## What happens
1. The lead posts 4 tasks: *research* and *risks* (no dependencies), *draft* (needs both), *review* (needs the draft).
2. Two researchers claim the first two tasks and work in parallel.
3. When both finish, the writer's task unlocks, then the reviewer's. Dependent tasks receive their teammates' results as input.
4. If a task fails, tasks depending on it are marked blocked so the lead never waits forever.
5. The lead combines all results into a final brief.

## Persistence
Task definitions, dependencies, status, owner, claim generation, and results are stored locally in SQLite.
By default, each goal gets its own database under the current user's local application-data directory
(`ai-agent-team/<goal-hash>-task-board.db`; for example, `~/Library/Application Support/ai-agent-team/`
on macOS). Set `AI_AGENT_TEAM_DB_PATH` to choose an explicit database file instead.

Run the same goal again to reuse its board. Completed work is retained and remaining work continues;
claims left behind by a process that has exited are returned to Pending on startup. A live process's
claims are left alone, so another run using the same database will wait for that worker rather than
steal its task. An explicit database path can hold only one goal at a time; use a different file for a
different goal, or remove that database and its `-wal`/`-shm` sidecars to start fresh.

## Run
Requires the .NET 7 SDK or newer.
```bash
cd ai-projects/ai-agent-team/src/AgentTeam
dotnet build
dotnet run                                   # simulated model, no key needed
dotnet run -- "Plan a company hackathon"     # custom goal
OPENROUTER_API_KEY=your-key dotnet run       # real LLM
```
The key can also be a `KEY=value` line in a `.env` file in the directory you run from (gitignored; never commit it).
`OPENROUTER_MODEL` optionally sets the preferred model. Free fallback models are tried on errors.

Note: OpenRouter's free tier is limited to about 50 requests per day without credits, so live runs can fail with HTTP 429.

For a local persistence/resume smoke check, use a test database and run twice with the exact same goal:
```bash
AI_AGENT_TEAM_DB_PATH=./task-board-smoke.db dotnet run -- "Persistence smoke check"
AI_AGENT_TEAM_DB_PATH=./task-board-smoke.db dotnet run -- "Persistence smoke check"
```
The second run should log `resumed` and synthesize from the saved results. Delete `task-board-smoke.db`
and its SQLite `-wal`/`-shm` sidecars afterward if you want to reset the demo.
