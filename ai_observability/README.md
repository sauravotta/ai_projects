# AI Observability (.NET + Langfuse)

Shows how to make an AI agent observable: every question becomes a **trace** in a [Langfuse](https://langfuse.com)
dashboard with nested steps, latency, token usage, errors, and quality **scores**.

Langfuse has no official .NET SDK, so this project uses what .NET does best: the standard
**OpenTelemetry** SDK (`ActivitySource`) exporting over OTLP/HTTP to Langfuse's `/api/public/otel` endpoint.
Langfuse-specific meaning is added with `langfuse.*` span attributes, and scores are posted to its REST API.

## What gets traced
A small multi-agent pipeline (Planner, Tool, Answerer). One trace per question:
```
ask
 +- planner (agent)  -> planner-llm (generation: model, tokens)
 +- tool:calculator | tool:clock | tool:lookup (tool, ERROR level on failure)
 +- answerer (agent) -> answerer-llm (generation: model, tokens)
```
Scores on every trace: `tool_success` (boolean), `answer_length_ok` (boolean), `relevance_overlap` (0 to 1, a crude proxy).
The 8 built-in questions include a failing tool call (`10 / 0`) and a slow lookup, so the dashboard has errors and latency outliers to show.

## Run
Requires the .NET 7 SDK or newer (if you use a newer SDK, change `TargetFramework` in the `.csproj`).

1. Create a free project on [Langfuse Cloud](https://cloud.langfuse.com) (Hobby tier) and copy its API keys.
2. Configure keys, either as environment variables or in a `.env` file in the directory you run from (gitignored, never commit it):
   ```bash
   cd ai_projects/ai_observability/src/AiObservability
   cp ../../.env.example .env      # then fill in your keys
   dotnet run                      # or: dotnet run -- 3   (run only the first 3 questions)
   ```
| Variable | Purpose |
|----------|---------|
| `LANGFUSE_PUBLIC_KEY`, `LANGFUSE_SECRET_KEY` | Enables tracing. Without them the demo still runs, with tracing off |
| `LANGFUSE_HOST` | Region/host, default `https://cloud.langfuse.com` (US: `https://us.cloud.langfuse.com`) |
| `OPENROUTER_API_KEY` | Real LLM via OpenRouter. Without it a simulated model is used |
| `OPENROUTER_MODEL` | Preferred model; free fallbacks are tried on errors |

OpenRouter's free tier is limited to about 50 requests per day (each question uses 2 LLM calls).

## Tour of the dashboard
- **Tracing:** open a trace to see the step tree, timings, inputs/outputs, and the model and token usage on each generation.
- **Sessions:** all questions from one run are grouped under `demo-xxxxxx`.
- **Dashboards:** latency percentiles, token and cost by model, error counts, and the three score trends.
- **Filter** by tag `ai-observability-demo`, by `ERROR` level, or by low `tool_success`.

## Files (`src/AiObservability`)
| File | Role |
|------|------|
| `Tracing.cs` | OpenTelemetry setup, Langfuse OTLP export, helpers for observations and the scores API |
| `Pipeline.cs` | Planner, tool and answerer steps with tracing and scoring |
| `Models.cs` | `IModel`, `SimulatedModel`, `OpenRouterModel` (one generation per attempt) |
| `Tools.cs` | Calculator (safe expression parser), clock, slow lookup |
| `Program.cs` | Config loading and the demo question set |

## Notes
- Cost appears in Langfuse only for models it has pricing for; free OpenRouter models report 0.
- Tested with the export pointed at a local stand-in endpoint (protobuf OTLP, auth header, attributes, scores). Verify against your own Langfuse project.
