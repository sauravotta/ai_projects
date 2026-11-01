using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiObservability;

public record LlmResult(string Text, string Model, int InputTokens, int OutputTokens);

public interface IModel
{
    Task<LlmResult> CompleteAsync(string name, IReadOnlyList<(string Role, string Content)> messages);
}

/// <summary>Offline stand-in for an LLM so the demo (and its traces) work without any key.</summary>
public class SimulatedModel : IModel
{
    public async Task<LlmResult> CompleteAsync(string name, IReadOnlyList<(string Role, string Content)> messages)
    {
        using var gen = Tracing.Start($"{name}-llm", "generation");
        gen?.SetTag("langfuse.observation.model.name", "simulated-model");
        gen.SetIO(input: messages.Select(m => new { role = m.Role, content = m.Content }));
        await Task.Delay(Random.Shared.Next(300, 1200));
        var question = messages[^1].Content;
        string text;
        if (name == "planner")
        {
            var q = question.ToLowerInvariant();
            var expr = Regex.Match(q, @"[\d(][\d\s.+\-*/()]*[\d)]");
            if (expr.Success && Regex.IsMatch(q, @"what is|calculate|\d\s*[+\-*/]\s*\d"))
                text = JsonSerializer.Serialize(new { tool = "calculator", arg = expr.Value.Trim() });
            else if (q.Contains("time") || q.Contains("date"))
                text = JsonSerializer.Serialize(new { tool = "clock", arg = "" });
            else if (q.Contains("who") || q.Contains("explain"))
                text = JsonSerializer.Serialize(new { tool = "lookup", arg = question });
            else
                text = JsonSerializer.Serialize(new { tool = "none", arg = "" });
        }
        else
        {
            text = "Simulated answer based on: " + question.Replace("\n", " ");
        }
        var result = new LlmResult(text, "simulated-model", question.Length / 4, text.Length / 4);
        gen?.SetTag("langfuse.observation.usage_details",
            JsonSerializer.Serialize(new { input = result.InputTokens, output = result.OutputTokens }));
        gen.SetIO(output: text);
        return result;
    }
}

/// <summary>OpenRouter (OpenAI-compatible) chat client with free-model fallback. Each call is a Langfuse generation.</summary>
public class OpenRouterModel : IModel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly string[] _models;

    public OpenRouterModel(string apiKey, IEnumerable<string> models)
    {
        _models = models.Distinct().ToArray();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<LlmResult> CompleteAsync(string name, IReadOnlyList<(string Role, string Content)> messages)
    {
        var failures = new List<string>();
        foreach (var model in _models)
        {
            // One generation per attempt, so failed/rate-limited attempts are visible in the trace.
            using var gen = Tracing.Start($"{name}-llm", "generation");
            gen?.SetTag("langfuse.observation.model.name", model);
            gen?.SetTag("gen_ai.request.model", model);
            gen.SetIO(input: messages.Select(m => new { role = m.Role, content = m.Content }));
            try
            {
                using var resp = await Http.PostAsJsonAsync("https://openrouter.ai/api/v1/chat/completions", new
                {
                    model,
                    max_tokens = 200,
                    messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                });
                if (!resp.IsSuccessStatusCode)
                {
                    var msg = $"HTTP {(int)resp.StatusCode}";
                    gen.MarkError(msg);
                    failures.Add($"{model}: {msg}");
                    continue;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    gen.MarkError("empty response");
                    failures.Add($"{model}: empty");
                    continue;
                }
                int inTok = 0, outTok = 0;
                if (root.TryGetProperty("usage", out var u))
                {
                    inTok = u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                    outTok = u.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
                }
                gen?.SetTag("langfuse.observation.usage_details",
                    JsonSerializer.Serialize(new { input = inTok, output = outTok }));
                gen.SetIO(output: text);
                return new LlmResult(text, model, inTok, outTok);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                gen.MarkError(e.GetType().Name);
                failures.Add($"{model}: {e.GetType().Name}");
            }
        }
        throw new InvalidOperationException("all models unavailable (" + string.Join("; ", failures) + ")");
    }
}
