using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IssueSyncGrantTests(PostgresContainerFixture postgres)
{
    [Theory]
    [InlineData("issue_sync_creator_not_owner")]
    [InlineData("issue_sync_repository_not_selected")]
    [InlineData("issue_sync_project_inactive")]
    public async Task EveryCycleRevalidatesOwnerAndRepositoryWithoutAUserBearer(string error)
    {
        await using var db = await CreateDbAsync();
        var proof = Proof();
        var row = Row(proof);
        db.IssueSyncGrants.Add(row);
        await db.SaveChangesAsync();
        var projects = new ProjectAuthority(proof);
        var authority = new IssueSyncGrantAuthority(db, projects, TimeProvider.System);
        Assert.Equal(row.GrantId, (await authority.RequireAsync(row.GrantId, default)).GrantId);
        projects.Failure = error;
        var denied = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => authority.RequireAsync(row.GrantId, default));
        Assert.Equal(error, denied.Code);
        Assert.Equal(2, projects.Calls);
        if (error is "issue_sync_creator_not_owner" or "issue_sync_project_inactive")
        {
            projects.Failure = null;
            var ended = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                authority.RequireAsync(row.GrantId, default));
            Assert.Equal("issue_sync_grant_revoked", ended.Code);
            Assert.Equal(2, projects.Calls);
        }
    }

    [Fact]
    public async Task RevocationIsDurableAndNextCycleFailsBeforeProjectLookup()
    {
        await using var db = await CreateDbAsync();
        var proof = Proof();
        var row = Row(proof);
        db.IssueSyncGrants.Add(row);
        await db.SaveChangesAsync();
        var projects = new ProjectAuthority(proof);
        var authority = new IssueSyncGrantAuthority(db, projects, TimeProvider.System);
        await authority.RevokeAsync(row.GrantId, new(proof.ProjectId, proof.TenantId, proof.CreatorId, false), default);
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => authority.RequireAsync(row.GrantId, default));
        Assert.Equal("issue_sync_grant_revoked", error.Code);
        Assert.Equal(0, projects.Calls);
        db.ChangeTracker.Clear();
        Assert.False((await db.IssueSyncGrants.SingleAsync()).Active);
        Assert.Empty(await db.RuntimeGrantHeads.ToArrayAsync());
        Assert.Empty(await db.SecretGrantHeads.ToArrayAsync());
    }

    [Fact]
    public async Task ChangedRepositoryConnectionTenantOrCreatorNeverReusesGrant()
    {
        await using var db = await CreateDbAsync();
        var proof = Proof();
        var row = Row(proof);
        db.IssueSyncGrants.Add(row);
        await db.SaveChangesAsync();
        var projects = new ProjectAuthority(proof);
        var authority = new IssueSyncGrantAuthority(db, projects, TimeProvider.System);
        foreach (var changed in new[] {
            proof with { TenantId = "other" }, proof with { CreatorId = Guid.NewGuid().ToString("D") },
            proof with { ConnectionId = "other" }, proof with { Repository = new("octo", "other") }
        })
        {
            projects.Proof = changed;
            var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => authority.RequireAsync(row.GrantId, default));
            Assert.Equal("issue_sync_repository_not_selected", error.Code);
        }
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            authority.RevokeAsync(row.GrantId, new(proof.ProjectId, "other", proof.CreatorId, false), default));
        Assert.True((await db.IssueSyncGrants.AsNoTracking().SingleAsync()).Active);
    }

    [Fact]
    public async Task CreationRejectsUnknownOrConsumedSelectionAndNeverCreatesRunAuthority()
    {
        await using var db = await CreateDbAsync();
        var proof = Proof();
        var authority = new IssueSyncGrantAuthority(db, new ProjectAuthority(proof), TimeProvider.System);
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            authority.CreateAsync(proof, new string('a', 64), default));
        Assert.Equal("issue_sync_selection_invalid", error.Code);
        Assert.Empty(await db.IssueSyncGrants.ToArrayAsync());
        Assert.Empty(await db.RuntimeGrantHeads.ToArrayAsync());
    }

    [Fact]
    public async Task SelectionIsClaimedOnceAndNoUserTokenOrRunGrantIsStored()
    {
        await using var db = await CreateDbAsync();
        var proof = Proof();
        var code = new string('s', 64);
        var owner = Guid.Parse(proof.CreatorId);
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new() { Id = owner, Issuer = "https://identity.test/", Subject = owner.ToString(),
            CreatedAt = now });
        db.RepoAppConnections.Add(new()
        {
            ConnectionId = proof.ConnectionId, OwnerId = owner, GitHubLogin = "octo",
            AccessTokenSecretId = "access-reference", AccessTokenSecretVersion = "v1",
            AccessTokenExpiresAt = now.AddHours(1), RefreshTokenSecretId = "refresh-reference",
            RefreshTokenSecretVersion = "v1", RefreshTokenExpiresAt = now.AddDays(1),
            ConnectionRevision = 1, CredentialRevision = 1, State = RepoAppConnectionState.Connected,
            CreatedAt = now, UpdatedAt = now
        });
        db.RepoAppRepositorySelections.Add(new()
        {
            CodeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code))),
            OwnerId = owner, ConnectionId = proof.ConnectionId, ConnectionRevision = 1,
            InstallationId = 1, RepositoryId = 2, RepositoryFullName = proof.Repository.FullName,
            CreatedAt = now, ExpiresAt = now.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        var projects = new ProjectAuthority(proof);
        var authority = new IssueSyncGrantAuthority(db, projects, TimeProvider.System);
        var receipt = await authority.CreateAsync(proof, code, default);
        Assert.True(receipt.Active);
        Assert.Equal(proof, receipt.Project);
        Assert.NotEqual(Guid.Empty, receipt.GrantId);
        db.ChangeTracker.Clear();
        Assert.Single(await db.IssueSyncGrants.ToArrayAsync());
        Assert.Equal(proof.ProjectId, (await db.RepoAppRepositorySelections.SingleAsync()).ProjectId);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => authority.CreateAsync(proof, code, default));
        Assert.Empty(await db.RuntimeGrantHeads.ToArrayAsync());
        Assert.Empty(await db.SecretGrantHeads.ToArrayAsync());
    }

    [Fact]
    public async Task AnotherOwnerCannotRevokeButCurrentAdministratorCan()
    {
        await using var db = await CreateDbAsync();
        var proof = Proof();
        var grant = Row(proof);
        db.IssueSyncGrants.Add(grant);
        await db.SaveChangesAsync();
        var authority = new IssueSyncGrantAuthority(db, new ProjectAuthority(proof), TimeProvider.System);
        var actor = new IssueSyncRevocationProof(proof.ProjectId, proof.TenantId, Guid.NewGuid().ToString("D"), false);
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            authority.RevokeAsync(grant.GrantId, actor, default));
        Assert.Equal("issue_sync_grant_revocation_denied", error.Code);
        await authority.RevokeAsync(grant.GrantId, actor with { IsAdministrator = true }, default);
        Assert.False((await db.IssueSyncGrants.AsNoTracking().SingleAsync()).Active);
    }

    [Fact]
    public async Task MintAuditPersistsGrantOwnerAndRepositoryWithoutCredentials()
    {
        await using var db = await CreateDbAsync();
        var proof = Proof();
        var grant = Row(proof);
        var authority = new IssueSyncGrantAuthority(db, new ProjectAuthority(proof), TimeProvider.System);
        await authority.RecordMintAsync(grant, "requested", default);
        await authority.RecordMintAsync(grant, "minted", default);
        var rows = await db.IssueSyncMintAudit.OrderBy(row => row.CreatedAt).ToArrayAsync();
        Assert.Equal(["requested", "minted"], rows.Select(row => row.Outcome).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Equal(grant.GrantId, row.GrantId);
            Assert.Equal(grant.CreatorId, row.OwnerId);
            Assert.Equal(proof.Repository.FullName, row.Repository);
        });
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(IssueSyncMintAudit))!.GetProperties(),
            property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MigrationMatchesModelAndContainsOnlyGrantMetadata()
    {
        await using var db = await CreateDbAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261021000100_AddIssueSyncGrants", await db.Database.GetAppliedMigrationsAsync());
        var properties = db.Model.FindEntityType(typeof(IssueSyncGrant))!.GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(properties, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IdentityBrokerDbContext> CreateDbAsync()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync();
        return new(new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(connection, options => options.MigrationsHistoryTable("__ef_migrations_history", "identity_broker")).Options);
    }
    private static IssueSyncProjectProof Proof() =>
        new("project", "tenant", Guid.NewGuid().ToString("D"), "connection", new("octo", "widget"));
    private static IssueSyncGrant Row(IssueSyncProjectProof proof) => new()
    {
        GrantId = Guid.NewGuid(), ProjectId = proof.ProjectId, TenantId = proof.TenantId,
        CreatorId = Guid.Parse(proof.CreatorId), ConnectionId = proof.ConnectionId,
        RepositoryOwner = proof.Repository.Owner, RepositoryName = proof.Repository.Name,
        SelectionHash = new string('a', 64), Active = true, CreatedAt = DateTimeOffset.UtcNow
    };
    private sealed class ProjectAuthority(IssueSyncProjectProof proof) : IIssueSyncProjectAuthority
    {
        internal IssueSyncProjectProof Proof { get; set; } = proof;
        internal string? Failure { get; set; }
        internal int Calls { get; private set; }
        public Task<IssueSyncProjectProof> ReadAsync(IssueSyncAuthorityQuery query, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("project", query.ProjectId);
            Assert.Equal("tenant", query.TenantId);
            Assert.True(Guid.TryParse(query.CreatorId, out _));
            if (Failure is not null) throw new RuntimeAuthorizationException(Failure);
            return Task.FromResult(Proof);
        }
    }
}
