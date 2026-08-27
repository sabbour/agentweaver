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
}
