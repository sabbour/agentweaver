using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Api.Skills;
using Agentweaver.Api.Webhooks;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Agentweaver.Tests.Auth;

public sealed class GitHubConnectionsPersistenceStoreTests
{
    [Fact]
    public async Task AuthorizationClaim_IsSingleUseAndExpiresInDatabasePredicate()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using (var setup = new MemoryDbContext(options))
        {
            await new GitHubConnectionsPersistenceStore(setup).AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = "state",
                ExternalTransactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId(),
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
        (await new GitHubConnectionsPersistenceStore(first).ClaimAuthorizationAsync("state", "entra", DateTimeOffset.UtcNow))
            .Should().Be(AuthorizationClaimResult.Claimed);
        (await new GitHubConnectionsPersistenceStore(second).ClaimAuthorizationAsync("state", "entra", DateTimeOffset.UtcNow))
            .Should().Be(AuthorizationClaimResult.Consumed);
    }

    [Fact]
    public async Task ExternalTransactionHandle_IsDistinctSafeAndBoundToAppAndSubject()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var transactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId();
        transactionId.Should().HaveLength(43);

        await using (var setup = new MemoryDbContext(options))
        {
            await new GitHubConnectionsPersistenceStore(setup).AddAuthorizationAsync(new GitHubAuthorizationRecord
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
        var store = new GitHubConnectionsPersistenceStore(db);
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
        var transactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId();
        await using (var setup = new MemoryDbContext(options))
        {
            await new GitHubConnectionsPersistenceStore(setup).AddAuthorizationAsync(new GitHubAuthorizationRecord
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
        (await new GitHubConnectionsPersistenceStore(db).GetAuthorizationTransactionAsync(
            transactionId, GitHubAppKind.Copilot, "entra"))!.Status.Should().Be(GitHubAuthorizationStatus.Expired);
    }

    [Fact]
    public async Task BindingReplacement_DeactivatesBeforeInsertAndLeavesOneActiveBinding()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var store = new GitHubConnectionsPersistenceStore(db);

        (await store.ReplaceCopilotBindingAsync(Binding("first"))).Should().Be(BindingWriteResult.Bound);
        (await store.ReplaceCopilotBindingAsync(Binding("second"))).Should().Be(BindingWriteResult.Bound);

        var bindings = await db.ProjectCopilotBindings.AsNoTracking().ToListAsync();
        bindings.Should().HaveCount(2);
        bindings.Count(x => x.Status == GitHubBindingStatus.Active).Should().Be(1);
        bindings.Single(x => x.Status == GitHubBindingStatus.Active).Id.Should().Be("second");
        bindings.Single(x => x.Id == "first").DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PlatformDefaultBindingReplacement_UpsertsTheSingletonRow()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var store = new GitHubConnectionsPersistenceStore(db);

        (await store.ReplacePlatformDefaultCopilotBindingAsync(PlatformBinding("first"))).Should().Be(BindingWriteResult.Bound);
        (await store.ReplacePlatformDefaultCopilotBindingAsync(PlatformBinding("second"))).Should().Be(BindingWriteResult.Bound);

        var bindings = await db.PlatformDefaultCopilotBindings.AsNoTracking().ToListAsync();
        bindings.Should().HaveCount(1);
        bindings.Single().CredentialReference.Should().Be("kv-copilot-platform-second");
        bindings.Single().Status.Should().Be(GitHubBindingStatus.Active);
    }

    [Fact]
    public async Task PlatformDefaultBinding_RejectsNonSingletonId()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));

