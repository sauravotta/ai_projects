package com.example.fleet;

import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.UUID;

/**
 * Durable task queue and result store. Each operation uses its own SQLite connection so worker
 * threads can safely share the store and SQLite can arbitrate claims across fleet instances.
 */
public final class TaskStore {

    enum FailureOutcome { RETRIED, DEAD_LETTERED, LOST_CLAIM }

    record TaskSnapshot(String status, String result, int attempts) {}

    private final String jdbcUrl;
    private final String instanceId = UUID.randomUUID().toString();
    private final long instanceLeaseMs;

    public TaskStore(Path databasePath, long instanceLeaseMs) {
        if (instanceLeaseMs < 1) {
            throw new IllegalArgumentException("instanceLeaseMs must be positive");
        }
        this.instanceLeaseMs = instanceLeaseMs;
        try {
            Path absolutePath = databasePath.toAbsolutePath();
            Path parent = absolutePath.getParent();
            if (parent != null) {
                Files.createDirectories(parent);
            }
            jdbcUrl = "jdbc:sqlite:" + absolutePath;
            initialize();
        } catch (Exception e) {
            throw new IllegalStateException("Could not initialize fleet database " + databasePath, e);
        }
    }

    public TaskStore(Path databasePath) {
        this(databasePath, 10_000);
    }

    public Task create(String prompt) {
        try (Connection connection = open();
             PreparedStatement insert = connection.prepareStatement(
                     "INSERT INTO tasks(prompt, attempts, status, available_at, created_at, updated_at) " +
                             "VALUES(?, 0, 'pending', ?, ?, ?)")) {
            long now = System.currentTimeMillis();
            insert.setString(1, prompt);
            insert.setLong(2, now);
            insert.setLong(3, now);
            insert.setLong(4, now);
            insert.executeUpdate();
            try (Statement statement = connection.createStatement();
                 ResultSet result = statement.executeQuery("SELECT last_insert_rowid()")) {
                return new Task(result.getInt(1), prompt, 0);
            }
        } catch (SQLException e) {
            throw failure("Could not create task", e);
        }
    }

    /** Inserts an explicitly identified task once, making repeated submissions idempotent. */
    public TaskSnapshot submit(Task task) {
        try (Connection connection = open()) {
            long now = System.currentTimeMillis();
            try (PreparedStatement insert = connection.prepareStatement(
                    "INSERT OR IGNORE INTO tasks(task_id, prompt, attempts, status, available_at, created_at, updated_at) " +
                            "VALUES(?, ?, ?, 'pending', ?, ?, ?)")) {
                insert.setInt(1, task.id());
                insert.setString(2, task.prompt());
                insert.setInt(3, task.attempts());
                insert.setLong(4, now);
                insert.setLong(5, now);
                insert.setLong(6, now);
                insert.executeUpdate();
            }
            TaskSnapshot snapshot = get(connection, task.id());
            try (PreparedStatement query = connection.prepareStatement("SELECT prompt FROM tasks WHERE task_id=?")) {
                query.setInt(1, task.id());
                try (ResultSet result = query.executeQuery()) {
                    if (!result.next() || !task.prompt().equals(result.getString(1))) {
                        throw new IllegalArgumentException("Task id " + task.id() + " already has a different prompt");
                    }
                }
            }
            return snapshot;
        } catch (SQLException e) {
            throw failure("Could not submit task " + task.id(), e);
        }
    }

    Task claim(String worker) {
        try (Connection connection = open(); Statement transaction = connection.createStatement()) {
            transaction.execute("BEGIN IMMEDIATE");
            try {
                Task task = null;
                try (PreparedStatement select = connection.prepareStatement(
                        "SELECT task_id, prompt, attempts FROM tasks " +
                                "WHERE status='pending' AND available_at<=? ORDER BY created_at, task_id LIMIT 1")) {
                    select.setLong(1, System.currentTimeMillis());
                    try (ResultSet result = select.executeQuery()) {
                        if (result.next()) {
                            task = new Task(result.getInt(1), result.getString(2), result.getInt(3));
                        }
                    }
                }
                if (task != null) {
                    try (PreparedStatement update = connection.prepareStatement(
                            "UPDATE tasks SET status='running', owner_id=?, worker=?, updated_at=? " +
                                    "WHERE task_id=? AND status='pending'")) {
                        update.setString(1, instanceId);
                        update.setString(2, worker);
                        update.setLong(3, System.currentTimeMillis());
                        update.setInt(4, task.id());
                        if (update.executeUpdate() != 1) {
                            task = null;
                        }
                    }
                }
                transaction.execute("COMMIT");
                return task;
            } catch (SQLException | RuntimeException e) {
                transaction.execute("ROLLBACK");
                throw e;
            }
        } catch (SQLException e) {
            throw failure("Could not claim a task", e);
        }
    }

    boolean complete(Task task, String result) {
        return updateOwnedTask(
                "UPDATE tasks SET status='completed', result=?, error=NULL, owner_id=NULL, worker=NULL, updated_at=? " +
                        "WHERE task_id=? AND status='running' AND owner_id=? AND attempts=?",
                result, System.currentTimeMillis(), task.id(), instanceId, task.attempts());
    }

