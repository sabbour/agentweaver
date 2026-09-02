using Agentweaver.Api.Memory;
using Agentweaver.Api.Webhooks;
using Agentweaver.Domain;
using System.Text.Json;

namespace Agentweaver.Api.Auth;

public enum GitHubCapabilityOperation { RepositoryRead, RepositoryWrite, CopilotInference }
public enum GitHubCapabilityBrokerOutcome { Issued, CapabilityUnavailable }

/// <summary>
/// Internal run-bound broker boundary. It accepts only a purpose and opaque snapshot reference,
/// never a user, project, repository, grant, or ambient scope.
/// </summary>
internal sealed class GitHubCapabilityBroker(
    GitHubConnectionsPersistenceStore persistence,
    IGitHubConnectionsCredentialVault vault,
    RepoAppInstallationTokenService installationTokens)
{
    internal static readonly TimeSpan MaximumCapabilityLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions CredentialJsonOptions = new(JsonSerializerDefaults.Web);

    public Task<FencedGitHubCapabilitySnapshot?> TryFenceAsync(
        GitHubCapabilityPurpose purpose,
        SnapshotRef snapshotRef,
        DateTimeOffset now,
        CancellationToken ct) =>
        persistence.TryFenceLiveSnapshotAsync(purpose, snapshotRef, now, ct);

    /// <summary>
    /// Mints one short-lived installation credential after snapshot fencing. The caller receives
    /// the value only in its callback. The API never uses this value for a GitHub command.
    /// </summary>
    internal async Task<GitHubCapabilityBrokerOutcome> TryUseRepositoryCredentialAsync(
        SnapshotRef snapshotRef,
        DateTimeOffset now,
        Func<string, DateTimeOffset, Task> useCredential,
        CancellationToken ct)
    {
        var fenced = await persistence.TryFenceLiveSnapshotAsync(
            GitHubCapabilityPurpose.UnattendedRepository,
            snapshotRef,
            now,
            ct).ConfigureAwait(false);
        if (fenced is null)
            return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

        string? token = null;
        DateTimeOffset? expiresAt = null;
        var outcome = await installationTokens.MintForRepositoryAsync(
            fenced.InstallationId!.Value,
            fenced.RepositoryId!.Value,
            (value, expires) =>
            {
                token = value;
                expiresAt = expires;
                return Task.CompletedTask;
            },
            ct).ConfigureAwait(false);
        if (outcome != RepoAppInstallationOutcome.Success || string.IsNullOrWhiteSpace(token) ||
            expiresAt is null || expiresAt <= now)
            return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

        if (await persistence.TryFenceLiveSnapshotAsync(
                GitHubCapabilityPurpose.UnattendedRepository,
                snapshotRef,
                now,
                ct).ConfigureAwait(false) is null)
            return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

        await useCredential(token, expiresAt.Value).ConfigureAwait(false);
        return GitHubCapabilityBrokerOutcome.Issued;
    }

    /// <summary>
    /// Reads one short-lived Copilot credential only after fencing the immutable unattended
    /// Copilot snapshot before and after the vault read. The credential is exposed solely to the
    /// supplied in-process callback; callers must not persist or log it.
    /// </summary>
    internal async Task<GitHubCapabilityBrokerOutcome> TryUseCopilotCredentialAsync(
        SnapshotRef snapshotRef,
        DateTimeOffset now,
        Func<string, DateTimeOffset, Task> useCredential,
        CancellationToken ct)
    {
        var fenced = await persistence.TryFenceLiveSnapshotAsync(
            GitHubCapabilityPurpose.UnattendedCopilot,
            snapshotRef,
            now,
            ct).ConfigureAwait(false);
        if (fenced is null)
            return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

        var secret = await vault.ReadCurrentAsync(fenced.CredentialLocator!, ct).ConfigureAwait(false);
        if (!secret.Found || !TryGetUsableAccessToken(secret.Value, now, out var token, out var expiresAt) ||
            expiresAt <= now)
        {
            return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;
        }
        var maximumExpiresAt = now.Add(MaximumCapabilityLifetime);
        if (expiresAt > maximumExpiresAt)
            expiresAt = maximumExpiresAt;

        if (await persistence.TryFenceLiveSnapshotAsync(
                GitHubCapabilityPurpose.UnattendedCopilot,
                snapshotRef,
                now,
                ct).ConfigureAwait(false) is null)
        {
            return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;
        }

        await useCredential(token, expiresAt).ConfigureAwait(false);
        return GitHubCapabilityBrokerOutcome.Issued;
    }

    /// <summary>
    /// Redeems one single-use marketplace capability. The capability is claimed before the vault
    /// read and re-fenced afterwards, preventing replay and binding replacement races.
    /// </summary>
    internal async Task<GitHubCapabilityBrokerOutcome> TryUseMarketplaceCopilotCredentialAsync(
        SnapshotRef capabilityReference,
        string projectId,
        string entraObjectId,
        DateTimeOffset now,
        Func<string, DateTimeOffset, Task> useCredential,
        CancellationToken ct) =>
        await TryUseProjectCopilotCredentialAsync(
            capabilityReference,
            ProjectModelProviderCapabilityPurpose.MarketplaceCatalogClassification,
            projectId,
            entraObjectId,
            now,
            useCredential,
            ct).ConfigureAwait(false);

    /// <summary>
    /// Redeems a single-use non-run capability only for its persisted project operation purpose.
    /// The caller receives a credential solely via the broker-owned callback.
    /// </summary>
    internal async Task<GitHubCapabilityBrokerOutcome> TryUseProjectCopilotCredentialAsync(
        SnapshotRef capabilityReference,
        ProjectModelProviderCapabilityPurpose purpose,
        string projectId,
        string entraObjectId,
        DateTimeOffset now,
        Func<string, DateTimeOffset, Task> useCredential,
        CancellationToken ct)
    {
        var capability = await persistence.TryClaimProjectCopilotCapabilityAsync(
            capabilityReference, purpose, projectId, entraObjectId, now, ct).ConfigureAwait(false);
        if (capability is null)
            return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

        try
        {
            var secret = await vault.ReadCurrentAsync(capability.CredentialLocator!, ct).ConfigureAwait(false);
            if (!secret.Found || !TryGetUsableAccessToken(secret.Value, now, out var token, out var expiresAt) ||
                expiresAt <= now)
                return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

            if (!await persistence.IsClaimedMarketplaceCopilotCapabilityLiveAsync(capability, ct).ConfigureAwait(false))
                return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

            var maximumExpiresAt = now.Add(MaximumCapabilityLifetime);
            if (expiresAt > maximumExpiresAt)
                expiresAt = maximumExpiresAt;
            if (expiresAt > capability.ExpiresAt)
                expiresAt = capability.ExpiresAt;
            if (expiresAt <= now)
                return GitHubCapabilityBrokerOutcome.CapabilityUnavailable;

            await useCredential(token, expiresAt).ConfigureAwait(false);
            return GitHubCapabilityBrokerOutcome.Issued;
        }
        finally
        {
            await persistence.DeleteClaimedMarketplaceCopilotCapabilityAsync(
                capability, CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static bool IsOperationAllowed(
        GitHubCapabilityPurpose purpose,
        GitHubCapabilityOperation operation) =>
        purpose switch
        {
            GitHubCapabilityPurpose.InteractiveRepository or GitHubCapabilityPurpose.UnattendedRepository
                => operation is GitHubCapabilityOperation.RepositoryRead or GitHubCapabilityOperation.RepositoryWrite,
            GitHubCapabilityPurpose.InteractiveCopilot or GitHubCapabilityPurpose.UnattendedCopilot
                => operation == GitHubCapabilityOperation.CopilotInference,
            _ => false,
        };

    private static bool TryGetUsableAccessToken(
        string? value,
        DateTimeOffset now,
        out string accessToken,
        out DateTimeOffset expiresAt)
    {
        accessToken = string.Empty;
        expiresAt = now.Add(MaximumCapabilityLifetime);
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(value, CredentialJsonOptions);
            if (!string.Equals(credential?.Status, "signed-in", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(credential?.AccessToken))
            {
                return false;
            }

            if (credential!.ExpiresAt is { } expiry)
                expiresAt = expiry;

            accessToken = credential.AccessToken!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Credential(string? Status, string? AccessToken, DateTimeOffset? ExpiresAt);
}
