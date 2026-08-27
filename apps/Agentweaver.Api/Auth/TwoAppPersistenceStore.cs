using Agentweaver.Api.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Agentweaver.Api.Auth;

public enum AuthorizationClaimResult { Claimed, Invalid, Consumed }
public enum BindingWriteResult { Bound, Unavailable }
public enum InvocationClaimResult { Claimed, Duplicate }
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
            db.GitHubAuditRecords.Add(audit);
            await db.GitHubAuthorizations.Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GitHubAuthorizationStatus.Failed).SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
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
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return BindingWriteResult.Unavailable;
        }
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
        foreach (var property in record.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(x => x.PropertyType == typeof(string)))
        {
            if (property.GetValue(record) is string value && CredentialPattern.IsMatch(value))
                throw new ArgumentException("Two-App persistence accepts only redacted credential references and metadata.", property.Name);
        }
    }
}
