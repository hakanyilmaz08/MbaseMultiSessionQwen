using Microsoft.Data.Sqlite;
using System;

public static class ExplanationLogger
{
    private const string ConnectionString = "Data Source=ipd_results.db";

    /// <summary>
    /// Round-level explanation (e.g. every 10th round).
    /// Tied to a specific decisions row via FK.
    /// </summary>
    public static void InsertRoundExplanation(
        string model,
        string game,
        string context,
        int round,
        string promptVersion,
        int runId,
        string uniqueName,
        string explanationType,
        string explanationText,
        string? playerRole = null
    )
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // 1) Find corresponding decision row
        long decisionId;

        using (var findCmd = connection.CreateCommand())
        {
            findCmd.CommandText = @"
                SELECT id
                FROM decisions
                WHERE run_id         = $run_id
                  AND model          = $model
                  AND game           = $game
                  AND context        = $context
                  AND round          = $round
                  AND prompt_version = $prompt_version
                  AND unique_name    = $unique_name
                  AND (
                        ($player_role IS NULL AND player_role IS NULL) OR
                        player_role = $player_role
                      )
                ORDER BY id DESC
                LIMIT 1;
            ";

            findCmd.Parameters.AddWithValue("$run_id", runId);
            findCmd.Parameters.AddWithValue("$model", model);
            findCmd.Parameters.AddWithValue("$game", game);
            findCmd.Parameters.AddWithValue("$context", context);
            findCmd.Parameters.AddWithValue("$round", round);
            findCmd.Parameters.AddWithValue("$prompt_version", promptVersion);
            findCmd.Parameters.AddWithValue("$unique_name", uniqueName);
            findCmd.Parameters.AddWithValue("$player_role", (object?)playerRole ?? DBNull.Value);

            var result = findCmd.ExecuteScalar();
            if (result == null || result == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"No decision row found for (run_id={runId}, unique_name={uniqueName}, round={round}, player_role={playerRole}, prompt_version={promptVersion}).");
            }

            decisionId = (long)result;
        }

        // 2) Insert explanation
        const string insertSql = @"
            INSERT INTO decision_explanations
                (decision_id, explanation_type, round, explanation)
            VALUES
                ($decision_id, $explanation_type, $round, $explanation);
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;

        cmd.Parameters.AddWithValue("$decision_id", decisionId);
        cmd.Parameters.AddWithValue("$explanation_type", explanationType);
        cmd.Parameters.AddWithValue("$round", round);
        cmd.Parameters.AddWithValue("$explanation", explanationText);

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Post-game explanation: attaches to the last decision row for that agent/run.
    /// </summary>
    public static void InsertPostGameExplanation(
        string model,
        string game,
        string context,
        string promptVersion,
        int runId,
        string uniqueName,
        string explanationType,
        string explanationText,
        string? playerRole = null
    )
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        long decisionId;

        using (var findCmd = connection.CreateCommand())
        {
            findCmd.CommandText = @"
                SELECT id
                FROM decisions
                WHERE run_id         = $run_id
                  AND model          = $model
                  AND game           = $game
                  AND context        = $context
                  AND prompt_version = $prompt_version
                  AND unique_name    = $unique_name
                  AND (
                        ($player_role IS NULL AND player_role IS NULL) OR
                        player_role = $player_role
                      )
                ORDER BY round DESC, id DESC
                LIMIT 1;
            ";

            findCmd.Parameters.AddWithValue("$run_id", runId);
            findCmd.Parameters.AddWithValue("$model", model);
            findCmd.Parameters.AddWithValue("$game", game);
            findCmd.Parameters.AddWithValue("$context", context);
            findCmd.Parameters.AddWithValue("$prompt_version", promptVersion);
            findCmd.Parameters.AddWithValue("$unique_name", uniqueName);
            findCmd.Parameters.AddWithValue("$player_role", (object?)playerRole ?? DBNull.Value);

            var result = findCmd.ExecuteScalar();
            if (result == null || result == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"No final decision row found for (run_id={runId}, unique_name={uniqueName}, player_role={playerRole}, prompt_version={promptVersion}).");
            }

            decisionId = (long)result;
        }

        const string insertSql = @"
            INSERT INTO decision_explanations
                (decision_id, explanation_type, round, explanation)
            VALUES
                ($decision_id, $explanation_type, NULL, $explanation);
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;

        cmd.Parameters.AddWithValue("$decision_id", decisionId);
        cmd.Parameters.AddWithValue("$explanation_type", explanationType);
        cmd.Parameters.AddWithValue("$explanation", explanationText);

        cmd.ExecuteNonQuery();
    }
}
