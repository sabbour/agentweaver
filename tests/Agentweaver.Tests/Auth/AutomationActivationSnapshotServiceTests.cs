using System.Security.Claims;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Api.Webhooks;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class AutomationActivationSnapshotServiceTests
{
    [Fact]
    public async Task Activate_RequiresHumanEntraSubjectAndCurrentProjectOwner()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        var service = CreateService(db, roles);
        var internalPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("agentweaver_internal", "true")], "test"));

        (await service.ActivateAsync(
            new CallerContext { User = "api-key-looking", EntraObjectId = null }, internalPrincipal, project)).Outcome
            .Should().Be(AutomationActivationOutcome.HumanEntraSubjectRequired);
        (await service.ActivateAsync(Human("member"), HumanPrincipal(), project)).Outcome
            .Should().Be(AutomationActivationOutcome.ProjectOwnerRequired);

        var audits = await db.GitHubAuditRecords.OrderBy(x => x.Id).ToListAsync();
        audits.Should().OnlyContain(x => x.Action == GitHubAuditAction.AutomationActivated &&
                                         x.Outcome == GitHubAuditOutcome.Denied);
        audits[0].ActorKind.Should().Be(GitHubAuditActorKind.GitHubWebhook);
        audits[0].EntraObjectId.Should().BeNull();
        audits[1].ActorKind.Should().Be(GitHubAuditActorKind.HumanEntraSubject);
        audits[1].EntraObjectId.Should().Be("member");
    }

    [Fact]
    public async Task Activate_ResolvesOnlyOneLiveTupleAndFencesItsExactIdentity()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles);

        var result = await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);

        result.Outcome.Should().Be(AutomationActivationOutcome.Activated);
        result.Activation.Should().NotBeNull();
        var stored = await db.AutomationActivations.SingleAsync();
        stored.RepositoryGrantDigest.Should().Be("repo-digest");
        stored.CopilotBindingId.Should().Be("binding");
        stored.CopilotBindingGrantDigest.Should().Be("copilot-digest");
        stored.AutomationKey.Should().Be("internal-activation-snapshot");
        var fenced = await service.TryFenceAsync(result.Activation!.ActivationId);
        fenced.Should().BeEquivalentTo(result.Activation);

        db.GitHubRepositoryGrants.Single().PermissionDigest = "changed";
        await db.SaveChangesAsync();
        (await service.TryFenceAsync(result.Activation.ActivationId)).Should().BeNull();
    }

    [Fact]
    public async Task Activate_DeniesAbsentAndAmbiguousLivePrerequisites()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles);

        (await service.ActivateAsync(Human("owner"), HumanPrincipal(), project)).Outcome
            .Should().Be(AutomationActivationOutcome.RepositoryGrantUnavailable);

        await SeedAuthorityAsync(db, project);
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 2, AppKind = GitHubAppKind.Repo, ProjectId = project.ToString(), CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 2, RepositoryId = 20, ProjectId = project.ToString(), FullNameDisplay = "not-authority",
            PermissionDigest = "second-digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        (await service.ActivateAsync(Human("owner"), HumanPrincipal(), project)).Outcome
            .Should().Be(AutomationActivationOutcome.RepositoryGrantAmbiguous);
        (await db.GitHubAuditRecords.CountAsync(x => x.Action == GitHubAuditAction.AutomationActivated &&
                                                      x.Outcome == GitHubAuditOutcome.Denied)).Should().Be(2);
    }

    [Fact]
    public async Task Activate_DeniesRevokedGrantAndRevokedBinding()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles);

        db.GitHubRepositoryGrants.Single().RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        (await service.ActivateAsync(Human("owner"), HumanPrincipal(), project)).Outcome
            .Should().Be(AutomationActivationOutcome.RepositoryGrantUnavailable);

        db.GitHubRepositoryGrants.Single().RevokedAt = null;
        db.ProjectCopilotBindings.Single().Status = GitHubBindingStatus.Revoked;
        db.ProjectCopilotBindings.Single().DeactivatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        (await service.ActivateAsync(Human("owner"), HumanPrincipal(), project)).Outcome
            .Should().Be(AutomationActivationOutcome.CopilotBindingUnavailable);
    }

    [Fact]
    public async Task Activation_IsInsertOnlyAndConcurrentReplacementIsDenied()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles);
        var first = await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);

        (await service.ActivateAsync(Human("owner"), HumanPrincipal(), project)).Outcome
            .Should().Be(AutomationActivationOutcome.Conflict);
        var activation = await db.AutomationActivations.SingleAsync();
        activation.RepositoryGrantDigest = "substituted";
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Clear();
        (await db.AutomationActivations.SingleAsync()).Id.Should().Be(first.Activation!.ActivationId);
    }

    [Fact]
    public async Task BindingReplacementAndGrantInvalidation_AtomicallyInvalidateActivation()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles);
        var first = await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);

        var persistence = new GitHubConnectionsPersistenceStore(db);
        (await persistence.ReplaceCopilotBindingAsync(Binding(project, "binding-2", "copilot-digest-2")))
            .Should().Be(BindingWriteResult.Bound);
        db.ChangeTracker.Clear();
        (await db.AutomationActivations.SingleAsync()).Status.Should().Be(AutomationActivationStatus.Invalidated);
        (await service.TryFenceAsync(first.Activation!.ActivationId)).Should().BeNull();

        var second = await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);
        second.Outcome.Should().Be(AutomationActivationOutcome.Activated);
        await new RepoAppInstallationLifecycleService(db).InvalidateForPermissionChangeAsync(1, 10);
        db.ChangeTracker.Clear();
        (await db.AutomationActivations.SingleAsync(x => x.Id == second.Activation!.ActivationId)).Status
            .Should().Be(AutomationActivationStatus.Invalidated);
    }

    [Fact]
    public async Task ActivationAudit_IsAllowlistedAndRedactsCredentialsAndProviderData()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");

        (await CreateService(db, roles).ActivateAsync(Human("owner"), HumanPrincipal(), project)).Outcome
            .Should().Be(AutomationActivationOutcome.Activated);

        var audit = await db.GitHubAuditRecords.SingleAsync();
        audit.Action.Should().Be(GitHubAuditAction.AutomationActivated);
        audit.GrantDigest.Should().Be("repo-digest");
        System.Text.Json.JsonSerializer.Serialize(new { audit, activation = await db.AutomationActivations.SingleAsync() })
            .Should().NotContain("owner/repository").And.NotContain("ghu_").And.NotContain("credential");
    }

    private static AutomationActivationSnapshotService CreateService(MemoryDbContext db, MutableRoles roles) =>
        new(new GitHubConnectionsPersistenceStore(db), roles);

    private static CallerContext Human(string subject) => new() { User = subject, EntraObjectId = subject };
    private static ClaimsPrincipal HumanPrincipal() =>
        new(new ClaimsIdentity([new Claim("oid", "owner")], "test"));

    private static async Task<ProjectId> SeedProjectAsync(MemoryDbContext db)
    {
        var project = ProjectId.New();
        db.Projects.Add(new ProjectRecord { ProjectId = project.ToString() });
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task SeedAuthorityAsync(MemoryDbContext db, ProjectId project)
    {
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 1, AppKind = GitHubAppKind.Repo, ProjectId = project.ToString(), CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 1, RepositoryId = 10, ProjectId = project.ToString(), FullNameDisplay = "owner/repository",
            PermissionDigest = "repo-digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        db.ProjectCopilotBindings.Add(Binding(project, "binding", "copilot-digest"));
        await db.SaveChangesAsync();
    }

    private static ProjectCopilotBindingRecord Binding(ProjectId project, string id, string digest) => new()
    {
        Id = id, ProjectId = project.ToString(), EntraObjectId = "owner",
        CredentialReference = $"credential-{id}", CredentialVersion = $"version-{id}", GrantDigest = digest,
        Status = GitHubBindingStatus.Active, BoundAt = DateTimeOffset.UtcNow,
    };

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Agentweaver.Api")).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    private sealed class MutableRoles : IProjectRoleAssignmentStore
    {
        private readonly HashSet<(ProjectId Project, string Subject)> owners = [];

        public void SetOwner(ProjectId project, string subject) => owners.Add((project, subject));
        public Task<ProjectRoleAssignment?> GetAsync(ProjectId projectId, string principalId, CancellationToken ct = default) =>
            Task.FromResult(owners.Contains((projectId, principalId))
                ? new ProjectRoleAssignment { ProjectId = projectId, PrincipalId = principalId, Role = ProjectRole.Owner, GrantedBy = "test", GrantedAt = DateTimeOffset.UtcNow }
                : null);
        public Task UpsertAsync(ProjectRoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(ProjectRoleAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string principalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ProjectId projectId, string principalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(ProjectId projectId, string principalId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
