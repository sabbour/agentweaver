using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class TwoAppPersistencePostgresTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task ProjectCascadeAndActiveBindingConstraintMatchSqlite()
    {
        var projectId = $"two-app-{Guid.NewGuid():N}";
        await using (var db = await postgres.CreateDbContextAsync())
        {
            db.Projects.Add(new ProjectRecord
            {
                ProjectId = projectId,
                Name = "Two App test",
                OriginKind = "blank",
                WorkingDirectory = "two-app-worktree",
                Owner = "owner",
                DefaultProvider = "github_copilot",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            var store = new TwoAppPersistenceStore(db);
            (await store.ReplaceCopilotBindingAsync(Binding("first", projectId))).Should().Be(BindingWriteResult.Bound);
        }

        await using (var conflicting = await postgres.CreateDbContextAsync())
        {
            conflicting.ProjectCopilotBindings.Add(Binding("second", projectId));
            var save = () => conflicting.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>();
        }

        await using (var delete = await postgres.CreateDbContextAsync())
        {
            delete.Projects.Remove(new ProjectRecord { ProjectId = projectId });
            await delete.SaveChangesAsync();
        }

        await using var verify = await postgres.CreateDbContextAsync();
        (await verify.ProjectCopilotBindings.CountAsync(x => x.ProjectId == projectId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task LifecycleDeliveryClaim_UsesSameUniqueDeliveryContractAsSqlite()
    {
        var deliveryId = $"delivery-{Guid.NewGuid():N}";
        await using (var first = await postgres.CreateDbContextAsync())
        {
            (await new TwoAppPersistenceStore(first).ClaimLifecycleDeliveryAsync(LifecycleDelivery(deliveryId)))
                .Should().Be(InvocationClaimResult.Claimed);
        }

        await using var second = await postgres.CreateDbContextAsync();
        (await new TwoAppPersistenceStore(second).ClaimLifecycleDeliveryAsync(LifecycleDelivery(deliveryId)))
            .Should().Be(InvocationClaimResult.Duplicate);
    }

    [PostgresFact]
    public async Task CapabilitySnapshots_AllowPurposeCoexistenceAndRejectRunPurposeRaces()
    {
        var projectId = $"capability-snapshots-{Guid.NewGuid():N}";
        await using (var setup = await postgres.CreateDbContextAsync())
        {
            setup.Projects.Add(new ProjectRecord
            {
                ProjectId = projectId,
                Name = "Snapshot project",
                OriginKind = "blank",
                WorkingDirectory = "snapshot-worktree",
                Owner = "owner",
                DefaultProvider = "github_copilot",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            setup.RunGitHubCapabilitySnapshots.AddRange(
                InteractiveSnapshot(projectId, "run", GitHubCapabilityPurpose.InteractiveRepository),
                InteractiveSnapshot(projectId, "run", GitHubCapabilityPurpose.InteractiveCopilot));
            await setup.SaveChangesAsync();
        }

        await using (var first = await postgres.CreateDbContextAsync())
        await using (var second = await postgres.CreateDbContextAsync())
        {
            first.RunGitHubCapabilitySnapshots.Add(InteractiveSnapshot(
                projectId, "race", GitHubCapabilityPurpose.InteractiveRepository));
            second.RunGitHubCapabilitySnapshots.Add(InteractiveSnapshot(
                projectId, "race", GitHubCapabilityPurpose.InteractiveRepository));
            await first.SaveChangesAsync();
            var save = () => second.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verify = await postgres.CreateDbContextAsync();
        (await verify.RunGitHubCapabilitySnapshots.CountAsync(x => x.ProjectId == projectId)).Should().Be(3);
        (await verify.RunGitHubCapabilitySnapshots
            .Where(x => x.RunId == "run")
            .Select(x => x.SnapshotRef)
            .ToListAsync()).Should().OnlyHaveUniqueItems();
    }

    private static GitHubLifecycleDeliveryRecord LifecycleDelivery(string deliveryId) => new()
    {
        DeliveryId = deliveryId,
        EventName = "installation",
        InstallationId = 101,
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    private static ProjectCopilotBindingRecord Binding(string id, string projectId) => new()
    {
        Id = id,
        ProjectId = projectId,
        EntraObjectId = "entra",
        CredentialReference = "kv-copilot",
        CredentialVersion = "version",
        GrantDigest = "digest",
        Status = GitHubBindingStatus.Active,
        BoundAt = DateTimeOffset.UtcNow,
    };

    private static RunGitHubCapabilitySnapshotRecord InteractiveSnapshot(
        string projectId,
        string runId,
        GitHubCapabilityPurpose purpose) => new()
    {
        SnapshotRef = SnapshotRef.Create().Value,
        RunId = runId,
        Purpose = purpose,
        AppKind = GitHubAppKind.Repo,
        SourceKind = GitHubCapabilitySnapshotSourceKind.UserAuthorization,
        ProjectId = projectId,
        EntraObjectId = "entra",
        SourceAuthorizationId = $"authorization-{runId}-{purpose}",
        RepositoryId = purpose == GitHubCapabilityPurpose.InteractiveRepository ? 202 : null,
        CredentialReference = "repo-app-user-credential-version",
        CredentialVersion = "version",
        GrantDigest = "grant-digest",
        CapturedAt = DateTimeOffset.UtcNow,
    };
}
