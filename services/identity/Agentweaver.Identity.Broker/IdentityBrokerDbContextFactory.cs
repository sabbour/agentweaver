using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Lets EF Core design-time tooling (`dotnet ef migrations add`) construct
/// <see cref="IdentityBrokerDbContext"/> without running the full host composition in
/// <c>Program.cs</c> (which requires real signing/external-provider configuration that design
/// time has no reason to need). The connection string here is never used to connect — only to
/// let the Npgsql provider generate migration SQL — so a design-time-only placeholder is
/// intentional and is not a production configuration path.
/// </summary>
public sealed class IdentityBrokerDbContextFactory : IDesignTimeDbContextFactory<IdentityBrokerDbContext>
{
    public IdentityBrokerDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<IdentityBrokerDbContext>();
        builder.UseNpgsql(
            "Host=localhost;Database=agentweaver_identity_broker_designtime;Username=postgres;Password=postgres",
            npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityBrokerDbContext.Schema));
        return new IdentityBrokerDbContext(builder.Options);
    }
}
