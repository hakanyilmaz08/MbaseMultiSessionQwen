using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public static class DbInit
{
    public static void EnsureCreated()
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS experiment_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    experiment_type TEXT NOT NULL,
                    run_label TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT,
                    status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
                    error TEXT
                );

                CREATE TABLE IF NOT EXISTS adaptive_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_label TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT,
                    status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
                    error TEXT
                );

                CREATE TABLE IF NOT EXISTS decisions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    experiment_run_id INTEGER,
                    unique_name TEXT NOT NULL,
                    model_profile_key TEXT,
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
                    timestamp TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    FOREIGN KEY (experiment_run_id)
                        REFERENCES experiment_runs(id)
                );

                CREATE TABLE IF NOT EXISTS decision_explanations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    decision_id INTEGER NOT NULL,
                    explanation_type TEXT NOT NULL,
                    round INTEGER,
                    explanation TEXT NOT NULL,
                    timestamp TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    FOREIGN KEY (decision_id)
                        REFERENCES decisions(id)
                        ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS game_selection_decisions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    experiment_run_id INTEGER,
                    run_id INTEGER NOT NULL,
                    unique_name TEXT NOT NULL,
                    model_profile_key TEXT,
                    model TEXT NOT NULL,
                    context TEXT NOT NULL,
                    prompt_version TEXT NOT NULL,
                    player_role TEXT NOT NULL,
                    selected_game TEXT NOT NULL CHECK (selected_game IN ('PD', 'SD')),
                    resolved_game TEXT NOT NULL CHECK (resolved_game IN ('PD', 'SD')),
                    random_roll INTEGER,
                    raw_response TEXT NOT NULL,
                    explanation TEXT NOT NULL,
                    timestamp TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    FOREIGN KEY (experiment_run_id)
                        REFERENCES experiment_runs(id)
                );
                """);

            EnsureColumn(
                connection,
                transaction,
                "decisions",
                "experiment_run_id",
                "INTEGER REFERENCES experiment_runs(id)");
            EnsureColumn(
                connection,
                transaction,
                "decisions",
                "model_profile_key",
                "TEXT");
            EnsureColumn(
                connection,
                transaction,
                "game_selection_decisions",
                "experiment_run_id",
                "INTEGER REFERENCES experiment_runs(id)");
            EnsureColumn(
                connection,
                transaction,
                "game_selection_decisions",
                "model_profile_key",
                "TEXT");

            Execute(connection, transaction, """
                INSERT OR IGNORE INTO experiment_runs
                    (id, experiment_type, run_label, started_at, completed_at, status, error)
                SELECT id, 'adaptive', run_label, started_at, completed_at, status, error
                FROM adaptive_runs;

                CREATE INDEX IF NOT EXISTS idx_experiment_runs_status_completed
                    ON experiment_runs (status, completed_at);

                CREATE INDEX IF NOT EXISTS idx_adaptive_runs_status_completed
                    ON adaptive_runs (status, completed_at);

                CREATE INDEX IF NOT EXISTS idx_decisions_run_id
                    ON decisions (run_id);

                CREATE INDEX IF NOT EXISTS idx_decisions_experiment_run_id
                    ON decisions (experiment_run_id);

                CREATE INDEX IF NOT EXISTS idx_decisions_model_game_ctx
                    ON decisions (model, game, context);

                CREATE INDEX IF NOT EXISTS idx_decisions_model_profile_key
                    ON decisions (model_profile_key);

                CREATE UNIQUE INDEX IF NOT EXISTS ux_decisions_experiment_identity
                    ON decisions (experiment_run_id, unique_name, player_role, round)
                    WHERE experiment_run_id IS NOT NULL;

                CREATE INDEX IF NOT EXISTS idx_decision_explanations_decision_id
                    ON decision_explanations (decision_id);

                CREATE INDEX IF NOT EXISTS idx_game_selection_decisions_run_context
                    ON game_selection_decisions (run_id, context, prompt_version);

                CREATE INDEX IF NOT EXISTS idx_game_selection_experiment_run_id
                    ON game_selection_decisions (experiment_run_id);

                CREATE INDEX IF NOT EXISTS idx_game_selection_model_profile_key
                    ON game_selection_decisions (model_profile_key);

                CREATE UNIQUE INDEX IF NOT EXISTS ux_game_selection_experiment_identity
                    ON game_selection_decisions
                        (experiment_run_id, run_id, unique_name, player_role)
                    WHERE experiment_run_id IS NOT NULL;
                """);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = $"PRAGMA table_info({table});";

        var found = false;
        using (var reader = lookup.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }

        if (found)
            return;

        Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
