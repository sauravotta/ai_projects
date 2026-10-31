namespace AgentTeam;

public enum TaskStatus { Pending, InProgress, Done, Failed }

public class TeamTask
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Role { get; init; }
    public required string Prompt { get; init; }
    public int[] DependsOn { get; init; } = Array.Empty<int>();
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public string? Owner { get; set; }
    public string? Result { get; set; }
}

/// <summary>
/// The board the whole team shares. Teammates claim work atomically; a task only becomes claimable
/// when everything it depends on is Done. All access goes through one lock.
/// </summary>
public class SharedTaskList
{
    private readonly object _gate = new();
    private readonly List<TeamTask> _tasks = new();

    public void Add(TeamTask task)
    {
        lock (_gate) _tasks.Add(task);
    }

    public TeamTask? Claim(string worker, string role)
    {
        lock (_gate)
        {
            var next = _tasks.FirstOrDefault(t => t.Status == TaskStatus.Pending && t.Role == role
                && t.DependsOn.All(d => _tasks.First(x => x.Id == d).Status == TaskStatus.Done));
            if (next is null) return null;
            next.Status = TaskStatus.InProgress;
            next.Owner = worker;
            return next;
        }
    }

    public void Complete(TeamTask task, string result)
    {
        lock (_gate)
        {
            task.Status = TaskStatus.Done;
            task.Result = result;
        }
    }

    public void Fail(TeamTask task, string reason)
    {
        lock (_gate)
        {
            task.Status = TaskStatus.Failed;
            task.Result = reason;
            // Anything that (transitively) depends on a failed task can never run.
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var t in _tasks.Where(t => t.Status == TaskStatus.Pending
                             && t.DependsOn.Any(d => _tasks.First(x => x.Id == d).Status == TaskStatus.Failed)))
                {
                    t.Status = TaskStatus.Failed;
                    t.Result = "blocked: a dependency failed";
                    changed = true;
                }
            }
        }
    }

    public string ResultOf(int id)
    {
        lock (_gate) return _tasks.First(t => t.Id == id).Result ?? "";
    }

    public bool IsSettled()
    {
        lock (_gate) return _tasks.All(t => t.Status is TaskStatus.Done or TaskStatus.Failed);
    }

    public List<TeamTask> Snapshot()
    {
        lock (_gate) return _tasks.Select(t => new TeamTask
        {
            Id = t.Id, Title = t.Title, Role = t.Role, Prompt = t.Prompt, DependsOn = t.DependsOn,
            Status = t.Status, Owner = t.Owner, Result = t.Result,
        }).ToList();
    }
}
