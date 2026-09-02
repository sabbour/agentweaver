using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentweaver.Api.Auth;

public enum AuthorizationClaimResult { Claimed, Invalid, Consumed }
public enum BindingWriteResult { Bound, Unavailable }
public enum InvocationClaimResult { Claimed, Duplicate }
/// <summary>
/// Internal-only authorization state used to transfer the callback cookie to a browser opened
/// from MCP. OAuth state and callback-cookie material never leave the API process.
/// </summary>
internal sealed record McpBrowserHandoffTransaction(
    string State,
    string PkceVerifierReference,
    DateTimeOffset ExpiresAt,
    GitHubAuthorizationStatus Status);

public enum AutomationActivationWriteResult
{
    Activated,
    RepositoryGrantUnavailable,
    RepositoryGrantAmbiguous,
    CopilotBindingUnavailable,
    CopilotBindingAmbiguous,
    Conflict,
}
public sealed record FencedAutomationActivation(
    string ActivationId,
    string ProjectId,
    long InstallationId,
    long RepositoryId,
    string RepositoryGrantDigest,
    AutomationModelProviderSource ModelProviderSource,
    string? CopilotBindingId,
    string? CopilotBindingGrantDigest,
    string? ByokProviderId);
/// <summary>Redacted status summary of a project's most recent automation activation attempt.</summary>
public sealed record AutomationActivationSummary(
    AutomationActivationStatus Status,
    AutomationModelProviderSource ModelProviderSource,
    DateTimeOffset ActivatedAt);
public sealed record SnapshotRef
{
    public SnapshotRef(string value)
    {
        if (value.Length != 43 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Snapshot references must be 32-byte base64url values.", nameof(value));
        Value = value;
    }

    public string Value { get; }

    public static SnapshotRef Create() =>
        new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'));
}

/// <summary>
/// Safe, closed capability context. It intentionally excludes credential references and versions.
/// </summary>
public sealed record FencedGitHubCapabilitySnapshot(
    SnapshotRef SnapshotRef,
    GitHubCapabilityPurpose Purpose,
    GitHubAppKind AppKind,
    string ProjectId,
    long? RepositoryId,
    long? InstallationId,
    string GrantDigest)
{
    // This is an in-process broker-to-vault implementation detail. It is never serializable or
    // visible outside this assembly's credential boundary.
    internal GitHubConnectionsCredentialLocator? CredentialLocator { get; init; }
}

/// <summary>
/// Broker-only metadata recovered from a claimed marketplace capability. It deliberately excludes
/// the caller-visible opaque reference and all credential material.
/// </summary>
internal sealed record FencedMarketplaceCopilotCapability(
    SnapshotRef CapabilityReference,
    ProjectModelProviderCapabilityPurpose Purpose,
    string ProjectId,
    string EntraObjectId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ConsumedAt,
    DateTimeOffset ClaimLeaseExpiresAt,
    string SourceBindingId,
    string CredentialReference,
    string CredentialVersion,
    string GrantDigest)
{
    internal GitHubConnectionsCredentialLocator? CredentialLocator { get; init; }
}

/// <summary>
/// Server-only repository scope recovered by atomically consuming a selection code.
/// It deliberately excludes the code, credential reference, and display metadata.
/// </summary>
internal sealed record ConsumedGitHubRepositorySelection(
    string EntraObjectId,
    long RepositoryId,
    string RepoAppAuthorizationId);

public sealed record CapabilitySnapshotBackfillResult(int Migrated, int Unavailable);
internal sealed record RepoAppAuthorizationTransaction(
    string State,
    string ExternalTransactionId,
    GitHubAppKind AppKind,
    GitHubAuthorizationPurpose Purpose,
    string EntraObjectId,
    long ExpiresAtUnixMilliseconds,
    string ReturnRouteKey,
    string PkceVerifierProtected,
    string CallbackCookieHash,
    string? BrowserSessionId);
internal sealed record CopilotAuthorizationTransaction(string State, string EntraObjectId, string ProjectId, long ExpiresAtUnixMilliseconds, string ReturnRouteKey, string PkceVerifierProtected, string CallbackCookieHash, string? BrowserSessionId);
/// <summary>
/// The project-pinned Repo App installation-binding transaction. It carries no PKCE verifier or
/// return route because the installation flow never exchanges an authorization code — GitHub
/// hands back only `installation_id`/`setup_action`, which the callback resolves against this
/// state row before calling <see cref="Webhooks.RepoAppInstallationLifecycleService.BindAsync"/>.
/// </summary>
internal sealed record RepoAppInstallationAuthorizationTransaction(
    string State,
    string EntraObjectId,
    string ProjectId,
    long ExpiresAtUnixMilliseconds,
    string CallbackCookieHash,
    string? BrowserSessionId);
internal sealed record PlatformDefaultCopilotAuthorizationTransaction(string State, string EntraObjectId, long ExpiresAtUnixMilliseconds, string ReturnRouteKey, string PkceVerifierProtected, string CallbackCookieHash, string? BrowserSessionId);
internal sealed record RepoAppCredentialReference(
    string Id,
    string CredentialReference,
    string CredentialVersion,
    DateTimeOffset CreatedAt);
internal sealed record PlatformDefaultCopilotAuthorizationCompletion(
    bool Completed,
    RepoAppCredentialReference? ReplacedCredential);
internal sealed record CopilotBindingSnapshotSource(
    string Id,
    string CredentialReference,
    string CredentialVersion,
    string GrantDigest);
internal sealed record RepoAppAuthorizationCompletion(
    bool Completed,
    IReadOnlyList<RepoAppCredentialReference> RevokedCredentials);
internal sealed class RepoAppCredentialLease(
    Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) : IAsyncDisposable
{
    private bool _completed;

    public async Task CommitAsync(CancellationToken ct)
    {
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Persistence boundary for the GitHub connections model. It accepts only opaque credential
/// references and exposes guarded state transitions rather than mutable entity access.
/// </summary>
/// <remarks>
/// <paramref name="projectStore"/> is the authoritative source for a project's persisted origin
/// (<see cref="IsIntentionallyBlankOriginProjectAsync"/>). It is intentionally NOT the EF
/// <c>db.Projects</c> set: under the default SQLite provider, <see cref="MemoryDbContext"/> lives
/// in a separate companion database file (<c>memory.db</c>) from the main operational database
/// that <c>SqliteProjectStore</c> writes real project rows to (see
/// <c>SqliteMemoryDbPathResolver</c>), so <c>db.Projects</c> is structurally always empty for real
/// projects under SQLite. <paramref name="projectStore"/> is optional only so unit tests exercising
/// unrelated persistence methods (authorization claims, Copilot bindings, invocation/lifecycle
/// delivery idempotency, etc.) do not need to wire one; any code path that actually needs to
/// classify a project's origin requires it and fails closed if it is absent.
/// </remarks>
public sealed class GitHubConnectionsPersistenceStore(
    MemoryDbContext db, IProjectStore? projectStore = null, ByokProviderConfigurationService? byokSettings = null)
{
    private const int MarketplaceCapabilityCleanupBatchSize = 100;
    internal static readonly TimeSpan MarketplaceCapabilityClaimLease = TimeSpan.FromMinutes(5);
    private static readonly Regex CredentialPattern = new(
        @"(?:gh[ups]_|github_pat_|-----BEGIN [A-Z ]+-----|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task AddAuthorizationAsync(GitHubAuthorizationRecord authorization, CancellationToken ct = default)
    {
        EnsureSafe(authorization);
        EnsureAuthorizationTransaction(authorization);
        db.GitHubAuthorizations.Add(authorization);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static string CreateExternalTransactionId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task AddAppAuthorizationAsync(GitHubAppAuthorizationRecord authorization, CancellationToken ct = default)
    {
        EnsureSafe(authorization);
        db.GitHubAppAuthorizations.Add(authorization);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<AuthorizationClaimResult> ClaimAuthorizationAsync(
        string state,
        string entraObjectId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var changed = await db.GitHubAuthorizations
            .Where(x => x.State == state &&
                        x.EntraObjectId == entraObjectId &&
                        x.Status == GitHubAuthorizationStatus.Pending &&
                        x.ExpiresAtUnixMilliseconds >= now.ToUnixTimeMilliseconds())
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubAuthorizationStatus.Redeeming), ct)
            .ConfigureAwait(false);
        if (changed == 1)
            return AuthorizationClaimResult.Claimed;

        var status = await db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.State == state && x.EntraObjectId == entraObjectId)
            .Select(x => (GitHubAuthorizationStatus?)x.Status)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return status is GitHubAuthorizationStatus.Redeeming or GitHubAuthorizationStatus.Completed or GitHubAuthorizationStatus.Failed
            ? AuthorizationClaimResult.Consumed
            : AuthorizationClaimResult.Invalid;
    }

    internal async Task<bool> TryAddRepositorySelectionCodeAsync(
        GitHubRepositorySelectionCodeRecord selection,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(selection.CodeHash) ||
            string.IsNullOrWhiteSpace(selection.EntraObjectId) ||
            string.IsNullOrWhiteSpace(selection.RepoAppAuthorizationId) ||
            selection.RepositoryId <= 0 ||
            selection.ExpiresAtUnixMilliseconds <= selection.CreatedAt.ToUnixTimeMilliseconds())
            throw new ArgumentException("Repository selection codes must have valid, bounded scope.");

        db.GitHubRepositorySelectionCodes.Add(selection);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Atomically changes an unexpired caller-bound selection code from usable to consumed.
    /// A consumed or expired code is deliberately indistinguishable from an unknown code.
    /// </summary>
    internal async Task<ConsumedGitHubRepositorySelection?> TryConsumeRepositorySelectionCodeAsync(
        string codeHash,
        string entraObjectId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            var changed = await db.GitHubRepositorySelectionCodes
                .Where(x => x.CodeHash == codeHash &&
                            x.EntraObjectId == entraObjectId &&
                            x.ConsumedAtUnixMilliseconds == null &&
                            x.ExpiresAtUnixMilliseconds > now.ToUnixTimeMilliseconds() &&
                            db.GitHubAppAuthorizations.Any(authorization =>
                                authorization.Id == x.RepoAppAuthorizationId &&
                                authorization.EntraObjectId == entraObjectId &&
                                authorization.AppKind == GitHubAppKind.Repo &&
                                authorization.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                                authorization.RevokedAt == null))
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.ConsumedAtUnixMilliseconds,
                    now.ToUnixTimeMilliseconds()), ct)
                .ConfigureAwait(false);
            if (changed != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            var selection = await db.GitHubRepositorySelectionCodes.AsNoTracking()
                .Where(x => x.CodeHash == codeHash &&
                            x.EntraObjectId == entraObjectId)
                .Select(x => new ConsumedGitHubRepositorySelection(
                    x.EntraObjectId,
                    x.RepositoryId,
                    x.RepoAppAuthorizationId))
                .SingleAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return selection;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal Task<RepoAppCredentialReference?> GetLiveRepoAppCredentialAsync(
        string entraObjectId,
        CancellationToken ct = default) =>
        GetActiveRepoAppCredentialAsync(entraObjectId, ct);

    internal Task<RepoAppCredentialReference?> GetLiveRepoAppCredentialAsync(
        string entraObjectId,
        string authorizationId,
        CancellationToken ct = default) =>
        db.GitHubAppAuthorizations.AsNoTracking()
            .Where(x => x.Id == authorizationId &&
                        x.EntraObjectId == entraObjectId &&
                        x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                        x.RevokedAt == null)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.CreatedAt))
            .SingleOrDefaultAsync(ct);

    internal Task<bool> IsLiveRepoAppCredentialAsync(
        RepoAppCredentialReference credential,
        CancellationToken ct = default) =>
        db.GitHubAppAuthorizations.AsNoTracking().AnyAsync(x =>
            x.Id == credential.Id &&
            x.AppKind == GitHubAppKind.Repo &&
            x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
            x.CredentialReference == credential.CredentialReference &&
            x.CredentialVersion == credential.CredentialVersion &&
            x.RevokedAt == null, ct);

    internal Task<RepoAppAuthorizationTransaction?> GetRepoAppAuthorizationTransactionAsync(
        string state,
        CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.State == state)
            .Select(x => new RepoAppAuthorizationTransaction(
                x.State,
                x.ExternalTransactionId,
                x.AppKind,
                x.Purpose,
                x.EntraObjectId,
                x.ExpiresAtUnixMilliseconds,
                x.ReturnRouteKey,
                x.PkceVerifierProtected,
                x.CallbackCookieHash,
                x.BrowserSessionId))
            .SingleOrDefaultAsync(ct);
    internal Task<CopilotAuthorizationTransaction?> GetCopilotAuthorizationTransactionAsync(string state, CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking().Where(x => x.State == state && x.AppKind == GitHubAppKind.Copilot && x.Purpose == GitHubAuthorizationPurpose.InteractiveCopilot && x.ProjectId != null)
            .Select(x => new CopilotAuthorizationTransaction(x.State, x.EntraObjectId, x.ProjectId!, x.ExpiresAtUnixMilliseconds, x.ReturnRouteKey, x.PkceVerifierProtected, x.CallbackCookieHash, x.BrowserSessionId)).SingleOrDefaultAsync(ct);
    internal Task<CopilotAuthorizationTransaction?> GetCopilotAuthorizationTransactionByIdAsync(string id, string subject, CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking().Where(x => x.ExternalTransactionId == id && x.EntraObjectId == subject && x.AppKind == GitHubAppKind.Copilot && x.Purpose == GitHubAuthorizationPurpose.InteractiveCopilot && x.ProjectId != null)
            .Select(x => new CopilotAuthorizationTransaction(x.State, x.EntraObjectId, x.ProjectId!, x.ExpiresAtUnixMilliseconds, x.ReturnRouteKey, x.PkceVerifierProtected, x.CallbackCookieHash, x.BrowserSessionId)).SingleOrDefaultAsync(ct);
    internal Task<RepoAppInstallationAuthorizationTransaction?> GetRepoAppInstallationAuthorizationTransactionAsync(
        string state,
        CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.State == state &&
                        x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.UnattendedRepositoryInstallation &&
                        x.ProjectId != null)
            .Select(x => new RepoAppInstallationAuthorizationTransaction(
                x.State,
                x.EntraObjectId,
                x.ProjectId!,
                x.ExpiresAtUnixMilliseconds,
                x.CallbackCookieHash,
                x.BrowserSessionId))
            .SingleOrDefaultAsync(ct);
    internal Task<PlatformDefaultCopilotAuthorizationTransaction?> GetPlatformDefaultCopilotAuthorizationTransactionAsync(string state, CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.State == state &&
                        x.AppKind == GitHubAppKind.Copilot &&
                        x.Purpose == GitHubAuthorizationPurpose.PlatformDefaultCopilot &&
                        x.ProjectId == null)
            .Select(x => new PlatformDefaultCopilotAuthorizationTransaction(
                x.State,
                x.EntraObjectId,
                x.ExpiresAtUnixMilliseconds,
                x.ReturnRouteKey,
                x.PkceVerifierProtected,
                x.CallbackCookieHash,
                x.BrowserSessionId))
            .SingleOrDefaultAsync(ct);
    internal Task<McpBrowserHandoffTransaction?> GetMcpBrowserHandoffTransactionAsync(
        string transactionId,
        GitHubAppKind appKind,
        GitHubAuthorizationPurpose purpose,
        string entraObjectId,
        string browserSessionId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return
        db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.ExternalTransactionId == transactionId &&
                        x.AppKind == appKind &&
                        x.Purpose == purpose &&
                        x.EntraObjectId == entraObjectId &&
                        x.BrowserSessionId == browserSessionId &&
                        x.Status == GitHubAuthorizationStatus.Pending &&
                        x.ExpiresAtUnixMilliseconds >= now)
            .Select(x => new McpBrowserHandoffTransaction(
                x.State,
                x.PkceVerifierProtected,
                DateTimeOffset.FromUnixTimeMilliseconds(x.ExpiresAtUnixMilliseconds),
                x.Status))
            .SingleOrDefaultAsync(ct);
    }

    internal async Task<bool> BindMcpBrowserSessionAsync(
        string transactionId,
        GitHubAppKind appKind,
        GitHubAuthorizationPurpose purpose,
        string entraObjectId,
        string browserSessionId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changed = await db.GitHubAuthorizations
            .Where(x => x.ExternalTransactionId == transactionId &&
                        x.AppKind == appKind &&
                        x.Purpose == purpose &&
                        x.EntraObjectId == entraObjectId &&
                        x.Status == GitHubAuthorizationStatus.Pending &&
                        x.ExpiresAtUnixMilliseconds >= now &&
                        x.BrowserSessionId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BrowserSessionId, browserSessionId), ct)
            .ConfigureAwait(false);
        if (changed == 1)
            return true;

        return await db.GitHubAuthorizations.AsNoTracking().AnyAsync(x =>
            x.ExternalTransactionId == transactionId &&
            x.AppKind == appKind &&
            x.Purpose == purpose &&
            x.EntraObjectId == entraObjectId &&
            x.Status == GitHubAuthorizationStatus.Pending &&
            x.ExpiresAtUnixMilliseconds >= now &&
            x.BrowserSessionId == browserSessionId, ct).ConfigureAwait(false);
    }

    public async Task<AuthorizationClaimResult> ClaimAuthorizationByTransactionIdAsync(
        string transactionId,
        GitHubAppKind appKind,
        string entraObjectId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var changed = await db.GitHubAuthorizations
            .Where(x => x.ExternalTransactionId == transactionId &&
                        x.AppKind == appKind &&
                        x.EntraObjectId == entraObjectId &&
                        x.Status == GitHubAuthorizationStatus.Pending &&
                        x.ExpiresAtUnixMilliseconds >= now.ToUnixTimeMilliseconds())
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubAuthorizationStatus.Redeeming), ct)
            .ConfigureAwait(false);
        if (changed == 1)
            return AuthorizationClaimResult.Claimed;

