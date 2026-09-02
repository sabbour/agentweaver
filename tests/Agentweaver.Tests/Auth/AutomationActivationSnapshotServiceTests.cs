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

    [Fact]
    public async Task Activate_WhenByokIsTheActiveDeploymentProvider_SkipsCopilotBindingEntirely()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        // Only a live repository grant — deliberately no Copilot binding of any kind — to prove
        // BYOK activation does not require one.
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 1, AppKind = GitHubAppKind.Repo, ProjectId = project.ToString(), CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 1, RepositoryId = 10, ProjectId = project.ToString(), FullNameDisplay = "owner/repository",
            PermissionDigest = "repo-digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var byokSecrets = new InMemorySecretStore();
        var byokSettings = new ByokProviderConfigurationService(byokSecrets);
        var provider = await byokSettings.AddAsync(new ByokProviderConfiguration(
            Id: "unused", Name: "My BYOK", Type: "openai", BaseUrl: "https://api.example.com/v1",
            Model: "my-model", ApiKey: "sk-secret"), CancellationToken.None);
        await byokSettings.SetActiveAsync(provider.Id, CancellationToken.None);
        var service = new AutomationActivationSnapshotService(
            new GitHubConnectionsPersistenceStore(db, byokSettings: byokSettings), roles);

        var result = await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);

        result.Outcome.Should().Be(AutomationActivationOutcome.Activated);
        result.Activation!.ModelProviderSource.Should().Be(AutomationModelProviderSource.Byok);
        result.Activation.CopilotBindingId.Should().BeNull();
        result.Activation.ByokProviderId.Should().Be(provider.Id);
        var stored = await db.AutomationActivations.SingleAsync();
        stored.ModelProviderSource.Should().Be(AutomationModelProviderSource.Byok);
        stored.ByokProviderId.Should().Be(provider.Id);
        stored.CopilotBindingId.Should().BeNull();

        // Fencing succeeds while the same provider stays active...
        (await service.TryFenceAsync(result.Activation.ActivationId)).Should().NotBeNull();

        // ...but fails once a different provider becomes active (BYOK fencing is exact-id, not a
        // reversible digest comparison, since there is no grant/permission digest for an LLM key).
        var otherProvider = await byokSettings.AddAsync(new ByokProviderConfiguration(
            Id: "unused", Name: "Other BYOK", Type: "openai", BaseUrl: "https://api.example.com/v1",
            Model: "other-model", ApiKey: "sk-other"), CancellationToken.None);
        await byokSettings.SetActiveAsync(otherProvider.Id, CancellationToken.None);
        (await service.TryFenceAsync(result.Activation.ActivationId)).Should().BeNull();

        // ...and fails once BYOK is switched off entirely (back to GitHub Copilot mode).
        await byokSettings.SetActiveAsync(null, CancellationToken.None);
        (await service.TryFenceAsync(result.Activation.ActivationId)).Should().BeNull();
    }

    [Fact]
    public async Task Deactivate_RequiresHumanEntraSubjectAndCurrentProjectOwner_ThenFreesTheProjectForReactivation()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles);
        var internalPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("agentweaver_internal", "true")], "test"));

        (await service.DeactivateAsync(
            new CallerContext { User = "api-key-looking", EntraObjectId = null }, internalPrincipal, project))
            .Should().Be(AutomationDeactivationOutcome.HumanEntraSubjectRequired);
        (await service.DeactivateAsync(Human("member"), HumanPrincipal(), project))
            .Should().Be(AutomationDeactivationOutcome.ProjectOwnerRequired);
        (await service.DeactivateAsync(Human("owner"), HumanPrincipal(), project))
            .Should().Be(AutomationDeactivationOutcome.NotActive, "nothing has been activated yet");

        var activated = await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);
        activated.Outcome.Should().Be(AutomationActivationOutcome.Activated);

        (await service.DeactivateAsync(Human("owner"), HumanPrincipal(), project))
            .Should().Be(AutomationDeactivationOutcome.Deactivated);
        db.ChangeTracker.Clear(); // TryDeactivateAutomationActivationAsync uses ExecuteUpdateAsync, which
                                  // bypasses the change tracker/identity map.
        (await db.AutomationActivations.SingleAsync()).Status.Should().Be(AutomationActivationStatus.Inactive);
        (await service.TryFenceAsync(activated.Activation!.ActivationId)).Should().BeNull(
            "an Inactive activation must not fence as live");

        // Deactivating again is a no-op (nothing currently Active).
        (await service.DeactivateAsync(Human("owner"), HumanPrincipal(), project))
            .Should().Be(AutomationDeactivationOutcome.NotActive);

        // The unique-active-per-project index frees up once Inactive, so a fresh activation succeeds.
        var reactivated = await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);
        reactivated.Outcome.Should().Be(AutomationActivationOutcome.Activated);
        (await service.TryFenceAsync(reactivated.Activation!.ActivationId)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetStatus_ReflectsNoneThenActiveThenInactive()
    {
        await using var db = await OpenDatabaseAsync();
        var project = await SeedProjectAsync(db);
        await SeedAuthorityAsync(db, project);
        var roles = new MutableRoles();
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles);

        (await service.GetStatusAsync(project)).IsActive.Should().BeFalse();

        await service.ActivateAsync(Human("owner"), HumanPrincipal(), project);
        var active = await service.GetStatusAsync(project);
        active.IsActive.Should().BeTrue();
        active.ModelProviderSource.Should().Be("github_copilot");
        active.ActivatedAt.Should().NotBeNull();

        await service.DeactivateAsync(Human("owner"), HumanPrincipal(), project);
        (await service.GetStatusAsync(project)).IsActive.Should().BeFalse();
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
