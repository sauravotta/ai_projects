using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

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
    internal long ClaimVersion { get; set; }
}

/// <summary>
/// A SQLite-backed board shared by teammates and preserved across process restarts.
/// </summary>
public class SharedTaskList
{
    private readonly string _databasePath;
    public string DatabasePath => _databasePath;

    public SharedTaskList(string? databasePath = null)
    {
        _databasePath = Path.GetFullPath(databasePath ?? GetDefaultDatabasePath());
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS BoardMetadata (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS Tasks (
                    Id INTEGER PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    Prompt TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    Owner TEXT,
                    Result TEXT,
                    ClaimVersion INTEGER NOT NULL DEFAULT 0,
                    ClaimProcessId INTEGER,
                    ClaimProcessStartTicks INTEGER
                );
                CREATE TABLE IF NOT EXISTS TaskDependencies (
                    TaskId INTEGER NOT NULL REFERENCES Tasks(Id) ON DELETE CASCADE,
                    DependencyId INTEGER NOT NULL REFERENCES Tasks(Id),
                    PRIMARY KEY (TaskId, DependencyId),
                    CHECK (TaskId <> DependencyId)
                );
                CREATE INDEX IF NOT EXISTS IX_Tasks_Status_Role ON Tasks(Status, Role, Id);
                CREATE INDEX IF NOT EXISTS IX_TaskDependencies_DependencyId ON TaskDependencies(DependencyId);
                """;
            command.ExecuteNonQuery();
        }

        RecoverAbandonedClaims(connection);
    }

    public bool Initialize(string goal, IReadOnlyCollection<TeamTask> plannedTasks)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        var existingCount = ExecuteScalar<long>(connection, transaction, "SELECT COUNT(*) FROM Tasks;");
        var storedGoal = ExecuteScalar<string?>(connection, transaction,
            "SELECT Value FROM BoardMetadata WHERE Key = 'goal';");

        if (existingCount > 0)
        {
            if (!string.Equals(storedGoal, goal, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The persisted task board belongs to a different goal. Use another database or clear '{_databasePath}'.");
            transaction.Commit();
            return true;
        }

        if (plannedTasks.Select(t => t.Id).Distinct().Count() != plannedTasks.Count)
            throw new ArgumentException("Task IDs must be unique.", nameof(plannedTasks));

        foreach (var task in plannedTasks)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Tasks (Id, Title, Role, Prompt, Status, Owner, Result)
                VALUES ($id, $title, $role, $prompt, $status, $owner, $result);
                """;
            command.Parameters.AddWithValue("$id", task.Id);
            command.Parameters.AddWithValue("$title", task.Title);
            command.Parameters.AddWithValue("$role", task.Role);
            command.Parameters.AddWithValue("$prompt", task.Prompt);
            command.Parameters.AddWithValue("$status", (int)TaskStatus.Pending);
            command.Parameters.AddWithValue("$owner", DBNull.Value);
            command.Parameters.AddWithValue("$result", DBNull.Value);
            command.ExecuteNonQuery();
        }

        foreach (var task in plannedTasks)
        foreach (var dependencyId in task.DependsOn.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO TaskDependencies (TaskId, DependencyId) VALUES ($taskId, $dependencyId);";
            command.Parameters.AddWithValue("$taskId", task.Id);
            command.Parameters.AddWithValue("$dependencyId", dependencyId);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO BoardMetadata (Key, Value) VALUES ('goal', $goal);";
            command.Parameters.AddWithValue("$goal", goal);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return false;
    }

    public void Add(TeamTask task)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        InsertTask(connection, transaction, task);
        foreach (var dependencyId in task.DependsOn.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO TaskDependencies (TaskId, DependencyId) VALUES ($taskId, $dependencyId);";
            command.Parameters.AddWithValue("$taskId", task.Id);
            command.Parameters.AddWithValue("$dependencyId", dependencyId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public TeamTask? Claim(string worker, string role)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var id = ExecuteScalar<long?>(connection, transaction, """
            SELECT t.Id
            FROM Tasks t
            WHERE t.Status = $pending AND t.Role = $role
              AND NOT EXISTS (
                  SELECT 1 FROM TaskDependencies d
                  JOIN Tasks dependency ON dependency.Id = d.DependencyId
                  WHERE d.TaskId = t.Id AND dependency.Status <> $done
              )
            ORDER BY t.Id
            LIMIT 1;
            """, ("$pending", (int)TaskStatus.Pending), ("$role", role), ("$done", (int)TaskStatus.Done));
        if (id is null)
        {
            transaction.Commit();
            return null;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Tasks
                SET Status = $inProgress, Owner = $owner, Result = NULL,
                    ClaimVersion = ClaimVersion + 1,
                    ClaimProcessId = $processId, ClaimProcessStartTicks = $processStartTicks
                WHERE Id = $id AND Status = $pending;
                """;
            command.Parameters.AddWithValue("$inProgress", (int)TaskStatus.InProgress);
            command.Parameters.AddWithValue("$owner", worker);
            command.Parameters.AddWithValue("$processId", Environment.ProcessId);
            command.Parameters.AddWithValue("$processStartTicks", CurrentProcessStartTicks());
            command.Parameters.AddWithValue("$id", id.Value);
            command.Parameters.AddWithValue("$pending", (int)TaskStatus.Pending);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The selected task could not be claimed.");
        }

        var task = ReadTask(connection, transaction, checked((int)id.Value));
        transaction.Commit();
        return task;
    }

    public void Complete(TeamTask task, string result)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        UpdateClaimedTask(connection, transaction, task, TaskStatus.Done, result);
        transaction.Commit();
        task.Status = TaskStatus.Done;
        task.Result = result;
    }

    public void Fail(TeamTask task, string reason)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        UpdateClaimedTask(connection, transaction, task, TaskStatus.Failed, reason);

        int changed;
        do
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Tasks
                SET Status = $failed, Result = 'blocked: a dependency failed'
                WHERE Status = $pending
                  AND Id IN (
                      SELECT d.TaskId
                      FROM TaskDependencies d
                      JOIN Tasks dependency ON dependency.Id = d.DependencyId
                      WHERE dependency.Status = $failed
                  );
                """;
            command.Parameters.AddWithValue("$failed", (int)TaskStatus.Failed);
            command.Parameters.AddWithValue("$pending", (int)TaskStatus.Pending);
            changed = command.ExecuteNonQuery();
        } while (changed > 0);

        transaction.Commit();
        task.Status = TaskStatus.Failed;
        task.Result = reason;
    }

    public string ResultOf(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Result FROM Tasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string ?? "";
    }

    public bool IsSettled()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM Tasks WHERE Status NOT IN ($done, $failed);";
        command.Parameters.AddWithValue("$done", (int)TaskStatus.Done);
        command.Parameters.AddWithValue("$failed", (int)TaskStatus.Failed);
        return Convert.ToInt64(command.ExecuteScalar()) == 0;
    }

    public List<TeamTask> Snapshot()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Tasks ORDER BY Id;";
        var ids = new List<int>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) ids.Add(reader.GetInt32(0));
        }
        return ids.Select(id => ReadTask(connection, null, id)).ToList();
    }

    private static void InsertTask(SqliteConnection connection, SqliteTransaction transaction, TeamTask task)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Tasks (Id, Title, Role, Prompt, Status, Owner, Result)
            VALUES ($id, $title, $role, $prompt, $status, $owner, $result);
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$role", task.Role);
        command.Parameters.AddWithValue("$prompt", task.Prompt);
        command.Parameters.AddWithValue("$status", (int)task.Status);
        command.Parameters.AddWithValue("$owner", (object?)task.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)task.Result ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpdateClaimedTask(
        SqliteConnection connection, SqliteTransaction transaction, TeamTask task, TaskStatus status, string result)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Tasks
            SET Status = $status, Result = $result, ClaimProcessId = NULL, ClaimProcessStartTicks = NULL
            WHERE Id = $id AND Status = $inProgress AND Owner = $owner AND ClaimVersion = $claimVersion;
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$inProgress", (int)TaskStatus.InProgress);
        command.Parameters.AddWithValue("$owner", (object?)task.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("$claimVersion", task.ClaimVersion);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Task #{task.Id} is no longer owned by this claim.");
    }

    private static TeamTask ReadTask(SqliteConnection connection, SqliteTransaction? transaction, int id)
    {
        var dependencies = new List<int>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT DependencyId FROM TaskDependencies WHERE TaskId = $id ORDER BY DependencyId;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) dependencies.Add(reader.GetInt32(0));
        }

        TeamTask task;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT Id, Title, Role, Prompt, Status, Owner, Result, ClaimVersion
                FROM Tasks WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException($"Task #{id} does not exist.");
            task = new TeamTask
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Role = reader.GetString(2),
                Prompt = reader.GetString(3),
                Status = (TaskStatus)reader.GetInt32(4),
                Owner = reader.IsDBNull(5) ? null : reader.GetString(5),
                Result = reader.IsDBNull(6) ? null : reader.GetString(6),
                ClaimVersion = reader.GetInt64(7),
                DependsOn = dependencies.ToArray(),
            };
        }
        return task;
    }

    private static T ExecuteScalar<T>(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var result = command.ExecuteScalar();
        if (result is null || result is DBNull) return default!;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private void RecoverAbandonedClaims(SqliteConnection connection)
    {
        var claims = new List<(int Id, int ProcessId, long StartTicks, long Version)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, ClaimProcessId, ClaimProcessStartTicks, ClaimVersion
                FROM Tasks WHERE Status = $inProgress;
                """;
            command.Parameters.AddWithValue("$inProgress", (int)TaskStatus.InProgress);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(1) && !reader.IsDBNull(2))
                    claims.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        foreach (var claim in claims.Where(c => !IsSameProcessRunning(c.ProcessId, c.StartTicks)))
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Tasks
                SET Status = $pending, ClaimProcessId = NULL, ClaimProcessStartTicks = NULL
                WHERE Id = $id AND Status = $inProgress AND ClaimVersion = $version;
                """;
            command.Parameters.AddWithValue("$pending", (int)TaskStatus.Pending);
            command.Parameters.AddWithValue("$id", claim.Id);
            command.Parameters.AddWithValue("$inProgress", (int)TaskStatus.InProgress);
            command.Parameters.AddWithValue("$version", claim.Version);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static bool IsSameProcessRunning(int processId, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static long CurrentProcessStartTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string GetDefaultDatabasePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("AI_AGENT_TEAM_DB_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = Environment.CurrentDirectory;
        return Path.Combine(appData, "ai-agent-team", "task-board.db");
    }

    public static string GetDatabasePathForGoal(string goal)
    {
        var configuredPath = Environment.GetEnvironmentVariable("AI_AGENT_TEAM_DB_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = Environment.CurrentDirectory;
        var goalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goal))).ToLowerInvariant();
        return Path.Combine(appData, "ai-agent-team", $"{goalHash}-task-board.db");
    }
}
