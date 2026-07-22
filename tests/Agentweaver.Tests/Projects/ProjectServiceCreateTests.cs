using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Unit tests for ProjectService creation paths (blank + from-GitHub).
/// Uses TestSqliteDb for an isolated database and real or no-op initializers.
/// </summary>
public sealed class ProjectServiceCreateTests : IAsyncDisposable
{
    private readonly string _testRoot;

    public ProjectServiceCreateTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-svc-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(50);
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private string NewDir(bool create = false)
    {
        var path = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        if (create) Directory.CreateDirectory(path);
        return path;
    }

    private static ProjectService BuildService(
        IProjectStore store,
        IProjectWorkspaceProvider? workspace = null,
        ProjectGitInitializer? gitInit = null,
        IGitHubTokenStore? tokenStore = null,
        IGitHubTokenScopeProvider? scopeProvider = null,
        ISkillStore? skillStore = null,
        SqliteDb? db = null)
    {
        workspace     ??= TestWorkspaceProviders.CreateLocal();
        gitInit       ??= new NoOpGitInitializer();
        tokenStore    ??= new InMemoryGitHubTokenStore();
        scopeProvider ??= new FixedInstallationScopeProvider();

        return new ProjectService(
            store, workspace, gitInit, tokenStore, scopeProvider,
            NullLogger<ProjectService>.Instance);
    }

