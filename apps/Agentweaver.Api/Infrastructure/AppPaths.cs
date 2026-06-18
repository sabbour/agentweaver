namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Resolves the application data directory used for the SQLite database and the
/// git worktrees. The same logic runs locally and in a hosted environment; the
/// location is overridable through configuration so no environment-specific code
/// path is required (Principle VI). The system temp directory is never used.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Resolve();

    private static string Resolve()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = AppContext.BaseDirectory;
        }

        var dataDir = Path.Combine(baseDir, "agentweaver");
        Directory.CreateDirectory(dataDir);
        return dataDir;
    }
}
