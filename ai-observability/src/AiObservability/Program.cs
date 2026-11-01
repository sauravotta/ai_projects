using AiObservability;

// Settings come from environment variables, falling back to KEY=value lines in ./.env (gitignored).
string? Setting(string key)
{
    var v = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrWhiteSpace(v)) return v;
    if (!File.Exists(".env")) return null;
    foreach (var line in File.ReadAllLines(".env"))
    {
        var l = line.Trim();
        if (l.StartsWith(key + "=")) return l[(key.Length + 1)..].Trim().Trim('"', '\'');
    }
    return null;
}

Tracing.Init(Setting("LANGFUSE_PUBLIC_KEY"), Setting("LANGFUSE_SECRET_KEY"), Setting("LANGFUSE_HOST"));

var apiKey = Setting("OPENROUTER_API_KEY");
IModel model = string.IsNullOrWhiteSpace(apiKey)
    ? new SimulatedModel()
    : new OpenRouterModel(apiKey, new[]
    {
        Setting("OPENROUTER_MODEL") ?? "nvidia/nemotron-3-ultra-550b-a55b:free",
        "nvidia/nemotron-3.5-lightning:free",
        "google/gemma-4-31b-it:free",
        "google/gemma-4-26b-a4b-it:free",
    });

// Varied traffic so the dashboard has something to show.
string[] questions =
{
    "What is 1234.5 + 6789?",
    "What is 18 * (4 + 3)?",
    "What is 10 / 0?",                 // tool fails: ERROR span, tool_success = 0
    "What time is it right now?",
    "Explain what an AI agent is.",    // slow lookup tool: latency outlier
    "Who wrote Pride and Prejudice?",
    "Tell me a one-line joke.",
    "What is 99 - 1?",
};
var limit = args.Length > 0 && int.TryParse(args[0], out var n) ? Math.Min(n, questions.Length) : questions.Length;
var session = "demo-" + Guid.NewGuid().ToString("N")[..6];

Console.WriteLine($"tracing: {(Tracing.Enabled ? "Langfuse ON" : "off (set LANGFUSE_PUBLIC_KEY / LANGFUSE_SECRET_KEY)")} | " +
                  $"llm: {(model is SimulatedModel ? "simulated (set OPENROUTER_API_KEY)" : "OpenRouter")} | session: {session}");

var pipeline = new Pipeline(model);
foreach (var q in questions.Take(limit))
{
    Console.WriteLine($"\nQ: {q}");
    try
    {
        Console.WriteLine("A: " + await pipeline.AskAsync(q, session));
    }
    catch (InvalidOperationException e)
    {
        Console.WriteLine("ERROR: " + e.Message);
    }
}

Tracing.Shutdown();
if (Tracing.Enabled) Console.WriteLine("\nDone. Open your Langfuse project > Tracing / Sessions / Dashboards.");
