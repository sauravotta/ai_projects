using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentTeam;

/// <summary>Calls an OpenAI-compatible chat endpoint (OpenRouter), falling back through free models.</summary>
public class OpenRouterModel : IModel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly string[] _models;

    public OpenRouterModel(string apiKey, IEnumerable<string> models)
    {
        _models = models.Distinct().ToArray();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> CompleteAsync(string role, string prompt, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var model in _models)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var body = new
                    {
                        model,
                        messages = new object[]
                        {
                            new { role = "system", content = $"You are the {role} on a small team. Be concise (max 60 words)." },
                            new { role = "user", content = prompt },
                        },
                    };
                    using var resp = await Http.PostAsJsonAsync("https://openrouter.ai/api/v1/chat/completions", body, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
                            .GetProperty("content").GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                        failures.Add($"{model}: empty");
                        break;
                    }
                    failures.Add($"{model}: HTTP {(int)resp.StatusCode}");
                    if (resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests) break;
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
                {
                    if (ct.IsCancellationRequested) throw;
                    failures.Add($"{model}: {e.GetType().Name}");
                    break;
                }
            }
        }
        throw new InvalidOperationException("All models failed (" + string.Join("; ", failures) + ")");
    }
}
