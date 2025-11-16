using Microsoft.Data.Sqlite;

public static class DbInit
{
    private const string ConnectionString = "Data Source=ipd_results.db";

 
    public static void EnsureCreated()
    {
        using var connection = new SqliteConnection(ConnectionString);
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
        ";

        using var command = connection.CreateCommand();
        command.CommandText = cmdText;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops the 'decisions' table if it exists. 
    /// Use for resetting the schema during development.
    /// </summary>
    public static void DropDecisionsTable()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        const string sql = @"DROP TABLE IF EXISTS decisions;";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }



public static void EnsureAltered()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        var cmdText = @"
      
ALTER TABLE decisions ADD COLUMN round INTEGER;
ALTER TABLE decisions ADD COLUMN pair_id TEXT;
        ";

        using var command = connection.CreateCommand();
        command.CommandText = cmdText;
        command.ExecuteNonQuery();
    }
    
}
