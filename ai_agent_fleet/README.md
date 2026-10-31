# AI Agent Fleet (Java)

A dependency-free Java demo of the **fleet** pattern: many identical agents run in parallel on a shared
queue of tasks, under a supervisor that scales, monitors, and heals the pool.

Swarm vs fleet: in [ai_agent_swarm](../ai_agent_swarm) a few *different* agents hand one task to each other.
In a fleet, *the same* agent is replicated to process many independent tasks at scale.

## Components
| Class | Role |
|-------|------|
| `Task` | Immutable unit of work, tracks retry attempts |
| `Agent` | Worker thread: pulls a task, simulates an LLM call, reports back, beats a heartbeat |
| `FleetManager` | Shared queue plus supervisor: autoscaling, health checks, retries, dead-letter list |
| `Main` | Demo: 40 tasks, fleet of 2 growing to 8 |

## What the demo shows
- **Autoscaling:** one agent is added per 250 ms tick while the queue holds more than 2 tasks per agent (max 8).
- **Failure detection:** 8% of tasks hard-crash an agent. Its heartbeat goes stale (>1.5 s), and the supervisor replaces it and recovers its in-flight task.
- **Retries:** soft failures (10%) are re-queued. After 3 attempts a task is dead-lettered.
- **Summary:** completed, dead-lettered, restarts, and peak fleet size.

The LLM call is simulated (sleep plus random failures) so the demo runs offline with no API key.
To use a real model, replace the body of `Agent.work()`.

## Run
Requires JDK 21+ and Maven.
```bash
cd ai_projects/ai_agent_fleet
mvn -q package
java -jar target/ai-agent-fleet-1.0.0.jar        # optional arg: number of tasks
```
