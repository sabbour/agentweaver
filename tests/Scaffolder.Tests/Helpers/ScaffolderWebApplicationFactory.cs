using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Scaffolder.Tests.Helpers;

/// <summary>
/// Configures the API for integration tests: injects a unique temp SQLite
/// database per factory instance and a known test API key so HTTP tests can
/// authenticate without any real secrets.
/// </summary>
public sealed class ScaffolderWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestUser = "test-user";

    private readonly string _dbPath;
    private readonly string _worktreesPath;

    public ScaffolderWebApplicationFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"scaffolder-waf-{Guid.NewGuid():N}.db");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"scaffolder-wt-{Guid.NewGuid():N}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Worktrees:BasePath"] = _worktreesPath,
                ["Auth:ApiKey"] = TestApiKey,
                ["Auth:User"] = TestUser,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); }
            catch { /* best effort */ }
        }

        try { Directory.Delete(_worktreesPath, recursive: true); }
        catch { /* best effort */ }
    }
}
