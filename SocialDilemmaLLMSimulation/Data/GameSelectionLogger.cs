using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public static class GameSelectionLogger
{
    public static void InsertSelection(
        int runId,
        string uniqueName,
        string model,
        string context,
        string promptVersion,
        string playerRole,
        string selectedGame,
        string resolvedGame,
        int? randomRoll,
        string rawResponse,
        string explanation)
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();

        const string sql = @"
            INSERT INTO game_selection_decisions
                (run_id, unique_name, model, context, prompt_version, player_role,
                 selected_game, resolved_game, random_roll, raw_response, explanation)
            VALUES
                ($run_id, $unique_name, $model, $context, $prompt_version, $player_role,
                 $selected_game, $resolved_game, $random_roll, $raw_response, $explanation);
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$run_id", runId);
        cmd.Parameters.AddWithValue("$unique_name", uniqueName);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$context", context);
        cmd.Parameters.AddWithValue("$prompt_version", promptVersion);
        cmd.Parameters.AddWithValue("$player_role", playerRole);
        cmd.Parameters.AddWithValue("$selected_game", selectedGame);
        cmd.Parameters.AddWithValue("$resolved_game", resolvedGame);
        cmd.Parameters.AddWithValue("$random_roll", (object?)randomRoll ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$raw_response", rawResponse);
        cmd.Parameters.AddWithValue("$explanation", explanation);
        cmd.ExecuteNonQuery();
    }
}
