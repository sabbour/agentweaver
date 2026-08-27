using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class TwoAppPersistenceStoreTests
{
    [Fact]
    public async Task AuthorizationClaim_IsSingleUseAndExpiresInDatabasePredicate()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using (var setup = new MemoryDbContext(options))
        {
            await new TwoAppPersistenceStore(setup).AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = "state",
                AppKind = GitHubAppKind.Copilot,
                Purpose = GitHubAuthorizationPurpose.InteractiveCopilot,
                EntraObjectId = "entra",
                ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                ReturnRouteKey = "projects",
                PkceVerifierProtected = "protected",
                CallbackCookieHash = "hash",
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await using var first = new MemoryDbContext(options);
        await using var second = new MemoryDbContext(options);
        (await new TwoAppPersistenceStore(first).ClaimAuthorizationAsync("state", "entra", DateTimeOffset.UtcNow))
            .Should().Be(AuthorizationClaimResult.Claimed);
        (await new TwoAppPersistenceStore(second).ClaimAuthorizationAsync("state", "entra", DateTimeOffset.UtcNow))
            .Should().Be(AuthorizationClaimResult.Consumed);
    }

    [Fact]
    public async Task BindingReplacement_DeactivatesBeforeInsertAndLeavesOneActiveBinding()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var store = new TwoAppPersistenceStore(db);

        (await store.ReplaceCopilotBindingAsync(Binding("first"))).Should().Be(BindingWriteResult.Bound);
        (await store.ReplaceCopilotBindingAsync(Binding("second"))).Should().Be(BindingWriteResult.Bound);

        var bindings = await db.ProjectCopilotBindings.AsNoTracking().ToListAsync();
        bindings.Should().HaveCount(2);
        bindings.Count(x => x.Status == GitHubBindingStatus.Active).Should().Be(1);
        bindings.Single(x => x.Status == GitHubBindingStatus.Active).Id.Should().Be("second");
        bindings.Single(x => x.Id == "first").DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ActiveBindingUniqueIndexRejectsConcurrentInsert()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using (var first = new MemoryDbContext(options))
        {
            first.ProjectCopilotBindings.Add(Binding("first"));
            await first.SaveChangesAsync();
        }

        await using var second = new MemoryDbContext(options);
        second.ProjectCopilotBindings.Add(Binding("second"));
        var action = () => second.SaveChangesAsync();
        await action.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task SQLiteProjectDeletion_CascadesTwoAppRecords()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        (await new TwoAppPersistenceStore(db).ReplaceCopilotBindingAsync(Binding("binding")))
            .Should().Be(BindingWriteResult.Bound);

        db.Projects.Remove(new ProjectRecord { ProjectId = "project" });
        await db.SaveChangesAsync();
        (await db.ProjectCopilotBindings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InvocationClaim_UsesDatabaseUniqueConstraintInsteadOfPrecheck()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using (var db = new MemoryDbContext(options))
        {
            db.GitHubInstallations.Add(new GitHubInstallationRecord
            {
                InstallationId = 101,
                AppKind = GitHubAppKind.Repo,
                ProjectId = "project",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
            {
                InstallationId = 101,
                RepositoryId = 202,
                ProjectId = "project",
                FullNameDisplay = "owner/repository",
                PermissionDigest = "digest",
                GrantedAt = DateTimeOffset.UtcNow,
            });
            db.AutomationActivations.Add(new AutomationActivationRecord
            {
                Id = "activation",
                ProjectId = "project",
                InstallationId = 101,
                RepositoryId = 202,
                AutomationKey = "nightly",
                Status = AutomationActivationStatus.Active,
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var first = new MemoryDbContext(options);
        await using var second = new MemoryDbContext(options);
        (await new TwoAppPersistenceStore(first).ClaimInvocationAsync(Invocation("one"))).Should().Be(InvocationClaimResult.Claimed);
        (await new TwoAppPersistenceStore(second).ClaimInvocationAsync(Invocation("two"))).Should().Be(InvocationClaimResult.Duplicate);
    }

    [Fact]
    public async Task SnapshotIsWriteOnceAndFailsClosedOnCredentialRotation()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var store = new TwoAppPersistenceStore(db);
        var snapshot = new RunGitHubIdentitySnapshotRecord
        {
            RunId = "run",
            ProjectId = "project",
            AppKind = GitHubAppKind.Copilot,
            Purpose = GitHubAuthorizationPurpose.UnattendedCopilot,
            CredentialReference = "kv-copilot-project",
            CredentialVersion = "etag-1",
            GrantDigest = "digest",
            CapturedAt = DateTimeOffset.UtcNow,
        };

        (await store.AddRunIdentitySnapshotAsync(snapshot)).Should().BeTrue();
        (await store.AddRunIdentitySnapshotAsync(snapshot)).Should().BeFalse();
        (await store.HasPinnedSnapshotVersionAsync("run", "etag-1")).Should().BeTrue();
        (await store.HasPinnedSnapshotVersionAsync("run", "etag-2")).Should().BeFalse();
    }

    [Theory]
    [InlineData("ghu_sensitive")]
    [InlineData("github_pat_sensitive")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("eyJheader.payload.")]
    public async Task PersistenceBoundaryRejectsCredentialMaterial(string credential)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var binding = Binding("binding");
        binding.CredentialReference = credential;

        var action = () => new TwoAppPersistenceStore(db).ReplaceCopilotBindingAsync(binding);
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void AuditActorEnumCannotRepresentInternalPrincipal()
    {
        Enum.GetNames<GitHubAuditActorKind>().Should().BeEquivalentTo(
            [nameof(GitHubAuditActorKind.HumanEntraSubject), nameof(GitHubAuditActorKind.GitHubWebhook)]);
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MemoryDbContext(Options(connection));
        await db.Database.EnsureCreatedAsync();
        db.Projects.Add(Project("project"));
        await db.SaveChangesAsync();
        return connection;
    }

    private static DbContextOptions<MemoryDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;

    private static ProjectCopilotBindingRecord Binding(string id) => new()
    {
        Id = id,
        ProjectId = "project",
        EntraObjectId = "entra",
        CredentialReference = "kv-copilot-project",
        CredentialVersion = "version",
        GrantDigest = "digest",
        Status = GitHubBindingStatus.Active,
        BoundAt = DateTimeOffset.UtcNow,
    };

    private static AutomationInvocationRecord Invocation(string id) => new()
    {
        Id = id,
        ProjectId = "project",
        ActivationId = "activation",
        OccurrenceKey = "occurrence",
        DeliveryId = "delivery",
        EventName = "schedule",
        InstallationId = 101,
        RepositoryId = 202,
        Outcome = AutomationInvocationOutcome.Claimed,
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    private static ProjectRecord Project(string id) => new()
    {
        ProjectId = id,
        Name = "Project",
        OriginKind = "blank",
        WorkingDirectory = "C:\\project",
        Owner = "owner",
        DefaultProvider = "github_copilot",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
