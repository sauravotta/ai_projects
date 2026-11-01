# Starter AI Agent

A minimal command-line AI agent that can call tools. It uses [OpenRouter](https://openrouter.ai)
(OpenAI-compatible API) with free models by default.

## How it works
1. You type a message at the `you>` prompt.
2. The message and the tool list are sent to the model.
3. If the model requests a tool, the code runs it and returns the result to the model (up to 5 steps).
4. When the model replies with plain text, it is printed after `agent>`.

Built-in example tools:
- `get_time`: returns the current local date and time.
- `add`: adds two numbers.

Rate limits: free models are often rate-limited. `chat()` retries each model twice (2 s, then 4 s wait)
and falls back through a list of other free models. If all fail, an `error>` message is printed
and the session continues.

## Project structure
| File | Purpose |
|------|---------|
| `agent.py` | Agent loop, tools, and OpenRouter client |
| `requirements.txt` | Python dependencies (`openai`, `python-dotenv`) |
| `.env.example` | Template for environment variables |

## Run locally
Requires Python 3.9+ and a free OpenRouter API key from https://openrouter.ai/keys.

```bash
git clone https://github.com/sauravotta/ai-projects.git
cd ai-projects/starter-ai-agent
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env    # then put your key in .env (never in .env.example)
python agent.py
```

Example session:
```
you> What time is it?
you> What is 1234.5 + 6789?
you> exit
```

## Configuration (`.env`)
| Variable | Description |
|----------|-------------|
| `OPENROUTER_API_KEY` | Your OpenRouter API key (required) |
| `OPENROUTER_MODEL` | Preferred model, tried first (default `nvidia/nemotron-3-ultra-550b-a55b:free`) |

## Adding a tool
Write a function, add it to `TOOLS`, and add its JSON schema to `TOOL_SCHEMAS` in `agent.py`.

## Troubleshooting
- `429` / "rate-limited": wait a bit and retry, or set `OPENROUTER_MODEL` to another `:free` model.
- "Missing credentials": `.env` is missing or `OPENROUTER_API_KEY` is empty.