    FailureOutcome fail(Task task, String reason, int maxAttempts) {
        long now = System.currentTimeMillis();
        if (task.attempts() + 1 >= maxAttempts) {
            boolean updated = updateOwnedTask(
                    "UPDATE tasks SET status='dead', error=?, owner_id=NULL, worker=NULL, updated_at=? " +
                            "WHERE task_id=? AND status='running' AND owner_id=? AND attempts=?",
                    reason, now, task.id(), instanceId, task.attempts());
            return updated ? FailureOutcome.DEAD_LETTERED : FailureOutcome.LOST_CLAIM;
        }
        long delayMs = 1000L << task.attempts();
        boolean updated = updateOwnedTask(
                "UPDATE tasks SET status='pending', attempts=attempts+1, error=?, owner_id=NULL, worker=NULL, " +
                        "available_at=?, updated_at=? WHERE task_id=? AND status='running' AND owner_id=? AND attempts=?",
                reason, now + delayMs, now, task.id(), instanceId, task.attempts());
        return updated ? FailureOutcome.RETRIED : FailureOutcome.LOST_CLAIM;
    }

    TaskSnapshot snapshot(int taskId) {
        try (Connection connection = open()) {
            return get(connection, taskId);
        } catch (SQLException e) {
            throw failure("Could not read task " + taskId, e);
        }
    }

    long queuedCount() {
        return count("SELECT COUNT(*) FROM tasks WHERE status='pending'");
    }

    long completedCount() {
        return count("SELECT COUNT(*) FROM tasks WHERE status='completed'");
    }

    long deadLetterCount() {
        return count("SELECT COUNT(*) FROM tasks WHERE status='dead'");
    }

    void heartbeat() {
        try (Connection connection = open();
             PreparedStatement update = connection.prepareStatement(
                     "UPDATE fleet_instances SET heartbeat_at=? WHERE instance_id=?")) {
            update.setLong(1, System.currentTimeMillis());
            update.setString(2, instanceId);
            update.executeUpdate();
        } catch (SQLException e) {
            throw failure("Could not update fleet heartbeat", e);
        }
    }

    String instanceId() {
        return instanceId;
    }

    private void initialize() throws SQLException {
        try (Connection connection = open(); Statement statement = connection.createStatement()) {
            statement.execute("PRAGMA journal_mode=WAL");
            statement.execute("PRAGMA synchronous=FULL");
            statement.execute("CREATE TABLE IF NOT EXISTS fleet_instances (" +
                    "instance_id TEXT PRIMARY KEY, heartbeat_at INTEGER NOT NULL)");
            statement.execute("CREATE TABLE IF NOT EXISTS tasks (" +
                    "task_id INTEGER PRIMARY KEY AUTOINCREMENT, prompt TEXT NOT NULL, attempts INTEGER NOT NULL, " +
                    "status TEXT NOT NULL CHECK(status IN ('pending','running','completed','dead')), " +
                    "result TEXT, error TEXT, available_at INTEGER NOT NULL, owner_id TEXT, worker TEXT, " +
                    "created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL)");
            statement.execute("CREATE INDEX IF NOT EXISTS tasks_ready_idx ON tasks(status, available_at, created_at)");
            long now = System.currentTimeMillis();
            statement.execute("BEGIN IMMEDIATE");
            try {
                try (PreparedStatement delete = connection.prepareStatement(
                        "DELETE FROM fleet_instances WHERE heartbeat_at < ?")) {
                    delete.setLong(1, now - instanceLeaseMs);
                    delete.executeUpdate();
                }
                statement.executeUpdate("UPDATE tasks SET status='pending', owner_id=NULL, worker=NULL, " +
                        "available_at=" + now + ", error='recovered after worker lease expired', updated_at=" + now +
                        " WHERE status='running' AND owner_id NOT IN (SELECT instance_id FROM fleet_instances)");
                try (PreparedStatement register = connection.prepareStatement(
                        "INSERT INTO fleet_instances(instance_id, heartbeat_at) VALUES(?, ?)")) {
                    register.setString(1, instanceId);
                    register.setLong(2, now);
                    register.executeUpdate();
                }
                statement.execute("COMMIT");
            } catch (SQLException e) {
                statement.execute("ROLLBACK");
                throw e;
            }
        }
    }

    private boolean updateOwnedTask(String sql, Object... values) {
        try (Connection connection = open(); PreparedStatement update = connection.prepareStatement(sql)) {
            for (int i = 0; i < values.length; i++) {
                update.setObject(i + 1, values[i]);
            }
            return update.executeUpdate() == 1;
        } catch (SQLException e) {
            throw failure("Could not update task state", e);
        }
    }

    private TaskSnapshot get(Connection connection, int taskId) throws SQLException {
        try (PreparedStatement query = connection.prepareStatement(
                "SELECT status, result, attempts FROM tasks WHERE task_id=?")) {
            query.setInt(1, taskId);
            try (ResultSet result = query.executeQuery()) {
                if (!result.next()) {
                    throw new IllegalArgumentException("Unknown task id " + taskId);
                }
                return new TaskSnapshot(result.getString(1), result.getString(2), result.getInt(3));
            }
        }
    }

    private long count(String sql) {
        try (Connection connection = open(); Statement statement = connection.createStatement();
             ResultSet result = statement.executeQuery(sql)) {
            return result.getLong(1);
        } catch (SQLException e) {
            throw failure("Could not read task counts", e);
        }
    }

    private Connection open() throws SQLException {
        Connection connection = DriverManager.getConnection(jdbcUrl);
        try (Statement statement = connection.createStatement()) {
            statement.execute("PRAGMA busy_timeout=5000");
            statement.execute("PRAGMA foreign_keys=ON");
        }
        return connection;
    }

    private static IllegalStateException failure(String message, SQLException cause) {
        return new IllegalStateException(message, cause);
    }
}
