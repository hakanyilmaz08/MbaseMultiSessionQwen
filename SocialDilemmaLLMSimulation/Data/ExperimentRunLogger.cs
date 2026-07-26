using System.Globalization;
using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public static class ExperimentRunLogger
{
    public static long Start(string experimentType, string runLabel)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var id = Insert(connection, transaction, experimentType, runLabel);
        transaction.Commit();
        return id;
    }

    public static void Complete(long experimentRunId)
        => SetStatus(experimentRunId, "completed", null);

    public static void Fail(long experimentRunId, Exception exception)
        => SetStatus(experimentRunId, "failed", exception.ToString());

    internal static long Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string experimentType,
        string runLabel)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO experiment_runs
                (experiment_type, run_label, started_at, status)
            VALUES
                ($experiment_type, $run_label, $started_at, 'running')
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$experiment_type", experimentType);
        command.Parameters.AddWithValue("$run_label", runLabel);
        command.Parameters.AddWithValue("$started_at", UtcTimestamp());
        return (long)command.ExecuteScalar()!;
    }

    internal static string UtcTimestamp()
        => DateTimeOffset.UtcNow.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);

    private static void SetStatus(long experimentRunId, string status, string? error)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE experiment_runs
            SET completed_at = $completed_at,
                status = $status,
                error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", experimentRunId);
        command.Parameters.AddWithValue("$completed_at", UtcTimestamp());
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Experiment run {experimentRunId} was not found.");
    }
}
