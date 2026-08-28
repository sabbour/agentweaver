using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Auth;

internal enum GitHubRepositorySelectionOutcome
{
    Issued,
    GitHubBindingUnavailable,
    GitHubCapabilityUnavailable,
}

internal sealed record GitHubRepositorySelectionCandidate(
    long RepositoryId,
    string FullName,
    string OwnerLogin,
    bool IsPrivate,
    string DefaultBranch,
    DateTimeOffset? PushedAt);

internal sealed record GitHubRepositorySelectionIssueResult(
    GitHubRepositorySelectionOutcome Outcome,
    string? Code,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// The pre-project broker boundary for the caller's explicit Repo App authorization.
/// It turns a server-verified repository choice into a short-lived, single-use opaque code.
/// No repository authority or credential material crosses this boundary to a client.
/// </summary>
internal sealed class GitHubRepositorySelectionBroker(
    TwoAppPersistenceStore persistence,
    ITwoAppCredentialVault vault,
    GitHubRepositorySelectionClient repositories)
{
    internal static readonly TimeSpan SelectionCodeLifetime = TimeSpan.FromMinutes(5);

    internal async Task<(GitHubRepositorySelectionOutcome Outcome, IReadOnlyList<GitHubRepositorySelectionCandidate> Candidates)>
        ListAsync(string entraObjectId, CancellationToken ct)
    {
        var result = await WithCredentialAsync(
            entraObjectId,
            token => repositories.ListAsync(token, ct),
            ct).ConfigureAwait(false);
        return result.Candidates is null
            ? (result.Outcome, [])
            : (result.Outcome, result.Candidates);
    }

    internal async Task<GitHubRepositorySelectionIssueResult> IssueAsync(
        string entraObjectId,
        long repositoryId,
        CancellationToken ct)
    {
        if (repositoryId <= 0)
            return new(GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);

        var result = await WithCredentialAsync(
            entraObjectId,
            token => repositories.ListAsync(token, ct),
            ct).ConfigureAwait(false);
        if (result.Candidates is null)
            return new(result.Outcome, null, null);
        if (!result.Candidates.Any(candidate => candidate.RepositoryId == repositoryId))
            return new(GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(SelectionCodeLifetime);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var code = CreateCode();
            var inserted = await persistence.TryAddRepositorySelectionCodeAsync(new GitHubRepositorySelectionCodeRecord
            {
                CodeHash = HashCode(code),
                EntraObjectId = entraObjectId,
                RepositoryId = repositoryId,
                CreatedAt = now,
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
            }, ct).ConfigureAwait(false);
            if (inserted)
                return new(GitHubRepositorySelectionOutcome.Issued, code, expiresAt);
        }

        return new(GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);
    }

    /// <summary>
    /// Available to the next project-creation stack layer only. The code is bound to the caller,
    /// expires strictly, and is consumed by the conditional persistence update before its scope is
    /// returned. Callers must resolve clone metadata server-side from the returned canonical ID.
    /// </summary>
    internal Task<ConsumedGitHubRepositorySelection?> TryConsumeAsync(
        string code,
        string entraObjectId,
        CancellationToken ct) =>
        !IsCodeWellFormed(code)
            ? Task.FromResult<ConsumedGitHubRepositorySelection?>(null)
            : persistence.TryConsumeRepositorySelectionCodeAsync(HashCode(code), entraObjectId, DateTimeOffset.UtcNow, ct);

    private async Task<(GitHubRepositorySelectionOutcome Outcome, IReadOnlyList<GitHubRepositorySelectionCandidate>? Candidates)>
        WithCredentialAsync(
            string entraObjectId,
            Func<string, Task<IReadOnlyList<GitHubRepositorySelectionCandidate>?>> operation,
            CancellationToken ct)
    {
        var credential = await persistence.GetLiveRepoAppCredentialAsync(entraObjectId, ct).ConfigureAwait(false);
        if (credential is null)
            return (GitHubRepositorySelectionOutcome.GitHubBindingUnavailable, null);

        SecretGetResult secret;
        try
        {
            secret = await vault.ReadCurrentAsync(
                TwoAppCredentialLocator.ForRepoAppUser(credential.CredentialReference), ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return (GitHubRepositorySelectionOutcome.GitHubBindingUnavailable, null);
        }

        if (!secret.Found || !TryGetUsableAccessToken(secret.Value, out var accessToken))
            return (GitHubRepositorySelectionOutcome.GitHubBindingUnavailable, null);

        IReadOnlyList<GitHubRepositorySelectionCandidate>? candidates;
        try
        {
            candidates = await operation(accessToken!).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   (ex is HttpRequestException || ex is JsonException || ex is TaskCanceledException))
        {
            return (GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null);
        }

        if (candidates is null ||
            !await persistence.IsLiveRepoAppCredentialAsync(credential, ct).ConfigureAwait(false))
            return (GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null);

        return (GitHubRepositorySelectionOutcome.Issued, candidates);
    }

    private static bool TryGetUsableAccessToken(string? value, out string? accessToken)
    {
        accessToken = null;
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
            accessToken = token.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateCode() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsCodeWellFormed(string? code) =>
        code is { Length: 43 } &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
}