    // =========================================================================
    // PC-01: CreateBlankAsync creates directory and records project in DB
    // =========================================================================
    [Fact]
    public async Task CreateBlankAsync_CreatesDirectoryAndRecord()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store, db: testDb.Db);
        var dir     = NewDir();

        var project = await service.CreateBlankAsync("My Project", dir, null, null, null, "test-user");

        project.Should().NotBeNull();
        project.Name.Should().Be("My Project");
        project.WorkingDirectory.Should().Be(Path.GetFullPath(dir));
        project.DefaultBranch.Should().Be("main");
        Directory.Exists(project.WorkingDirectory).Should().BeTrue();

        var retrieved = await store.GetAsync(project.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("My Project");
    }

    // =========================================================================
    // PC-02: CreateBlankAsync rejects non-empty existing directory
    // =========================================================================
    [Fact]
    public async Task CreateBlankAsync_RejectsNonEmptyDirectory()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store, db: testDb.Db);
        var dir     = NewDir(create: true);
        File.WriteAllText(Path.Combine(dir, "existing.txt"), "content");

        var act = async () => await service.CreateBlankAsync("Proj", dir, null, null, null, "user");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not empty*");
    }

    // =========================================================================
    // PC-03: CreateFromGitHubAsync requires a signed-in token
    // =========================================================================
    [Fact]
    public async Task CreateFromGitHubAsync_RequiresSignedInToken()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store      = new SqliteProjectStore(testDb.Db);
        var tokenStore = new InMemoryGitHubTokenStore(); // NeverSignedIn
        var service    = BuildService(store, tokenStore: tokenStore, db: testDb.Db);
        var dir        = NewDir();

        var act = async () =>
            await service.CreateFromGitHubAsync("Proj", "https://github.com/owner/repo", dir, null, null, null, "user");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GitHub sign-in is required*");
    }

    // =========================================================================
    // PC-04: CreateFromGitHubAsync succeeds when token is set (uses stub git)
    // =========================================================================
    [Fact]
    public async Task CreateFromGitHubAsync_Succeeds_WhenTokenIsSet()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store      = new SqliteProjectStore(testDb.Db);
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;
        await tokenStore.SetAsync(scope, new GitHubToken(
            "ghp_test_token", null, null, "testuser", null, ["repo", "read:user"]));

        var service = BuildService(store, tokenStore: tokenStore, db: testDb.Db);
        var dir     = NewDir();

        var project = await service.CreateFromGitHubAsync(
            "GitHub Project", "https://github.com/owner/repo", dir, null, null, null, "test-user");

        project.Should().NotBeNull();
        project.Origin.Kind.Should().Be(ProjectOriginKind.FromGitHub);
        project.Origin.SourceRepository.Should().Be("https://github.com/owner/repo");
        Directory.Exists(project.WorkingDirectory).Should().BeTrue();
    }

    // =========================================================================
    // PC-05: CreateBlankAsync rollback — if DB insert fails, created directory removed
    // =========================================================================
    [Fact]
    public async Task CreateBlankAsync_Rollback_IfDbInsertFails()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var faultyStore = new FaultingProjectStore(new SqliteProjectStore(testDb.Db), throwOnInsert: true);
        var service     = BuildService(faultyStore, db: testDb.Db);
        var dir         = NewDir();

        var act = async () =>
            await service.CreateBlankAsync("RollbackProject", dir, null, null, null, "user");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inject fault*");

        // The directory that was app-created must be removed on rollback.
        Directory.Exists(dir).Should().BeFalse("rollback must remove app-created directory");
    }

    [Fact]
    public async Task RollbackCreationAsync_CancelledAfterDefaultsCommit_PurgesOnlyCreatedProject()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var skillStore = new SqliteSkillStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var service = BuildService(projectStore, skillStore: skillStore);
        var project = await service.CreateBlankAsync(
            "Rollback after defaults",
            NewDir(),
            null,
            null,
            null,
            "user");
        var otherProject = await service.CreateBlankAsync(
            "Preserved project",
            NewDir(),
            null,
            null,
            null,
            "user");
        var committedDefault = BuiltInSkill(project.Id, "system-design");
        var preservedSkill = BuiltInSkill(otherProject.Id, "prototype-ux");
        await skillStore.InsertAsync(committedDefault);
        await skillStore.AssignAsync(project.Id, committedDefault.Id, "Tank", DateTimeOffset.UtcNow);
        await skillStore.InsertAsync(preservedSkill);
        await skillStore.AssignAsync(otherProject.Id, preservedSkill.Id, "Trinity", DateTimeOffset.UtcNow);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await service.RollbackCreationAsync(
            project.Id,
            runStore,
            new RunWorkflowRegistry(),
            cancelled.Token);

        (await projectStore.GetAsync(project.Id)).Should().BeNull();
        Directory.Exists(project.WorkingDirectory).Should().BeFalse();
        (await skillStore.ListByProjectAsync(project.Id)).Should().BeEmpty();
        (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().BeEmpty();
        (await skillStore.ListByProjectAsync(otherProject.Id))
            .Should().ContainSingle(skill => skill.Id == preservedSkill.Id);
        (await skillStore.ListAssignmentsByProjectAsync(otherProject.Id))
            .Should().ContainSingle(assignment => assignment.SkillId == preservedSkill.Id);
    }

    [Fact]
    public async Task RollbackCreationAsync_ProjectDeleteFailure_DoesNotPurgeSkillState()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var skillStore = new SqliteSkillStore(testDb.Db);
        var createService = BuildService(projectStore, db: testDb.Db);
        var project = await createService.CreateBlankAsync(
            "Rollback delete failure",
            NewDir(),
            null,
            null,
            null,
            "user");
        var skill = BuiltInSkill(project.Id, "system-design");
        await skillStore.InsertAsync(skill);
        await skillStore.AssignAsync(project.Id, skill.Id, "Tank", DateTimeOffset.UtcNow);
        var faultingStore = new FaultingProjectStore(projectStore, throwOnDelete: true);
        var rollbackService = BuildService(faultingStore, db: testDb.Db);

        var act = () => rollbackService.RollbackCreationAsync(
            project.Id,
            new SqliteRunStore(testDb.Db),
            new RunWorkflowRegistry());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*delete failure*");
        (await projectStore.GetAsync(project.Id)).Should().NotBeNull();
        (await skillStore.ListByProjectAsync(project.Id))
            .Should().ContainSingle(existing => existing.Id == skill.Id);
        (await skillStore.ListAssignmentsByProjectAsync(project.Id))
            .Should().ContainSingle(existing => existing.SkillId == skill.Id);
    }

    // =========================================================================
    // PC-06: RenameAsync updates name and returns true for existing project
    // =========================================================================
    [Fact]
    public async Task RenameAsync_UpdatesName_ReturnsTrue()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store, db: testDb.Db);
        var dir     = NewDir();
        var project = await service.CreateBlankAsync("Original", dir, null, null, null, "user");

        var result = await service.RenameAsync(project.Id, "Renamed");

        result.Should().BeTrue();
        var retrieved = await store.GetAsync(project.Id);
        retrieved!.Name.Should().Be("Renamed");
    }

    // =========================================================================
    // PC-07: RenameAsync returns false for unknown project
    // =========================================================================
    [Fact]
    public async Task RenameAsync_ReturnsFalse_ForUnknownProject()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store, db: testDb.Db);

        var result = await service.RenameAsync(ProjectId.New(), "New Name");

        result.Should().BeFalse();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    // =========================================================================
    // PC-09: CreateBlankAsync materializes default.yaml into .agentweaver/workflows/
    //   so the directory is visible/editable in the Workspace; the WorkflowRegistry
    //   treats the on-disk 'default' as the built-in copy (no reserved-id conflict)
    // =========================================================================
    [Fact]
    public async Task CreateBlankAsync_MaterializesDefaultWorkflowYaml()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store, db: testDb.Db);
        var dir     = NewDir();

        var project = await service.CreateBlankAsync("Workflow Check", dir, null, null, null, "user");

        var defaultYaml = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows", "default.yaml");
        File.Exists(defaultYaml).Should().BeTrue(
            "the default workflow is materialized so .agentweaver/workflows/ is visible/editable in the Workspace");
    }

    // =========================================================================
    // PC-10: WorkflowRegistry for a new blank project returns exactly ONE
    //   'default' workflow entry, and it must be Valid (built-in, no duplicate)
    // =========================================================================
    [Fact]
    public async Task WorkflowRegistry_NewBlankProject_ExactlyOneDefaultWorkflow_Valid()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store, db: testDb.Db);
        var dir     = NewDir();

        var project  = await service.CreateBlankAsync("Registry Check", dir, null, null, null, "user");
        var registry = new Agentweaver.Api.Workflows.WorkflowRegistry();
        var results  = registry.List(project);

        var defaultEntries = results.Where(r => r.Definition?.Id == "default" || r.Source == "built-in").ToList();
        defaultEntries.Should().HaveCount(1, "exactly one 'default' workflow must be present");
        defaultEntries[0].IsValid.Should().BeTrue("the built-in default must always be valid");
        defaultEntries[0].IsBuiltIn.Should().BeTrue("the single default entry must be the built-in");
    }

    // =========================================================================
    // PC-11: CreateBlankAsync materializes the GitHub Copilot agent definition
    //   into .github/agents/agentweaver.agent.md (agent-file-gen)
    // =========================================================================
    [Fact]
    public async Task CreateBlankAsync_MaterializesAgentDefinition()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store, db: testDb.Db);
        var dir     = NewDir();

        var project = await service.CreateBlankAsync("Agent Def Check", dir, null, null, null, "user");

        var agentFile = Path.Combine(project.WorkingDirectory, ".github", "agents", "agentweaver.agent.md");
        File.Exists(agentFile).Should().BeTrue(
            "the agent definition must be materialized into every new project's .github/agents/");
        File.ReadAllText(agentFile).Should().Be(
            Agentweaver.Api.Projects.AgentDefinitionTemplate.Content,
            "the materialized copy must match the embedded template");
    }

    /// <summary>Stub git initializer: creates directories without real git operations.</summary>
    private sealed class NoOpGitInitializer : ProjectGitInitializer
    {
        public NoOpGitInitializer()
            : base(NullLogger<ProjectGitInitializer>.Instance) { }

        public override string InitBlank(string workingDirectory, string defaultBranch)
        {
            Directory.CreateDirectory(workingDirectory);
            return defaultBranch;
        }

        public override string Clone(string workingDirectory, string sourceRepository, string accessToken)
        {
            Directory.CreateDirectory(workingDirectory);
            return "main";
        }
    }

    private static Skill BuiltInSkill(ProjectId projectId, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new Skill
        {
            Id = SkillId.New(),
            ProjectId = projectId,
            Name = name,
            Description = name,
            Instructions = "instructions",
            Provenance = SkillProvenance.BuiltIn,
            SourceLocation = $"catalog/skills/{name}",
            ContentHash = SkillParser.ComputeContentHash(name, name, "instructions", []),
            Status = SkillStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// IProjectStore wrapper that faults on InsertAsync to test rollback behavior.
    /// </summary>
    private sealed class FaultingProjectStore : IProjectStore
    {
        private readonly IProjectStore _inner;
        private readonly bool _throwOnInsert;
        private readonly bool _throwOnDelete;

        public FaultingProjectStore(
            IProjectStore inner,
            bool throwOnInsert = false,
            bool throwOnDelete = false)
        {
            _inner = inner;
            _throwOnInsert = throwOnInsert;
            _throwOnDelete = throwOnDelete;
        }

        public Task InsertAsync(Project project, CancellationToken ct = default)
        {
            if (_throwOnInsert)
                throw new InvalidOperationException("inject fault: simulated DB insert failure");
            return _inner.InsertAsync(project, ct);
        }

        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) =>
            _inner.GetAsync(id, ct);

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            _inner.ListAsync(ct);

        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateNameAsync(id, name, updatedAt, ct);

        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateProviderSettingsAsync(id, settings, updatedAt, ct);

        public Task UpdateGenerationModelSettingsAsync(ProjectId id, string? blueprintGenerationModel, string? workflowGenerationModel, string? outcomeSpecGenerationModel, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateGenerationModelSettingsAsync(id, blueprintGenerationModel, workflowGenerationModel, outcomeSpecGenerationModel, updatedAt, ct);

        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) =>
            _inner.TryBeginDeleteAsync(id, ct);

        public Task DeleteAsync(ProjectId id, CancellationToken ct = default)
        {
            if (_throwOnDelete)
                throw new InvalidOperationException("inject fault: simulated DB delete failure");
            return _inner.DeleteAsync(id, ct);
        }

        public Task UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdatePickupSettingsAsync(id, maxReadyPerHeartbeat, autopilot, autoApproveTools, updatedAt, ct);

        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateDefaultWorkflowAsync(id, workflowId, updatedAt, ct);

        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateActiveReviewPolicyAsync(id, policyName, updatedAt, ct);
        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateSandboxProfileAsync(id, sandboxProfile, updatedAt, ct);

        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateOriginAsync(id, origin, updatedAt, ct);

        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateSourceBlueprintAsync(id, blueprintId, blueprintType, updatedAt, ct);

        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateAllowedWorkflowIdsAsync(id, allowedWorkflowIds, updatedAt, ct);

        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) =>
            _inner.TryBeginTeamMutationAsync(id, expectedRevision, ct);
    }
}
