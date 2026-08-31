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

    /// <summary>
    /// Removes a small batch of terminal capabilities on browse requests. A crash between claim and
    /// deletion is harmless: the next browse reclaims the bounded storage without touching live rows.
    /// </summary>
    internal async Task PruneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<GitHubConnectionsPersistenceStore>();
        await persistence.PruneMarketplaceCopilotCapabilitiesAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether a caller retains the active, project-bound Copilot connection required to
    /// serve an LLM-derived cache entry. It never issues a capability or reads a credential.
    /// </summary>
    internal async Task<bool> HasActiveBindingAsync(ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
            return false;

        using var scope = scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<GitHubConnectionsPersistenceStore>();
        return await persistence.HasActiveMarketplaceCopilotBindingAsync(
            projectId.ToString(), caller.EntraObjectId, ct).ConfigureAwait(false);
    }

    internal async Task<string?> TryIssueAsync(
        ProjectId projectId,
        CallerContext caller,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
            return null;

        using var scope = scopeFactory.CreateScope();
        var now = DateTimeOffset.UtcNow;
        var persistence = scope.ServiceProvider.GetRequiredService<GitHubConnectionsPersistenceStore>();
        var capability = await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            projectId.ToString(),
            caller.EntraObjectId,
            now,
            now.Add(CapabilityLifetime),
            ct).ConfigureAwait(false);
        return capability?.Value;
    }
}
