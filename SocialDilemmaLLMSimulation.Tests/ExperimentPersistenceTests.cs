using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;
using Xunit;

public sealed class ExperimentPersistenceTests
{
    [Fact]
    public void EnsureCreatedMigratesLegacyRowsWithoutClaimingRunIdentity()
    {
        using var database = new TemporaryExperimentDatabase();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE decisions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    unique_name TEXT NOT NULL,
                    model TEXT NOT NULL,
                    game TEXT NOT NULL,
                    context TEXT NOT NULL,
                    round INTEGER NOT NULL,
                    choice INTEGER NOT NULL,
                    payoff INTEGER NOT NULL,
                    run_id INTEGER,
                    raw_response TEXT NOT NULL,
                    prompt_version TEXT NOT NULL,
                    player_role TEXT,
                    pair_id TEXT,
                    timestamp TEXT
                );

                CREATE TABLE adaptive_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_label TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT,
                    status TEXT NOT NULL,
                    error TEXT
                );

                INSERT INTO decisions
                    (unique_name, model, game, context, round, choice, payoff,
                     run_id, raw_response, prompt_version, player_role)
                VALUES
                    ('legacy', 'model', 'PD', 'Team', 1, 1, 5,
                     1, 'c', 'v4', 'A');

                INSERT INTO adaptive_runs
                    (id, run_label, started_at, completed_at, status)
                VALUES
                    (42, 'legacy-model', '2025-01-01T00:00:00.000Z',
                     '2025-01-01T00:01:00.000Z', 'completed');
                """;
            command.ExecuteNonQuery();
        }

        DbInit.EnsureCreated();

        using var migrated = database.OpenConnection();
        Assert.True(ColumnExists(migrated, "decisions", "experiment_run_id"));
        Assert.True(ColumnExists(migrated, "game_selection_decisions", "experiment_run_id"));
        Assert.Equal(
            "adaptive",
            ExecuteScalar<string>(
                migrated,
                "SELECT experiment_type FROM experiment_runs WHERE id = 42;"));
        Assert.Equal(
            1L,
            ExecuteScalar<long>(
                migrated,
                "SELECT COUNT(*) FROM decisions WHERE unique_name = 'legacy' AND experiment_run_id IS NULL;"));
    }

    [Fact]
    public void ContextRunWriteCommitsAsOneUnitAndRollsBackInvalidBatch()
    {
        using var database = new TemporaryExperimentDatabase();
        DbInit.EnsureCreated();
        var experimentRunId = ExperimentRunLogger.Start("standard-pd", "model-a_vs_model-b");

        var decisions = new[]
        {
            Decision("A", "model-a", 1),
            Decision("B", "model-b", 1)
        };
        var explanations = new[]
        {
            new ContextExplanationWrite("A", 1, "post_game", null, "A explanation"),
            new ContextExplanationWrite("B", 1, "post_game", null, "B explanation")
        };

        ContextRunLogger.InsertContextRun(experimentRunId, decisions, explanations);

        using (var connection = database.OpenConnection())
        {
            Assert.Equal(
                2L,
                ExecuteScalar<long>(
                    connection,
                    $"SELECT COUNT(*) FROM decisions WHERE experiment_run_id = {experimentRunId};"));
            Assert.Equal(
                2L,
                ExecuteScalar<long>(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM decision_explanations e
                    INNER JOIN decisions d ON d.id = e.decision_id
                    WHERE d.experiment_run_id IS NOT NULL;
                    """));
        }

        var invalidExplanations = new[]
        {
            new ContextExplanationWrite("missing", 1, "post_game", null, "invalid")
        };
        Assert.Throws<InvalidOperationException>(
            () => ContextRunLogger.InsertContextRun(
                experimentRunId,
                new[] { Decision("A", "model-a", 2) },
                invalidExplanations));

        using var afterRollback = database.OpenConnection();
        Assert.Equal(
            2L,
            ExecuteScalar<long>(
                afterRollback,
                $"SELECT COUNT(*) FROM decisions WHERE experiment_run_id = {experimentRunId};"));
    }

    [Fact]
    public void BothSelectionRowsRollBackWhenEitherInsertFails()
    {
        using var database = new TemporaryExperimentDatabase();
        DbInit.EnsureCreated();
        var experimentRunId = ExperimentRunLogger.Start("adaptive", "model");
        var duplicateIdentity = new[]
        {
            Selection("model-a", "A"),
            Selection("model-b", "A")
        };

        Assert.Throws<SqliteException>(
            () => GameSelectionLogger.InsertSelections(experimentRunId, duplicateIdentity));

        using var connection = database.OpenConnection();
        Assert.Equal(
            0L,
            ExecuteScalar<long>(
                connection,
                $"SELECT COUNT(*) FROM game_selection_decisions WHERE experiment_run_id = {experimentRunId};"));
    }

    [Fact]
    public void AdaptiveExportUsesRunIdentityInsteadOfNearbyLegacyRows()
    {
        using var database = new TemporaryExperimentDatabase();
        DbInit.EnsureCreated();
        var adaptiveRunId = AdaptiveRunLogger.Start("model-a_vs_model-b");

        ContextRunLogger.InsertContextRun(
            adaptiveRunId,
            new[]
            {
                Decision("A", "model-a", 1),
                Decision("B", "model-b", 1)
            },
            new[]
            {
                new ContextExplanationWrite("A", 1, "post_game", null, "A explanation"),
                new ContextExplanationWrite("B", 1, "post_game", null, "B explanation")
            });
        GameSelectionLogger.InsertSelections(
            adaptiveRunId,
            new[]
            {
                Selection("model-a", "A"),
                Selection("model-b", "B")
            });

        ContextRunLogger.InsertContextRun(
            null,
            new[]
            {
                Decision("A", "unrelated-a", 1) with { UniqueName = "legacy-nearby" },
                Decision("B", "unrelated-b", 1) with { UniqueName = "legacy-nearby" }
            },
            new[]
            {
                new ContextExplanationWrite("A", 1, "post_game", null, "legacy A"),
                new ContextExplanationWrite("B", 1, "post_game", null, "legacy B")
            });
        AdaptiveRunLogger.Complete(adaptiveRunId);

        var exported = AdaptiveRunTextExporter.ExportLastPlayAdaptive();

        Assert.Equal(2, exported.GameSelectionDecisionCount);
        Assert.Equal(2, exported.DecisionExplanationCount);
        Assert.Equal(1, exported.ContextRunSummaryCount);
    }

    private static ContextDecisionWrite Decision(string playerRole, string model, int round)
        => new(
            model,
            "PD",
            "Team",
            round,
            1,
            round * 5,
            "c",
            "v4",
            1,
            "test-context",
            playerRole,
            "test-context");

    private static GameSelectionWrite Selection(string model, string playerRole)
        => new(
            3,
            "selection-run-3",
            model,
            "All Contexts",
            "all",
            playerRole,
            "PD",
            "PD",
            null,
            "GAME: PD",
            "explanation");

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private sealed class TemporaryExperimentDatabase : IDisposable
    {
        private readonly string? _previousDatabasePath;
        private readonly string? _previousExportPath;
        private readonly string _directory;

        public TemporaryExperimentDatabase()
        {
            _previousDatabasePath = Environment.GetEnvironmentVariable("MBASE_DB_PATH");
            _previousExportPath = Environment.GetEnvironmentVariable("MBASE_EXPORT_DIR");
            _directory = Path.Combine(Path.GetTempPath(), $"mbase-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable(
                "MBASE_DB_PATH",
                Path.Combine(_directory, "test.db"));
            Environment.SetEnvironmentVariable(
                "MBASE_EXPORT_DIR",
                Path.Combine(_directory, "exports"));
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MBASE_DB_PATH", _previousDatabasePath);
            Environment.SetEnvironmentVariable("MBASE_EXPORT_DIR", _previousExportPath);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
