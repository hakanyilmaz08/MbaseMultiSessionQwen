using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

internal sealed class TemporaryExperimentEnvironment : IDisposable
{
    private readonly string? _previousDatabasePath;
    private readonly string? _previousExportPath;
    private readonly string? _previousRandomSeed;
    private readonly string _directory;

    public TemporaryExperimentEnvironment()
    {
        _previousDatabasePath = Environment.GetEnvironmentVariable("MBASE_DB_PATH");
        _previousExportPath = Environment.GetEnvironmentVariable("MBASE_EXPORT_DIR");
        _previousRandomSeed = Environment.GetEnvironmentVariable("ADAPTIVE_RANDOM_SEED");
        _directory = Path.Combine(Path.GetTempPath(), $"mbase-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("MBASE_DB_PATH", Path.Combine(_directory, "test.db"));
        Environment.SetEnvironmentVariable("MBASE_EXPORT_DIR", Path.Combine(_directory, "exports"));
        Environment.SetEnvironmentVariable("ADAPTIVE_RANDOM_SEED", "12345");
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();
        return connection;
    }

    public T ExecuteScalar<T>(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MBASE_DB_PATH", _previousDatabasePath);
        Environment.SetEnvironmentVariable("MBASE_EXPORT_DIR", _previousExportPath);
        Environment.SetEnvironmentVariable("ADAPTIVE_RANDOM_SEED", _previousRandomSeed);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
