using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AiObservability;

/// <summary>
/// Langfuse has no official .NET SDK, but it ingests standard OpenTelemetry traces (OTLP over HTTP) and
/// maps <c>langfuse.*</c> span attributes to its data model. Scores go through the REST API.
/// When keys are missing, tracing is off: <see cref="Start"/> returns null and everything still runs.
/// </summary>
public static class Tracing
{
    public const string SourceName = "AiObservability";
    private static readonly ActivitySource Source = new(SourceName);
    private static readonly AsyncLocal<string?> Session = new();
    private static TracerProvider? _provider;
    private static HttpClient? _http;
    private static string _host = "https://cloud.langfuse.com";

    public static bool Enabled => _provider is not null;

    public static void Init(string? publicKey, string? secretKey, string? host)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(secretKey)) return;
        _host = (string.IsNullOrWhiteSpace(host) ? _host : host).TrimEnd('/');
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}"));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        _provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("ai-observability-demo"))
            .AddSource(SourceName)
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri($"{_host}/api/public/otel/v1/traces");
                o.Protocol = OtlpExportProtocol.HttpProtobuf; // Langfuse does not accept gRPC
                o.Headers = $"Authorization=Basic {auth},x-langfuse-ingestion-version=4";
            })
            .Build();
    }

    public static void SetSession(string sessionId) => Session.Value = sessionId;

    /// <summary>Starts an observation. type: span | agent | tool | generation.</summary>
    public static Activity? Start(string name, string type)
    {
        var a = Source.StartActivity(name);
        if (a is null) return null;
        a.SetTag("langfuse.observation.type", type);
        // Trace attributes are copied onto every span so filters work on any observation.
        if (Session.Value is { } s) a.SetTag("langfuse.session.id", s);
        a.SetTag("langfuse.trace.tags", new[] { "ai-observability-demo" });
        return a;
    }

    public static void SetIO(this Activity? a, object? input = null, object? output = null)
    {
        if (a is null) return;
        if (input is not null) a.SetTag("langfuse.observation.input", JsonSerializer.Serialize(input));
        if (output is not null) a.SetTag("langfuse.observation.output", JsonSerializer.Serialize(output));
    }

    public static void MarkError(this Activity? a, string message)
    {
        if (a is null) return;
        a.SetStatus(ActivityStatusCode.Error, message);
        a.SetTag("langfuse.observation.level", "ERROR");
        a.SetTag("langfuse.observation.status_message", message);
    }

    /// <summary>Attaches a score to the trace of the given activity (REST API; OTLP has no scores).</summary>
    public static void Score(Activity? root, string name, double value, string dataType)
    {
        if (root is null || _http is null) return;
        try
        {
            using var resp = _http.PostAsJsonAsync($"{_host}/api/public/scores",
                new { traceId = root.TraceId.ToString(), name, value, dataType }).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"  (score '{name}' not recorded: HTTP {(int)resp.StatusCode})");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  (score '{name}' not recorded: {e.GetType().Name})");
        }
    }

    public static void Shutdown()
    {
        _provider?.ForceFlush(10_000);
        _provider?.Dispose();
    }
}
