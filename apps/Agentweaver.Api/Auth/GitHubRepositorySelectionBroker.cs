using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Security;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

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
/// Server-only clone input recovered after an atomically consumed selection code. It must never
/// cross an HTTP or MCP response boundary.
/// </summary>
internal sealed record ResolvedGitHubRepositorySelection(
    string SourceRepository,
    string AccessToken);

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
    private static readonly JsonSerializerOptions CredentialJsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<(GitHubRepositorySelectionOutcome Outcome, IReadOnlyList<GitHubRepositorySelectionCandidate> Candidates)>
        ListAsync(CallerContext caller, CancellationToken ct)
    {
        var result = await GetCandidatesAsync(caller, ct).ConfigureAwait(false);
        return result.Candidates is null
            ? (result.Outcome, [])
            : (result.Outcome, result.Candidates);
    }

    internal Task<(GitHubRepositorySelectionOutcome Outcome, IReadOnlyList<GitHubRepositorySelectionCandidate> Candidates)>
        ListAsync(string entraObjectId, CancellationToken ct) =>
        ListAsync(new CallerContext { User = entraObjectId, EntraObjectId = entraObjectId }, ct);

    internal async Task<GitHubRepositorySelectionIssueResult> IssueAsync(
        CallerContext caller,
        string fullName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return new(GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);

        var result = await GetCandidatesAsync(caller, ct).ConfigureAwait(false);
        if (result.Candidates is null)
            return new(result.Outcome, null, null);
        var repository = result.Candidates.SingleOrDefault(candidate =>
            string.Equals(candidate.FullName, fullName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (repository is null || result.Credential is null)
            return new(GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(SelectionCodeLifetime);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var code = CreateCode();
            var inserted = await persistence.TryAddRepositorySelectionCodeAsync(new GitHubRepositorySelectionCodeRecord
            {
                CodeHash = HashCode(code),
                EntraObjectId = GetCallerSubject(caller),
                RepoAppAuthorizationId = result.Credential.Id,
                RepositoryId = repository.RepositoryId,
                CreatedAt = now,
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
            }, ct).ConfigureAwait(false);
            if (inserted)
                return new(GitHubRepositorySelectionOutcome.Issued, code, expiresAt);
        }

        return new(GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);
    }

    internal Task<GitHubRepositorySelectionIssueResult> IssueAsync(
        string entraObjectId,
        string fullName,
        CancellationToken ct) =>
        IssueAsync(
            new CallerContext { User = entraObjectId, EntraObjectId = entraObjectId },
            fullName,
            ct);

    /// <summary>
    /// The code is bound to the caller, expires strictly, and is consumed by the conditional
    /// persistence update before its server-only scope is returned.
    /// </summary>
    internal Task<ConsumedGitHubRepositorySelection?> TryConsumeAsync(
        string code,
        string entraObjectId,
        CancellationToken ct) =>
        !IsCodeWellFormed(code)
            ? Task.FromResult<ConsumedGitHubRepositorySelection?>(null)
            : persistence.TryConsumeRepositorySelectionCodeAsync(
                HashCode(code),
                entraObjectId,
                DateTimeOffset.UtcNow,
                ct);

    /// <summary>
    /// Consumes the code before recovering the matching repository's canonical clone URL through
    /// the exact live Repo App authorization that issued it. The caller never supplies clone
    /// metadata or a credential.
    /// </summary>
    internal async Task<ResolvedGitHubRepositorySelection?> TryConsumeAndResolveAsync(
        string code,
        CallerContext caller,
        CancellationToken ct)
    {
        var callerSubject = GetCallerSubject(caller);
        var consumed = !IsCodeWellFormed(code)
            ? null
            : await persistence.TryConsumeRepositorySelectionCodeAsync(
                HashCode(code), callerSubject, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (consumed is null)
            return null;

        var credential = await persistence.GetLiveRepoAppCredentialAsync(
            callerSubject, consumed.RepoAppAuthorizationId, ct).ConfigureAwait(false);
        if (credential is null)
            return null;
        SecretGetResult secret;
        try
        {
            secret = await vault.ReadCurrentAsync(
                TwoAppCredentialLocator.ForRepoAppUser(credential.CredentialReference), ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (!secret.Found || !TryGetUsableAccessToken(secret.Value, out var accessToken))
            return null;
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        IReadOnlyList<GitHubRepositorySelectionCandidate>? candidates;
        try
        {
            candidates = await repositories.ListAsync(accessToken!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   (ex is HttpRequestException || ex is JsonException || ex is TaskCanceledException))
        {
            return null;
        }

        var repository = candidates?.SingleOrDefault(candidate => candidate.RepositoryId == consumed.RepositoryId);
        if (repository is null || !await persistence.IsLiveRepoAppCredentialAsync(credential, ct).ConfigureAwait(false))
            return null;

        return new ResolvedGitHubRepositorySelection(
            $"https://github.com/{repository.FullName}",
            accessToken);
    }

    private async Task<(
        GitHubRepositorySelectionOutcome Outcome,
        IReadOnlyList<GitHubRepositorySelectionCandidate>? Candidates,
        RepoAppCredentialReference? Credential)>
        GetCandidatesAsync(CallerContext caller, CancellationToken ct) =>
        await WithCredentialAsync(GetCallerSubject(caller), token => repositories.ListAsync(token, ct), ct)
            .ConfigureAwait(false);

    private async Task<(
        GitHubRepositorySelectionOutcome Outcome,
        IReadOnlyList<GitHubRepositorySelectionCandidate>? Candidates,
        RepoAppCredentialReference? Credential)>
        WithCredentialAsync(
            string entraObjectId,
            Func<string, Task<IReadOnlyList<GitHubRepositorySelectionCandidate>?>> operation,
            CancellationToken ct)
    {
        var credential = await persistence.GetLiveRepoAppCredentialAsync(entraObjectId, ct).ConfigureAwait(false);
        if (credential is null)
            return (GitHubRepositorySelectionOutcome.GitHubBindingUnavailable, null, null);

        SecretGetResult secret;
        try
        {
            secret = await vault.ReadCurrentAsync(
                TwoAppCredentialLocator.ForRepoAppUser(credential.CredentialReference), ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return (GitHubRepositorySelectionOutcome.GitHubBindingUnavailable, null, null);
        }

        if (!secret.Found || !TryGetUsableAccessToken(secret.Value, out var accessToken))
            return (GitHubRepositorySelectionOutcome.GitHubBindingUnavailable, null, null);

        IReadOnlyList<GitHubRepositorySelectionCandidate>? candidates;
        try
        {
            candidates = await operation(accessToken!).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   (ex is HttpRequestException || ex is JsonException || ex is TaskCanceledException))
        {
            return (GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);
        }

        if (candidates is null ||
            !await persistence.IsLiveRepoAppCredentialAsync(credential, ct).ConfigureAwait(false))
            return (GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable, null, null);

        return (GitHubRepositorySelectionOutcome.Issued, candidates, credential);
    }

    private static string GetCallerSubject(CallerContext caller) =>
        caller.EntraObjectId ?? caller.User;

    private static bool TryGetUsableAccessToken(string? value, out string? accessToken)
    {
        accessToken = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(value, CredentialJsonOptions);
            if (!string.Equals(credential?.Status, "signed-in", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(credential?.AccessToken))
                return false;
            accessToken = credential!.AccessToken;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Credential(string? Status, string? AccessToken);

    private static string CreateCode() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsCodeWellFormed(string? code) =>
        code is { Length: 43 } &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
}
