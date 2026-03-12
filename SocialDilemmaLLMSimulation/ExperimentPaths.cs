using Microsoft.Data.Sqlite;

namespace SocialDilemmaLLMSimulation;

public static class ExperimentPaths
{
    public static string WorkspaceRoot => Directory.GetCurrentDirectory();

    public static string ResolveFromWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return WorkspaceRoot;

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(WorkspaceRoot, path));
    }

    public static string DatabasePath
        => ResolveDatabasePath();

    public static string DatabaseConnectionString
        => new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();

    public static string EnsureResultsDirectory()
        => EnsureDirectory(Util.DetectEnv("MBASE_RESULTS_DIR", Path.Combine("artifacts", "results")));

    public static string EnsureExportsDirectory()
        => EnsureDirectory(Util.DetectEnv("MBASE_EXPORT_DIR", Path.Combine("artifacts", "exports")));

    public static string ResolveResultsFile(string fileName)
        => Path.Combine(EnsureResultsDirectory(), fileName);

    public static string EnsureDirectory(string path)
    {
        var fullPath = ResolveFromWorkspace(path);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static string ResolveDatabasePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("MBASE_DB_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return ResolveFromWorkspace(configuredPath);

        var projectDatabase = Path.Combine(WorkspaceRoot, "SocialDilemmaLLMSimulation", "ipd_results.db");
        if (File.Exists(projectDatabase))
            return projectDatabase;

        return ResolveFromWorkspace("ipd_results.db");
    }
}
