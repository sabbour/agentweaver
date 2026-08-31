using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Redeems credentials exclusively through an immutable snapshot belonging to the requested run.
/// The provider has no ambient user, project, or token-store input.
/// </summary>
internal sealed class RunGitHubCapabilityCredentialProvider(IServiceScopeFactory scopeFactory) :
    IGitHubCopilotCapabilityCredentialProvider,
    IGitHubRepositoryCapabilityCredentialProvider
{
    Task<GitHubCapabilitySnapshotCredential?> IGitHubCopilotCapabilityCredentialProvider.GetCredentialAsync(
        string runId,
        CancellationToken ct) =>
        GetCredentialAsync(runId, GitHubCapabilityPurpose.UnattendedCopilot, ct);

    Task<GitHubCapabilitySnapshotCredential?> IGitHubRepositoryCapabilityCredentialProvider.GetCredentialAsync(
        string runId,
        CancellationToken ct) =>
        GetCredentialAsync(runId, GitHubCapabilityPurpose.UnattendedRepository, ct);

    async Task<GitHubCapabilitySnapshotCredential?> IGitHubCopilotCapabilityCredentialProvider.GetMarketplaceCredentialAsync(
        string capabilityReference,
        string projectId,
        string entraObjectId,
        CancellationToken ct) =>
        await GetProjectOperationCredentialAsync(
            capabilityReference,
            projectId,
            entraObjectId,
            GitHubProjectCopilotCapabilityPurpose.MarketplaceCatalogClassification,
            ct).ConfigureAwait(false);

    async Task<GitHubCapabilitySnapshotCredential?> IGitHubCopilotCapabilityCredentialProvider.GetProjectOperationCredentialAsync(
        string capabilityReference,
        string projectId,
        string entraObjectId,
        GitHubProjectCopilotCapabilityPurpose purpose,
        CancellationToken ct) =>
        await GetProjectOperationCredentialAsync(capabilityReference, projectId, entraObjectId, purpose, ct)
            .ConfigureAwait(false);

    private async Task<GitHubCapabilitySnapshotCredential?> GetProjectOperationCredentialAsync(
        string capabilityReference,
        string projectId,
        string entraObjectId,
        GitHubProjectCopilotCapabilityPurpose purpose,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(capabilityReference) ||
            string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(entraObjectId))
            return null;

        GitHubCapabilitySnapshotCredential? credential = null;
        using var scope = scopeFactory.CreateScope();
        var broker = scope.ServiceProvider.GetRequiredService<GitHubCapabilityBroker>();
        var outcome = await broker.TryUseProjectCopilotCredentialAsync(
            new SnapshotRef(capabilityReference),
            purpose,
            projectId,
            entraObjectId,
            DateTimeOffset.UtcNow,
            (token, expiresAt) =>
            {
                credential = new GitHubCapabilitySnapshotCredential(capabilityReference, token, expiresAt);
                return Task.CompletedTask;
            },
            ct).ConfigureAwait(false);
        return outcome == GitHubCapabilityBrokerOutcome.Issued ? credential : null;
    }

    private async Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
        string runId,
        GitHubCapabilityPurpose purpose,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        using var scope = scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<GitHubConnectionsPersistenceStore>();
        var snapshot = (await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.Purpose == purpose);
        if (snapshot is null)
            return null;

        GitHubCapabilitySnapshotCredential? credential = null;
        var broker = scope.ServiceProvider.GetRequiredService<GitHubCapabilityBroker>();
        var outcome = purpose == GitHubCapabilityPurpose.UnattendedCopilot
            ? await broker.TryUseCopilotCredentialAsync(
                    new SnapshotRef(snapshot.SnapshotRef),
                    DateTimeOffset.UtcNow,
                    (token, expiresAt) =>
                    {
                        credential = new GitHubCapabilitySnapshotCredential(
                            snapshot.SnapshotRef, token, expiresAt);
                        return Task.CompletedTask;
                    },
                    ct).ConfigureAwait(false)
            : await broker.TryUseRepositoryCredentialAsync(
                    new SnapshotRef(snapshot.SnapshotRef),
                    DateTimeOffset.UtcNow,
                    (token, expiresAt) =>
                    {
                        credential = new GitHubCapabilitySnapshotCredential(
                            snapshot.SnapshotRef, token, expiresAt);
                        return Task.CompletedTask;
                    },
                    ct).ConfigureAwait(false);

        return outcome == GitHubCapabilityBrokerOutcome.Issued ? credential : null;
    }
}
