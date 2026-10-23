using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentweaver.Environment;

/// <summary>Constructs the Environment context for EF tooling without requiring host credentials or live services.</summary>
public sealed class EnvironmentDbContextFactory : IDesignTimeDbContextFactory<EnvironmentDbContext>
{
    /// <summary>Creates a PostgreSQL context; the placeholder connection is used only to scaffold provider migrations.</summary>
    public EnvironmentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=agentweaver_environment_design;Username=design_time",
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    EnvironmentDbContext.Schema))
            .Options;
        return new EnvironmentDbContext(options);
    }
}
