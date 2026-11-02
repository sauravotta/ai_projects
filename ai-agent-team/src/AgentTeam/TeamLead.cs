namespace AgentTeam;

/// <summary>
/// Breaks the goal into tasks on the shared list, starts the teammates, watches progress,
/// then synthesizes the teammates' results into the final deliverable.
/// </summary>
public class TeamLead
{
    private SharedTaskList _board = null!;
    private readonly IModel _model;
    private readonly string? _databasePath;
    private List<Teammate> _team = new();

    public TeamLead(IModel model, string? databasePath = null)
    {
        _model = model;
        _databasePath = databasePath;
    }

    public async Task<string> RunAsync(string goal, CancellationToken ct)
    {
        _board = new SharedTaskList(_databasePath ?? SharedTaskList.GetDatabasePathForGoal(goal));
        _team = new List<Teammate>
        {
            new Teammate("Ada", "Researcher", _board, _model),
            new Teammate("Alan", "Researcher", _board, _model),
            new Teammate("Linus", "Writer", _board, _model),
            new Teammate("Grace", "Reviewer", _board, _model),
        };
        Log.Say("Lead", $"goal: {goal}");
        var resumed = _board.Initialize(goal, Plan(goal));
        Log.Say("Lead", $"state database: {_board.DatabasePath}");
        Log.Say("Lead", $"{(resumed ? "resumed" : "posted")} {_board.Snapshot().Count} tasks, " +
                        $"team: {string.Join(", ", _team.Select(t => $"{t.Name} ({t.Role})"))}");

        using var workersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = _team.Select(t => Task.Run(() => t.RunAsync(workersCts.Token))).ToList();

        while (!_board.IsSettled())
        {
            await Task.Delay(300, ct);
            var s = _board.Snapshot();
            Log.Say("Lead", $"progress: {s.Count(t => t.Status == TaskStatus.Done)}/{s.Count} done, " +
                            $"{s.Count(t => t.Status == TaskStatus.InProgress)} in progress");
        }
        workersCts.Cancel();
        try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }

        var final = _board.Snapshot();
        var failed = final.Where(t => t.Status == TaskStatus.Failed).ToList();
        if (failed.Count > 0)
            Log.Say("Lead", $"{failed.Count} task(s) failed or blocked: {string.Join(", ", failed.Select(t => "#" + t.Id))}");

        if (failed.Count == final.Count)
            throw new InvalidOperationException("every task failed, nothing to synthesize: " + failed[0].Result);

        var notes = string.Join("\n", final.Where(t => t.Status == TaskStatus.Done)
            .Select(t => $"{t.Title} ({t.Owner}): {t.Result}"));
        Log.Say("Lead", "synthesizing final answer");
        return await _model.CompleteAsync("Team Lead",
            $"Combine your team's work into a short final brief for the goal \"{goal}\".\n\n{notes}", ct);
    }

    private static IReadOnlyCollection<TeamTask> Plan(string goal)
    {
        return new[]
        {
            new TeamTask { Id = 1, Role = "Researcher", Title = "Research the topic",
                Prompt = $"List 3 key facts or considerations for: {goal}" },
            new TeamTask { Id = 2, Role = "Researcher", Title = "Identify risks",
                Prompt = $"List 2 main risks or pitfalls for: {goal}" },
            new TeamTask { Id = 3, Role = "Writer", Title = "Draft the brief", DependsOn = new[] { 1, 2 },
                Prompt = $"Write a short draft brief (under 60 words) for: {goal}" },
            new TeamTask { Id = 4, Role = "Reviewer", Title = "Review the draft", DependsOn = new[] { 3 },
                Prompt = "Review the draft: give 2 concrete improvements." },
        };
    }
}