        var action = () => new GitHubConnectionsPersistenceStore(db).ReplacePlatformDefaultCopilotBindingAsync(new PlatformDefaultCopilotBindingRecord
        {
            Id = "not-platform-default",
            EntraObjectId = "entra",
            CredentialReference = "kv-copilot-platform",
            CredentialVersion = "version",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CompletePlatformDefaultAuthorization_InvalidatesActiveAutomationActivations()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var store = new GitHubConnectionsPersistenceStore(db);
        db.GitHubAuthorizations.Add(new GitHubAuthorizationRecord
        {
            State = "platform-state",
            ExternalTransactionId = "tx-platform",
            AppKind = GitHubAppKind.Copilot,
            Purpose = GitHubAuthorizationPurpose.PlatformDefaultCopilot,
            EntraObjectId = "entra",
            ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            ReturnRouteKey = "platform-settings",
            PkceVerifierProtected = "pkce",
            CallbackCookieHash = "cookie",
            Status = GitHubAuthorizationStatus.Redeeming,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.PlatformDefaultCopilotBindings.Add(PlatformBinding("old"));
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 1,
            AppKind = GitHubAppKind.Repo,
            ProjectId = "project",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 1,
            RepositoryId = 2,
            ProjectId = "project",
            FullNameDisplay = "owner/repository",
            PermissionDigest = "repo-digest",
            GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        db.AutomationActivations.Add(new AutomationActivationRecord
        {
            Id = "activation-platform",
            ProjectId = "project",
            InstallationId = 1,
            RepositoryId = 2,
            RepositoryGrantDigest = "repo-digest",
            CopilotBindingId = PlatformDefaultCopilotBindingRecord.SingletonId,
            CopilotBindingGrantDigest = PlatformBinding("old").GrantDigest,
            AutomationKey = "internal-activation-snapshot",
            Status = AutomationActivationStatus.Active,
            ActivatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var completed = await store.CompletePlatformDefaultCopilotAuthorizationAsync(
            "platform-state",
            PlatformBinding("new"),
            Audit(),
            CancellationToken.None);

        completed.Completed.Should().BeTrue();
        db.ChangeTracker.Clear();
        var activation = await db.AutomationActivations.SingleAsync();
        activation.Status.Should().Be(AutomationActivationStatus.Invalidated);
        activation.InvalidatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokePlatformDefaultBinding_InvalidatesActiveAutomationActivations()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var store = new GitHubConnectionsPersistenceStore(db);
        db.PlatformDefaultCopilotBindings.Add(PlatformBinding("active"));
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 1,
            AppKind = GitHubAppKind.Repo,
            ProjectId = "project",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 1,
            RepositoryId = 2,
            ProjectId = "project",
            FullNameDisplay = "owner/repository",
            PermissionDigest = "repo-digest",
            GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        db.AutomationActivations.Add(new AutomationActivationRecord
        {
            Id = "activation-platform",
            ProjectId = "project",
            InstallationId = 1,
            RepositoryId = 2,
            RepositoryGrantDigest = "repo-digest",
            CopilotBindingId = PlatformDefaultCopilotBindingRecord.SingletonId,
            CopilotBindingGrantDigest = PlatformBinding("active").GrantDigest,
            AutomationKey = "internal-activation-snapshot",
            Status = AutomationActivationStatus.Active,
            ActivatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var revoked = await store.RevokePlatformDefaultCopilotBindingAsync(Audit(), CancellationToken.None);

        revoked.Should().NotBeNull();
        db.ChangeTracker.Clear();
        var activation = await db.AutomationActivations.SingleAsync();
        activation.Status.Should().Be(AutomationActivationStatus.Invalidated);
        activation.InvalidatedAt.Should().NotBeNull();
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
    public async Task SQLiteProjectDeletion_CascadesGitHubConnectionsRecords()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        (await new GitHubConnectionsPersistenceStore(db).ReplaceCopilotBindingAsync(Binding("binding")))
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
                RepositoryGrantDigest = "digest",
                CopilotBindingId = "binding",
                CopilotBindingGrantDigest = "binding-digest",
                ModelProviderSource = AutomationModelProviderSource.GitHubCopilot,
                AutomationKey = "nightly",
                Status = AutomationActivationStatus.Active,
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var first = new MemoryDbContext(options);
        await using var second = new MemoryDbContext(options);
        (await new GitHubConnectionsPersistenceStore(first).ClaimInvocationAsync(Invocation("one", "delivery", "push"))).Should().Be(InvocationClaimResult.Claimed);
        (await new GitHubConnectionsPersistenceStore(second).ClaimInvocationAsync(Invocation("two", "delivery", "workflow_dispatch"))).Should().Be(InvocationClaimResult.Duplicate);
    }

    [Fact]
    public async Task LifecycleDeliveryClaim_RacesUseProviderUniqueDeliveryId()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var first = new MemoryDbContext(options);
        await using var second = new MemoryDbContext(options);

        (await new GitHubConnectionsPersistenceStore(first).ClaimLifecycleDeliveryAsync(LifecycleDelivery("delivery"))).Should()
            .Be(InvocationClaimResult.Claimed);
        (await new GitHubConnectionsPersistenceStore(second).ClaimLifecycleDeliveryAsync(LifecycleDelivery("delivery"))).Should()
            .Be(InvocationClaimResult.Duplicate);
    }

    [Fact]
    public async Task LifecycleDeliveryClaim_RequiresDeliveryId()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));

        var action = () => new GitHubConnectionsPersistenceStore(db).ClaimLifecycleDeliveryAsync(LifecycleDelivery(""));
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SnapshotPinsStableGrantIdentityNotRotatingCredentialReference()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var store = new GitHubConnectionsPersistenceStore(db);
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
        var store = new GitHubConnectionsPersistenceStore(db);
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
        var store = new GitHubConnectionsPersistenceStore(db);
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
    public async Task InteractiveRepositorySnapshotFence_DoesNotRequireAnInstallationOrRepositoryGrant()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        await SeedCapabilitySourcesAsync(db);
        var store = new GitHubConnectionsPersistenceStore(db);
        var snapshot = CapabilitySnapshot(GitHubCapabilityPurpose.InteractiveRepository);
        (await store.TryInsertCapabilitySnapshotAsync(snapshot)).Should().BeTrue();
        var reference = new SnapshotRef(snapshot.SnapshotRef);

        (await store.TryFenceLiveSnapshotAsync(snapshot.Purpose, reference, DateTimeOffset.UtcNow)).Should().NotBeNull();
        await db.GitHubRepositoryGrants.ExecuteDeleteAsync();
        await db.GitHubInstallations.ExecuteDeleteAsync();

        (await store.TryFenceLiveSnapshotAsync(snapshot.Purpose, reference, DateTimeOffset.UtcNow)).Should().NotBeNull();
    }

    [Fact]
    public async Task CapabilitySnapshotInsertAndBackfill_RejectDuplicatesAndNeverResolveReplacementSources()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        await SeedCapabilitySourcesAsync(db);
        var store = new GitHubConnectionsPersistenceStore(db);
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

    [Fact]
    public async Task CapabilitySnapshotLifecycle_CapturesRootAndInheritsChildRetryAndResumeWithoutFallback()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        // Only live v2-authoritative sources are seeded here: no v1 RunGitHubIdentitySnapshotRecord
        // is ever inserted. Production never populates that legacy table, so a test that relies on
        // it does not exercise the real trusted root-construction seam.
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var root = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeTrue();
        var rootSnapshots = await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString());
        rootSnapshots.Select(snapshot => snapshot.Purpose).Should().BeEquivalentTo(
        [
            GitHubCapabilityPurpose.UnattendedRepository,
            GitHubCapabilityPurpose.UnattendedCopilot,
        ]);

        var child = RunForSnapshotLifecycle(projectId) with { ParentRunId = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(child, CancellationToken.None)).Should().BeTrue();
        var childSnapshots = await persistence.GetCapabilitySnapshotsAsync(child.Id.ToString());
        childSnapshots.Should().HaveCount(rootSnapshots.Count);
        foreach (var rootSnapshot in rootSnapshots)
        {
            var childSnapshot = childSnapshots.Single(snapshot => snapshot.Purpose == rootSnapshot.Purpose);
            childSnapshot.SnapshotRef.Should().NotBe(rootSnapshot.SnapshotRef);
            childSnapshot.SourceAuthorizationId.Should().Be(rootSnapshot.SourceAuthorizationId);
            childSnapshot.GrantDigest.Should().Be(rootSnapshot.GrantDigest);
        }

        var retry = RunForSnapshotLifecycle(projectId) with { RetriedFrom = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(retry, CancellationToken.None)).Should().BeTrue();
        var retrySnapshots = await persistence.GetCapabilitySnapshotsAsync(retry.Id.ToString());
        retrySnapshots.Should().HaveCount(rootSnapshots.Count);
        foreach (var rootSnapshot in rootSnapshots)
        {
            var retrySnapshot = retrySnapshots.Single(snapshot => snapshot.Purpose == rootSnapshot.Purpose);
            retrySnapshot.SnapshotRef.Should().NotBe(rootSnapshot.SnapshotRef);
            retrySnapshot.GrantDigest.Should().Be(rootSnapshot.GrantDigest);
        }

        // Recovery (resume) revisits the SAME run: already-captured snapshots are re-fenced, not
        // recaptured or duplicated.
        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeTrue();
        var rootSnapshotsAfterResume = await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString());
        rootSnapshotsAfterResume.Select(snapshot => snapshot.SnapshotRef).Should().BeEquivalentTo(
            rootSnapshots.Select(snapshot => snapshot.SnapshotRef));

        var revokedAt = DateTimeOffset.UtcNow;
        // Revoking the underlying repository installation (rather than the now-unused Interactive
        // repo-app-user authorization) is what makes the persisted UnattendedRepository snapshot
        // re-fence as unavailable for root/child/retry alike.
        await db.GitHubInstallations
            .Where(installation => installation.InstallationId == 101)
            .ExecuteUpdateAsync(update => update.SetProperty(installation => installation.RevokedAt, revokedAt));
        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
        (await lifecycle.PrepareForLaunchAsync(child, CancellationToken.None)).Should().BeFalse();
        (await lifecycle.PrepareForLaunchAsync(retry, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_RootChildRetryAndResumeSucceedWithZeroSnapshotsForBlankOriginProject()
    {
        // A project whose persisted origin is explicitly blank legitimately requires zero
        // capability snapshots: launches must not be denied for projects that simply don't use
        // GitHub. This is decided purely by the persisted origin, not by absence of history rows.
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        var projectStore = new FakeProjectStore();
        projectStore.Seed(BlankDomainProject(projectId));
        var persistence = new GitHubConnectionsPersistenceStore(db, projectStore);
        var lifecycle = CreateLifecycle(db, persistence);
        var root = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeTrue();
        (await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString())).Should().BeEmpty();

        var child = RunForSnapshotLifecycle(projectId) with { ParentRunId = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(child, CancellationToken.None)).Should().BeTrue();
        (await persistence.GetCapabilitySnapshotsAsync(child.Id.ToString())).Should().BeEmpty();

        var retry = RunForSnapshotLifecycle(projectId) with { RetriedFrom = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(retry, CancellationToken.None)).Should().BeTrue();
        (await persistence.GetCapabilitySnapshotsAsync(retry.Id.ToString())).Should().BeEmpty();

        // Recovery/resume of the same blank-origin root re-attempts root construction and still
        // legitimately succeeds with zero snapshots.
        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_AgentHostRequiresFencedUnattendedCopilotSnapshot()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        await db.ProjectCopilotBindings.ExecuteDeleteAsync();

        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var run = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(run, CancellationToken.None)).Should().BeTrue(
            "other valid snapshots may still be captured for an interactive run");
        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeFalse(
            "AgentHost /configure redeems the unattended Copilot capability and cannot use an ambient or partial fallback");
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_AgentHostRejectsMissingProjectCredentialBeforeLaunch()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var secrets = new SeededCopilotCredentialStore();
        await secrets.DeleteSecretAsync("copilot-app-project-project-version");
        var lifecycle = CreateLifecycle(db, persistence, secrets);
        var run = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeFalse(
            "binding metadata alone cannot prove that the project credential is redeemable");
        var snapshots = await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString());
        snapshots.Should().ContainSingle(snapshot =>
            snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot &&
            snapshot.SourceBindingId == "binding");
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_ProjectCredentialDeletionAfterPreflightFailsClosedWithoutPlatformFallback()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "platform-admin",
            CredentialReference = "copilot-app-platform-default-version",
            CredentialVersion = "platform-version",
            GrantDigest = "platform-digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var persistence = new GitHubConnectionsPersistenceStore(db);
        var secrets = new SeededCopilotCredentialStore();
        var lifecycle = CreateLifecycle(db, persistence, secrets);
        var run = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeTrue();
        var snapshot = (await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString()))
            .Single(candidate => candidate.Purpose == GitHubCapabilityPurpose.UnattendedCopilot);
        snapshot.SourceBindingId.Should().Be("binding");

