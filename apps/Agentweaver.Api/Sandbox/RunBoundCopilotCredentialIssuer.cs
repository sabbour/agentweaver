using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// The sole AgentHost-launch adapter for Copilot credentials. It derives both run and purpose
/// server-side, selects an immutable snapshot, and asks the broker to re-fence it before delivery.
/// </summary>
public interface IRunBoundCopilotCredentialIssuer
{
    Task<RunBoundCopilotCredential?> TryIssueAsync(string runId, CancellationToken ct = default);
}

internal sealed class RunBoundCopilotCredentialIssuer(
    IServiceScopeFactory scopeFactory,
    IRunStore runStore) : IRunBoundCopilotCredentialIssuer
{
    public async Task<RunBoundCopilotCredential?> TryIssueAsync(string runId, CancellationToken ct = default)
    {
        if (!RunId.TryParse(runId, out var id))
            return null;

        var run = await runStore.GetAsync(id, ct).ConfigureAwait(false);
        if (run?.ProjectId is null)
            return null;

        var purpose = string.IsNullOrWhiteSpace(run.SubmittingUser)
            ? GitHubCapabilityPurpose.UnattendedCopilot
            : GitHubCapabilityPurpose.InteractiveCopilot;

        using var scope = scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<TwoAppPersistenceStore>();
        var snapshot = (await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false))
            .SingleOrDefault(x => x.Purpose == purpose);
        if (snapshot is null)
            return null;

        return await scope.ServiceProvider.GetRequiredService<GitHubCapabilityBroker>()
            .TryAcquireCopilotCredentialAsync(
                purpose,
                new SnapshotRef(snapshot.SnapshotRef),
                DateTimeOffset.UtcNow,
                ct)
            .ConfigureAwait(false);
    }
}
