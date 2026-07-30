using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Auth;

public sealed class ProjectRoleAssignmentStoreConcurrencyTests
{
    [Fact]
    public async Task SqliteStore_ConcurrentOwnerRemovals_LeaveOneExplicitOwner()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectRoleAssignmentStore(testDb.Db);
        var projectId = ProjectId.New();
        await InsertProjectAsync(new SqliteProjectStore(testDb.Db), projectId);

        await SeedOwnersAsync(store, projectId, "owner-a", "owner-b");

        var results = await RunConcurrentAsync(
            () => store.DeleteEnsuringOwnerInvariantAsync(projectId, "owner-a"),
            () => store.DeleteEnsuringOwnerInvariantAsync(projectId, "owner-b"));

        results.Select(x => x.Status).Should().BeEquivalentTo(
            [ProjectRoleAssignmentStoreMutationStatus.Ok, ProjectRoleAssignmentStoreMutationStatus.LastOwnerConflict]);

        var owners = (await store.ListByProjectAsync(projectId)).Where(x => x.Role == ProjectRole.Owner).ToList();
        owners.Should().HaveCount(1, "the store-level delete must enforce the last-owner invariant atomically");
    }

    private static async Task SeedOwnersAsync(IProjectRoleAssignmentStore store, ProjectId projectId, string ownerA, string ownerB)
    {
        await store.UpsertAsync(new ProjectRoleAssignment
        {
            ProjectId = projectId,
            PrincipalId = ownerA,
            Role = ProjectRole.Owner,
            GrantedBy = "seed",
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await store.UpsertAsync(new ProjectRoleAssignment
        {
            ProjectId = projectId,
            PrincipalId = ownerB,
            Role = ProjectRole.Owner,
            GrantedBy = "seed",
            GrantedAt = DateTimeOffset.UtcNow.AddSeconds(1),
        });
    }

    private static async Task InsertProjectAsync(IProjectStore store, ProjectId projectId)
    {
        await store.InsertAsync(new Project
        {
            Id = projectId,
            Name = "RBAC Concurrency",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = Path.Combine(Path.GetTempPath(), projectId.ToString()),
            DefaultBranch = "main",
            Owner = "seed-owner",
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<T[]> RunConcurrentAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task1 = Task.Run(async () =>
        {
            await gate.Task;
            return await first();
        });
        var task2 = Task.Run(async () =>
        {
            await gate.Task;
            return await second();
        });

        gate.SetResult();
        return await Task.WhenAll(task1, task2);
    }
}
