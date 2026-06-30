using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Metrics;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Builds a <see cref="MetricsService"/> wired for SQLite mode (the default provider). The
/// provider-agnostic <see cref="MetricsService"/> constructor takes an <see cref="IServiceScopeFactory"/>
/// and <see cref="IConfiguration"/> (used only to reach <c>MemoryDbContext</c> in Postgres mode); in
/// SQLite mode those are never exercised, so an empty scope factory and a config without
/// <c>Database:Provider</c> (defaulting to sqlite) are sufficient.
/// </summary>
public static class TestMetrics
{
    public static MetricsService SqliteService(
        SqliteDb db, IProjectStore projectStore, HeartbeatStatusStore heartbeat) =>
        new(db, projectStore, heartbeat,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new SqliteTokenUsageStore(db),
            new ConfigurationBuilder().Build());
}
