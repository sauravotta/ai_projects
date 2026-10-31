namespace AgentTeam;

public static class Log
{
    private static readonly object Gate = new();

    public static void Say(string who, string message)
    {
        lock (Gate) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {who,-7} {message}");
    }
}
