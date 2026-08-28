using Agentweaver.Api.Memory;
using Agentweaver.Api.Webhooks;
using System.Text.Json;

namespace Agentweaver.Api.Auth;

public enum GitHubCapabilityOperation { RepositoryRead, RepositoryWrite, CopilotInference }
public enum GitHubCapabilityBrokerOutcome { Issued, CapabilityUnavailable }

/// <summary>Safe bounded capability metadata. It deliberately contains no credential material.</summary>
internal sealed record GitHubCapabilityGrant(
    GitHubCapabilityPurpose Purpose,
    GitHubCapabilityOperation Operation,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Internal run-bound broker boundary. It accepts only a purpose and opaque snapshot reference,
/// never a user, project, repository, grant, or ambient scope.
/// </summary>
internal sealed class GitHubCapabilityBroker(
    TwoAppPersistenceStore persistence,
    ITwoAppCredentialVault vault,
    RepoAppInstallationTokenService installationTokens)
{
    internal static readonly TimeSpan MaximumCapabilityLifetime = TimeSpan.FromMinutes(10);

    public Task<FencedGitHubCapabilitySnapshot?> TryFenceAsync(
        GitHubCapabilityPurpose purpose,
        SnapshotRef snapshotRef,
        DateTimeOffset now,
        CancellationToken ct) =>
        persistence.TryFenceLiveSnapshotAsync(purpose, snapshotRef, now, ct);

    /// <summary>
    /// Fences before and after vault/provider work. Credentials stay within this internal
    /// boundary; the returned value only authorizes a bounded broker-owned operation.
    /// </summary>
    internal async Task<(GitHubCapabilityBrokerOutcome Outcome, GitHubCapabilityGrant? Grant)> TryAuthorizeAsync(
        GitHubCapabilityPurpose purpose,
        SnapshotRef snapshotRef,
        GitHubCapabilityOperation operation,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!IsOperationAllowed(purpose, operation))
            return (GitHubCapabilityBrokerOutcome.CapabilityUnavailable, null);

        var fenced = await persistence.TryFenceLiveSnapshotAsync(purpose, snapshotRef, now, ct)
            .ConfigureAwait(false);
        if (fenced is null)
            return (GitHubCapabilityBrokerOutcome.CapabilityUnavailable, null);

        DateTimeOffset? providerExpiresAt;
        if (purpose == GitHubCapabilityPurpose.UnattendedRepository)
        {
            providerExpiresAt = null;
            var outcome = await installationTokens.MintForRepositoryAsync(
                fenced.InstallationId!.Value,
                fenced.RepositoryId!.Value,
                (_, expiresAt) =>
                {
                    providerExpiresAt = expiresAt;
                    return Task.CompletedTask;
                },
                ct).ConfigureAwait(false);
            if (outcome != RepoAppInstallationOutcome.Success || providerExpiresAt is null)
                return (GitHubCapabilityBrokerOutcome.CapabilityUnavailable, null);
        }
        else
        {
            var secret = await vault.ReadCurrentAsync(fenced.CredentialLocator!, ct).ConfigureAwait(false);
            if (!secret.Found || !HasUsableAccessToken(secret.Value, out providerExpiresAt))
                return (GitHubCapabilityBrokerOutcome.CapabilityUnavailable, null);
        }

        // Persistence opens no transaction for this check, so no transaction spans provider I/O.
        if (await persistence.TryFenceLiveSnapshotAsync(purpose, snapshotRef, now, ct).ConfigureAwait(false) is null)
            return (GitHubCapabilityBrokerOutcome.CapabilityUnavailable, null);

        var expiresAt = now.Add(MaximumCapabilityLifetime);
        if (providerExpiresAt is not null && providerExpiresAt < expiresAt)
            expiresAt = providerExpiresAt.Value;
        return expiresAt <= now
            ? (GitHubCapabilityBrokerOutcome.CapabilityUnavailable, null)
            : (GitHubCapabilityBrokerOutcome.Issued, new(fenced.Purpose, operation, expiresAt));
    }

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

    private static bool HasUsableAccessToken(string? value, out DateTimeOffset? expiresAt)
    {
        expiresAt = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (!document.RootElement.TryGetProperty("status", out var status) ||
                !string.Equals(status.GetString(), "signed-in", StringComparison.Ordinal) ||
                !document.RootElement.TryGetProperty("accessToken", out var token) ||
                string.IsNullOrWhiteSpace(token.GetString()))
                return false;
            if (document.RootElement.TryGetProperty("expiresAt", out var expiry) &&
                expiry.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expiry.GetString(), out var parsed))
                expiresAt = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
