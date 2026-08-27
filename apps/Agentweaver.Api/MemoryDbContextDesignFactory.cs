using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentweaver.Api;

public sealed class MemoryDbContextDesignFactory : IDesignTimeDbContextFactory<MemoryDbContext>
{
    public MemoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>();
        if (args.Contains("--postgres-migrations", StringComparer.Ordinal))
        {
            options.UseNpgsql(
                "Host=localhost;Database=agentweaver_design;Username=postgres;Password=postgres",
                npg => npg.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres"));
        }
        else
        {
            options.UseSqlite("Data Source=agentweaver-design.db", sqlite => sqlite.MigrationsAssembly("Agentweaver.Api"));
        }

        return new MemoryDbContext(options.Options);
    }
}
