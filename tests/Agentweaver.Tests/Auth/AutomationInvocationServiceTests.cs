using System.Security.Claims;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class AutomationInvocationServiceTests
{
    [Fact]
    public async Task Claim_RequiresFencedActivationAndIsIdempotent()
    {
        await using var db = await OpenDatabaseAsync();
        var activation = await ActivateAsync(db);
        var service = new AutomationInvocationService(db, new GitHubConnectionsPersistenceStore(db));

        (await service.TryClaimAsync(activation.ActivationId, "weekly:2026-08-31", "delivery-1", "schedule", 1, 99))
            .Should().BeFalse("a trigger must prove the activation's exact repository identity");
        (await service.TryClaimAsync(activation.ActivationId, "weekly:2026-08-31", "delivery-1", "schedule", 1, 10))
            .Should().BeTrue();
        (await service.TryClaimAsync(activation.ActivationId, "weekly:2026-08-31", "delivery-1", "schedule", 1, 10))
            .Should().BeFalse("the durable activation-occurrence claim is unique");
        (await db.AutomationInvocations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PrepareRun_CopiesExactActivationCapabilitiesAndFailsClosedAfterInvalidation()
    {
        await using var db = await OpenDatabaseAsync();
        var activation = await ActivateAsync(db);
        var service = new AutomationInvocationService(db, new GitHubConnectionsPersistenceStore(db));
        (await service.TryClaimAsync(activation.ActivationId, "event:1", "delivery-2", "issues", 1, 10)).Should().BeTrue();
        var invocationId = (await db.AutomationInvocations.SingleAsync()).Id;
        var projectId = ProjectId.Parse(activation.ProjectId);

        var taskId = BacklogTaskId.New();
        (await service.TryBindBacklogTaskAsync(invocationId, projectId, taskId)).Should().BeTrue();
        (await service.TryPrepareRunAsync(projectId, taskId, "run-1")).Should().BeTrue();
        var snapshots = await db.RunGitHubCapabilitySnapshots.Where(x => x.RunId == "run-1").ToListAsync();
        snapshots.Should().ContainSingle(x => x.Purpose == GitHubCapabilityPurpose.UnattendedRepository &&
                                             x.InstallationId == 1 && x.RepositoryId == 10 &&
                                             x.GrantDigest == "repo-digest");
        snapshots.Should().ContainSingle(x => x.Purpose == GitHubCapabilityPurpose.UnattendedCopilot &&
                                             x.SourceBindingId == "binding" && x.GrantDigest == "copilot-digest");
        (await service.TryPrepareRunAsync(projectId, taskId, "run-1")).Should().BeTrue("the same immutable pair is replay-safe");

        db.ProjectCopilotBindings.Single().Status = GitHubBindingStatus.Revoked;
        await db.SaveChangesAsync();
        (await service.TryPrepareRunAsync(projectId, taskId, "run-2")).Should().BeFalse();
    }

    [Fact]
    public async Task ProjectClaimAndPreparation_RequireServerBoundProjectAndTask()
    {
        await using var db = await OpenDatabaseAsync();
        var project = ProjectId.New();
        var service = new AutomationInvocationService(db, new GitHubConnectionsPersistenceStore(db));

        (await service.TryClaimForProjectAsync(project, "schedule:missing", null, "schedule")).Should().BeNull(
            "an automated trigger must not create a pickup task without an active fenced activation");

        var activation = await ActivateAsync(db, project);
        var claim = await service.TryClaimForProjectAsync(project, "schedule:valid", null, "schedule");
        claim.Should().NotBeNull();

        var taskId = BacklogTaskId.New();
        (await service.TryBindBacklogTaskAsync(claim!.InvocationId, project, taskId)).Should().BeTrue();
        (await service.TryPrepareRunAsync(ProjectId.New(), taskId, "cross-project-run")).Should().BeFalse(
            "a task id cannot select an invocation owned by another project");
        (await service.TryPrepareRunAsync(project, BacklogTaskId.New(), "forged-task-run")).Should().BeFalse(
            "only the task atomically bound by the trusted trigger path can prepare an unattended run");
        (await service.TryPrepareRunAsync(project, taskId, "valid-run")).Should().BeTrue();
    }

    [Fact]
    public async Task RetrievedClaim_RequiresExactProjectActivationAndOccurrenceIdentity()
    {
        await using var db = await OpenDatabaseAsync();
        var activation = await ActivateAsync(db);
        var project = ProjectId.Parse(activation.ProjectId);
        var service = new AutomationInvocationService(db, new GitHubConnectionsPersistenceStore(db));
        const string occurrenceKey = "workflow-event-trigger:on-issue-opened:issue.opened:delivery-1";

        var claimed = await service.TryClaimForProjectAsync(
            project, occurrenceKey, "delivery-1", "issue.opened");
        claimed.Should().NotBeNull();
        (await service.TryClaimForProjectAsync(project, occurrenceKey, "delivery-1", "issue.opened"))
            .Should().BeNull("the duplicate delivery is rejected by the durable unique claim");

        var recovered = await service.TryGetClaimedInvocationForProjectAsync(
            project, occurrenceKey, "delivery-1", "issue.opened");
        recovered.Should().BeEquivalentTo(claimed,
            "only the original durable claim is eligible for recovery");
        (await service.TryGetClaimedInvocationForProjectAsync(
            ProjectId.New(), occurrenceKey, "delivery-1", "issue.opened")).Should().BeNull();
        (await service.TryGetClaimedInvocationForProjectAsync(
            project, occurrenceKey + ":other", "delivery-1", "issue.opened")).Should().BeNull();
        (await service.TryGetClaimedInvocationForProjectAsync(
            project, occurrenceKey, "other-delivery", "issue.opened")).Should().BeNull();

        var invalidatedAt = DateTimeOffset.UtcNow;
        await db.AutomationActivations
            .Where(x => x.Id == activation.ActivationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, AutomationActivationStatus.Invalidated)
                .SetProperty(x => x.InvalidatedAt, invalidatedAt));
        db.AutomationActivations.Add(new AutomationActivationRecord
        {
            Id = "replacement-activation", ProjectId = project.ToString(), InstallationId = 1, RepositoryId = 10,
            RepositoryGrantDigest = "repo-digest", CopilotBindingId = "binding",
            CopilotBindingGrantDigest = "copilot-digest", AutomationKey = "replacement-automation",
            Status = AutomationActivationStatus.Active, ActivatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        (await service.TryGetClaimedInvocationForProjectAsync(
            project, occurrenceKey, "delivery-1", "issue.opened")).Should().BeNull(
            "recovery must reject an invocation whose activation identity differs from the current fenced activation");
    }

    [Fact]
    public async Task DiscardInvocationForDeletedTask_AllowsTriggerOccurrenceToBeClaimedAgain()
    {
        await using var db = await OpenDatabaseAsync();
        var activation = await ActivateAsync(db);
        var service = new AutomationInvocationService(db, new GitHubConnectionsPersistenceStore(db));
        var project = ProjectId.Parse(activation.ProjectId);

        (await service.TryClaimForProjectAsync(project, "event:retry", "delivery-retry", "issues")).Should().NotBeNull();
        var invocation = await db.AutomationInvocations.SingleAsync();
        var provisionalTaskId = BacklogTaskId.New();
        (await service.TryDiscardInvocationForTaskAsync(invocation.Id, project, provisionalTaskId)).Should().BeTrue();
        (await db.AutomationInvocations.ToListAsync()).Should().BeEmpty();

        (await service.TryClaimForProjectAsync(project, "event:retry", "delivery-retry", "issues")).Should().NotBeNull(
            "a trigger whose task binding failed must be able to safely retry the same occurrence");
        var rebound = await db.AutomationInvocations.SingleAsync();
        var boundTaskId = BacklogTaskId.New();
        (await service.TryBindBacklogTaskAsync(rebound.Id, project, boundTaskId)).Should().BeTrue();
        (await service.TryDiscardInvocationForTaskAsync(rebound.Id, project, BacklogTaskId.New())).Should().BeFalse(
            "a bound invocation is never discarded for a different task");
        (await service.TryDiscardInvocationForTaskAsync(rebound.Id, project, boundTaskId)).Should().BeTrue(
            "publication recovery may release a binding only after that exact provisional task was deleted");
    }

    [Fact]
    public async Task AdoptLegacyProvisionalTask_IsIdempotentUnderConcurrentRetry()
    {
        var databaseName = $"automation-invocation-{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{databaseName}?mode=memory&cache=shared;Default Timeout=5";
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connectionString, options => options.MigrationsAssembly("Agentweaver.Api")).Options;
        await using var firstDb = new MemoryDbContext(options);
        await firstDb.Database.MigrateAsync();
        var activation = await ActivateAsync(firstDb);
        var project = ProjectId.Parse(activation.ProjectId);
        var first = new AutomationInvocationService(firstDb, new GitHubConnectionsPersistenceStore(firstDb));
        (await first.TryClaimForProjectAsync(project, "schedule:concurrent-legacy", null, "schedule")).Should().NotBeNull();
        var invocationId = await firstDb.AutomationInvocations.Select(x => x.Id).SingleAsync();

        await using var secondDb = new MemoryDbContext(options);
        var second = new AutomationInvocationService(secondDb, new GitHubConnectionsPersistenceStore(secondDb));
        var taskId = BacklogTaskId.New();
        var results = await Task.WhenAll(
            first.TryAdoptLegacyProvisionalTaskAsync(invocationId, project, taskId),
            second.TryAdoptLegacyProvisionalTaskAsync(invocationId, project, taskId));

        results.Should().OnlyContain(result => result,
            "concurrent retries may converge only on the exact same server-owned legacy task");
        firstDb.ChangeTracker.Clear();
        var invocation = await firstDb.AutomationInvocations.SingleAsync();
        invocation.PendingBacklogTaskId.Should().Be(taskId.ToString());
        invocation.BacklogTaskId.Should().BeNull();
    }

    private static async Task<FencedAutomationActivation> ActivateAsync(MemoryDbContext db, ProjectId? projectId = null)
    {
        var project = projectId ?? ProjectId.New();
        db.Projects.Add(new ProjectRecord { ProjectId = project.ToString() });
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 1, AppKind = GitHubAppKind.Repo, ProjectId = project.ToString(), CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 1, RepositoryId = 10, ProjectId = project.ToString(), FullNameDisplay = "owner/repository",
            PermissionDigest = "repo-digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = "binding", ProjectId = project.ToString(), EntraObjectId = "owner",
            CredentialReference = "credential", CredentialVersion = "version", GrantDigest = "copilot-digest",
            Status = GitHubBindingStatus.Active, BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var roles = new OwnerRoles(project, "owner");
        var result = await new AutomationActivationSnapshotService(new GitHubConnectionsPersistenceStore(db), roles)
            .ActivateAsync(new CallerContext { User = "owner", EntraObjectId = "owner" },
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "owner")], "test")), project);
        result.Activation.Should().NotBeNull();
        return result.Activation!;
    }

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Agentweaver.Api")).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    private sealed class OwnerRoles(ProjectId project, string owner) : IProjectRoleAssignmentStore
    {
        public Task<ProjectRoleAssignment?> GetAsync(ProjectId projectId, string principalId, CancellationToken ct = default) =>
            Task.FromResult(projectId == project && principalId == owner
                ? new ProjectRoleAssignment { ProjectId = project, PrincipalId = owner, Role = ProjectRole.Owner, GrantedBy = owner, GrantedAt = DateTimeOffset.UtcNow }
                : null);
        public Task UpsertAsync(ProjectRoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(ProjectRoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string principalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ProjectId projectId, string principalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(ProjectId projectId, string principalId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
