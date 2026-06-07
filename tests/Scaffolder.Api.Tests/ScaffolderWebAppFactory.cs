using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scaffolder.Api.Persistence;

namespace Scaffolder.Api.Tests;

/// <summary>
/// Shared WebApplicationFactory for all API tests.
/// Each factory instance uses a unique per-test SQLite file so tests are isolated.
/// </summary>
public sealed class ScaffolderWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"scaffolder-test-{Guid.NewGuid()}.db");

    private readonly string _runRoot = Path.Combine(
        Path.GetTempPath(), $"scaffolder-runroot-{Guid.NewGuid()}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use "Testing" environment so Program.cs uses EnsureCreated instead of MigrateAsync
        builder.UseEnvironment("Testing");

        // Provide the RunRoot setting (required by ScaffolderOptions)
        builder.UseSetting("Scaffolder:RunRoot", _runRoot);

        // Override the SQLite connection string so each test gets its own database
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");

        builder.ConfigureServices(services =>
        {
            // Remove existing DbContext registrations added by Program.cs
            services.RemoveAll<DbContextOptions<ScaffolderDbContext>>();
            services.RemoveAll<IDbContextFactory<ScaffolderDbContext>>();

            var connectionString = $"Data Source={_dbPath}";

            services.AddDbContext<ScaffolderDbContext>(options =>
                options.UseSqlite(connectionString));

            services.AddDbContextFactory<ScaffolderDbContext>(options =>
                options.UseSqlite(connectionString), ServiceLifetime.Scoped);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }
    }
}
