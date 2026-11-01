# AI Agent Fleet (Java)

A dependency-free Java demo of the **fleet** pattern: many identical agents run in parallel on a shared
queue of tasks, under a supervisor that scales, monitors, and heals the pool.

Swarm vs fleet: in [ai-agent-swarm](../ai-agent-swarm) a few *different* agents hand one task to each other.
In a fleet, *the same* agent is replicated to process many independent tasks at scale.

## Components
| Class | Role |
|-------|------|
| `Task` | Immutable unit of work, tracks retry attempts |
| `Agent` | Worker thread: pulls a task, calls its `Model`, reports back, beats a heartbeat |
| `Model` | Interface for the agent's brain; `SimulatedModel` (offline) and `OpenRouterModel` (real LLM) |
| `FleetManager` | Shared queue plus supervisor: autoscaling, health checks, retries, dead-letter list |
| `Main` | Demo: 40 tasks, fleet of 2 growing to 8 |

## What the demo shows
- **Autoscaling:** one agent is added per 250 ms tick while the queue holds more than 2 tasks per agent (max 8).
- **Failure detection:** 8% of tasks hard-crash an agent. Its heartbeat goes stale (>1.5 s), and the supervisor replaces it and recovers its in-flight task.
- **Retries:** soft failures (10%) are re-queued. After 4 attempts, with exponential backoff, a task is dead-lettered.
- **Summary:** completed, dead-lettered, restarts, and peak fleet size.

## Models and credentials
- **Simulated (default):** no key needed. Random latency, 10% soft failures, 8% hard crashes.
- **Live:** set `OPENROUTER_API_KEY` (environment variable, or a `KEY=value` line in a `.env` file in the directory you run from) to call a real model through [OpenRouter](https://openrouter.ai). Optional `OPENROUTER_MODEL` sets the preferred model; free fallback models are tried on 429/403/404.
- Live mode uses a smaller fleet (2 to 3 agents, 6 tasks by default). Retries use exponential backoff and a task is dead-lettered after 4 attempts, which can happen on rate-limited free models.
- `.env` is gitignored. Never commit your key.

## Run
Requires JDK 21+ and Maven.
```bash
cd ai-projects/ai-agent-fleet
mvn -q package
java -jar target/ai-agent-fleet-1.0.0.jar        # optional arg: number of tasks
OPENROUTER_API_KEY=your-key java -jar target/ai-agent-fleet-1.0.0.jar
```