        var status = await db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.ExternalTransactionId == transactionId &&
                        x.AppKind == appKind &&
                        x.EntraObjectId == entraObjectId)
            .Select(x => (GitHubAuthorizationStatus?)x.Status)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return status is GitHubAuthorizationStatus.Redeeming or GitHubAuthorizationStatus.Completed or GitHubAuthorizationStatus.Failed
            ? AuthorizationClaimResult.Consumed
            : AuthorizationClaimResult.Invalid;
    }

    public async Task<GitHubAuthorizationTransactionHandle?> GetAuthorizationTransactionAsync(
        string transactionId,
        GitHubAppKind appKind,
        string entraObjectId,
        CancellationToken ct = default)
    {
        var transaction = await db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.ExternalTransactionId == transactionId &&
                        x.AppKind == appKind &&
                        x.EntraObjectId == entraObjectId)
            .Select(x => new GitHubAuthorizationTransactionHandle(
                x.ExternalTransactionId,
                x.AppKind,
                DateTimeOffset.FromUnixTimeMilliseconds(x.ExpiresAtUnixMilliseconds),
                x.Status))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (transaction is null || transaction.ExpiresAt >= DateTimeOffset.UtcNow ||
            transaction.Status is not (GitHubAuthorizationStatus.Pending or GitHubAuthorizationStatus.Redeeming))
            return transaction;

        var completedAt = DateTimeOffset.UtcNow;
        await db.GitHubAuthorizations
            .Where(x => x.ExternalTransactionId == transactionId &&
                        x.AppKind == appKind &&
                        x.EntraObjectId == entraObjectId &&
                        (x.Status == GitHubAuthorizationStatus.Pending || x.Status == GitHubAuthorizationStatus.Redeeming) &&
                        x.ExpiresAtUnixMilliseconds < completedAt.ToUnixTimeMilliseconds())
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubAuthorizationStatus.Expired)
                .SetProperty(x => x.CompletedAt, completedAt), ct)
            .ConfigureAwait(false);
        return transaction with { Status = GitHubAuthorizationStatus.Expired };
    }

    public Task CompleteAuthorizationAsync(
        string state,
        bool succeeded,
        CancellationToken ct = default)
    {
        var status = succeeded ? GitHubAuthorizationStatus.Completed : GitHubAuthorizationStatus.Failed;
        DateTimeOffset? completedAt = DateTimeOffset.UtcNow;
        return db.GitHubAuthorizations
            .Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.CompletedAt, completedAt), ct);
    }

    internal async Task<RepoAppAuthorizationCompletion> CompleteRepoAppAuthorizationAsync(
        string state,
        GitHubAppAuthorizationRecord authorization,
        GitHubAuditRecord audit,
        CancellationToken ct = default)
    {
        EnsureSafe(authorization);
        EnsureSafe(audit);
        var completedAt = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        db.GitHubAppAuthorizations.Add(authorization);
        db.GitHubAuditRecords.Add(audit);
        var revokedCredentials = await GetActiveRepoAppCredentialsAsync(authorization.EntraObjectId, ct)
            .ConfigureAwait(false);
        await db.GitHubAppAuthorizations
            .Where(x => x.EntraObjectId == authorization.EntraObjectId &&
                        x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                        x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, completedAt), ct)
            .ConfigureAwait(false);
        var changed = await db.GitHubAuthorizations
            .Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubAuthorizationStatus.Completed)
                .SetProperty(x => x.CompletedAt, completedAt), ct)
            .ConfigureAwait(false);
        if (changed != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return new(false, []);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(true, revokedCredentials);
    }

    internal async Task CompleteRepoAppAuthorizationFailureAsync(
        string state,
        GitHubAuditRecord audit,
        CancellationToken ct = default)
    {
        EnsureSafe(audit);
        var completedAt = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.GitHubAuditRecords.Add(audit);
        await db.GitHubAuthorizations
            .Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubAuthorizationStatus.Failed)
                .SetProperty(x => x.CompletedAt, completedAt), ct)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    internal async Task<RepoAppCredentialReference?> GetActiveRepoAppCredentialAsync(
        string entraObjectId,
        CancellationToken ct = default)
    {
        var candidates = await db.GitHubAppAuthorizations.AsNoTracking()
            .Where(x => x.EntraObjectId == entraObjectId &&
                        x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                        x.RevokedAt == null)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.CreatedAt))
            .ToListAsync(ct).ConfigureAwait(false);
        return candidates.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
    }

    internal async Task<IReadOnlyList<RepoAppCredentialReference>> GetActiveRepoAppCredentialsAsync(
        string entraObjectId,
        CancellationToken ct = default) =>
        await db.GitHubAppAuthorizations.AsNoTracking()
            .Where(x => x.EntraObjectId == entraObjectId &&
                        x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                        x.RevokedAt == null)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.CreatedAt))
            .ToListAsync(ct).ConfigureAwait(false);

    internal async Task<IReadOnlyList<RepoAppCredentialReference>> RevokeRepoAppCredentialsAsync(
        string entraObjectId,
        GitHubAuditRecord audit,
        CancellationToken ct = default)
    {
        EnsureSafe(audit);
        var revokedAt = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        db.GitHubAuditRecords.Add(audit);
        var revokedCredentials = await GetActiveRepoAppCredentialsAsync(entraObjectId, ct).ConfigureAwait(false);
        var changed = await db.GitHubAppAuthorizations
            .Where(x => x.EntraObjectId == entraObjectId &&
                        x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                        x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, revokedAt), ct)
            .ConfigureAwait(false);
        await db.GitHubAuthorizations
            .Where(x => x.EntraObjectId == entraObjectId &&
                        x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                        (x.Status == GitHubAuthorizationStatus.Pending || x.Status == GitHubAuthorizationStatus.Redeeming))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubAuthorizationStatus.Failed)
                .SetProperty(x => x.CompletedAt, revokedAt), ct)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return changed == 0 ? [] : revokedCredentials;
    }

    internal async Task<bool> RevokeRepoAppCredentialIfCurrentAsync(
        RepoAppCredentialReference credential,
        GitHubAuditRecord audit,
        CancellationToken ct = default)
    {
        EnsureSafe(audit);
        var revokedAt = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        db.GitHubAuditRecords.Add(audit);
        var changed = await db.GitHubAppAuthorizations
            .Where(x => x.Id == credential.Id &&
                        x.CredentialReference == credential.CredentialReference &&
                        x.CredentialVersion == credential.CredentialVersion &&
                        x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, revokedAt), ct)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return false;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    internal async Task<RepoAppCredentialLease?> TryAcquireRepoAppCredentialLeaseAsync(
        RepoAppCredentialReference credential,
        CancellationToken ct = default)
    {
        var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            var changed = await db.GitHubAppAuthorizations
                .Where(x => x.Id == credential.Id &&
                            x.CredentialReference == credential.CredentialReference &&
                            x.CredentialVersion == credential.CredentialVersion &&
                            x.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CredentialReference, x => x.CredentialReference), ct)
                .ConfigureAwait(false);
            if (changed == 1)
                return new RepoAppCredentialLease(transaction);

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<bool> RevokeRepoAppCredentialUnderLeaseAsync(
        RepoAppCredentialReference credential,
        GitHubAuditRecord audit,
        CancellationToken ct = default)
    {
        EnsureSafe(audit);
        db.GitHubAuditRecords.Add(audit);
        var revokedAt = DateTimeOffset.UtcNow;
        var changed = await db.GitHubAppAuthorizations
            .Where(x => x.Id == credential.Id &&
                        x.CredentialReference == credential.CredentialReference &&
                        x.CredentialVersion == credential.CredentialVersion &&
                        x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, revokedAt), ct)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return changed == 1;
    }

    internal void ClearPendingChanges() => db.ChangeTracker.Clear();

    public async Task<BindingWriteResult> ReplaceCopilotBindingAsync(
        ProjectCopilotBindingRecord binding,
        CancellationToken ct = default)
    {
        EnsureSafe(binding);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.ProjectCopilotBindings
            .Where(x => x.ProjectId == binding.ProjectId && x.Status == GitHubBindingStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubBindingStatus.Inactive)
                .SetProperty(x => x.DeactivatedAt, now), ct)
            .ConfigureAwait(false);
        await db.AutomationActivations
            .Where(x => x.ProjectId == binding.ProjectId && x.Status == AutomationActivationStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, AutomationActivationStatus.Invalidated)
                .SetProperty(x => x.InvalidatedAt, now), ct)
            .ConfigureAwait(false);
        db.ChangeTracker.Clear();

        db.ProjectCopilotBindings.Add(binding);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return BindingWriteResult.Bound;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return BindingWriteResult.Unavailable;
        }
    }

    public async Task<BindingWriteResult> ReplacePlatformDefaultCopilotBindingAsync(
        PlatformDefaultCopilotBindingRecord binding,
        CancellationToken ct = default)
    {
        EnsureSafe(binding);
        EnsurePlatformDefaultCopilotBinding(binding);
        var existing = await db.PlatformDefaultCopilotBindings
            .SingleOrDefaultAsync(x => x.Id == PlatformDefaultCopilotBindingRecord.SingletonId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            db.PlatformDefaultCopilotBindings.Add(binding);
        }
        else
        {
            existing.EntraObjectId = binding.EntraObjectId;
            existing.CredentialReference = binding.CredentialReference;
            existing.CredentialVersion = binding.CredentialVersion;
            existing.GrantDigest = binding.GrantDigest;
            existing.Status = binding.Status;
            existing.BoundAt = binding.BoundAt;
            existing.DeactivatedAt = binding.DeactivatedAt;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return BindingWriteResult.Bound;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return BindingWriteResult.Unavailable;
        }
    }

    internal async Task<bool> CompleteCopilotAuthorizationAsync(string state, ProjectCopilotBindingRecord binding, GitHubAuditRecord audit, CancellationToken ct = default)
        {
            EnsureSafe(binding); EnsureSafe(audit);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await db.ProjectCopilotBindings.Where(x => x.ProjectId == binding.ProjectId && x.Status == GitHubBindingStatus.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GitHubBindingStatus.Inactive).SetProperty(x => x.DeactivatedAt, now), ct).ConfigureAwait(false);
            await db.AutomationActivations.Where(x => x.ProjectId == binding.ProjectId && x.Status == AutomationActivationStatus.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AutomationActivationStatus.Invalidated).SetProperty(x => x.InvalidatedAt, now), ct).ConfigureAwait(false);
            db.ChangeTracker.Clear(); db.ProjectCopilotBindings.Add(binding); db.GitHubAuditRecords.Add(audit);
            try
            {
                var claimed = await db.GitHubAuthorizations.Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GitHubAuthorizationStatus.Completed).SetProperty(x => x.CompletedAt, now), ct).ConfigureAwait(false);
                if (claimed != 1) { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); db.ChangeTracker.Clear(); return false; }
                await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true;
            }
            catch (DbUpdateException) { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); db.ChangeTracker.Clear(); return false; }
        }

    internal async Task<PlatformDefaultCopilotAuthorizationCompletion> CompletePlatformDefaultCopilotAuthorizationAsync(
        string state,
        PlatformDefaultCopilotBindingRecord binding,
        GitHubAuditRecord audit,
        CancellationToken ct = default)
    {
        EnsureSafe(binding);
        EnsurePlatformDefaultCopilotBinding(binding);
        EnsureSafe(audit);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var existing = await db.PlatformDefaultCopilotBindings
            .SingleOrDefaultAsync(x => x.Id == PlatformDefaultCopilotBindingRecord.SingletonId, ct)
            .ConfigureAwait(false);
        RepoAppCredentialReference? replacedCredential = null;
        if (existing is null)
        {
            db.PlatformDefaultCopilotBindings.Add(binding);
        }
        else
        {
            if (existing.Status == GitHubBindingStatus.Active &&
                existing.DeactivatedAt is null &&
                (!string.Equals(existing.CredentialReference, binding.CredentialReference, StringComparison.Ordinal) ||
                 !string.Equals(existing.CredentialVersion, binding.CredentialVersion, StringComparison.Ordinal)))
            {
                replacedCredential = new RepoAppCredentialReference(
                    existing.Id,
                    existing.CredentialReference,
                    existing.CredentialVersion,
                    existing.BoundAt);
            }
            existing.EntraObjectId = binding.EntraObjectId;
            existing.CredentialReference = binding.CredentialReference;
            existing.CredentialVersion = binding.CredentialVersion;
            existing.GrantDigest = binding.GrantDigest;
            existing.Status = binding.Status;
            existing.BoundAt = binding.BoundAt;
            existing.DeactivatedAt = binding.DeactivatedAt;
        }

        await db.AutomationActivations
            .Where(x => x.CopilotBindingId == PlatformDefaultCopilotBindingRecord.SingletonId &&
                        x.Status == AutomationActivationStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, AutomationActivationStatus.Invalidated)
                .SetProperty(x => x.InvalidatedAt, now), ct)
            .ConfigureAwait(false);
        db.GitHubAuditRecords.Add(audit);
        try
        {
            var claimed = await db.GitHubAuthorizations.Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GitHubAuthorizationStatus.Completed).SetProperty(x => x.CompletedAt, now), ct).ConfigureAwait(false);
            if (claimed != 1)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return new(false, null);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new(true, replacedCredential);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return new(false, null);
        }
    }

    internal async Task CompleteCopilotAuthorizationFailureAsync(string state, GitHubAuditRecord audit, CancellationToken ct = default)
        {
            EnsureSafe(audit); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var completedAt = DateTimeOffset.UtcNow;
            db.GitHubAuditRecords.Add(audit);
            await db.GitHubAuthorizations.Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GitHubAuthorizationStatus.Failed).SetProperty(x => x.CompletedAt, completedAt), ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false);
        }

    internal async Task<RepoAppCredentialReference?> RevokeCopilotBindingAsync(string projectId, GitHubAuditRecord audit, CancellationToken ct = default)
        {
            EnsureSafe(audit); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var binding = await db.ProjectCopilotBindings.Where(x => x.ProjectId == projectId && x.Status == GitHubBindingStatus.Active)
                .Select(x => new RepoAppCredentialReference(x.Id, x.CredentialReference, x.CredentialVersion, x.BoundAt)).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (binding is null) { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); return null; }
            var now = DateTimeOffset.UtcNow; db.GitHubAuditRecords.Add(audit);
            await db.ProjectCopilotBindings.Where(x => x.Id == binding.Id && x.Status == GitHubBindingStatus.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GitHubBindingStatus.Revoked).SetProperty(x => x.DeactivatedAt, now), ct).ConfigureAwait(false);
            await db.AutomationActivations.Where(x => x.ProjectId == projectId && x.Status == AutomationActivationStatus.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AutomationActivationStatus.Invalidated).SetProperty(x => x.InvalidatedAt, now), ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return binding;
        }

    internal Task<RepoAppCredentialReference?> GetActiveCopilotBindingAsync(
        string projectId,
        CancellationToken ct = default) =>
        db.ProjectCopilotBindings.AsNoTracking()
            .Where(x => x.ProjectId == projectId &&
                        x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.BoundAt))
            .SingleOrDefaultAsync(ct);

    internal async Task<RepoAppCredentialReference?> RevokePlatformDefaultCopilotBindingAsync(
        GitHubAuditRecord audit,
        CancellationToken ct = default)
    {
        EnsureSafe(audit);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        var binding = await db.PlatformDefaultCopilotBindings
            .Where(x => x.Id == PlatformDefaultCopilotBindingRecord.SingletonId &&
                        x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null)
            .Select(x => new RepoAppCredentialReference(x.Id, x.CredentialReference, x.CredentialVersion, x.BoundAt))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (binding is null)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        db.GitHubAuditRecords.Add(audit);
        await db.PlatformDefaultCopilotBindings
            .Where(x => x.Id == binding.Id && x.Status == GitHubBindingStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, GitHubBindingStatus.Revoked)
                .SetProperty(x => x.DeactivatedAt, now), ct)
            .ConfigureAwait(false);
        await db.AutomationActivations
            .Where(x => x.CopilotBindingId == PlatformDefaultCopilotBindingRecord.SingletonId &&
                        x.Status == AutomationActivationStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, AutomationActivationStatus.Invalidated)
                .SetProperty(x => x.InvalidatedAt, now), ct)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return binding;
    }

    /// <summary>
    /// Returns the project's own active GitHub Copilot binding, if any, regardless of which caller
    /// bound it. Used by <see cref="EffectiveModelProviderResolver"/> to decide whether a project's
    /// explicit model-provider override wins over the platform default; project role authorization
    /// for the calling operation is enforced by the endpoint, not here.
    /// </summary>
    internal Task<RepoAppCredentialReference?> GetActiveProjectCopilotBindingAsync(
        string projectId,
        CancellationToken ct = default) =>
        db.ProjectCopilotBindings.AsNoTracking()
            .Where(x => x.ProjectId == projectId &&
                        x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.BoundAt))
            .SingleOrDefaultAsync(ct);

    internal Task<RepoAppCredentialReference?> GetActivePlatformDefaultCopilotBindingAsync(
        CancellationToken ct = default) =>
        db.PlatformDefaultCopilotBindings.AsNoTracking()
            .Where(x => x.Id == PlatformDefaultCopilotBindingRecord.SingletonId &&
                        x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.BoundAt))
            .SingleOrDefaultAsync(ct);

    internal async Task<IReadOnlyList<RepoAppCredentialReference>> ListActiveCopilotBindingsAsync(
        string? excludeBindingId,
        CancellationToken ct = default)
    {
        var projectBindings = await db.ProjectCopilotBindings.AsNoTracking()
            .Where(x => x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null &&
                        x.Id != excludeBindingId)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.BoundAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var platformBindings = await db.PlatformDefaultCopilotBindings.AsNoTracking()
            .Where(x => x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null &&
                        x.Id != excludeBindingId)
            .Select(x => new RepoAppCredentialReference(
                x.Id, x.CredentialReference, x.CredentialVersion, x.BoundAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return projectBindings.Concat(platformBindings).ToArray();
    }

    /// <summary>
    /// Resolves exactly one current, project-bound Repo App grant and Copilot binding, then
    /// atomically records their immutable identity tuple. Callers cannot supply any repository,
    /// installation, provider display, permission, or credential value.
    /// </summary>
    internal async Task<(AutomationActivationWriteResult Result, AutomationActivationRecord? Activation)>
        TryCreateAutomationActivationSnapshotAsync(
            string projectId,
            string? entraObjectId,
            GitHubAuditActorKind actorKind,
            CancellationToken ct = default)
    {
        // When a deployment-wide BYOK provider is active, it is the automation's model-provider
        // source and no GitHub Copilot binding is required at all — mirrors the same "byok"
        // precedence already used by the project's GitHub connection status endpoint. This is an
        // interim, minimal representation (see AutomationModelProviderSource) that may be renamed
        // once the model-provider-resolver-unification work lands.
        var activeByokProviderId = byokSettings is null
            ? null
            : await byokSettings.GetActiveProviderIdAsync(ct).ConfigureAwait(false);

        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            var grants = await db.GitHubRepositoryGrants.AsNoTracking()
                .Where(grant => grant.ProjectId == projectId && grant.RevokedAt == null &&
                    db.GitHubInstallations.Any(installation =>
                        installation.InstallationId == grant.InstallationId &&
                        installation.AppKind == GitHubAppKind.Repo &&
                        installation.ProjectId == projectId &&
                        installation.RevokedAt == null))
                .Select(grant => new { grant.InstallationId, grant.RepositoryId, grant.PermissionDigest })
                .ToListAsync(ct).ConfigureAwait(false);

            List<(string Id, string GrantDigest)> bindings = [];
            if (activeByokProviderId is null)
            {
                var projectBindings = await db.ProjectCopilotBindings.AsNoTracking()
                    .Where(binding => binding.ProjectId == projectId &&
                        binding.Status == GitHubBindingStatus.Active &&
                        binding.DeactivatedAt == null)
                    .Select(binding => new { binding.Id, binding.GrantDigest })
                    .ToListAsync(ct).ConfigureAwait(false);
                var resolvedBindings = projectBindings.Count > 0
                    ? projectBindings
                    : await db.PlatformDefaultCopilotBindings.AsNoTracking()
                        .Where(binding => binding.Id == PlatformDefaultCopilotBindingRecord.SingletonId &&
                            binding.Status == GitHubBindingStatus.Active &&
                            binding.DeactivatedAt == null)
                        .Select(binding => new { binding.Id, binding.GrantDigest })
                        .ToListAsync(ct).ConfigureAwait(false);
                bindings = resolvedBindings.Select(b => (b.Id, b.GrantDigest)).ToList();
            }

            var result = grants.Count switch
            {
                0 => AutomationActivationWriteResult.RepositoryGrantUnavailable,
                > 1 => AutomationActivationWriteResult.RepositoryGrantAmbiguous,
                _ when activeByokProviderId is not null => AutomationActivationWriteResult.Activated,
                _ when bindings.Count == 0 => AutomationActivationWriteResult.CopilotBindingUnavailable,
                _ when bindings.Count > 1 => AutomationActivationWriteResult.CopilotBindingAmbiguous,
                _ => AutomationActivationWriteResult.Activated,
            };
            if (result != AutomationActivationWriteResult.Activated)
            {
                db.GitHubAuditRecords.Add(CreateActivationAudit(
                    projectId, entraObjectId, actorKind, GitHubAuditOutcome.Denied,
                    result is AutomationActivationWriteResult.RepositoryGrantAmbiguous or
                        AutomationActivationWriteResult.CopilotBindingAmbiguous
                        ? GitHubAuditReasonCode.ActivationPrerequisiteAmbiguous
                        : GitHubAuditReasonCode.BindingUnavailable,
                    null));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return (result, null);
            }

            var grant = grants[0];
            var activation = new AutomationActivationRecord
            {
                Id = SnapshotRef.Create().Value,
                ProjectId = projectId,
                InstallationId = grant.InstallationId,
                RepositoryId = grant.RepositoryId,
                RepositoryGrantDigest = grant.PermissionDigest,
                AutomationKey = "internal-activation-snapshot",
                Status = AutomationActivationStatus.Active,
                ActivatedAt = DateTimeOffset.UtcNow,
            };
            if (activeByokProviderId is not null)
            {
                activation.ModelProviderSource = AutomationModelProviderSource.Byok;
                activation.ByokProviderId = activeByokProviderId;
            }
            else
            {
                var binding = bindings[0];
                activation.ModelProviderSource = AutomationModelProviderSource.GitHubCopilot;
                activation.CopilotBindingId = binding.Id;
                activation.CopilotBindingGrantDigest = binding.GrantDigest;
            }
            EnsureAutomationActivationSnapshot(activation);
            db.AutomationActivations.Add(activation);
            db.GitHubAuditRecords.Add(CreateActivationAudit(
                projectId, entraObjectId, actorKind, GitHubAuditOutcome.Succeeded,
                GitHubAuditReasonCode.None, grant.PermissionDigest, activation.Id));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (AutomationActivationWriteResult.Activated, activation);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            await AppendAuditAsync(CreateActivationAudit(
                projectId, entraObjectId, actorKind, GitHubAuditOutcome.Denied,
                GitHubAuditReasonCode.ActivationConflict, null), ct).ConfigureAwait(false);
            return (AutomationActivationWriteResult.Conflict, null);
        }
    }

    /// <summary>
    /// Fences a previously inserted activation against the exact live grant and binding tuple.
    /// A current replacement is never substituted for the captured identity.
    /// </summary>
    internal async Task<FencedAutomationActivation?> TryFenceAutomationActivationAsync(
        string activationId,
        CancellationToken ct = default)
    {
        var activation = await db.AutomationActivations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == activationId &&
                x.Status == AutomationActivationStatus.Active, ct).ConfigureAwait(false);
        if (activation is null || string.IsNullOrWhiteSpace(activation.RepositoryGrantDigest))
            return null;

        var repositoryGrantIsLive = await db.GitHubRepositoryGrants.AsNoTracking().AnyAsync(grant =>
            grant.InstallationId == activation.InstallationId &&
            grant.RepositoryId == activation.RepositoryId &&
            grant.ProjectId == activation.ProjectId &&
            grant.PermissionDigest == activation.RepositoryGrantDigest &&
            grant.RevokedAt == null &&
            db.GitHubInstallations.Any(installation =>
                installation.InstallationId == activation.InstallationId &&
                installation.AppKind == GitHubAppKind.Repo &&
                installation.ProjectId == activation.ProjectId &&
                installation.RevokedAt == null), ct).ConfigureAwait(false);
        if (!repositoryGrantIsLive)
            return null;

        if (activation.ModelProviderSource == AutomationModelProviderSource.Byok)
        {
            if (string.IsNullOrWhiteSpace(activation.ByokProviderId))
                return null;
            // BYOK's live check is exact-id equality against the current deployment-wide active
            // provider (there is no reversible digest to compare, unlike a Copilot binding grant).
            var currentActiveByokProviderId = byokSettings is null
                ? null
                : await byokSettings.GetActiveProviderIdAsync(ct).ConfigureAwait(false);
            if (!string.Equals(currentActiveByokProviderId, activation.ByokProviderId, StringComparison.Ordinal))
                return null;

            return new(
                activation.Id, activation.ProjectId, activation.InstallationId, activation.RepositoryId,
                activation.RepositoryGrantDigest, AutomationModelProviderSource.Byok,
                CopilotBindingId: null, CopilotBindingGrantDigest: null, ByokProviderId: activation.ByokProviderId);
        }

        if (string.IsNullOrWhiteSpace(activation.CopilotBindingId) ||
            string.IsNullOrWhiteSpace(activation.CopilotBindingGrantDigest))
            return null;

        var copilotBindingIsLive = await IsLiveCopilotBindingAsync(
                activation.ProjectId,
                activation.CopilotBindingId,
                activation.CopilotBindingGrantDigest,
                ct).ConfigureAwait(false);

        return !copilotBindingIsLive ? null : new(
            activation.Id, activation.ProjectId, activation.InstallationId, activation.RepositoryId,
            activation.RepositoryGrantDigest, AutomationModelProviderSource.GitHubCopilot,
            activation.CopilotBindingId, activation.CopilotBindingGrantDigest, ByokProviderId: null);
    }

    /// <summary>
    /// Deactivates the project's sole active automation activation, if any, marking it
    /// <see cref="AutomationActivationStatus.Inactive"/> so no further schedule/event trigger fires
    /// until a project Owner activates it again. Distinct from
    /// <see cref="AutomationActivationStatus.Invalidated"/>, which is reserved for the automatic
    /// invalidation that already happens when the underlying grant/binding is replaced or revoked.
    /// </summary>
    internal async Task<bool> TryDeactivateAutomationActivationAsync(
        string projectId,
        string? entraObjectId,
        GitHubAuditActorKind actorKind,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await db.AutomationActivations
            .Where(x => x.ProjectId == projectId && x.Status == AutomationActivationStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, AutomationActivationStatus.Inactive)
                .SetProperty(x => x.InvalidatedAt, now), ct)
            .ConfigureAwait(false);
        if (updated == 0)
            return false;

        await AppendAuditAsync(CreateActivationAudit(
            projectId, entraObjectId, actorKind, GitHubAuditOutcome.Succeeded,
            GitHubAuditReasonCode.None, null, action: GitHubAuditAction.AutomationDeactivated), ct)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Redacted, read-only status for a project's automation activation: never returns repository,
    /// installation, or credential identity, only the fields an Owner-facing settings UI needs to
    /// show whether automation is on and which kind of model-provider authority backs it.
    /// </summary>
    internal async Task<AutomationActivationSummary?> GetAutomationActivationSummaryAsync(
        string projectId,
        CancellationToken ct = default)
    {
        // Ordered client-side (a project accumulates only a handful of activation rows over its
        // lifetime): SQLite cannot translate ORDER BY over a DateTimeOffset column server-side.
        var rows = await db.AutomationActivations.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Select(x => new AutomationActivationSummary(x.Status, x.ModelProviderSource, x.ActivatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.OrderByDescending(x => x.ActivatedAt).FirstOrDefault();
    }


    public async Task<InvocationClaimResult> ClaimInvocationAsync(
        AutomationInvocationRecord invocation,
        CancellationToken ct = default)
    {
        EnsureSafe(invocation);
        db.AutomationInvocations.Add(invocation);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return InvocationClaimResult.Claimed;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return InvocationClaimResult.Duplicate;
        }
    }

    /// <summary>
    /// Atomically claims one GitHub lifecycle delivery through its provider-enforced unique key.
    /// Callers can perform lifecycle state updates in their surrounding database transaction.
    /// </summary>
    public async Task<InvocationClaimResult> ClaimLifecycleDeliveryAsync(
        GitHubLifecycleDeliveryRecord delivery,
        CancellationToken ct = default)
    {
        EnsureSafe(delivery);
        if (string.IsNullOrWhiteSpace(delivery.DeliveryId))
            throw new ArgumentException("Lifecycle delivery claims require an X-GitHub-Delivery value.", nameof(delivery));

        db.GitHubLifecycleDeliveries.Add(delivery);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return InvocationClaimResult.Claimed;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return InvocationClaimResult.Duplicate;
        }
    }

    public async Task<bool> AddRunIdentitySnapshotAsync(
        RunGitHubIdentitySnapshotRecord snapshot,
        CancellationToken ct = default)
    {
        EnsureSafe(snapshot);
        db.RunGitHubIdentitySnapshots.Add(snapshot);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<bool> HasPinnedSnapshotVersionAsync(
        string runId,
        string credentialVersion,
        CancellationToken ct = default) =>
        await db.RunGitHubIdentitySnapshots.AsNoTracking()
            .AnyAsync(x => x.RunId == runId && x.CredentialVersion == credentialVersion, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Inserts one immutable v2 capability snapshot. A duplicated run-purpose pair is a closed
    /// launch failure; callers must never update or replace a prior selection.
    /// </summary>
    public async Task<bool> TryInsertCapabilitySnapshotAsync(
        RunGitHubCapabilitySnapshotRecord snapshot,
        CancellationToken ct = default)
    {
        EnsureSafe(snapshot);
        EnsureCapabilitySnapshot(snapshot);
        db.RunGitHubCapabilitySnapshots.Add(snapshot);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Fences one explicit snapshot against its matching live source. No current/default identity
    /// is queried, and a mismatch is deliberately indistinguishable from an absent snapshot.
    /// </summary>
    public async Task<FencedGitHubCapabilitySnapshot?> TryFenceLiveSnapshotAsync(
        GitHubCapabilityPurpose purpose,
        SnapshotRef snapshotRef,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var snapshot = await db.RunGitHubCapabilitySnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotRef == snapshotRef.Value && x.Purpose == purpose, ct)
            .ConfigureAwait(false);
        if (snapshot is null || (snapshot.SnapshotExpiresAt is not null && snapshot.SnapshotExpiresAt <= now))
            return null;

        var isLive = snapshot.SourceKind switch
        {
            GitHubCapabilitySnapshotSourceKind.UserAuthorization => await db.GitHubAppAuthorizations.AsNoTracking()
                .AnyAsync(x => x.Id == snapshot.SourceAuthorizationId &&
                               x.AppKind == GitHubAppKind.Repo &&
                               x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                               x.EntraObjectId == snapshot.EntraObjectId &&
                               x.CredentialReference == snapshot.CredentialReference &&
                               x.CredentialVersion == snapshot.CredentialVersion &&
                               x.GrantDigest == snapshot.GrantDigest &&
                               x.RevokedAt == null, ct).ConfigureAwait(false),
            GitHubCapabilitySnapshotSourceKind.RepositoryGrant => await db.GitHubRepositoryGrants.AsNoTracking()
                .AnyAsync(x => x.InstallationId == snapshot.InstallationId &&
                               x.RepositoryId == snapshot.RepositoryId &&
                               x.ProjectId == snapshot.ProjectId &&
                               x.PermissionDigest == snapshot.GrantDigest &&
                               x.RevokedAt == null &&
                               db.GitHubInstallations.Any(installation =>
                                   installation.InstallationId == snapshot.InstallationId &&
                                   installation.AppKind == GitHubAppKind.Repo &&
                                   installation.ProjectId == snapshot.ProjectId &&
                                   installation.RevokedAt == null), ct).ConfigureAwait(false),
            GitHubCapabilitySnapshotSourceKind.CopilotBinding => string.Equals(
                snapshot.SourceBindingId,
                PlatformDefaultCopilotBindingRecord.SingletonId,
                StringComparison.Ordinal)
                ? await db.PlatformDefaultCopilotBindings.AsNoTracking()
                    .AnyAsync(x => x.Id == snapshot.SourceBindingId &&
                                   x.CredentialReference == snapshot.CredentialReference &&
                                   x.CredentialVersion == snapshot.CredentialVersion &&
                                   x.GrantDigest == snapshot.GrantDigest &&
                                   x.Status == GitHubBindingStatus.Active &&
                                   x.DeactivatedAt == null, ct).ConfigureAwait(false)
                : await db.ProjectCopilotBindings.AsNoTracking()
                    .AnyAsync(x => x.Id == snapshot.SourceBindingId &&
                                   x.ProjectId == snapshot.ProjectId &&
                                   x.CredentialReference == snapshot.CredentialReference &&
                                   x.CredentialVersion == snapshot.CredentialVersion &&
                                   x.GrantDigest == snapshot.GrantDigest &&
                                   x.Status == GitHubBindingStatus.Active &&
                                   x.DeactivatedAt == null, ct).ConfigureAwait(false),
            _ => false,
        };
        return !isLive
            ? null
            : new(snapshotRef, snapshot.Purpose, snapshot.AppKind, snapshot.ProjectId ?? string.Empty,
                snapshot.RepositoryId, snapshot.InstallationId, snapshot.GrantDigest)
            {
                CredentialLocator = snapshot.SourceKind switch
                {
                    GitHubCapabilitySnapshotSourceKind.UserAuthorization =>
                        GitHubConnectionsCredentialLocator.ForRepoAppUser(snapshot.CredentialReference!),
                    GitHubCapabilitySnapshotSourceKind.CopilotBinding =>
                        GitHubConnectionsCredentialLocator.ForCopilotBinding(snapshot.CredentialReference!),
                    _ => null,
                },
            };
    }

    /// <summary>
    /// Issues a new capability only for the active Copilot binding owned by the current human
    /// subject. This is intentionally a project operation, not a synthetic run snapshot.
    /// </summary>
    internal async Task<SnapshotRef?> TryIssueMarketplaceCopilotCapabilityAsync(
        string projectId,
        string entraObjectId,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken ct = default) =>
        await TryIssueProjectCopilotCapabilityAsync(
            ProjectModelProviderCapabilityPurpose.MarketplaceCatalogClassification,
            projectId,
            entraObjectId,
            now,
            expiresAt,
            ct).ConfigureAwait(false);

    /// <summary>
    /// Issues one short-lived, caller-bound capability for the supplied non-run operation against
    /// the project's EFFECTIVE model provider — its own active GitHub Copilot binding when present
    /// (an explicit override, owned by any project member, not only the caller), otherwise the
    /// platform-default GitHub Copilot binding. This matches
    /// <see cref="EffectiveModelProviderResolver"/>'s precedence and
    /// <see cref="CaptureRootCapabilitySnapshotsAsync"/>'s run-snapshot precedence. The capability's
    /// purpose and calling caller are persisted and must match when the broker redeems it; the
    /// caller identity is bound for replay protection only, not to restrict which binding is used.
    /// </summary>
    internal async Task<SnapshotRef?> TryIssueProjectCopilotCapabilityAsync(
        ProjectModelProviderCapabilityPurpose purpose,
        string projectId,
        string entraObjectId,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(purpose) ||
            string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(entraObjectId) ||
            expiresAt <= now)
            return null;

        var binding = await GetActiveCopilotBindingOrPlatformDefaultAsync(projectId, ct).ConfigureAwait(false);
        if (binding is null)
            return null;

        var capability = SnapshotRef.Create();
        var record = new ProjectModelProviderCapabilityRecord
        {
            CapabilityRef = capability.Value,
            Purpose = (int)purpose,
            ProjectId = projectId,
            EntraObjectId = entraObjectId,
            SourceBindingId = binding.Id,
            CredentialReference = binding.CredentialReference,
            CredentialVersion = binding.CredentialVersion,
            GrantDigest = binding.GrantDigest,
            IssuedAt = now,
            ExpiresAt = expiresAt,
        };
        EnsureSafe(record);
        db.MarketplaceCopilotCapabilities.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return capability;
    }

    /// <summary>
    /// Whether the project currently has a redeemable effective Copilot provider — its own active
    /// binding (owned by any project member) or, absent that, the platform-default binding. Used to
    /// gate serving a cached LLM-derived catalog entry without minting a fresh capability.
    /// </summary>
    internal async Task<bool> HasActiveMarketplaceCopilotBindingAsync(
        string projectId,
        string entraObjectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(entraObjectId))
            return false;

        return await GetActiveCopilotBindingOrPlatformDefaultAsync(projectId, ct).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Atomically consumes an unexpired caller- and project-bound marketplace capability. The
    /// broker receives a bounded lease and re-fences its exact Copilot binding after the vault read
    /// before exposing a credential.
    /// </summary>
    internal async Task<FencedMarketplaceCopilotCapability?> TryClaimMarketplaceCopilotCapabilityAsync(
        SnapshotRef capabilityReference,
        string projectId,
        string entraObjectId,
        DateTimeOffset now,
        CancellationToken ct = default) =>
        await TryClaimProjectCopilotCapabilityAsync(
            capabilityReference,
            ProjectModelProviderCapabilityPurpose.MarketplaceCatalogClassification,
            projectId,
            entraObjectId,
            now,
            ct).ConfigureAwait(false);

    /// <summary>
    /// Atomically consumes one unexpired capability only when its operation purpose, caller, and
    /// project all match the authority issued by the server.
    /// </summary>
    internal async Task<FencedMarketplaceCopilotCapability?> TryClaimProjectCopilotCapabilityAsync(
        SnapshotRef capabilityReference,
        ProjectModelProviderCapabilityPurpose purpose,
        string projectId,
        string entraObjectId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(purpose) ||
            string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(entraObjectId))
            return null;

        // ExecuteUpdate cannot translate DateTimeOffset updates for SQLite. Parameterized SQL keeps
        // the atomic compare-and-set predicate identical across SQLite and PostgreSQL.
        var claimLeaseExpiresAt = now.Add(MarketplaceCapabilityClaimLease);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE marketplace_copilot_capabilities
             SET consumed_at = {now},
                 claim_lease_expires_at = {claimLeaseExpiresAt}
             WHERE capability_ref = {capabilityReference.Value}
               AND purpose = {(int)purpose}
               AND project_id = {projectId}
               AND entra_object_id = {entraObjectId}
               AND consumed_at IS NULL
               AND expires_at > {now}
             """,
            ct).ConfigureAwait(false);
        if (changed != 1)
            return null;

        var capability = await db.MarketplaceCopilotCapabilities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CapabilityRef == capabilityReference.Value &&
                                       x.Purpose == (int)purpose &&
                                       x.ProjectId == projectId &&
                                       x.EntraObjectId == entraObjectId &&
                                       x.ConsumedAt == now &&
                                       x.ClaimLeaseExpiresAt == claimLeaseExpiresAt, ct)
            .ConfigureAwait(false);
        if (capability is null)
            return null;

        return new(
            capabilityReference,
            purpose,
            projectId,
            entraObjectId,
            capability.ExpiresAt,
            capability.ConsumedAt!.Value,
            capability.ClaimLeaseExpiresAt!.Value,
            capability.SourceBindingId,
            capability.CredentialReference,
            capability.CredentialVersion,
            capability.GrantDigest)
        {
            CredentialLocator = GitHubConnectionsCredentialLocator.ForCopilotProject(capability.CredentialReference),
        };
    }

    /// <summary>
    /// Deletes a bounded set of expired marketplace capabilities. Claimed records remain protected
    /// through their lease, then an abandoned claim is reclaimed without allowing it to be replayed.
    /// </summary>
    internal async Task<int> PruneMarketplaceCopilotCapabilitiesAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // DateTimeOffset comparisons are not translatable by the SQLite provider. The parameterized
        // statement below is portable to SQLite and PostgreSQL and limits every maintenance pass.
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM marketplace_copilot_capabilities
             WHERE capability_ref IN (
                 SELECT capability_ref
                 FROM marketplace_copilot_capabilities
                 WHERE expires_at <= {now}
                   AND (
                       consumed_at IS NULL
                       OR claim_lease_expires_at IS NULL
                       OR claim_lease_expires_at <= {now}
                   )
                 ORDER BY expires_at, capability_ref
                 LIMIT {MarketplaceCapabilityCleanupBatchSize}
             )
             """,
            ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a capability once its exact claim has reached a terminal broker outcome. The
    /// compare-and-delete predicate preserves caller/project and lease fencing.
    /// </summary>
    internal Task<int> DeleteClaimedMarketplaceCopilotCapabilityAsync(
        FencedMarketplaceCopilotCapability capability,
        CancellationToken ct = default) =>
        db.MarketplaceCopilotCapabilities
            .Where(x => x.CapabilityRef == capability.CapabilityReference.Value &&
                        x.Purpose == (int)capability.Purpose &&
                        x.ProjectId == capability.ProjectId &&
                        x.EntraObjectId == capability.EntraObjectId &&
                        x.ConsumedAt == capability.ConsumedAt &&
                        x.ClaimLeaseExpiresAt == capability.ClaimLeaseExpiresAt)
            .ExecuteDeleteAsync(ct);

    internal async Task<bool> IsClaimedMarketplaceCopilotCapabilityLiveAsync(
        FencedMarketplaceCopilotCapability capability,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (capability.ExpiresAt <= now ||
            capability.ClaimLeaseExpiresAt <= now ||
            !await db.MarketplaceCopilotCapabilities.AsNoTracking().AnyAsync(record =>
                record.CapabilityRef == capability.CapabilityReference.Value &&
                record.Purpose == (int)capability.Purpose &&
                record.ProjectId == capability.ProjectId &&
                record.EntraObjectId == capability.EntraObjectId &&
                record.ConsumedAt == capability.ConsumedAt &&
                record.ClaimLeaseExpiresAt == capability.ClaimLeaseExpiresAt, ct).ConfigureAwait(false))
            return false;

        // Re-confirms the exact credential-bearing binding this capability was issued from is
        // still active — by SourceBindingId + GrantDigest, not by the caller's own Entra subject.
        // The caller may be redeeming the project's effective (project-override-or-platform-
        // default) provider, which can legitimately be owned by a different project member.
        return await IsLiveCopilotBindingAsync(
            capability.ProjectId, capability.SourceBindingId, capability.GrantDigest, ct).ConfigureAwait(false);
    }


    internal Task<List<RunGitHubCapabilitySnapshotRecord>> GetCapabilitySnapshotsAsync(
        string runId,
        CancellationToken ct = default) =>
        db.RunGitHubCapabilitySnapshots.AsNoTracking()
            .Where(snapshot => snapshot.RunId == runId)
            .OrderBy(snapshot => snapshot.Purpose)
            .ToListAsync(ct);

    internal async Task<bool> TryCapturePlatformDefaultUnattendedCopilotSnapshotAsync(
        string runId,
        CancellationToken ct = default)
    {
        var binding = await db.PlatformDefaultCopilotBindings.AsNoTracking()
            .Where(x => x.Id == PlatformDefaultCopilotBindingRecord.SingletonId &&
                        x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null)
            .Select(x => new CopilotBindingSnapshotSource(
                x.Id,
                x.CredentialReference,
                x.CredentialVersion,
                x.GrantDigest))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (binding is null)
            return false;

        return await TryInsertCapabilitySnapshotAsync(new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value,
            RunId = runId,
            Purpose = GitHubCapabilityPurpose.UnattendedCopilot,
            AppKind = GitHubAppKind.Copilot,
            SourceKind = GitHubCapabilitySnapshotSourceKind.CopilotBinding,
            ProjectId = null,
            SourceBindingId = binding.Id,
            CredentialReference = binding.CredentialReference,
            CredentialVersion = binding.CredentialVersion,
            GrantDigest = binding.GrantDigest,
            CapturedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically creates fresh opaque references for a child or retry from the exact immutable
    /// parent snapshot rows. Existing target snapshots are never replaced. An empty parent set is
    /// only accepted when the project's persisted origin is explicitly
    /// <see cref="ProjectOriginKind.Blank"/>; otherwise a run that should have inherited real
    /// capability protection is denied rather than silently launched with none. A missing or
    /// unparseable <paramref name="projectId"/> can never prove a blank origin — it is denied
    /// fail-closed rather than treated as an automatic pass. This is the persistence half of the
    /// fix for the proven defect where a null <c>Run.ProjectId</c> let an empty-source child/retry
    /// launch with zero GitHub capability snapshots.
    /// </summary>
    internal async Task<bool> TryInheritCapabilitySnapshotsAsync(
        string sourceRunId,
        string targetRunId,
        string? projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return false;

        var source = await GetCapabilitySnapshotsAsync(sourceRunId, ct).ConfigureAwait(false);
        if (source.Count == 0)
        {
            return await IsIntentionallyBlankOriginProjectAsync(projectId, ct).ConfigureAwait(false);
        }

        var target = await GetCapabilitySnapshotsAsync(targetRunId, ct).ConfigureAwait(false);
        if (target.Count != 0)
            return target.Count == source.Count &&
                target.Select(snapshot => snapshot.Purpose).SequenceEqual(source.Select(snapshot => snapshot.Purpose));

        var inherited = source.Select(snapshot => new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value,
            RunId = targetRunId,
            Purpose = snapshot.Purpose,
            AppKind = snapshot.AppKind,
            SourceKind = snapshot.SourceKind,
            ProjectId = snapshot.ProjectId,
            EntraObjectId = snapshot.EntraObjectId,
            SourceAuthorizationId = snapshot.SourceAuthorizationId,
            SourceBindingId = snapshot.SourceBindingId,
            InstallationId = snapshot.InstallationId,
            RepositoryId = snapshot.RepositoryId,
            CredentialReference = snapshot.CredentialReference,
            CredentialVersion = snapshot.CredentialVersion,
            GrantDigest = snapshot.GrantDigest,
            CapturedAt = snapshot.CapturedAt,
            SnapshotExpiresAt = snapshot.SnapshotExpiresAt,
        }).ToList();

        foreach (var snapshot in inherited)
            EnsureCapabilitySnapshot(snapshot);

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            db.RunGitHubCapabilitySnapshots.AddRange(inherited);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Performs an idempotent, fail-closed migration of finite v1 snapshots. It only accepts an
    /// exact current source tuple; ambiguous or stale data is unavailable rather than repaired
    /// from a replacement authorization or binding.
    /// </summary>
    public async Task<CapabilitySnapshotBackfillResult> BackfillCapabilitySnapshotsAsync(
        string? runId = null,
        CancellationToken ct = default)
    {
        var migrated = 0;
        var unavailable = 0;
        var legacySnapshots = await db.RunGitHubIdentitySnapshots.AsNoTracking()
            .Where(snapshot => runId == null || snapshot.RunId == runId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var legacy in legacySnapshots)
        {
            var purpose = (GitHubCapabilityPurpose)legacy.Purpose;
            RunGitHubCapabilitySnapshotRecord? snapshot = purpose switch
            {
                GitHubCapabilityPurpose.InteractiveRepository or GitHubCapabilityPurpose.InteractiveCopilot
                    when legacy.AppKind == GitHubAppKind.Repo && legacy.EntraObjectId is not null =>
                    await CreateUserAuthorizationSnapshotAsync(legacy, purpose, ct).ConfigureAwait(false),
                GitHubCapabilityPurpose.UnattendedRepository when legacy.AppKind == GitHubAppKind.Repo =>
                    await CreateRepositoryGrantSnapshotAsync(legacy, ct).ConfigureAwait(false),
                GitHubCapabilityPurpose.UnattendedCopilot when legacy.AppKind == GitHubAppKind.Copilot =>
                    await CreateCopilotBindingSnapshotAsync(legacy, ct).ConfigureAwait(false),
                _ => null,
            };
            if (snapshot is null || !await TryInsertCapabilitySnapshotAsync(snapshot, ct).ConfigureAwait(false))
            {
                unavailable++;
                continue;
            }
            migrated++;
        }
        return new(migrated, unavailable);
    }

    /// <summary>
    /// True when <paramref name="projectId"/>'s persisted origin is explicitly
    /// <see cref="ProjectOriginKind.Blank"/>. This is the sole authoritative signal for a project
    /// that legitimately requires zero capability snapshots. History records (installations,
    /// grants, bindings) are NOT used here: a <see cref="ProjectOriginKind.FromGitHub"/> project
    /// that has never completed onboarding, or whose history rows were all removed/never written,
    /// must still fail closed rather than be classified as blank. A project that cannot be found,
    /// or whose id is not a well-formed <see cref="ProjectId"/>, is treated as not blank (fail
    /// closed), since origin cannot be proven. Reads through <see cref="IProjectStore"/> (not the EF
    /// <c>db.Projects</c> set) so the check is correct for both the SQLite and Postgres providers;
    /// see the remarks on <see cref="GitHubConnectionsPersistenceStore"/> for why the EF set cannot be used here.
    /// </summary>
    internal async Task<bool> IsIntentionallyBlankOriginProjectAsync(
        string projectId,
        CancellationToken ct = default)
    {
        if (projectStore is null)
            throw new InvalidOperationException(
                "GitHubConnectionsPersistenceStore requires an IProjectStore to classify project origin; none was supplied.");
        if (!ProjectId.TryParse(projectId, out var id))
            return false;

        var project = await projectStore.GetAsync(id, ct).ConfigureAwait(false);
        return project is not null && project.Origin.Kind == ProjectOriginKind.Blank;
    }

    /// <summary>
    /// Trusted production root-capture seam. Selects and insert-only creates every currently live
    /// v2 capability snapshot directly from authoritative sources for a brand-new root run; it
    /// never reads the finite v1 legacy table. A project whose persisted origin is explicitly
    /// blank captures zero snapshots by design. A <see cref="ProjectOriginKind.FromGitHub"/>
    /// project that currently resolves neither the unattended-repository nor unattended-Copilot
    /// purpose is reported unavailable so the caller fails the launch closed instead of silently
    /// proceeding with no capability protection. Interactive-purpose snapshots are intentionally
    /// not captured here: no credential consumer ever redeems a run-bound Interactive snapshot
    /// (only <see cref="GitHubCapabilityPurpose.UnattendedRepository"/> and
    /// <see cref="GitHubCapabilityPurpose.UnattendedCopilot"/> are read back for run credentials),
    /// so capturing them would be dead write-only bookkeeping.
    /// </summary>
    public async Task<CapabilitySnapshotBackfillResult> CaptureRootCapabilitySnapshotsAsync(
        string runId,
        string projectId,
        CancellationToken ct = default)
    {
        var resolved = new List<RunGitHubCapabilitySnapshotRecord>();
        var unattendedRepository = await TryResolveUnattendedRepositorySnapshotAsync(runId, projectId, ct)
            .ConfigureAwait(false);
        if (unattendedRepository is not null)
            resolved.Add(unattendedRepository);
        var unattendedCopilot = await TryResolveUnattendedCopilotSnapshotAsync(runId, projectId, ct)
            .ConfigureAwait(false);
        if (unattendedCopilot is not null)
            resolved.Add(unattendedCopilot);

        if (resolved.Count == 0)
        {
            return await IsIntentionallyBlankOriginProjectAsync(projectId, ct).ConfigureAwait(false)
                ? new CapabilitySnapshotBackfillResult(0, 0)
                : new CapabilitySnapshotBackfillResult(0, 1);
        }

        // All-or-nothing: a partial commit would leave GetCapabilitySnapshotsAsync(runId) non-empty
        // after a failed attempt, which would make a later resume/retry of this exact run skip
        // capture entirely and silently fence only the partial set. Roll back completely instead,
        // so a subsequent attempt re-resolves the full live-source set from scratch.
        foreach (var snapshot in resolved)
        {
            EnsureSafe(snapshot);
            EnsureCapabilitySnapshot(snapshot);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            db.RunGitHubCapabilitySnapshots.AddRange(resolved);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new CapabilitySnapshotBackfillResult(resolved.Count, 0);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return new CapabilitySnapshotBackfillResult(0, 1);
        }
    }

    private async Task<RunGitHubCapabilitySnapshotRecord?> TryResolveUnattendedRepositorySnapshotAsync(
        string runId,
        string projectId,
        CancellationToken ct)
    {
        var grants = await db.GitHubRepositoryGrants.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.RevokedAt == null &&
                        db.GitHubInstallations.Any(installation =>
                            installation.InstallationId == x.InstallationId &&
                            installation.AppKind == GitHubAppKind.Repo &&
                            installation.ProjectId == projectId &&
                            installation.RevokedAt == null))
            .ToListAsync(ct).ConfigureAwait(false);
        var grant = grants.OrderByDescending(x => x.GrantedAt).FirstOrDefault();
        if (grant is null)
            return null;
        return new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value, RunId = runId,
            Purpose = GitHubCapabilityPurpose.UnattendedRepository, AppKind = GitHubAppKind.Repo,
            SourceKind = GitHubCapabilitySnapshotSourceKind.RepositoryGrant, ProjectId = projectId,
            InstallationId = grant.InstallationId, RepositoryId = grant.RepositoryId,
            GrantDigest = grant.PermissionDigest, CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task<RunGitHubCapabilitySnapshotRecord?> TryResolveUnattendedCopilotSnapshotAsync(
        string runId,
        string projectId,
        CancellationToken ct)
    {
        var binding = await GetActiveCopilotBindingOrPlatformDefaultAsync(projectId, ct).ConfigureAwait(false);
        if (binding is null)
            return null;
        return new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value, RunId = runId,
            Purpose = GitHubCapabilityPurpose.UnattendedCopilot, AppKind = GitHubAppKind.Copilot,
            SourceKind = GitHubCapabilitySnapshotSourceKind.CopilotBinding, ProjectId = projectId,
            SourceBindingId = binding.Id, CredentialReference = binding.CredentialReference,
            CredentialVersion = binding.CredentialVersion, GrantDigest = binding.GrantDigest,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    internal async Task<CopilotBindingSnapshotSource?> GetActiveCopilotBindingOrPlatformDefaultAsync(
        string projectId,
        CancellationToken ct)
    {
        var projectBinding = await db.ProjectCopilotBindings.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.Status == GitHubBindingStatus.Active && x.DeactivatedAt == null)
            .Select(x => new CopilotBindingSnapshotSource(
                x.Id,
                x.CredentialReference,
                x.CredentialVersion,
                x.GrantDigest))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (projectBinding is not null)
            return projectBinding;

        var platformBinding = await db.PlatformDefaultCopilotBindings.AsNoTracking()
            .Where(x => x.Id == PlatformDefaultCopilotBindingRecord.SingletonId &&
                        x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null)
            .Select(x => new CopilotBindingSnapshotSource(
                x.Id,
                x.CredentialReference,
                x.CredentialVersion,
                x.GrantDigest))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return platformBinding;
    }

    internal async Task<CopilotBindingSnapshotSource?> GetLiveAutomationCopilotBindingAsync(
        string projectId,
        string bindingId,
        string grantDigest,
        CancellationToken ct = default)
    {
        if (string.Equals(bindingId, PlatformDefaultCopilotBindingRecord.SingletonId, StringComparison.Ordinal))
        {
            return await db.PlatformDefaultCopilotBindings.AsNoTracking()
                .Where(x => x.Id == bindingId &&
                            x.GrantDigest == grantDigest &&
                            x.Status == GitHubBindingStatus.Active &&
                            x.DeactivatedAt == null)
                .Select(x => new CopilotBindingSnapshotSource(
                    x.Id,
                    x.CredentialReference,
                    x.CredentialVersion,
                    x.GrantDigest))
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return await db.ProjectCopilotBindings.AsNoTracking()
            .Where(x => x.Id == bindingId &&
                        x.ProjectId == projectId &&
                        x.GrantDigest == grantDigest &&
                        x.Status == GitHubBindingStatus.Active &&
                        x.DeactivatedAt == null)
            .Select(x => new CopilotBindingSnapshotSource(
                x.Id,
                x.CredentialReference,
                x.CredentialVersion,
                x.GrantDigest))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    internal async Task<bool> IsLiveCopilotBindingAsync(
        string projectId,
        string bindingId,
        string grantDigest,
        CancellationToken ct)
    {
        if (string.Equals(bindingId, PlatformDefaultCopilotBindingRecord.SingletonId, StringComparison.Ordinal))
        {
            return await db.PlatformDefaultCopilotBindings.AsNoTracking().AnyAsync(binding =>
                binding.Id == bindingId &&
                binding.GrantDigest == grantDigest &&
                binding.Status == GitHubBindingStatus.Active &&
                binding.DeactivatedAt == null, ct).ConfigureAwait(false);
        }

        return await db.ProjectCopilotBindings.AsNoTracking().AnyAsync(binding =>
            binding.Id == bindingId &&
            binding.ProjectId == projectId &&
            binding.GrantDigest == grantDigest &&
            binding.Status == GitHubBindingStatus.Active &&
            binding.DeactivatedAt == null, ct).ConfigureAwait(false);
    }

    public async Task AppendAuditAsync(GitHubAuditRecord audit, CancellationToken ct = default)
    {
        EnsureSafe(audit);
        if (audit.ActorKind == GitHubAuditActorKind.HumanEntraSubject && string.IsNullOrWhiteSpace(audit.EntraObjectId))
            throw new ArgumentException("Human audit records require an Entra subject.", nameof(audit));
        if (audit.ActorKind == GitHubAuditActorKind.GitHubWebhook && audit.EntraObjectId is not null)
            throw new ArgumentException("Webhook audit records cannot carry an Entra subject.", nameof(audit));

        db.GitHubAuditRecords.Add(audit);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ||
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 1555 or 2067
        };

    private static void EnsureAuthorizationTransaction(GitHubAuthorizationRecord authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization.ExternalTransactionId) ||
            authorization.ExternalTransactionId == authorization.State)
            throw new ArgumentException(
                "Authorization transactions require an externally safe ID distinct from OAuth state.",
                nameof(authorization));
    }

    private static void EnsurePlatformDefaultCopilotBinding(PlatformDefaultCopilotBindingRecord binding)
    {
        if (!string.Equals(binding.Id, PlatformDefaultCopilotBindingRecord.SingletonId, StringComparison.Ordinal))
            throw new ArgumentException("Platform default Copilot binding must use the singleton id.", nameof(binding));
    }

    private static void EnsureSafe(object record)
    {
        if (SensitiveDataRedactor.ContainsSensitiveValue(JsonSerializer.Serialize(record)))
            throw new ArgumentException("GitHub connections persistence accepts only redacted credential references and metadata.", nameof(record));
    }

    private static void EnsureCapabilitySnapshot(RunGitHubCapabilitySnapshotRecord snapshot)
    {
        _ = new SnapshotRef(snapshot.SnapshotRef);
        if (string.IsNullOrWhiteSpace(snapshot.RunId) || string.IsNullOrWhiteSpace(snapshot.GrantDigest))
            throw new ArgumentException("Capability snapshots require run and grant identity.", nameof(snapshot));

        var valid = snapshot.Purpose switch
        {
            GitHubCapabilityPurpose.InteractiveRepository => !string.IsNullOrWhiteSpace(snapshot.ProjectId) &&
                snapshot.AppKind == GitHubAppKind.Repo &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.UserAuthorization &&
                snapshot.EntraObjectId is not null && snapshot.SourceAuthorizationId is not null &&
                snapshot.SourceBindingId is null && snapshot.InstallationId is null &&
                snapshot.RepositoryId is not null && snapshot.CredentialReference is not null &&
                snapshot.CredentialVersion is not null,
            GitHubCapabilityPurpose.InteractiveCopilot => !string.IsNullOrWhiteSpace(snapshot.ProjectId) &&
                snapshot.AppKind == GitHubAppKind.Repo &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.UserAuthorization &&
                snapshot.EntraObjectId is not null && snapshot.SourceAuthorizationId is not null &&
                snapshot.SourceBindingId is null && snapshot.InstallationId is null &&
                snapshot.RepositoryId is null && snapshot.CredentialReference is not null &&
                snapshot.CredentialVersion is not null,
            GitHubCapabilityPurpose.UnattendedRepository => !string.IsNullOrWhiteSpace(snapshot.ProjectId) &&
                snapshot.AppKind == GitHubAppKind.Repo &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.RepositoryGrant &&
                snapshot.EntraObjectId is null && snapshot.SourceAuthorizationId is null &&
                snapshot.SourceBindingId is null && snapshot.InstallationId is not null &&
                snapshot.RepositoryId is not null && snapshot.CredentialReference is null &&
                snapshot.CredentialVersion is null,
            GitHubCapabilityPurpose.UnattendedCopilot => snapshot.AppKind == GitHubAppKind.Copilot &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.CopilotBinding &&
                snapshot.EntraObjectId is null && snapshot.SourceAuthorizationId is null &&
                snapshot.SourceBindingId is not null && snapshot.InstallationId is null &&
                snapshot.RepositoryId is null &&
                (string.Equals(snapshot.SourceBindingId, PlatformDefaultCopilotBindingRecord.SingletonId, StringComparison.Ordinal)
                    ? snapshot.ProjectId is null || !string.IsNullOrWhiteSpace(snapshot.ProjectId)
                    : !string.IsNullOrWhiteSpace(snapshot.ProjectId)) &&
                snapshot.CredentialReference is not null &&
                snapshot.CredentialVersion is not null,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("Capability snapshot purpose mapping is invalid.", nameof(snapshot));
    }

    private static void EnsureAutomationActivationSnapshot(AutomationActivationRecord activation)
    {
        _ = new SnapshotRef(activation.Id);
        var hasValidModelProviderSource = activation.ModelProviderSource switch
        {
            AutomationModelProviderSource.Byok => !string.IsNullOrWhiteSpace(activation.ByokProviderId) &&
                string.IsNullOrWhiteSpace(activation.CopilotBindingId) &&
                string.IsNullOrWhiteSpace(activation.CopilotBindingGrantDigest),
            _ => !string.IsNullOrWhiteSpace(activation.CopilotBindingId) &&
                 !string.IsNullOrWhiteSpace(activation.CopilotBindingGrantDigest) &&
                 string.IsNullOrWhiteSpace(activation.ByokProviderId),
        };
        if (string.IsNullOrWhiteSpace(activation.ProjectId) ||
            activation.InstallationId <= 0 || activation.RepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(activation.RepositoryGrantDigest) ||
            !hasValidModelProviderSource ||
            activation.Status != AutomationActivationStatus.Active)
            throw new ArgumentException("Activation snapshots require an exact live grant and model-provider tuple.", nameof(activation));
    }

    private static GitHubAuditRecord CreateActivationAudit(
        string projectId,
        string? entraObjectId,
        GitHubAuditActorKind actorKind,
        GitHubAuditOutcome outcome,
        GitHubAuditReasonCode reasonCode,
        string? grantDigest,
        string? correlationId = null,
        GitHubAuditAction action = GitHubAuditAction.AutomationActivated) =>
        new()
        {
            EntraObjectId = actorKind == GitHubAuditActorKind.HumanEntraSubject ? entraObjectId : null,
            ActorKind = actorKind,
            Action = action,
            ResourceId = projectId,
            AppKind = GitHubAppKind.Repo,
            CapabilityPurpose = GitHubCapabilityPurpose.UnattendedRepository,
            Outcome = outcome,
            ReasonCode = reasonCode,
            CorrelationId = correlationId ?? SnapshotRef.Create().Value,
            OccurredAt = DateTimeOffset.UtcNow,
            GrantDigest = grantDigest,
        };

    private async Task<RunGitHubCapabilitySnapshotRecord?> CreateUserAuthorizationSnapshotAsync(
        RunGitHubIdentitySnapshotRecord legacy,
        GitHubCapabilityPurpose purpose,
        CancellationToken ct)
    {
        var matches = await db.GitHubAppAuthorizations.AsNoTracking()
            .Where(x => x.AppKind == GitHubAppKind.Repo &&
                        x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
                        x.EntraObjectId == legacy.EntraObjectId &&
                        x.CredentialReference == legacy.CredentialReference &&
                        x.CredentialVersion == legacy.CredentialVersion &&
                        x.GrantDigest == legacy.GrantDigest &&
                        x.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        if (matches.Count != 1 ||
            (purpose == GitHubCapabilityPurpose.InteractiveRepository && legacy.RepositoryId is null))
            return null;
        return new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value,
            RunId = legacy.RunId,
            Purpose = purpose,
            AppKind = GitHubAppKind.Repo,
            SourceKind = GitHubCapabilitySnapshotSourceKind.UserAuthorization,
            ProjectId = legacy.ProjectId,
            EntraObjectId = legacy.EntraObjectId,
            SourceAuthorizationId = matches[0].Id,
            RepositoryId = purpose == GitHubCapabilityPurpose.InteractiveRepository ? legacy.RepositoryId : null,
            CredentialReference = legacy.CredentialReference,
            CredentialVersion = legacy.CredentialVersion,
            GrantDigest = legacy.GrantDigest,
            CapturedAt = legacy.CapturedAt,
        };
    }

    private async Task<RunGitHubCapabilitySnapshotRecord?> CreateRepositoryGrantSnapshotAsync(
        RunGitHubIdentitySnapshotRecord legacy,
        CancellationToken ct)
    {
        if (legacy.InstallationId is null || legacy.RepositoryId is null)
            return null;
        var isLive = await db.GitHubRepositoryGrants.AsNoTracking().AnyAsync(x =>
            x.InstallationId == legacy.InstallationId && x.RepositoryId == legacy.RepositoryId &&
            x.ProjectId == legacy.ProjectId && x.PermissionDigest == legacy.GrantDigest &&
            x.RevokedAt == null, ct).ConfigureAwait(false);
        return !isLive ? null : new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value, RunId = legacy.RunId,
            Purpose = GitHubCapabilityPurpose.UnattendedRepository, AppKind = GitHubAppKind.Repo,
            SourceKind = GitHubCapabilitySnapshotSourceKind.RepositoryGrant, ProjectId = legacy.ProjectId,
            InstallationId = legacy.InstallationId, RepositoryId = legacy.RepositoryId,
            GrantDigest = legacy.GrantDigest, CapturedAt = legacy.CapturedAt,
        };
    }

    private Task<bool> HasLiveRepositoryScopeAsync(
        string projectId,
        long repositoryId,
        CancellationToken ct) =>
        db.GitHubRepositoryGrants.AsNoTracking().AnyAsync(grant =>
            grant.ProjectId == projectId &&
            grant.RepositoryId == repositoryId &&
            grant.RevokedAt == null &&
            db.GitHubInstallations.Any(installation =>
                installation.InstallationId == grant.InstallationId &&
                installation.AppKind == GitHubAppKind.Repo &&
                installation.ProjectId == projectId &&
                installation.RevokedAt == null), ct);

    private async Task<RunGitHubCapabilitySnapshotRecord?> CreateCopilotBindingSnapshotAsync(
        RunGitHubIdentitySnapshotRecord legacy,
        CancellationToken ct)
    {
        var matches = await db.ProjectCopilotBindings.AsNoTracking()
            .Where(x => x.ProjectId == legacy.ProjectId &&
                        x.CredentialReference == legacy.CredentialReference &&
                        x.CredentialVersion == legacy.CredentialVersion &&
                        x.GrantDigest == legacy.GrantDigest &&
                        x.Status == GitHubBindingStatus.Active && x.DeactivatedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        return matches.Count != 1 ? null : new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value, RunId = legacy.RunId,
            Purpose = GitHubCapabilityPurpose.UnattendedCopilot, AppKind = GitHubAppKind.Copilot,
            SourceKind = GitHubCapabilitySnapshotSourceKind.CopilotBinding, ProjectId = legacy.ProjectId,
            SourceBindingId = matches[0].Id, CredentialReference = legacy.CredentialReference,
            CredentialVersion = legacy.CredentialVersion, GrantDigest = legacy.GrantDigest,
            CapturedAt = legacy.CapturedAt,
        };
    }

}
