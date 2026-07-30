namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Resolves the companion SQLite path used by <see cref="Memory.MemoryDbContext"/> and
/// <see cref="SqliteRunEventStream"/>.
/// </summary>
public static class SqliteMemoryDbPathResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        var configuredMemoryPath = configuration["Database:MemoryPath"];
        if (!string.IsNullOrWhiteSpace(configuredMemoryPath))
            return Path.GetFullPath(configuredMemoryPath);

        var configuredMainPath = configuration["Database:Path"];
        if (string.IsNullOrWhiteSpace(configuredMainPath))
            return Path.Combine(AppPaths.DataDirectory, "memory.db");

        var mainDbPath = Path.GetFullPath(configuredMainPath);
        var baseDirectory = Path.GetDirectoryName(mainDbPath) ?? AppPaths.DataDirectory;
        var mainDbFileName = Path.GetFileName(mainDbPath);

        // Preserve the longstanding production/local-dev default companion path when the main
        // operational database uses the default agentweaver.db filename.
        if (string.Equals(mainDbFileName, "agentweaver.db", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(baseDirectory, "memory.db");

        return Path.Combine(
            baseDirectory,
            $"{Path.GetFileNameWithoutExtension(mainDbFileName)}.memory.db");
    }
}
