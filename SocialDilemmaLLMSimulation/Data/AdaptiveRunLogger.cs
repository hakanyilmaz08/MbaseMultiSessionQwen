using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;
using System.Globalization;

public static class AdaptiveRunLogger
{
    public static long Start(string runLabel)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO adaptive_runs (run_label, started_at, status)
            VALUES ($run_label, $started_at, 'running');

            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$run_label", runLabel);
        command.Parameters.AddWithValue("$started_at", UtcTimestamp());
        return (long)command.ExecuteScalar()!;
    }

    public static void Complete(long adaptiveRunId)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE adaptive_runs
            SET completed_at = $completed_at,
                status = 'completed',
                error = NULL
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", adaptiveRunId);
        command.Parameters.AddWithValue("$completed_at", UtcTimestamp());
        command.ExecuteNonQuery();
    }

    public static void Fail(long adaptiveRunId, Exception exception)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE adaptive_runs
            SET completed_at = $completed_at,
                status = 'failed',
                error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", adaptiveRunId);
        command.Parameters.AddWithValue("$completed_at", UtcTimestamp());
        command.Parameters.AddWithValue("$error", exception.ToString());
        command.ExecuteNonQuery();
    }

    private static string UtcTimestamp()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
