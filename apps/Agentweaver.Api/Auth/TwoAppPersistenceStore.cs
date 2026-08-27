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
    internal TwoAppCredentialLocator? CredentialLocator { get; init; }
}

public sealed record CapabilitySnapshotBackfillResult(int Migrated, int Unavailable);
internal sealed record RepoAppAuthorizationTransaction(
    string State,
    GitHubAppKind AppKind,
    GitHubAuthorizationPurpose Purpose,
    string EntraObjectId,
    long ExpiresAtUnixMilliseconds,
    string ReturnRouteKey,
    string PkceVerifierProtected,
    string CallbackCookieHash);
internal sealed record CopilotAuthorizationTransaction(string State, string EntraObjectId, string ProjectId, long ExpiresAtUnixMilliseconds, string ReturnRouteKey, string PkceVerifierProtected, string CallbackCookieHash);
internal sealed record RepoAppCredentialReference(
    string Id,
    string CredentialReference,
    string CredentialVersion,
    DateTimeOffset CreatedAt);
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
/// Persistence boundary for the two GitHub App model. It accepts only opaque credential
/// references and exposes guarded state transitions rather than mutable entity access.
/// </summary>
public sealed class TwoAppPersistenceStore(MemoryDbContext db)
{
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

    internal Task<RepoAppAuthorizationTransaction?> GetRepoAppAuthorizationTransactionAsync(
        string state,
        CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking()
            .Where(x => x.State == state)
            .Select(x => new RepoAppAuthorizationTransaction(
                x.State,
                x.AppKind,
                x.Purpose,
                x.EntraObjectId,
                x.ExpiresAtUnixMilliseconds,
                x.ReturnRouteKey,
                x.PkceVerifierProtected,
                x.CallbackCookieHash))
            .SingleOrDefaultAsync(ct);
    internal Task<CopilotAuthorizationTransaction?> GetCopilotAuthorizationTransactionAsync(string state, CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking().Where(x => x.State == state && x.AppKind == GitHubAppKind.Copilot && x.Purpose == GitHubAuthorizationPurpose.InteractiveCopilot && x.ProjectId != null)
            .Select(x => new CopilotAuthorizationTransaction(x.State, x.EntraObjectId, x.ProjectId!, x.ExpiresAtUnixMilliseconds, x.ReturnRouteKey, x.PkceVerifierProtected, x.CallbackCookieHash)).SingleOrDefaultAsync(ct);
    internal Task<CopilotAuthorizationTransaction?> GetCopilotAuthorizationTransactionByIdAsync(string id, string subject, CancellationToken ct = default) =>
        db.GitHubAuthorizations.AsNoTracking().Where(x => x.ExternalTransactionId == id && x.EntraObjectId == subject && x.AppKind == GitHubAppKind.Copilot && x.Purpose == GitHubAuthorizationPurpose.InteractiveCopilot && x.ProjectId != null)
            .Select(x => new CopilotAuthorizationTransaction(x.State, x.EntraObjectId, x.ProjectId!, x.ExpiresAtUnixMilliseconds, x.ReturnRouteKey, x.PkceVerifierProtected, x.CallbackCookieHash)).SingleOrDefaultAsync(ct);

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
        CancellationToken ct = default) =>
        db.GitHubAuthorizations
            .Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, succeeded ? GitHubAuthorizationStatus.Completed : GitHubAuthorizationStatus.Failed)
                .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow), ct);

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

    internal async Task<bool> CompleteCopilotAuthorizationAsync(string state, ProjectCopilotBindingRecord binding, GitHubAuditRecord audit, CancellationToken ct = default)
        {
            EnsureSafe(binding); EnsureSafe(audit);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await db.ProjectCopilotBindings.Where(x => x.ProjectId == binding.ProjectId && x.Status == GitHubBindingStatus.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GitHubBindingStatus.Inactive).SetProperty(x => x.DeactivatedAt, now), ct).ConfigureAwait(false);
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
            GitHubCapabilitySnapshotSourceKind.CopilotBinding => await db.ProjectCopilotBindings.AsNoTracking()
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
            : new(snapshotRef, snapshot.Purpose, snapshot.AppKind, snapshot.ProjectId,
                snapshot.RepositoryId, snapshot.InstallationId, snapshot.GrantDigest)
            {
                CredentialLocator = snapshot.SourceKind switch
                {
                    GitHubCapabilitySnapshotSourceKind.UserAuthorization =>
                        TwoAppCredentialLocator.ForRepoAppUser(snapshot.CredentialReference!),
                    GitHubCapabilitySnapshotSourceKind.CopilotBinding =>
                        TwoAppCredentialLocator.ForCopilotProject(snapshot.CredentialReference!),
                    _ => null,
                },
            };
    }

    internal Task<List<RunGitHubCapabilitySnapshotRecord>> GetCapabilitySnapshotsAsync(
        string runId,
        CancellationToken ct = default) =>
        db.RunGitHubCapabilitySnapshots.AsNoTracking()
            .Where(snapshot => snapshot.RunId == runId)
            .OrderBy(snapshot => snapshot.Purpose)
            .ToListAsync(ct);

    /// <summary>
    /// Atomically creates fresh opaque references for a child or retry from the exact immutable
    /// parent snapshot rows. Existing target snapshots are never replaced. An empty parent set is
    /// only accepted when <paramref name="projectId"/> has never recorded any GitHub App history;
    /// otherwise a run that should have inherited real capability protection is denied rather than
    /// silently launched with none.
    /// </summary>
    internal async Task<bool> TryInheritCapabilitySnapshotsAsync(
        string sourceRunId,
        string targetRunId,
        string? projectId,
        CancellationToken ct = default)
    {
        var source = await GetCapabilitySnapshotsAsync(sourceRunId, ct).ConfigureAwait(false);
        if (source.Count == 0)
        {
            return string.IsNullOrWhiteSpace(projectId) ||
                !await HasProjectEverConfiguredGitHubCapabilityAsync(projectId, ct).ConfigureAwait(false);
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
    /// True when <paramref name="projectId"/> has ever recorded a GitHub App installation,
    /// repository grant, or Copilot binding, including now-revoked or deactivated rows. This is
    /// the sole signal distinguishing a project that legitimately never uses GitHub capability
    /// (zero required snapshots) from one that does but currently resolves nothing live.
    /// </summary>
    internal async Task<bool> HasProjectEverConfiguredGitHubCapabilityAsync(
        string projectId,
        CancellationToken ct = default) =>
        await db.GitHubInstallations.AsNoTracking().AnyAsync(x => x.ProjectId == projectId, ct)
            .ConfigureAwait(false) ||
        await db.GitHubRepositoryGrants.AsNoTracking().AnyAsync(x => x.ProjectId == projectId, ct)
            .ConfigureAwait(false) ||
        await db.ProjectCopilotBindings.AsNoTracking().AnyAsync(x => x.ProjectId == projectId, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Trusted production root-capture seam. Selects and insert-only creates every currently live
    /// v2 capability snapshot directly from authoritative sources for a brand-new root run; it
    /// never reads the finite v1 legacy table. A project with no GitHub App history captures zero
    /// snapshots by design. A project that has ever recorded GitHub App history but currently
    /// resolves none of the four purposes is reported unavailable so the caller fails the launch
    /// closed instead of silently proceeding with no capability protection.
    /// </summary>
    public async Task<CapabilitySnapshotBackfillResult> CaptureRootCapabilitySnapshotsAsync(
        string runId,
        string projectId,
        string entraObjectId,
        CancellationToken ct = default)
    {
        var resolved = new List<RunGitHubCapabilitySnapshotRecord>();
        var interactiveRepository = await TryResolveInteractiveRepositorySnapshotAsync(runId, projectId, entraObjectId, ct)
            .ConfigureAwait(false);
        if (interactiveRepository is not null)
            resolved.Add(interactiveRepository);
        var interactiveCopilot = await TryResolveInteractiveCopilotSnapshotAsync(runId, projectId, entraObjectId, ct)
            .ConfigureAwait(false);
        if (interactiveCopilot is not null)
            resolved.Add(interactiveCopilot);
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
            return await HasProjectEverConfiguredGitHubCapabilityAsync(projectId, ct).ConfigureAwait(false)
                ? new CapabilitySnapshotBackfillResult(0, 1)
                : new CapabilitySnapshotBackfillResult(0, 0);
        }

        var migrated = 0;
        foreach (var snapshot in resolved)
        {
            if (!await TryInsertCapabilitySnapshotAsync(snapshot, ct).ConfigureAwait(false))
                return new CapabilitySnapshotBackfillResult(migrated, 1);
            migrated++;
        }
        return new CapabilitySnapshotBackfillResult(migrated, 0);
    }

    private Task<GitHubAppAuthorizationRecord?> FindLiveRepoUserAuthorizationAsync(
        string entraObjectId,
        CancellationToken ct) =>
        db.GitHubAppAuthorizations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EntraObjectId == entraObjectId &&
            x.AppKind == GitHubAppKind.Repo &&
            x.Purpose == GitHubAuthorizationPurpose.InteractiveRepository &&
            x.RevokedAt == null, ct);

    private async Task<RunGitHubCapabilitySnapshotRecord?> TryResolveInteractiveRepositorySnapshotAsync(
        string runId,
        string projectId,
        string entraObjectId,
        CancellationToken ct)
    {
        var authorization = await FindLiveRepoUserAuthorizationAsync(entraObjectId, ct).ConfigureAwait(false);
        if (authorization is null)
            return null;
        var grants = await db.GitHubRepositoryGrants.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        var grant = grants.OrderByDescending(x => x.GrantedAt).FirstOrDefault();
        if (grant is null)
            return null;
        return new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value, RunId = runId,
            Purpose = GitHubCapabilityPurpose.InteractiveRepository, AppKind = GitHubAppKind.Repo,
            SourceKind = GitHubCapabilitySnapshotSourceKind.UserAuthorization, ProjectId = projectId,
            EntraObjectId = entraObjectId, SourceAuthorizationId = authorization.Id, RepositoryId = grant.RepositoryId,
            CredentialReference = authorization.CredentialReference, CredentialVersion = authorization.CredentialVersion,
            GrantDigest = authorization.GrantDigest, CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task<RunGitHubCapabilitySnapshotRecord?> TryResolveInteractiveCopilotSnapshotAsync(
        string runId,
        string projectId,
        string entraObjectId,
        CancellationToken ct)
    {
        var authorization = await FindLiveRepoUserAuthorizationAsync(entraObjectId, ct).ConfigureAwait(false);
        if (authorization is null)
            return null;
        return new RunGitHubCapabilitySnapshotRecord
        {
            SnapshotRef = SnapshotRef.Create().Value, RunId = runId,
            Purpose = GitHubCapabilityPurpose.InteractiveCopilot, AppKind = GitHubAppKind.Repo,
            SourceKind = GitHubCapabilitySnapshotSourceKind.UserAuthorization, ProjectId = projectId,
            EntraObjectId = entraObjectId, SourceAuthorizationId = authorization.Id,
            CredentialReference = authorization.CredentialReference, CredentialVersion = authorization.CredentialVersion,
            GrantDigest = authorization.GrantDigest, CapturedAt = DateTimeOffset.UtcNow,
        };
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
        var binding = await db.ProjectCopilotBindings.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ProjectId == projectId && x.Status == GitHubBindingStatus.Active && x.DeactivatedAt == null, ct)
            .ConfigureAwait(false);
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

    private static void EnsureSafe(object record)
    {
        if (SensitiveDataRedactor.ContainsSensitiveValue(JsonSerializer.Serialize(record)))
            throw new ArgumentException("Two-App persistence accepts only redacted credential references and metadata.", nameof(record));
    }

    private static void EnsureCapabilitySnapshot(RunGitHubCapabilitySnapshotRecord snapshot)
    {
        _ = new SnapshotRef(snapshot.SnapshotRef);
        if (string.IsNullOrWhiteSpace(snapshot.RunId) || string.IsNullOrWhiteSpace(snapshot.ProjectId) ||
            string.IsNullOrWhiteSpace(snapshot.GrantDigest))
            throw new ArgumentException("Capability snapshots require run, project, and grant identity.", nameof(snapshot));

        var valid = snapshot.Purpose switch
        {
            GitHubCapabilityPurpose.InteractiveRepository => snapshot.AppKind == GitHubAppKind.Repo &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.UserAuthorization &&
                snapshot.EntraObjectId is not null && snapshot.SourceAuthorizationId is not null &&
                snapshot.SourceBindingId is null && snapshot.InstallationId is null &&
                snapshot.RepositoryId is not null && snapshot.CredentialReference is not null &&
                snapshot.CredentialVersion is not null,
            GitHubCapabilityPurpose.InteractiveCopilot => snapshot.AppKind == GitHubAppKind.Repo &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.UserAuthorization &&
                snapshot.EntraObjectId is not null && snapshot.SourceAuthorizationId is not null &&
                snapshot.SourceBindingId is null && snapshot.InstallationId is null &&
                snapshot.RepositoryId is null && snapshot.CredentialReference is not null &&
                snapshot.CredentialVersion is not null,
            GitHubCapabilityPurpose.UnattendedRepository => snapshot.AppKind == GitHubAppKind.Repo &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.RepositoryGrant &&
                snapshot.EntraObjectId is null && snapshot.SourceAuthorizationId is null &&
                snapshot.SourceBindingId is null && snapshot.InstallationId is not null &&
                snapshot.RepositoryId is not null && snapshot.CredentialReference is null &&
                snapshot.CredentialVersion is null,
            GitHubCapabilityPurpose.UnattendedCopilot => snapshot.AppKind == GitHubAppKind.Copilot &&
                snapshot.SourceKind == GitHubCapabilitySnapshotSourceKind.CopilotBinding &&
                snapshot.EntraObjectId is null && snapshot.SourceAuthorizationId is null &&
                snapshot.SourceBindingId is not null && snapshot.InstallationId is null &&
                snapshot.RepositoryId is null && snapshot.CredentialReference is not null &&
                snapshot.CredentialVersion is not null,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("Capability snapshot purpose mapping is invalid.", nameof(snapshot));
    }

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
