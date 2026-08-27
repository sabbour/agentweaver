using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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
                ExternalTransactionId = TwoAppPersistenceStore.CreateExternalTransactionId(),
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
    public async Task ExternalTransactionHandle_IsDistinctSafeAndBoundToAppAndSubject()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var transactionId = TwoAppPersistenceStore.CreateExternalTransactionId();
        transactionId.Should().HaveLength(43);

        await using (var setup = new MemoryDbContext(options))
        {
            await new TwoAppPersistenceStore(setup).AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = "oauth-state-that-must-not-leak",
                ExternalTransactionId = transactionId,
                AppKind = GitHubAppKind.Repo,
                Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
                EntraObjectId = "entra-one",
                ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                ReturnRouteKey = "projects",
                PkceVerifierProtected = "protected",
                CallbackCookieHash = "hash",
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await using var db = new MemoryDbContext(options);
        var store = new TwoAppPersistenceStore(db);
        (await store.GetAuthorizationTransactionAsync(transactionId, GitHubAppKind.Repo, "entra-one"))
            .Should().Be(new GitHubAuthorizationTransactionHandle(
                transactionId,
                GitHubAppKind.Repo,
                DateTimeOffset.FromUnixTimeMilliseconds(
                    (await db.GitHubAuthorizations.SingleAsync()).ExpiresAtUnixMilliseconds),
                GitHubAuthorizationStatus.Pending));
        (await store.ClaimAuthorizationByTransactionIdAsync(
            transactionId, GitHubAppKind.Copilot, "entra-one", DateTimeOffset.UtcNow))
            .Should().Be(AuthorizationClaimResult.Invalid);
        (await store.ClaimAuthorizationByTransactionIdAsync(
            transactionId, GitHubAppKind.Repo, "entra-two", DateTimeOffset.UtcNow))
            .Should().Be(AuthorizationClaimResult.Invalid);
        (await store.ClaimAuthorizationByTransactionIdAsync(
            transactionId, GitHubAppKind.Repo, "entra-one", DateTimeOffset.UtcNow))
            .Should().Be(AuthorizationClaimResult.Claimed);

        var serialized = JsonSerializer.Serialize(await db.GitHubAuthorizations.SingleAsync());
        serialized.Should().NotContain("oauth-state-that-must-not-leak")
            .And.NotContain("protected")
            .And.NotContain("hash");
        JsonSerializer.Serialize((await store.GetAuthorizationTransactionAsync(
            transactionId, GitHubAppKind.Repo, "entra-one"))!).Should().Contain(transactionId);
    }

    [Fact]
    public async Task ExternalTransactionHandle_ReportsExpiredInsteadOfPending()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var transactionId = TwoAppPersistenceStore.CreateExternalTransactionId();
        await using (var setup = new MemoryDbContext(options))
        {
            await new TwoAppPersistenceStore(setup).AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = "expired-state",
                ExternalTransactionId = transactionId,
                AppKind = GitHubAppKind.Copilot,
                Purpose = GitHubAuthorizationPurpose.InteractiveCopilot,
                EntraObjectId = "entra",
                ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
                ReturnRouteKey = "projects",
                PkceVerifierProtected = "protected",
                CallbackCookieHash = "hash",
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await using var db = new MemoryDbContext(options);
        (await new TwoAppPersistenceStore(db).GetAuthorizationTransactionAsync(
            transactionId, GitHubAppKind.Copilot, "entra"))!.Status.Should().Be(GitHubAuthorizationStatus.Expired);
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
        (await new TwoAppPersistenceStore(first).ClaimInvocationAsync(Invocation("one", "delivery", "push"))).Should().Be(InvocationClaimResult.Claimed);
        (await new TwoAppPersistenceStore(second).ClaimInvocationAsync(Invocation("two", "delivery", "workflow_dispatch"))).Should().Be(InvocationClaimResult.Duplicate);
    }

    [Fact]
    public async Task LifecycleDeliveryClaim_RacesUseProviderUniqueDeliveryId()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var first = new MemoryDbContext(options);
        await using var second = new MemoryDbContext(options);

        (await new TwoAppPersistenceStore(first).ClaimLifecycleDeliveryAsync(LifecycleDelivery("delivery"))).Should()
            .Be(InvocationClaimResult.Claimed);
        (await new TwoAppPersistenceStore(second).ClaimLifecycleDeliveryAsync(LifecycleDelivery("delivery"))).Should()
            .Be(InvocationClaimResult.Duplicate);
    }

    [Fact]
    public async Task LifecycleDeliveryClaim_RequiresDeliveryId()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));

        var action = () => new TwoAppPersistenceStore(db).ClaimLifecycleDeliveryAsync(LifecycleDelivery(""));
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SnapshotPinsStableGrantIdentityNotRotatingCredentialReference()
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
            CredentialReference = "kv-copilot-grant-revision-a",
            CredentialVersion = "grant-identity-1",
            GrantDigest = "digest",
            CapturedAt = DateTimeOffset.UtcNow,
        };

        (await store.AddRunIdentitySnapshotAsync(snapshot)).Should().BeTrue();
        (await store.AddRunIdentitySnapshotAsync(snapshot)).Should().BeFalse();
        (await store.HasPinnedSnapshotVersionAsync("run", "grant-identity-1")).Should().BeTrue();
        (await store.HasPinnedSnapshotVersionAsync("run", "grant-identity-2")).Should().BeFalse();
    }

    [Fact]
    public async Task CapabilitySnapshots_KeepDistinctOpaqueReferencesForEveryPurpose()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        await SeedCapabilitySourcesAsync(db);
        var store = new TwoAppPersistenceStore(db);
        var snapshots = Enum.GetValues<GitHubCapabilityPurpose>()
        .Select(CapabilitySnapshot)
        .ToArray();

        foreach (var snapshot in snapshots)
        (await store.TryInsertCapabilitySnapshotAsync(snapshot)).Should().BeTrue();

        snapshots.Select(x => x.SnapshotRef).Should().OnlyHaveUniqueItems();
        for (var requested = 0; requested < snapshots.Length; requested++)
        for (var stored = 0; stored < snapshots.Length; stored++)
        {
        var fence = await store.TryFenceLiveSnapshotAsync(
            (GitHubCapabilityPurpose)requested,
            new SnapshotRef(snapshots[stored].SnapshotRef),
            DateTimeOffset.UtcNow);
        if (requested == stored)
            fence.Should().NotBeNull();
        else
            fence.Should().BeNull();
        }

        var serialized = JsonSerializer.Serialize(await store.TryFenceLiveSnapshotAsync(
            GitHubCapabilityPurpose.InteractiveRepository,
            new SnapshotRef(snapshots[0].SnapshotRef),
            DateTimeOffset.UtcNow));
        serialized.Should().NotContain("repo-app-user-credential-version")
        .And.NotContain("copilot-app-project-project-version");
    }

    [Fact]
    public async Task CapabilitySnapshotFence_FailsClosedForExpiredRevokedAndRotatedSources()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        await SeedCapabilitySourcesAsync(db);
        var store = new TwoAppPersistenceStore(db);
        var snapshot = CapabilitySnapshot(GitHubCapabilityPurpose.InteractiveRepository);
        (await store.TryInsertCapabilitySnapshotAsync(snapshot)).Should().BeTrue();
        var reference = new SnapshotRef(snapshot.SnapshotRef);

        (await store.TryFenceLiveSnapshotAsync(snapshot.Purpose, reference, DateTimeOffset.UtcNow)).Should().NotBeNull();
        await db.GitHubAppAuthorizations
        .Where(x => x.Id == "authorization")
        .ExecuteUpdateAsync(x => x.SetProperty(y => y.CredentialVersion, "rotated-version"));
        (await store.TryFenceLiveSnapshotAsync(snapshot.Purpose, reference, DateTimeOffset.UtcNow)).Should().BeNull();

        db.ChangeTracker.Clear();
        db.GitHubAppAuthorizations.Single(x => x.Id == "authorization").CredentialVersion = "version";
        db.GitHubAppAuthorizations.Single(x => x.Id == "authorization").RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        (await store.TryFenceLiveSnapshotAsync(snapshot.Purpose, reference, DateTimeOffset.UtcNow)).Should().BeNull();

        var expired = CapabilitySnapshot(GitHubCapabilityPurpose.UnattendedCopilot);
        expired.SnapshotRef = SnapshotRef.Create().Value;
        expired.SnapshotExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        (await store.TryInsertCapabilitySnapshotAsync(expired)).Should().BeTrue();
        (await store.TryFenceLiveSnapshotAsync(expired.Purpose, new SnapshotRef(expired.SnapshotRef), DateTimeOffset.UtcNow))
        .Should().BeNull();
    }

    [Fact]
    public async Task InteractiveRepositorySnapshotFence_FailsWhenCanonicalRepositoryScopeIsRevoked()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        await SeedCapabilitySourcesAsync(db);
        var store = new TwoAppPersistenceStore(db);
        var snapshot = CapabilitySnapshot(GitHubCapabilityPurpose.InteractiveRepository);
        (await store.TryInsertCapabilitySnapshotAsync(snapshot)).Should().BeTrue();
        var reference = new SnapshotRef(snapshot.SnapshotRef);

        (await store.TryFenceLiveSnapshotAsync(snapshot.Purpose, reference, DateTimeOffset.UtcNow)).Should().NotBeNull();
        var revokedAt = DateTimeOffset.UtcNow;
        await db.GitHubRepositoryGrants
        .Where(x => x.InstallationId == 101 && x.RepositoryId == 202)
        .ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAt, revokedAt));

        (await store.TryFenceLiveSnapshotAsync(snapshot.Purpose, reference, DateTimeOffset.UtcNow)).Should().BeNull();
    }

    [Fact]
    public async Task CapabilitySnapshotInsertAndBackfill_RejectDuplicatesAndNeverResolveReplacementSources()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        await SeedCapabilitySourcesAsync(db);
        var store = new TwoAppPersistenceStore(db);
        var snapshot = CapabilitySnapshot(GitHubCapabilityPurpose.UnattendedCopilot);

        (await store.TryInsertCapabilitySnapshotAsync(snapshot)).Should().BeTrue();
        (await store.TryInsertCapabilitySnapshotAsync(snapshot)).Should().BeFalse();

        db.RunGitHubIdentitySnapshots.Add(new RunGitHubIdentitySnapshotRecord
        {
        RunId = "legacy-run",
        ProjectId = "project",
        AppKind = GitHubAppKind.Copilot,
        Purpose = GitHubAuthorizationPurpose.UnattendedCopilot,
        CredentialReference = "copilot-app-project-project-replaced",
        CredentialVersion = "replaced",
        GrantDigest = "replaced-digest",
        CapturedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        (await store.BackfillCapabilitySnapshotsAsync()).Should().Be(new CapabilitySnapshotBackfillResult(0, 1));
        (await db.RunGitHubCapabilitySnapshots.CountAsync(x => x.RunId == "legacy-run")).Should().Be(0);
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

    private static AutomationInvocationRecord Invocation(string id, string deliveryId, string eventName) => new()
    {
        Id = id,
        ProjectId = "project",
        ActivationId = "activation",
        OccurrenceKey = $"occurrence-{id}",
        DeliveryId = deliveryId,
        EventName = eventName,
        InstallationId = 101,
        RepositoryId = 202,
        Outcome = AutomationInvocationOutcome.Claimed,
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    private static GitHubLifecycleDeliveryRecord LifecycleDelivery(string deliveryId) => new()
    {
        DeliveryId = deliveryId,
        EventName = "installation",
        InstallationId = 101,
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

    private static async Task SeedCapabilitySourcesAsync(MemoryDbContext db)
    {
        db.GitHubAppAuthorizations.Add(new GitHubAppAuthorizationRecord
        {
            Id = "authorization",
            EntraObjectId = "entra",
            AppKind = GitHubAppKind.Repo,
            Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
            CredentialReference = "repo-app-user-credential-version",
            CredentialVersion = "version",
            GrantDigest = "user-digest",
            CreatedAt = DateTimeOffset.UtcNow,
        });
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
            PermissionDigest = "repository-digest",
            GrantedAt = DateTimeOffset.UtcNow,
        });
        db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = "binding",
            ProjectId = "project",
            EntraObjectId = "entra",
            CredentialReference = "copilot-app-project-project-version",
            CredentialVersion = "version",
            GrantDigest = "copilot-digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static RunGitHubCapabilitySnapshotRecord CapabilitySnapshot(GitHubCapabilityPurpose purpose) =>
        purpose switch
        {
            GitHubCapabilityPurpose.InteractiveRepository => new()
            {
                SnapshotRef = SnapshotRef.Create().Value, RunId = "run", Purpose = purpose, AppKind = GitHubAppKind.Repo,
                SourceKind = GitHubCapabilitySnapshotSourceKind.UserAuthorization, ProjectId = "project",
                EntraObjectId = "entra", SourceAuthorizationId = "authorization", RepositoryId = 202,
                CredentialReference = "repo-app-user-credential-version", CredentialVersion = "version",
                GrantDigest = "user-digest", CapturedAt = DateTimeOffset.UtcNow,
            },
            GitHubCapabilityPurpose.InteractiveCopilot => new()
            {
                SnapshotRef = SnapshotRef.Create().Value, RunId = "run", Purpose = purpose, AppKind = GitHubAppKind.Repo,
                SourceKind = GitHubCapabilitySnapshotSourceKind.UserAuthorization, ProjectId = "project",
                EntraObjectId = "entra", SourceAuthorizationId = "authorization",
                CredentialReference = "repo-app-user-credential-version", CredentialVersion = "version",
                GrantDigest = "user-digest", CapturedAt = DateTimeOffset.UtcNow,
            },
            GitHubCapabilityPurpose.UnattendedRepository => new()
            {
                SnapshotRef = SnapshotRef.Create().Value, RunId = "run", Purpose = purpose, AppKind = GitHubAppKind.Repo,
                SourceKind = GitHubCapabilitySnapshotSourceKind.RepositoryGrant, ProjectId = "project",
                InstallationId = 101, RepositoryId = 202, GrantDigest = "repository-digest", CapturedAt = DateTimeOffset.UtcNow,
            },
            GitHubCapabilityPurpose.UnattendedCopilot => new()
            {
                SnapshotRef = SnapshotRef.Create().Value, RunId = "run", Purpose = purpose, AppKind = GitHubAppKind.Copilot,
                SourceKind = GitHubCapabilitySnapshotSourceKind.CopilotBinding, ProjectId = "project",
                SourceBindingId = "binding", CredentialReference = "copilot-app-project-project-version",
                CredentialVersion = "version", GrantDigest = "copilot-digest", CapturedAt = DateTimeOffset.UtcNow,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };
}
