using Agentweaver.Api.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Agentweaver.Api.Auth;

public enum AuthorizationClaimResult { Claimed, Invalid, Consumed }
public enum BindingWriteResult { Bound, Unavailable }
public enum InvocationClaimResult { Claimed, Duplicate }

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
        db.GitHubAuthorizations.Add(authorization);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

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

    public Task CompleteAuthorizationAsync(
        string state,
        bool succeeded,
        CancellationToken ct = default) =>
        db.GitHubAuthorizations
            .Where(x => x.State == state && x.Status == GitHubAuthorizationStatus.Redeeming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, succeeded ? GitHubAuthorizationStatus.Completed : GitHubAuthorizationStatus.Failed)
                .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow), ct);

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