        await secrets.DeleteSecretAsync("copilot-app-project-project-version");

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeFalse(
            "late credential deletion must not switch the immutable project snapshot to the platform binding");
        var snapshotAfterDeletion = (await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString()))
            .Single(candidate => candidate.Purpose == GitHubCapabilityPurpose.UnattendedCopilot);
        snapshotAfterDeletion.SnapshotRef.Should().Be(snapshot.SnapshotRef);
        snapshotAfterDeletion.SourceBindingId.Should().Be("binding");
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_AgentHostFallsBackToPlatformDefaultCopilotBinding()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        await db.ProjectCopilotBindings.ExecuteDeleteAsync();
        db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "platform-admin",
            CredentialReference = "copilot-app-platform-default-version",
            CredentialVersion = "platform-version",
            GrantDigest = "platform-digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var run = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeTrue();
        var snapshots = await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString());
        snapshots.Should().ContainSingle(snapshot =>
            snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot &&
            snapshot.SourceBindingId == PlatformDefaultCopilotBindingRecord.SingletonId &&
            snapshot.CredentialReference == "copilot-app-platform-default-version" &&
            snapshot.CredentialVersion == "platform-version" &&
            snapshot.GrantDigest == "platform-digest");

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeTrue(
            "resume must continue accepting a live platform-default binding snapshot");

        var revokedAt = DateTimeOffset.UtcNow;
        await db.PlatformDefaultCopilotBindings
            .Where(binding => binding.Id == PlatformDefaultCopilotBindingRecord.SingletonId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(binding => binding.Status, GitHubBindingStatus.Revoked)
                .SetProperty(binding => binding.DeactivatedAt, revokedAt));

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_PlatformScopedIgnoresIncidentalProjectCopilotBinding()
    {
        // Regression for personal/Operator ("Assistant") sessions: the run carries an INCIDENTAL
        // ProjectId (the project the caller happened to be viewing), and that project's OWN Copilot
        // binding is Active in the database — the exact production scenario from issue/PR #1116,
        // where the project's binding row was Active but its backing Key Vault secret was missing.
        // With platformScoped: true, credential resolution must go straight to the PLATFORM-level
        // Copilot connection and never touch (or require) the project's own binding at all.
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "platform-admin",
            CredentialReference = "copilot-app-platform-default-version",
            CredentialVersion = "platform-version",
            GrantDigest = "platform-digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var run = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None, platformScoped: true))
            .Should().BeTrue("a personal Operator/Assistant session must resolve via the platform connection " +
                "even though the run's incidental ProjectId has its own project-scoped binding");
        var snapshots = await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString());
        snapshots.Should().ContainSingle(snapshot =>
            snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot &&
            snapshot.SourceBindingId == PlatformDefaultCopilotBindingRecord.SingletonId &&
            snapshot.ProjectId == null &&
            snapshot.CredentialReference == "copilot-app-platform-default-version" &&
            snapshot.CredentialVersion == "platform-version" &&
            snapshot.GrantDigest == "platform-digest",
            "the platform binding must be used, not the project's own (incidental) Copilot binding");

        // Now simulate the confirmed production defect: the project's own Copilot binding is deleted
        // entirely (equivalent to "Active row but unusable"). A personal session must still launch,
        // because it never depended on the project's binding in the first place.
        await db.ProjectCopilotBindings.ExecuteDeleteAsync();
        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None, platformScoped: true))
            .Should().BeTrue("resume must continue accepting the platform snapshot regardless of the " +
                "project's own binding state");
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_AgentHostAllowsProjectlessRunWithPlatformDefaultCopilotBinding()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "platform-admin",
            CredentialReference = "copilot-app-platform-default-version",
            CredentialVersion = "platform-version",
            GrantDigest = "platform-digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var run = RunForSnapshotLifecycle(projectId: null);

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeTrue();
        var snapshots = await persistence.GetCapabilitySnapshotsAsync(run.Id.ToString());
        snapshots.Should().ContainSingle(snapshot =>
            snapshot.Purpose == GitHubCapabilityPurpose.UnattendedCopilot &&
            snapshot.SourceBindingId == PlatformDefaultCopilotBindingRecord.SingletonId &&
            snapshot.ProjectId == null &&
            snapshot.CredentialReference == "copilot-app-platform-default-version" &&
            snapshot.CredentialVersion == "platform-version" &&
            snapshot.GrantDigest == "platform-digest");

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeTrue(
            "resume must continue accepting the existing project-less platform-default Copilot snapshot");

        var revokedAt = DateTimeOffset.UtcNow;
        await db.PlatformDefaultCopilotBindings
            .Where(binding => binding.Id == PlatformDefaultCopilotBindingRecord.SingletonId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(binding => binding.Status, GitHubBindingStatus.Revoked)
                .SetProperty(binding => binding.DeactivatedAt, revokedAt));

        (await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_RootChildRetryAndResumeDenyWhenGitHubOriginProjectHasNoHistoryAtAll()
    {
        // Smith's proven defect: a GitHub-origin project that has never recorded ANY GitHub App
        // installation, repository grant, or Copilot binding row must still fail closed rather than
        // be classified as blank. History-record absence is not an authoritative "intentionally
        // non-GitHub" signal; only the persisted project origin is.
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        var projectStore = new FakeProjectStore();
        projectStore.Seed(GitHubOriginDomainProject(projectId));
        var persistence = new GitHubConnectionsPersistenceStore(db, projectStore);
        var lifecycle = CreateLifecycle(db, persistence);
        var root = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString())).Should().BeEmpty();

        var child = RunForSnapshotLifecycle(projectId) with { ParentRunId = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(child, CancellationToken.None)).Should().BeFalse();

        var retry = RunForSnapshotLifecycle(projectId) with { RetriedFrom = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(retry, CancellationToken.None)).Should().BeFalse();

        // Recovery/resume of the same denied root re-attempts root construction and still denies.
        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_RootChildRetryAndResumeDenyWhenGitHubOriginProjectHasHistoryButNoLiveSource()
    {
        // The GitHub-origin project has recorded GitHub App history (a now-revoked installation)
        // but currently resolves neither the unattended-repository nor unattended-Copilot purpose:
        // this must fail closed, not silently launch with zero capability protection.
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        var projectStore = new FakeProjectStore();
        projectStore.Seed(GitHubOriginDomainProject(projectId));
        var persistence = new GitHubConnectionsPersistenceStore(db, projectStore);
        // github_installations has an EF FK to the companion "projects" table (referential
        // integrity only; see the remarks on GitHubConnectionsPersistenceStore) — seed it too, or the insert
        // below fails a FK check. Origin classification itself reads only projectStore above.
        db.Projects.Add(Project(projectId.ToString()));
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 909,
            AppKind = GitHubAppKind.Repo,
            ProjectId = projectId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var lifecycle = CreateLifecycle(db, persistence);
        var root = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString())).Should().BeEmpty();

        // A run whose parent/retry source never captured anything (the root above was denied, but
        // an inherit call can also be reached directly, e.g. from data that predates this fix) must
        // also deny rather than silently launch with no capability protection.
        var child = RunForSnapshotLifecycle(projectId) with { ParentRunId = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(child, CancellationToken.None)).Should().BeFalse();

        var retry = RunForSnapshotLifecycle(projectId) with { RetriedFrom = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(retry, CancellationToken.None)).Should().BeFalse();

        // Recovery/resume of the same denied root re-attempts root construction and still denies.
        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_RootDeniesWhenProjectRecordIsMissing()
    {
        // A run's project cannot be found at all: origin cannot be proven, so the project must not
        // be treated as blank. Fail closed rather than silently launch with zero snapshots.
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var persistence = new GitHubConnectionsPersistenceStore(db, new FakeProjectStore());
        var lifecycle = CreateLifecycle(db, persistence);
        var projectId = ProjectId.New();
        var root = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString())).Should().BeEmpty();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_RootChildRetryAndResumeDenyWhenProjectIdIsMissing()
    {
        // Smith's proven authorization bypass: Run.ProjectId is nullable, and a null/missing
        // project id can never prove a project's persisted origin is intentionally blank. Root
        // construction and child/retry inheritance must both fail closed for every lifecycle path
        // (root, child, retry, resume) rather than silently succeed with zero capability
        // snapshots — regardless of whether any real capability sources exist.
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        // Seed live capability sources for the "project" id used by OpenDatabaseAsync so a failure
        // here can only be attributed to the missing/null ProjectId on the Run itself, not to an
        // absence of live sources.
        await SeedCapabilitySourcesAsync(db);
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var root = RunForSnapshotLifecycle(projectId: null);

        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString())).Should().BeEmpty();

        var child = RunForSnapshotLifecycle(projectId: null) with { ParentRunId = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(child, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(child.Id.ToString())).Should().BeEmpty();

        var retry = RunForSnapshotLifecycle(projectId: null) with { RetriedFrom = root.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(retry, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(retry.Id.ToString())).Should().BeEmpty();

        // Recovery/resume of the same denied root re-attempts root construction and still denies;
        // it must never be satisfied by the earlier failed attempt's absence of snapshot rows.
        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString())).Should().BeEmpty();
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_ResumeDeniesWhenSnapshotBearingRootLosesProjectId()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var root = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(root, CancellationToken.None)).Should().BeTrue();
        var snapshotsBeforeResume = await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString());
        snapshotsBeforeResume.Should().NotBeEmpty();

        (await lifecycle.PrepareForLaunchAsync(
            root with { ProjectId = null }, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(root.Id.ToString()))
            .Should().BeEquivalentTo(snapshotsBeforeResume);
    }

    [Fact]
    public async Task CapabilitySnapshotLifecycle_ChildAndRetryDenyWhenSnapshotBearingParentHasMissingProjectId()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        await using var db = new MemoryDbContext(options);
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        await SeedCapabilitySourcesAsync(db, projectId.ToString());
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var lifecycle = CreateLifecycle(db, persistence);
        var parent = RunForSnapshotLifecycle(projectId);

        (await lifecycle.PrepareForLaunchAsync(parent, CancellationToken.None)).Should().BeTrue();
        (await persistence.GetCapabilitySnapshotsAsync(parent.Id.ToString())).Should().NotBeEmpty();

        var child = RunForSnapshotLifecycle(projectId: null) with { ParentRunId = parent.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(child, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(child.Id.ToString())).Should().BeEmpty();

        var retry = RunForSnapshotLifecycle(projectId: null) with { RetriedFrom = parent.Id.ToString() };
        (await lifecycle.PrepareForLaunchAsync(retry, CancellationToken.None)).Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync(retry.Id.ToString())).Should().BeEmpty();

        (await persistence.TryInheritCapabilitySnapshotsAsync(parent.Id.ToString(), "target-run", projectId: null))
            .Should().BeFalse();
        (await persistence.GetCapabilitySnapshotsAsync("target-run")).Should().BeEmpty();
    }

    private static RunGitHubCapabilitySnapshotLifecycle CreateLifecycle(
        MemoryDbContext db,
        GitHubConnectionsPersistenceStore persistence,
        ISecretStore? secrets = null)
    {
        secrets ??= new SeededCopilotCredentialStore();
        var broker = new GitHubCapabilityBroker(
            persistence,
            new GitHubConnectionsCredentialVault(secrets),
            new RepoAppInstallationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                db,
                secrets,
                new NullHttpClientFactory()));
        return new RunGitHubCapabilitySnapshotLifecycle(persistence, broker);
    }

    private sealed class SeededCopilotCredentialStore : ISecretStore
    {
        private readonly Dictionary<string, string> _credentials = new(StringComparer.Ordinal)
        {
            ["copilot-app-project-project-version"] = Credential("project-token", "project-user"),
            ["copilot-app-platform-default-version"] = Credential("platform-token", "platform-user"),
        };

        public Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_credentials.TryGetValue(key, out var value)
                ? SecretGetResult.Of(value, "etag")
                : SecretGetResult.NotFound);

        public Task<string> SetSecretAsync(
            string key,
            string value,
            string? etag = null,
            CancellationToken ct = default)
        {
            _credentials[key] = value;
            return Task.FromResult("etag");
        }

        public Task DeleteSecretAsync(string key, CancellationToken ct = default)
        {
            _credentials.Remove(key);
            return Task.CompletedTask;
        }

        private static string Credential(string accessToken, string githubLogin) =>
            JsonSerializer.Serialize(new
            {
                status = "signed-in",
                accessToken,
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                githubLogin,
            });
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

        var action = () => new GitHubConnectionsPersistenceStore(db).ReplaceCopilotBindingAsync(binding);
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void AuditActorEnumCannotRepresentInternalPrincipal()
    {
        Enum.GetNames<GitHubAuditActorKind>().Should().BeEquivalentTo(
            [nameof(GitHubAuditActorKind.HumanEntraSubject), nameof(GitHubAuditActorKind.GitHubWebhook)]);
    }

    [Fact]
    public async Task MarketplaceCapability_ConnectIssueClassifyAndRetry_IsBoundShortLivedAndSingleUse()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var projectId = ProjectId.New();
        var now = DateTimeOffset.UtcNow;
        await using var db = new MemoryDbContext(options);
        db.Projects.Add(Project(projectId.ToString()));
        await db.SaveChangesAsync();
        var persistence = new GitHubConnectionsPersistenceStore(db);
        // Before the connect action has completed its durable binding, no capability is issued.
        (await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            projectId.ToString(), "entra", now, now.AddMinutes(2))).Should().BeNull();

        db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = "marketplace-binding",
            ProjectId = projectId.ToString(),
            EntraObjectId = "entra",
            CredentialReference = "copilot-app-project-marketplace",
            CredentialVersion = "version",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = now,
        });
        await db.SaveChangesAsync();

        // Any authorized project member may be issued a capability against the project's
        // effective binding -- issuance is not restricted to the literal binding owner, since the
        // resolver's precedence is per-project, not per-caller.
        var otherMemberCapability = await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            projectId.ToString(), "other-entra", now, now.AddMinutes(2));
        otherMemberCapability.Should().NotBeNull();
        (await db.MarketplaceCopilotCapabilities.CountAsync(x => x.CapabilityRef == otherMemberCapability!.Value))
            .Should().Be(1);

        var capability = (await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            projectId.ToString(), "entra", now, now.AddMinutes(2)))!;
        var secrets = new InMemorySecretStore();
        var vault = new GitHubConnectionsCredentialVault(secrets);
        await vault.WriteAsync(
            GitHubConnectionsCredentialLocator.ForCopilotProject("copilot-app-project-marketplace"),
            """{"status":"signed-in","accessToken":"marketplace-test-token","expiresAt":"2099-01-01T00:00:00Z"}""");
        var broker = new GitHubCapabilityBroker(
            persistence,
            vault,
            new RepoAppInstallationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                db,
                secrets,
                new NullHttpClientFactory()));

        // Invalid caller/project attempts do not redeem or consume the bound capability.
        (await broker.TryUseMarketplaceCopilotCredentialAsync(
            capability, projectId.ToString(), "wrong-entra", now, (_, _) => Task.CompletedTask, CancellationToken.None))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        (await broker.TryUseMarketplaceCopilotCredentialAsync(
            capability, ProjectId.New().ToString(), "entra", now, (_, _) => Task.CompletedTask, CancellationToken.None))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        (await db.MarketplaceCopilotCapabilities.CountAsync(x => x.CapabilityRef == capability.Value)).Should().Be(1,
            "wrong caller and project attempts must not consume or delete the fenced capability");

        // The post-connect retry reaches classification only after the broker redeems the explicit
        // capability; the classifier receives no token and cannot use an ambient identity.
        var classifier = new MarketplaceCapabilityClassifier(broker);
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);
        var index = await indexer.GetOrBuildForProjectAsync(
            "owner",
            "repo",
            "main",
            [new GitHubTreeBlob("skills/example/SKILL.md", 40)],
            capability.Value,
            "llm",
            CancellationToken.None,
            projectId,
            new CallerContext { User = "marketplace-owner", EntraObjectId = "entra" });
        index.Strategy.Should().Be("llm");
        index.Entries.Should().ContainSingle(entry => entry.Location == "skills/example");
        classifier.Redemptions.Should().Be(1);
        (await db.MarketplaceCopilotCapabilities.CountAsync(x => x.CapabilityRef == capability.Value)).Should().Be(0,
            "a classified capability is terminal and must not be retained");

        // Replays and independently expired capabilities fail closed without a model turn.
        (await broker.TryUseMarketplaceCopilotCredentialAsync(
            capability, projectId.ToString(), "entra", now, (_, _) => Task.CompletedTask, CancellationToken.None))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        var expired = SnapshotRef.Create();
        db.MarketplaceCopilotCapabilities.Add(new ProjectModelProviderCapabilityRecord
        {
            CapabilityRef = expired.Value,
            ProjectId = projectId.ToString(),
            EntraObjectId = "entra",
            SourceBindingId = "marketplace-binding",
            CredentialReference = "copilot-app-project-marketplace",
            CredentialVersion = "version",
            GrantDigest = "digest",
            IssuedAt = now.AddMinutes(-3),
            ExpiresAt = now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
        (await broker.TryUseMarketplaceCopilotCredentialAsync(
            expired, projectId.ToString(), "entra", now, (_, _) => Task.CompletedTask, CancellationToken.None))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        (await persistence.PruneMarketplaceCopilotCapabilitiesAsync(now)).Should().BeGreaterThan(0);
        (await db.MarketplaceCopilotCapabilities.CountAsync(x => x.CapabilityRef == expired.Value)).Should().Be(0,
            "expired unused capabilities are reclaimed by bounded cleanup");

        var failed = (await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            projectId.ToString(), "entra", now, now.AddMinutes(2)))!;
        Func<Task> failedClassification = async () => await broker.TryUseMarketplaceCopilotCredentialAsync(
            failed, projectId.ToString(), "entra", now,
            (_, _) => throw new InvalidOperationException("classification failed"), CancellationToken.None);
        await failedClassification.Should().ThrowAsync<InvalidOperationException>();
        (await db.MarketplaceCopilotCapabilities.CountAsync(x => x.CapabilityRef == failed.Value)).Should().Be(0,
            "a claimed capability is deleted even when classification fails");

        var binding = await db.ProjectCopilotBindings.SingleAsync(x => x.Id == "marketplace-binding");
        binding.Status = GitHubBindingStatus.Inactive;
        binding.DeactivatedAt = now;
        await db.SaveChangesAsync();
        (await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            projectId.ToString(), "entra", now, now.AddMinutes(2))).Should().BeNull(
            "an inactive or revoked connection cannot issue a marketplace capability");
        classifier.Redemptions.Should().Be(1, "reused and expired capabilities must not dispatch another model turn");
    }

    [Fact]
    public async Task BacklogDecompositionCapability_IsPurposeCallerProjectBoundAndSingleUse()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var now = DateTimeOffset.UtcNow;
        db.ProjectCopilotBindings.Add(MarketplaceBinding("backlog-binding"));
        await db.SaveChangesAsync();
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var capability = (await persistence.TryIssueProjectCopilotCapabilityAsync(
            ProjectModelProviderCapabilityPurpose.BacklogDecomposition,
            "project",
            "entra",
            now,
            now.AddMinutes(2)))!;
        var secrets = new InMemorySecretStore();
        var vault = new GitHubConnectionsCredentialVault(secrets);
        await vault.WriteAsync(
            GitHubConnectionsCredentialLocator.ForCopilotProject("copilot-app-project-marketplace"),
            JsonSerializer.Serialize(new
            {
                Status = "signed-in",
                AccessToken = "backlog-test-token",
                ExpiresAt = DateTimeOffset.Parse("2099-01-01T00:00:00Z"),
            }));
        var broker = new GitHubCapabilityBroker(
            persistence,
            vault,
            new RepoAppInstallationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                db,
                secrets,
                new NullHttpClientFactory()));
        var modelTurns = 0;

        async Task<GitHubCapabilityBrokerOutcome> RedeemAsync(
            ProjectModelProviderCapabilityPurpose purpose,
            string projectId,
            string entraObjectId) =>
            await broker.TryUseProjectCopilotCredentialAsync(
                capability,
                purpose,
                projectId,
                entraObjectId,
                now,
                (_, _) =>
                {
                    modelTurns++;
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        (await RedeemAsync(
            ProjectModelProviderCapabilityPurpose.MarketplaceCatalogClassification, "project", "entra"))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        (await RedeemAsync(
            ProjectModelProviderCapabilityPurpose.BacklogDecomposition, "other-project", "entra"))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        (await RedeemAsync(
            ProjectModelProviderCapabilityPurpose.BacklogDecomposition, "project", "other-entra"))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        modelTurns.Should().Be(0, "no model turn can occur before the exact capability is redeemed");

        (await RedeemAsync(
            ProjectModelProviderCapabilityPurpose.BacklogDecomposition, "project", "entra"))
            .Should().Be(GitHubCapabilityBrokerOutcome.Issued);
        modelTurns.Should().Be(1);
        (await RedeemAsync(
            ProjectModelProviderCapabilityPurpose.BacklogDecomposition, "project", "entra"))
            .Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);

        var expired = SnapshotRef.Create();
        db.MarketplaceCopilotCapabilities.Add(new ProjectModelProviderCapabilityRecord
        {
            CapabilityRef = expired.Value,
            Purpose = (int)ProjectModelProviderCapabilityPurpose.BacklogDecomposition,
            ProjectId = "project",
            EntraObjectId = "entra",
            SourceBindingId = "backlog-binding",
            CredentialReference = "copilot-app-project-marketplace",
            CredentialVersion = "version",
            GrantDigest = "digest",
            IssuedAt = now.AddMinutes(-3),
            ExpiresAt = now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        (await broker.TryUseProjectCopilotCredentialAsync(
            expired,
            ProjectModelProviderCapabilityPurpose.BacklogDecomposition,
            "project",
            "entra",
            now,
            (_, _) =>
            {
                modelTurns++;
                return Task.CompletedTask;
            },
            CancellationToken.None)).Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        modelTurns.Should().Be(1, "expired and replayed capability references cannot reach a model turn");
    }

    [Fact]
    public async Task PlatformScopedCapability_IsPurposeCallerBoundAndSingleUse()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var now = DateTimeOffset.UtcNow;
        db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "entra",
            CredentialReference = "copilot-app-platform-default-blueprint",
            CredentialVersion = "version-blueprint",
            GrantDigest = "digest-blueprint",
            Status = GitHubBindingStatus.Active,
            BoundAt = now,
        });
        await db.SaveChangesAsync();
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var capability = (await persistence.TryIssueProjectCopilotCapabilityAsync(
            ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
            projectId: null,
            entraObjectId: "entra",
            now,
            now.AddMinutes(2)))!;
        var secrets = new InMemorySecretStore();
        var vault = new GitHubConnectionsCredentialVault(secrets);
        await vault.WriteAsync(
            GitHubConnectionsCredentialLocator.ForCopilotBinding("copilot-app-platform-default-blueprint"),
            JsonSerializer.Serialize(new
            {
                Status = "signed-in",
                AccessToken = "platform-test-token",
                ExpiresAt = DateTimeOffset.Parse("2099-01-01T00:00:00Z"),
            }));
        var broker = new GitHubCapabilityBroker(
            persistence,
            vault,
            new RepoAppInstallationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                db,
                secrets,
                new NullHttpClientFactory()));
        var modelTurns = 0;

        async Task<GitHubCapabilityBrokerOutcome> RedeemAsync(string? projectId, string entraObjectId) =>
            await broker.TryUseProjectCopilotCredentialAsync(
                capability,
                ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
                projectId,
                entraObjectId,
                now,
                (_, _) =>
                {
                    modelTurns++;
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        (await RedeemAsync("project", "entra")).Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        (await RedeemAsync(null, "other-entra")).Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        modelTurns.Should().Be(0, "no model turn can occur before the exact platform-scoped capability is redeemed");

        (await RedeemAsync(null, "entra")).Should().Be(GitHubCapabilityBrokerOutcome.Issued);
        modelTurns.Should().Be(1);
        (await RedeemAsync(null, "entra")).Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);

        (await db.MarketplaceCopilotCapabilities.CountAsync(x => x.CapabilityRef == capability.Value)).Should().Be(0,
            "the broker deletes a claimed platform-scoped capability after redemption");
    }

    [Fact]
    public async Task MarketplaceCapabilityCleanup_IsBoundedToTerminalRecords()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var now = DateTimeOffset.UtcNow;
        db.MarketplaceCopilotCapabilities.AddRange(Enumerable.Range(0, 101).Select(index =>
            new ProjectModelProviderCapabilityRecord
            {
                CapabilityRef = SnapshotRef.Create().Value,
                ProjectId = "project",
                EntraObjectId = "entra",
                SourceBindingId = "binding",
                CredentialReference = "credential",
                CredentialVersion = "version",
                GrantDigest = "digest",
                IssuedAt = now.AddMinutes(-3),
                ExpiresAt = now.AddMinutes(-1),
            }));
        await db.SaveChangesAsync();

        var removed = await new GitHubConnectionsPersistenceStore(db).PruneMarketplaceCopilotCapabilitiesAsync(now);

        removed.Should().Be(100);
        (await db.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task MarketplaceCapabilityCleanup_DoesNotDeleteALiveClaim()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var now = DateTimeOffset.UtcNow;
        db.MarketplaceCopilotCapabilities.Add(new ProjectModelProviderCapabilityRecord
        {
            CapabilityRef = SnapshotRef.Create().Value,
            ProjectId = "project",
            EntraObjectId = "entra",
            SourceBindingId = "binding",
            CredentialReference = "credential",
            CredentialVersion = "version",
            GrantDigest = "digest",
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(2),
            ConsumedAt = now,
        });
        await db.SaveChangesAsync();

        (await new GitHubConnectionsPersistenceStore(db).PruneMarketplaceCopilotCapabilitiesAsync(now)).Should().Be(0);
        (await db.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task MarketplaceCapabilityCleanup_ReclaimsExpiredAbandonedClaimOnlyAfterItsLease()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var now = DateTimeOffset.UtcNow;
        db.ProjectCopilotBindings.Add(MarketplaceBinding("marketplace-binding"));
        await db.SaveChangesAsync();
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var capability = (await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            "project", "entra", now, now.AddMinutes(1)))!;

        // Simulate process death after the atomic claim and before the broker finally block.
        (await persistence.TryClaimMarketplaceCopilotCapabilityAsync(
            capability, "project", "entra", now)).Should().NotBeNull();

        (await persistence.PruneMarketplaceCopilotCapabilitiesAsync(now.AddMinutes(2))).Should().Be(0,
            "an active broker claim must remain protected after capability expiry until its lease ends");
        (await db.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(1);
        (await persistence.TryClaimMarketplaceCopilotCapabilityAsync(
            capability, "project", "entra", now.AddMinutes(2))).Should().BeNull(
            "maintenance protection does not make a claimed capability redeemable again");

        (await persistence.PruneMarketplaceCopilotCapabilitiesAsync(
            now.Add(GitHubConnectionsPersistenceStore.MarketplaceCapabilityClaimLease).AddSeconds(1))).Should().Be(1);
        (await db.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(0,
            "maintenance must eventually reclaim a crash-abandoned claim without another browse request");
    }

    [Fact]
    public async Task MarketplaceCapabilityMaintenance_ReclaimsExpiredUnconsumedCapabilityAfterCanceledOrCrashedBrowseWithoutAnotherBrowse()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var now = DateTimeOffset.UtcNow;
        await using (var setup = new MemoryDbContext(options))
        {
            setup.MarketplaceCopilotCapabilities.Add(new ProjectModelProviderCapabilityRecord
            {
                CapabilityRef = SnapshotRef.Create().Value,
                ProjectId = "project",
                EntraObjectId = "entra",
                SourceBindingId = "binding",
                CredentialReference = "credential",
                CredentialVersion = "version",
                GrantDigest = "digest",
                IssuedAt = now.AddMinutes(-3),
                ExpiresAt = now.AddMinutes(-1),
            });
            await setup.SaveChangesAsync();
        }

        using var services = new ServiceCollection()
            .AddScoped<MemoryDbContext>(_ => new MemoryDbContext(options))
            .AddScoped<GitHubConnectionsPersistenceStore>()
            .BuildServiceProvider();
        var maintenance = new MarketplaceCopilotCapabilityMaintenanceService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MarketplaceCopilotCapabilityMaintenanceService>.Instance);

        (await maintenance.SweepOnceAsync()).Should().Be(1);
        await using var verification = new MemoryDbContext(options);
        (await verification.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(0,
            "the independent sweep must reclaim a capability left unconsumed when a browse is canceled or crashes");
    }

    [Fact]
    public async Task MarketplaceCapabilityCleanup_DoesNotRaceAClaimedCapabilityDuringRedemption()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var now = DateTimeOffset.UtcNow;
        await using var db = new MemoryDbContext(options);
        db.ProjectCopilotBindings.Add(MarketplaceBinding("marketplace-binding"));
        await db.SaveChangesAsync();
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var capability = (await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            "project", "entra", now, now.AddMinutes(1)))!;
        var vault = new PruningCredentialVault(
            () => persistence.PruneMarketplaceCopilotCapabilitiesAsync(
                now.Add(GitHubConnectionsPersistenceStore.MarketplaceCapabilityClaimLease).AddSeconds(-1)),
            """{"status":"signed-in","accessToken":"marketplace-test-token","expiresAt":"2099-01-01T00:00:00Z"}""");
        var broker = new GitHubCapabilityBroker(
            persistence,
            vault,
            new RepoAppInstallationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                db,
                new InMemorySecretStore(),
                new NullHttpClientFactory()));

        var outcome = await broker.TryUseMarketplaceCopilotCredentialAsync(
            capability, "project", "entra", now, (_, _) => Task.CompletedTask, CancellationToken.None);

        outcome.Should().Be(GitHubCapabilityBrokerOutcome.Issued);
        vault.PrunedRecords.Should().Be(0,
            "generic expiry maintenance must never delete a capability after it has been claimed");
        (await db.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(0,
            "the broker owns terminal deletion after a successful redemption");
    }

    [Fact]
    public async Task MarketplaceCapabilityBroker_FailsClosedWhenMaintenanceReapsItsExpiredClaimDuringVaultRead()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var now = DateTimeOffset.UtcNow;
        db.ProjectCopilotBindings.Add(MarketplaceBinding("marketplace-binding"));
        await db.SaveChangesAsync();
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var capability = (await persistence.TryIssueMarketplaceCopilotCapabilityAsync(
            "project", "entra", now, now.AddMinutes(1)))!;
        var vault = new PruningCredentialVault(
            () => persistence.PruneMarketplaceCopilotCapabilitiesAsync(
                now.Add(GitHubConnectionsPersistenceStore.MarketplaceCapabilityClaimLease).AddSeconds(1)),
            """{"status":"signed-in","accessToken":"marketplace-test-token","expiresAt":"2099-01-01T00:00:00Z"}""");
        var broker = new GitHubCapabilityBroker(
            persistence,
            vault,
            new RepoAppInstallationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                db,
                new InMemorySecretStore(),
                new NullHttpClientFactory()));
        var cached = false;

        var outcome = await broker.TryUseMarketplaceCopilotCredentialAsync(
            capability, "project", "entra", now, (_, _) =>
            {
                cached = true;
                return Task.CompletedTask;
            }, CancellationToken.None);

        outcome.Should().Be(GitHubCapabilityBrokerOutcome.CapabilityUnavailable);
        cached.Should().BeFalse("the post-vault lease fence must prevent a late broker from caching a credential result");
        vault.PrunedRecords.Should().Be(1);
        (await db.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExpiredMarketplaceCapability_RedemptionRequiresConnectionAndDoesNotCacheAnEmptyClassification()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var now = DateTimeOffset.UtcNow;
        await using var db = new MemoryDbContext(options);
        var projectId = ProjectId.New();
        var binding = MarketplaceBinding("marketplace-binding");
        binding.ProjectId = projectId.ToString();
        db.Projects.Add(Project(projectId.ToString()));
        db.ProjectCopilotBindings.Add(binding);
        var expired = SnapshotRef.Create();
        db.MarketplaceCopilotCapabilities.Add(new ProjectModelProviderCapabilityRecord
        {
            CapabilityRef = expired.Value,
            ProjectId = projectId.ToString(),
            EntraObjectId = "entra",
            SourceBindingId = "marketplace-binding",
            CredentialReference = "copilot-app-project-marketplace",
            CredentialVersion = "version",
            GrantDigest = "digest",
            IssuedAt = now.AddMinutes(-3),
            ExpiresAt = now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
        var secrets = new InMemorySecretStore();
        var broker = new GitHubCapabilityBroker(
            new GitHubConnectionsPersistenceStore(db),
            new GitHubConnectionsCredentialVault(secrets),
            new RepoAppInstallationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                db,
                secrets,
                new NullHttpClientFactory()));
        var classifier = new MarketplaceCapabilityClassifier(broker);
        var indexer = new MarketplaceCatalogIndexer(new MarketplaceCatalogCache(), classifier);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await indexer.GetOrBuildForProjectAsync(
                "owner", "repo", "main", [new GitHubTreeBlob("skills/example/SKILL.md", 40)],
                expired.Value, "llm", CancellationToken.None, projectId,
                new CallerContext { User = "marketplace-owner", EntraObjectId = "entra" });
            result.RequiresGitHubConnection.Should().BeTrue();
            result.Entries.Should().BeEmpty();
        }

        classifier.Attempts.Should().Be(2,
            "an expired claim must fail closed as a connection requirement instead of caching an empty LLM result");
        classifier.Redemptions.Should().Be(0);
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

    private static ProjectCopilotBindingRecord MarketplaceBinding(string id) => new()
    {
        Id = id,
        ProjectId = "project",
        EntraObjectId = "entra",
        CredentialReference = "copilot-app-project-marketplace",
        CredentialVersion = "version",
        GrantDigest = "digest",
        Status = GitHubBindingStatus.Active,
        BoundAt = DateTimeOffset.UtcNow,
    };

    private static PlatformDefaultCopilotBindingRecord PlatformBinding(string suffix) => new()
    {
        Id = PlatformDefaultCopilotBindingRecord.SingletonId,
        EntraObjectId = "entra",
        CredentialReference = $"kv-copilot-platform-{suffix}",
        CredentialVersion = $"version-{suffix}",
        GrantDigest = $"digest-{suffix}",
        Status = GitHubBindingStatus.Active,
        BoundAt = DateTimeOffset.UtcNow,
    };

    private static GitHubAuditRecord Audit() => new()
    {
        EntraObjectId = "entra",
        ActorKind = GitHubAuditActorKind.HumanEntraSubject,
        Action = GitHubAuditAction.BindingChanged,
        ResourceId = PlatformDefaultCopilotBindingRecord.SingletonId,
        AppKind = GitHubAppKind.Copilot,
        CapabilityPurpose = GitHubCapabilityPurpose.UnattendedCopilot,
        Outcome = GitHubAuditOutcome.Succeeded,
        ReasonCode = GitHubAuditReasonCode.None,
        CorrelationId = Guid.NewGuid().ToString("N"),
        OccurredAt = DateTimeOffset.UtcNow,
        GrantDigest = "digest-new",
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

    /// <summary>
    /// A blank-origin domain <see cref="Project"/> for seeding <see cref="FakeProjectStore"/>.
    /// Capability-snapshot classification reads project origin through <see cref="IProjectStore"/>
    /// (not the EF <c>db.Projects</c> set used above), so tests that exercise the zero-snapshot
    /// classification path must seed this instead.
    /// </summary>
    private static Project BlankDomainProject(ProjectId id) => new()
    {
        Id = id,
        Name = "Project",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = "C:\\project",
        DefaultBranch = "main",
        Owner = "owner",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// A GitHub-origin domain <see cref="Project"/> for seeding <see cref="FakeProjectStore"/>. Used
    /// to prove that the zero-snapshot classification is decided by persisted origin, not by GitHub
    /// App history rows: a GitHub-origin project must never be treated as blank, regardless of how
    /// many (or how few) installation/grant/binding rows it has ever recorded.
    /// </summary>
    private static Project GitHubOriginDomainProject(ProjectId id, string sourceRepository = "owner/repository") => new()
    {
        Id = id,
        Name = "Project",
        Origin = ProjectOrigin.FromGitHub(sourceRepository),
        WorkingDirectory = "C:\\project",
        DefaultBranch = "main",
        Owner = "owner",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Minimal in-memory <see cref="IProjectStore"/> double for capability-snapshot lifecycle tests
    /// that need to prove classification is driven by persisted project origin
    /// (<see cref="GitHubConnectionsPersistenceStore.IsIntentionallyBlankOriginProjectAsync"/>). Only
    /// <see cref="GetAsync"/>/<see cref="InsertAsync"/> are implemented; the snapshot lifecycle under
    /// test never calls any other member.
    /// </summary>
    private sealed class FakeProjectStore : IProjectStore
    {
        private readonly Dictionary<ProjectId, Project> _projects = new();

        public void Seed(Project project) => _projects[project.Id] = project;

        public Task InsertAsync(Project project, CancellationToken ct = default)
        {
            _projects[project.Id] = project;
            return Task.CompletedTask;
        }

        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) =>
            Task.FromResult(_projects.TryGetValue(id, out var project) ? project : null);

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateGenerationModelSettingsAsync(
            ProjectId id,
            string? blueprintGenerationModel,
            string? workflowGenerationModel,
            string? outcomeSpecGenerationModel,
            DateTimeOffset updatedAt,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdatePickupSettingsAsync(
            ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static async Task SeedCapabilitySourcesAsync(
        MemoryDbContext db,
        string projectId = "project",
        string entraObjectId = "entra",
        long installationId = 101,
        long repositoryId = 202)
    {
        db.GitHubAppAuthorizations.Add(new GitHubAppAuthorizationRecord
        {
            Id = "authorization",
            EntraObjectId = entraObjectId,
            AppKind = GitHubAppKind.Repo,
            Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
            CredentialReference = "repo-app-user-credential-version",
            CredentialVersion = "version",
            GrantDigest = "user-digest",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = installationId,
            AppKind = GitHubAppKind.Repo,
            ProjectId = projectId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = installationId,
            RepositoryId = repositoryId,
            ProjectId = projectId,
            FullNameDisplay = "owner/repository",
            PermissionDigest = "repository-digest",
            GrantedAt = DateTimeOffset.UtcNow,
        });
        db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = "binding",
            ProjectId = projectId,
            EntraObjectId = entraObjectId,
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

    private sealed class MarketplaceCapabilityClassifier(GitHubCapabilityBroker broker) : IMarketplaceCatalogClassifier
    {
        public int Redemptions { get; private set; }
        public int Attempts { get; private set; }

        public Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyAsync(
            string owner,
            string repo,
            string branch,
            IReadOnlyList<string> treePaths,
            string? capabilityRunId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MarketplaceCatalogEntry>?>(null);

        public async Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyForProjectAsync(
            string owner,
            string repo,
            string branch,
            IReadOnlyList<string> treePaths,
            string? capabilityReference,
            CancellationToken ct,
            ProjectId? projectId = null,
            CallerContext? caller = null)
        {
            Attempts++;
            if (string.IsNullOrWhiteSpace(capabilityReference) || projectId is null ||
                string.IsNullOrWhiteSpace(caller?.EntraObjectId))
                throw new GitHubCopilotUnauthorizedException("Marketplace capability unavailable.");

            var redeemed = await broker.TryUseMarketplaceCopilotCredentialAsync(
                new SnapshotRef(capabilityReference),
                projectId.Value.ToString(),
                caller.EntraObjectId,
                DateTimeOffset.UtcNow,
                (_, _) => Task.CompletedTask,
                ct);
            if (redeemed != GitHubCapabilityBrokerOutcome.Issued)
                throw new GitHubCopilotUnauthorizedException("Marketplace capability unavailable.");

            Redemptions++;
            return [new MarketplaceCatalogEntry("skills/example", "example", "Example")];
        }
    }

    private sealed class PruningCredentialVault(
        Func<Task<int>> pruneAsync,
        string credential) : IGitHubConnectionsCredentialVault
    {
        public int PrunedRecords { get; private set; }

        public async Task<SecretGetResult> ReadCurrentAsync(
            GitHubConnectionsCredentialLocator locator,
            CancellationToken ct = default)
        {
            PrunedRecords = await pruneAsync().ConfigureAwait(false);
            return new SecretGetResult(credential, ETag: null, Found: true);
        }

        public Task WriteAsync(GitHubConnectionsCredentialLocator locator, string value, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TombstoneAndDeleteAsync(GitHubConnectionsCredentialLocator locator, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static Run RunForSnapshotLifecycle(ProjectId? projectId = null) => new()
    {
        Id = RunId.New(),
        RepositoryPath = "repository",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "snapshot lifecycle",
        SubmittingUser = "entra",
        Status = RunStatus.Pending,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = projectId,
    };

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler());
    }
}
