using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;


public static class DbInit
{
    public static void EnsureCreated()
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();

        var cmdText = @"
        CREATE TABLE IF NOT EXISTS decisions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,

            unique_name TEXT NOT NULL,

            model TEXT NOT NULL,
            game TEXT NOT NULL CHECK (game IN ('PD', 'SD')),
            context TEXT NOT NULL,

            round INTEGER NOT NULL,                       
            choice INTEGER NOT NULL CHECK (choice IN (0,1)),
            payoff INTEGER NOT NULL,
            run_id INTEGER,

            raw_response TEXT NOT NULL,
            prompt_version TEXT NOT NULL,

            player_role TEXT,
            pair_id TEXT,

            timestamp TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );

        CREATE INDEX IF NOT EXISTS idx_decisions_run_id
            ON decisions (run_id);

        CREATE INDEX IF NOT EXISTS idx_decisions_model_game_ctx
            ON decisions (model, game, context);

        -- Explanations table, FK -> decisions.id
        CREATE TABLE IF NOT EXISTS decision_explanations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            decision_id INTEGER NOT NULL,
            explanation_type TEXT NOT NULL,  -- 'round_10_block', 'post_game', etc.
            round INTEGER,                   -- NULL for post_game explanations
            explanation TEXT NOT NULL,
            timestamp TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),

            FOREIGN KEY (decision_id)
                REFERENCES decisions(id)
                ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_decision_explanations_decision_id
            ON decision_explanations (decision_id);

        CREATE TABLE IF NOT EXISTS game_selection_decisions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,

            run_id INTEGER NOT NULL,
            unique_name TEXT NOT NULL,

            model TEXT NOT NULL,
            context TEXT NOT NULL,
            prompt_version TEXT NOT NULL,
            player_role TEXT NOT NULL,

            selected_game TEXT NOT NULL CHECK (selected_game IN ('PD', 'SD')),
            resolved_game TEXT NOT NULL CHECK (resolved_game IN ('PD', 'SD')),
            random_roll INTEGER,

            raw_response TEXT NOT NULL,
            explanation TEXT NOT NULL,

            timestamp TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );

        CREATE INDEX IF NOT EXISTS idx_game_selection_decisions_run_context
            ON game_selection_decisions (run_id, context, prompt_version);
        ";

        using var command = connection.CreateCommand();
        command.CommandText = cmdText;
        command.ExecuteNonQuery();
    }

    // your DropDecisionsTable / EnsureAltered can stay as-is or be retired
}
