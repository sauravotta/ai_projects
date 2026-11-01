namespace AgentTeam;

/// <summary>Offline stand-in for an LLM: short random latency and canned, role-specific output.</summary>
public class SimulatedModel : IModel
{
    public async Task<string> CompleteAsync(string role, string prompt, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(600, 1400), ct);
        var inputs = prompt.Split("Inputs from teammates:").Length > 1 ? " (built on teammate inputs)" : "";
        return role switch
        {
            "Researcher" => "findings: demand is steady, margins depend on shipping, retention is key" + inputs,
            "Writer" => "draft brief: launch with 3 blends, monthly plans, and free first shipment" + inputs,
            "Reviewer" => "review: add pricing detail; state the target customer" + inputs,
            _ => "FINAL: launch small with 3 blends, price monthly plans clearly, watch shipping costs and retention; "
                 + "incorporate the reviewer's pricing and target-customer notes.",
        };
    }
}
