using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Backlog;

/// <summary>
/// Issues the one-time, caller- and project-bound authority used exclusively for backlog
/// decomposition. It never creates a run or exposes credential material.
/// </summary>
public sealed class BacklogDecomposeCopilotCapabilityIssuer(IServiceScopeFactory scopeFactory)
{
    internal static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(2);

    internal async Task<string?> TryIssueAsync(ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caller.EntraObjectId))
            return null;

        using var scope = scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<GitHubConnectionsPersistenceStore>();
        var now = DateTimeOffset.UtcNow;
        var capability = await persistence.TryIssueProjectCopilotCapabilityAsync(
            GitHubProjectCopilotCapabilityPurpose.BacklogDecomposition,
            projectId.ToString(),
            caller.EntraObjectId,
            now,
            now.Add(CapabilityLifetime),
            ct).ConfigureAwait(false);
        return capability?.Value;
    }
}
