using Microsoft.Data.Sqlite;


public static class DecisionLogger
{
    private const string ConnectionString = "Data Source=ipd_results.db";

    public static void InsertDecision(
        
        string model,
        string game,
        string context,
        int round,
        int choice,
        int payoff,
        string rawResponse,
        string promptVersion,
        int runId,
        string unique_name,
        string? playerRole = null,
        string? pairId = null
    )
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        const string sql = @"
        INSERT OR IGNORE INTO decisions
            (run_id, model, game, context, round, choice, payoff,
             raw_response, prompt_version,  player_role, unique_name, timestamp)
        VALUES
            ($run_id, $model, $game, $context, $round, $choice, $payoff,
             $raw_response, $prompt_version, $player_role,$unique_name,$timestamp);
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        cmd.Parameters.AddWithValue("$run_id", runId);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$game", game);
        cmd.Parameters.AddWithValue("$context", context);
        cmd.Parameters.AddWithValue("$round", round);
        cmd.Parameters.AddWithValue("$choice", choice);
        cmd.Parameters.AddWithValue("$payoff", payoff);
        cmd.Parameters.AddWithValue("$raw_response", rawResponse);
        cmd.Parameters.AddWithValue("$prompt_version", promptVersion);
       // cmd.Parameters.AddWithValue("$seed", (object?)seed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$player_role", (object?)playerRole ?? DBNull.Value);
        //cmd.Parameters.AddWithValue("$pair_id", (object?)pairId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$unique_name", unique_name);
        cmd.Parameters.AddWithValue("$timestamp", DateTime.Now.ToString());
        cmd.ExecuteNonQuery();
    }
}

