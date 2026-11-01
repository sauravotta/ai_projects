using AgentTeam;

// With OPENROUTER_API_KEY set (env var or ./.env) the team uses a real model; otherwise a simulated one.
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

var apiKey = Setting("OPENROUTER_API_KEY");
IModel model;
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Mode: simulated model (set OPENROUTER_API_KEY for a real LLM)");
    model = new SimulatedModel();
}
else
{
    Console.WriteLine("Mode: live OpenRouter model");
    model = new OpenRouterModel(apiKey, new[]
    {
        Setting("OPENROUTER_MODEL") ?? "nvidia/nemotron-3-ultra-550b-a55b:free",
        "nvidia/nemotron-3.5-lightning:free",
        "google/gemma-4-31b-it:free",
        "google/gemma-4-26b-a4b-it:free",
    });
}

var goal = args.Length > 0 ? string.Join(' ', args) : "Launch a small online coffee-subscription shop";
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
try
{
    var result = await new TeamLead(model).RunAsync(goal, cts.Token);
    Console.WriteLine("\n=== FINAL BRIEF ===\n" + result);
}
catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
{
    Console.WriteLine("\nerror> " + (e is OperationCanceledException ? "timed out or cancelled" : e.Message));
    return 1;
}
return 0;
