using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Skills;

/// <summary>
/// Acquires a broker-only capability for one marketplace classification request. It never returns
/// a GitHub credential, synthesizes a run ID, or accepts a project/caller binding from the client.
/// </summary>
public sealed class MarketplaceCopilotCapabilityIssuer(IServiceScopeFactory scopeFactory)
{
    internal static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(2);

    internal async Task<string?> TryIssueAsync(
        ProjectId projectId,
        CallerContext caller,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
            return null;

        using var scope = scopeFactory.CreateScope();
        var now = DateTimeOffset.UtcNow;
        var persistence = scope.ServiceProvider.GetRequiredService<TwoAppPersistenceStore>();
        var capability = await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            projectId.ToString(),
            caller.EntraObjectId,
            now,
            now.Add(CapabilityLifetime),
            ct).ConfigureAwait(false);
        return capability?.Value;
    }
}
