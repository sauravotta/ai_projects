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
| `SharedTaskList.cs` | Thread-safe board: atomic claiming, dependency gating, failure propagation |
| `IModel.cs`, `SimulatedModel.cs`, `OpenRouterModel.cs` | The agents' brain: offline simulation or a real LLM |

## What happens
1. The lead posts 4 tasks: *research* and *risks* (no dependencies), *draft* (needs both), *review* (needs the draft).
2. Two researchers claim the first two tasks and work in parallel.
3. When both finish, the writer's task unlocks, then the reviewer's. Dependent tasks receive their teammates' results as input.
4. If a task fails, tasks depending on it are marked blocked so the lead never waits forever.
5. The lead combines all results into a final brief.

## Run
Requires the .NET 7 SDK or newer (if you use a newer SDK, change `TargetFramework` in the `.csproj`).
```bash
cd ai-projects/ai-agent-team/src/AgentTeam
dotnet run                                   # simulated model, no key needed
dotnet run -- "Plan a company hackathon"     # custom goal
OPENROUTER_API_KEY=your-key dotnet run       # real LLM
```
The key can also be a `KEY=value` line in a `.env` file in the directory you run from (gitignored; never commit it).
`OPENROUTER_MODEL` optionally sets the preferred model. Free fallback models are tried on errors.

Note: OpenRouter's free tier is limited to about 50 requests per day without credits, so live runs can fail with HTTP 429.
