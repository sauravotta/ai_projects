namespace AgentTeam;

/// <summary>A specialist that repeatedly claims tasks for its role from the shared list.</summary>
public class Teammate
{
    public string Name { get; }
    public string Role { get; }
    private readonly SharedTaskList _board;
    private readonly IModel _model;

    public Teammate(string name, string role, SharedTaskList board, IModel model)
    {
        Name = name;
        Role = role;
        _board = board;
        _model = model;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var task = _board.Claim(Name, Role);
            if (task is null)
            {
                await Task.Delay(150, ct); // nothing ready yet: wait for dependencies or the lead to finish
                continue;
            }
            Log.Say(Name, $"claimed #{task.Id} \"{task.Title}\"");
            try
            {
                var context = string.Join("\n", task.DependsOn.Select(d => $"- {_board.ResultOf(d)}"));
                var prompt = context.Length > 0 ? $"{task.Prompt}\n\nInputs from teammates:\n{context}" : task.Prompt;
                _board.Complete(task, await _model.CompleteAsync(Role, prompt, ct));
                Log.Say(Name, $"finished #{task.Id}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _board.Fail(task, e.Message);
                Log.Say(Name, $"FAILED #{task.Id}: {e.Message}");
            }
        }
    }
}
