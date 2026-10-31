# Starter AI Agent

A minimal tool-calling agent loop (OpenAI API) with two example tools.

## Run
```bash
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env   # add your OPENAI_API_KEY
python agent.py
```

Add tools by writing a function and registering it in `TOOLS` and `TOOL_SCHEMAS` in `agent.py`.
