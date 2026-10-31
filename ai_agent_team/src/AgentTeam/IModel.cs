namespace AgentTeam;

/// <summary>The "brain" an agent calls to do its work.</summary>
public interface IModel
{
    Task<string> CompleteAsync(string role, string prompt, CancellationToken ct);
}
