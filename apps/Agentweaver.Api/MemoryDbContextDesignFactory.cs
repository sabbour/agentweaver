using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Api;

public sealed class MemoryDbContextDesignFactory : IDesignTimeDbContextFactory<MemoryDbContext>
{
    public MemoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>();
        if (args.Contains("--postgres-migrations", StringComparer.Ordinal))
        {
            options.UseNpgsql(
                ResolvePostgresConnectionString(),
                npg => npg.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres"));
        }
        else
        {
            options.UseSqlite("Data Source=agentweaver-design.db", sqlite => sqlite.MigrationsAssembly("Agentweaver.Api"));
        }

        return new MemoryDbContext(options.Options);
    }

    private static string ResolvePostgresConnectionString()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true);

        if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            configurationBuilder.AddUserSecrets<MemoryDbContextDesignFactory>(optional: true);

        var configuration = configurationBuilder
            .AddEnvironmentVariables()
            .Build();

        return configuration.GetConnectionString("Postgres")
            ?? configuration.GetConnectionString("MemoryDb")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres (or MemoryDb / Database:ConnectionString) is required when using --postgres-migrations.");
    }
}
