using Microsoft.Data.Sqlite;

namespace SocialDilemmaLLMSimulation;

public static class ExperimentPaths
{
    public static string WorkspaceRoot => ResolveWorkspaceRoot();

    public static void UseWorkspaceAsCurrentDirectory()
        => Directory.SetCurrentDirectory(WorkspaceRoot);

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
        => new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();

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
            return PrepareDatabasePath(configuredPath);

        var projectDatabase = Path.Combine("SocialDilemmaLLMSimulation", "ipd_results.db");
        if (File.Exists(Path.Combine(WorkspaceRoot, projectDatabase)))
            return PrepareDatabasePath(projectDatabase);

        return PrepareDatabasePath("ipd_results.db");
    }

    private static string PrepareDatabasePath(string path)
    {
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(WorkspaceRoot, path));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        return Path.IsPathRooted(path) ? fullPath : path;
    }

    private static string ResolveWorkspaceRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("MBASE_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.GetFullPath(configuredRoot);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            string? outermostSolutionRoot = null;
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SocialDilemmaLLMSimulation.sln")))
                    outermostSolutionRoot = directory.FullName;

                directory = directory.Parent;
            }

            if (!string.IsNullOrWhiteSpace(outermostSolutionRoot))
                return outermostSolutionRoot;
        }

        return Directory.GetCurrentDirectory();
    }
}
