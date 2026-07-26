using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public sealed record GameSelectionWrite(
    int RunId,
    string UniqueName,
    string Model,
    string Context,
    string PromptVersion,
    string PlayerRole,
    string SelectedGame,
    string ResolvedGame,
    int? RandomRoll,
    string RawResponse,
    string Explanation);

public static class GameSelectionLogger
{
    public static void InsertSelections(
        long? experimentRunId,
        IReadOnlyList<GameSelectionWrite> selections)
    {
        if (selections.Count == 0)
            throw new ArgumentException("At least one game selection is required.", nameof(selections));

        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            foreach (var selection in selections)
                InsertSelection(connection, transaction, experimentRunId, selection);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void InsertSelection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? experimentRunId,
        GameSelectionWrite selection)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO game_selection_decisions
                (experiment_run_id, run_id, unique_name, model, context, prompt_version,
                 player_role, selected_game, resolved_game, random_roll, raw_response, explanation)
            VALUES
                ($experiment_run_id, $run_id, $unique_name, $model, $context, $prompt_version,
                 $player_role, $selected_game, $resolved_game, $random_roll, $raw_response, $explanation);
            """;
        command.Parameters.AddWithValue("$experiment_run_id", (object?)experimentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$run_id", selection.RunId);
        command.Parameters.AddWithValue("$unique_name", selection.UniqueName);
        command.Parameters.AddWithValue("$model", selection.Model);
        command.Parameters.AddWithValue("$context", selection.Context);
        command.Parameters.AddWithValue("$prompt_version", selection.PromptVersion);
        command.Parameters.AddWithValue("$player_role", selection.PlayerRole);
        command.Parameters.AddWithValue("$selected_game", selection.SelectedGame);
        command.Parameters.AddWithValue("$resolved_game", selection.ResolvedGame);
        command.Parameters.AddWithValue("$random_roll", (object?)selection.RandomRoll ?? DBNull.Value);
        command.Parameters.AddWithValue("$raw_response", selection.RawResponse);
        command.Parameters.AddWithValue("$explanation", selection.Explanation);
        command.ExecuteNonQuery();
    }
}
