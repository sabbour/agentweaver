using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agentweaver.Identity.Broker;

/// <summary>Revocable project authority. It contains selection evidence, never a bearer or GitHub token.</summary>
public sealed class IssueSyncGrant
{
    public Guid GrantId { get; set; }
    public required string ProjectId { get; set; }
    public required string TenantId { get; set; }
    public Guid CreatorId { get; set; }
    public required string ConnectionId { get; set; }
    public required string RepositoryOwner { get; set; }
    public required string RepositoryName { get; set; }
    public required string SelectionHash { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only mint evidence, never credential material.</summary>
public sealed class IssueSyncMintAudit
{
    public Guid AuditId { get; set; }
    public Guid GrantId { get; set; }
    public Guid OwnerId { get; set; }
    public required string ProjectId { get; set; }
    public required string Repository { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class IssueSyncMintAuditModel : IEntityTypeConfiguration<IssueSyncMintAudit>
{
    public void Configure(EntityTypeBuilder<IssueSyncMintAudit> entity)
    {
        entity.ToTable("issue_sync_mint_audit");
        entity.HasKey(row => row.AuditId);
        entity.Property(row => row.ProjectId).HasMaxLength(256);
        entity.Property(row => row.Repository).HasMaxLength(201);
        entity.Property(row => row.Outcome).HasMaxLength(32);
        entity.HasIndex(row => new { row.GrantId, row.CreatedAt });
    }
}

internal sealed class IssueSyncGrantModel : IEntityTypeConfiguration<IssueSyncGrant>
{
    public void Configure(EntityTypeBuilder<IssueSyncGrant> entity)
    {
        entity.ToTable("issue_sync_grants");
        entity.HasKey(row => row.GrantId);
        entity.Property(row => row.ProjectId).HasMaxLength(256);
        entity.Property(row => row.TenantId).HasMaxLength(256);
        entity.Property(row => row.ConnectionId).HasMaxLength(128);
        entity.Property(row => row.RepositoryOwner).HasMaxLength(100);
        entity.Property(row => row.RepositoryName).HasMaxLength(100);
        entity.Property(row => row.SelectionHash).HasMaxLength(64).IsFixedLength();
        entity.HasIndex(row => new { row.ProjectId, row.Active });
    }
}

internal interface IIssueSyncProjectAuthority
{
    Task<IssueSyncProjectProof> ReadAsync(IssueSyncAuthorityQuery query, CancellationToken ct);
}

internal sealed class IssueSyncProjectAuthorityClient(IServiceProvider services) : IIssueSyncProjectAuthority
{
    public Task<IssueSyncProjectProof> ReadAsync(IssueSyncAuthorityQuery query, CancellationToken ct) =>
        IssueSyncServiceTransport.SendAsync<IssueSyncProjectProof>(services, IssueSyncGrantAuthority.ProjectsChannel,
            "/internal/issue-sync/project-authority", query, ct);
}

internal sealed class IssueSyncGrantAuthority(IdentityBrokerDbContext db, IIssueSyncProjectAuthority projects,
    TimeProvider time)
{
    internal const string ProjectsChannel = "IssueSyncProjects";
    internal const string WorkerChannel = "IssueSyncWorker";

    internal async Task<IssueSyncGrantReceipt> CreateAsync(IssueSyncProjectProof proof, string selectionCode,
        CancellationToken ct)
    {
        if (selectionCode is not { Length: >= 32 and <= 256 } || !Guid.TryParseExact(proof.CreatorId, "D", out var creator))
            throw new RuntimeAuthorizationException("issue_sync_selection_invalid");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selectionCode)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var selection = await db.RepoAppRepositorySelections.AsNoTracking()
            .SingleOrDefaultAsync(row => row.CodeHash == hash && row.OwnerId == creator, ct);
        if (selection is null || selection.ExpiresAt <= now || selection.ConsumedAt is not null ||
            selection.ProjectId is not null || selection.ConnectionId != proof.ConnectionId ||
            !string.Equals(selection.RepositoryFullName, proof.Repository.FullName, StringComparison.OrdinalIgnoreCase))
            throw new RuntimeAuthorizationException("issue_sync_selection_invalid");
        var claimed = await db.RepoAppRepositorySelections.Where(row => row.CodeHash == hash &&
            row.ProjectId == null && row.ConsumedAt == null && row.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ProjectId, proof.ProjectId)
                .SetProperty(row => row.IssueWriteRequested, true).SetProperty(row => row.ConsumedAt, now), ct);
        if (claimed != 1)
            throw new RuntimeAuthorizationException("issue_sync_selection_consumed");
        var grant = new IssueSyncGrant
        {
            GrantId = Guid.NewGuid(), ProjectId = proof.ProjectId, TenantId = proof.TenantId,
            CreatorId = creator, ConnectionId = proof.ConnectionId, RepositoryOwner = proof.Repository.Owner,
            RepositoryName = proof.Repository.Name, SelectionHash = hash, Active = true, CreatedAt = now
        };
        db.IssueSyncGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        await RequireProjectAsync(grant, ct);
        await tx.CommitAsync(ct);
        return Receipt(grant);
    }

    internal async Task<IssueSyncGrant> RequireAsync(Guid id, CancellationToken ct)
    {
        var grant = await db.IssueSyncGrants.AsNoTracking().SingleOrDefaultAsync(row => row.GrantId == id, ct);
        if (grant is null || !grant.Active)
            throw new RuntimeAuthorizationException("issue_sync_grant_revoked");
        try { await RequireProjectAsync(grant, ct); }
        catch (RuntimeAuthorizationException error) when (
            error.Code is "issue_sync_creator_not_owner" or "issue_sync_project_inactive")
        {
            await db.IssueSyncGrants.Where(row => row.GrantId == id).ExecuteUpdateAsync(
                set => set.SetProperty(row => row.Active, false), ct);
            throw;
        }
        return grant;
    }

    internal async Task RequireProjectAsync(IssueSyncGrant grant, CancellationToken ct)
    {
        var current = await projects.ReadAsync(
            new IssueSyncAuthorityQuery(grant.ProjectId, grant.TenantId, grant.CreatorId.ToString("D")), ct);
        if (current != Proof(grant))
            throw new RuntimeAuthorizationException("issue_sync_repository_not_selected");
    }

    internal async Task RevokeAsync(Guid id, IssueSyncRevocationProof proof, CancellationToken ct)
    {
        var grant = await db.IssueSyncGrants.AsNoTracking().SingleOrDefaultAsync(row => row.GrantId == id, ct);
        if (grant is null || grant.ProjectId != proof.ProjectId || grant.TenantId != proof.TenantId)
            throw new RuntimeAuthorizationException("issue_sync_grant_unknown");
        if (!proof.IsAdministrator && proof.ActorId != grant.CreatorId.ToString("D"))
            throw new RuntimeAuthorizationException("issue_sync_grant_revocation_denied");
        await db.IssueSyncGrants.Where(row => row.GrantId == id).ExecuteUpdateAsync(
            set => set.SetProperty(row => row.Active, false), ct);
    }

    internal async Task RecordMintAsync(IssueSyncGrant grant, string outcome, CancellationToken ct)
    {
        if (outcome is not ("requested" or "minted"))
            throw new ArgumentException("Unknown issue sync mint outcome.", nameof(outcome));
        db.IssueSyncMintAudit.Add(new()
        {
            AuditId = Guid.NewGuid(), GrantId = grant.GrantId, OwnerId = grant.CreatorId,
            ProjectId = grant.ProjectId, Repository = grant.RepositoryOwner + "/" + grant.RepositoryName,
            Outcome = outcome, CreatedAt = time.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }

    internal static IssueSyncProjectProof Proof(IssueSyncGrant row) => new(row.ProjectId, row.TenantId,
        row.CreatorId.ToString("D"), row.ConnectionId, new(row.RepositoryOwner, row.RepositoryName));
    internal static IssueSyncGrantReceipt Receipt(IssueSyncGrant row) => new(row.GrantId, Proof(row), row.Active);
}
