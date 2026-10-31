using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiObservability;

/// <summary>
/// Planner -> Tool -> Answerer. One trace per question:
///   ask -> planner (agent) -> LLM generation
///       -> tool:NAME (tool)
///       -> answerer (agent) -> LLM generation
/// plus custom scores on the trace.
/// </summary>
public class Pipeline
{
    private readonly IModel _model;

    public Pipeline(IModel model) => _model = model;

    public async Task<string> AskAsync(string question, string sessionId)
    {
        Tracing.SetSession(sessionId);
        using var root = Tracing.Start("ask", "span");
        root.SetIO(input: question);
        try
        {
            var plan = await PlanAsync(question);
            var (observation, toolOk) = await RunToolAsync(plan);
            var answer = await AnswerAsync(question, observation);
            root.SetIO(output: answer);
            Score(root, question, answer, toolOk);
            return answer;
        }
        catch (Exception e)
        {
            root.MarkError(e.Message);
            throw;
        }
    }

    private async Task<(string Tool, string Arg)> PlanAsync(string question)
    {
        using var span = Tracing.Start("planner", "agent");
        span.SetIO(input: question);
        var result = await _model.CompleteAsync("planner", new[]
        {
            ("system", "Decide which tool helps. Reply ONLY with JSON: {\"tool\": \"calculator\"|\"clock\"|\"lookup\"|\"none\", " +
                       "\"arg\": \"<input>\"}. calculator takes an arithmetic expression."),
            ("user", question),
        });
        var plan = ("none", "");
        var match = Regex.Match(result.Text, @"\{.*\}", RegexOptions.Singleline);
        if (match.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(match.Value);
                var tool = doc.RootElement.TryGetProperty("tool", out var t) ? t.GetString() ?? "none" : "none";
                var arg = doc.RootElement.TryGetProperty("arg", out var a) ? a.ToString() : "";
                if (Tools.Names.Contains(tool)) plan = (tool, arg);
            }
            catch (JsonException)
            {
                // Malformed plan: fall back to answering without a tool.
            }
        }
        span.SetIO(output: new { tool = plan.Item1, arg = plan.Item2 });
        return plan;
    }

    private static async Task<(string Observation, bool Ok)> RunToolAsync((string Tool, string Arg) plan)
    {
        if (plan.Tool == "none") return ("", true);
        using var span = Tracing.Start($"tool:{plan.Tool}", "tool");
        span.SetIO(input: plan.Arg);
        try
        {
            var result = await Tools.RunAsync(plan.Tool, plan.Arg);
            span.SetIO(output: result);
            return (result, true);
        }
        catch (Exception e) when (e is ArithmeticException or FormatException or ArgumentException)
        {
            span.MarkError(e.Message);
            return ($"tool error: {e.Message}", false);
        }
    }

    private async Task<string> AnswerAsync(string question, string observation)
    {
        using var span = Tracing.Start("answerer", "agent");
        var context = observation.Length > 0 ? $"\n\nTool result: {observation}" : "";
        span.SetIO(input: question + context);
        var result = await _model.CompleteAsync("answerer", new[]
        {
            ("system", "Answer the user concisely (max 50 words). If a tool result is given, use it. If it is an error, say so honestly."),
            ("user", question + context),
        });
        span.SetIO(output: result.Text);
        return result.Text;
    }

    private static void Score(Activity? root, string question, string answer, bool toolOk)
    {
        var words = Regex.Matches(question.ToLowerInvariant(), "[a-z]{4,}").Select(m => m.Value).ToHashSet();
        var answerWords = Regex.Matches(answer.ToLowerInvariant(), "[a-z]{4,}").Select(m => m.Value).ToHashSet();
        var overlap = words.Count == 0 ? 0 : (double)words.Intersect(answerWords).Count() / words.Count;
        var length = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Tracing.Score(root, "tool_success", toolOk ? 1 : 0, "BOOLEAN");
        Tracing.Score(root, "answer_length_ok", length is >= 5 and <= 80 ? 1 : 0, "BOOLEAN");
        // Crude relevance proxy: share of the question's content words echoed in the answer.
        Tracing.Score(root, "relevance_overlap", Math.Round(overlap, 2), "NUMERIC");
    }
}
