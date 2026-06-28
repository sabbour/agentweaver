using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Builds an <see cref="IServiceScopeFactory"/> whose scopes resolve a <see cref="MemoryDbContext"/>
/// over a given SQLite connection string. Used to exercise the singleton-over-IServiceScopeFactory
/// services (e.g. <c>GitHubOAuthRedirectService</c>) the same way they are wired in production, where
/// each operation opens its own scoped DbContext.
/// </summary>
public static class MemoryDbScopeFactory
{
    public static IServiceScopeFactory ForSqlite(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }
}
