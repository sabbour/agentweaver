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
        var service = new AutomationInvocationService(db, new TwoAppPersistenceStore(db));

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
        var service = new AutomationInvocationService(db, new TwoAppPersistenceStore(db));
        (await service.TryClaimAsync(activation.ActivationId, "event:1", "delivery-2", "issues", 1, 10)).Should().BeTrue();
        var invocationId = (await db.AutomationInvocations.SingleAsync()).Id;

        (await service.TryPrepareRunAsync(invocationId, "run-1")).Should().BeTrue();
        var snapshots = await db.RunGitHubCapabilitySnapshots.Where(x => x.RunId == "run-1").ToListAsync();
        snapshots.Should().ContainSingle(x => x.Purpose == GitHubCapabilityPurpose.UnattendedRepository &&
                                             x.InstallationId == 1 && x.RepositoryId == 10 &&
                                             x.GrantDigest == "repo-digest");
        snapshots.Should().ContainSingle(x => x.Purpose == GitHubCapabilityPurpose.UnattendedCopilot &&
                                             x.SourceBindingId == "binding" && x.GrantDigest == "copilot-digest");
        (await service.TryPrepareRunAsync(invocationId, "run-1")).Should().BeTrue("the same immutable pair is replay-safe");

        db.ProjectCopilotBindings.Single().Status = GitHubBindingStatus.Revoked;
        await db.SaveChangesAsync();
        (await service.TryPrepareRunAsync(invocationId, "run-2")).Should().BeFalse();
    }

    private static async Task<FencedAutomationActivation> ActivateAsync(MemoryDbContext db)
    {
        var project = ProjectId.New();
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
        var result = await new AutomationActivationSnapshotService(new TwoAppPersistenceStore(db), roles)
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
