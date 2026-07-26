using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public static class AdaptiveRunLogger
{
    public static long Start(string runLabel)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var experimentRunId = ExperimentRunLogger.Insert(
            connection,
            transaction,
            "adaptive",
            runLabel);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO adaptive_runs (id, run_label, started_at, status)
            SELECT id, run_label, started_at, status
            FROM experiment_runs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", experimentRunId);
        command.ExecuteNonQuery();

        transaction.Commit();
        return experimentRunId;
    }

    public static void Complete(long adaptiveRunId)
        => SetStatus(adaptiveRunId, "completed", null);

    public static void Fail(long adaptiveRunId, Exception exception)
        => SetStatus(adaptiveRunId, "failed", exception.ToString());

    private static void SetStatus(long adaptiveRunId, string status, string? error)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var completedAt = ExperimentRunLogger.UtcTimestamp();

        UpdateStatus(
            connection,
            transaction,
            "experiment_runs",
            adaptiveRunId,
            completedAt,
            status,
            error);
        UpdateStatus(
            connection,
            transaction,
            "adaptive_runs",
            adaptiveRunId,
            completedAt,
            status,
            error);

        transaction.Commit();
    }

    private static void UpdateStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        long id,
        string completedAt,
        string status,
        string? error)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {table}
            SET completed_at = $completed_at,
                status = $status,
                error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$completed_at", completedAt);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"{table} row {id} was not found.");
    }
}
