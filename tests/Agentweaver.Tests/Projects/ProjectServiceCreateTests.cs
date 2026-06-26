using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Domain;
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
        IGitHubTokenScopeProvider? scopeProvider = null)
    {
        workspace     ??= new LocalFilesystemWorkspaceProvider();
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
        var service = BuildService(store);
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
        var service = BuildService(store);
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
        var service    = BuildService(store, tokenStore: tokenStore);
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

        var service = BuildService(store, tokenStore: tokenStore);
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
        var service     = BuildService(faultyStore);
        var dir         = NewDir();

        var act = async () =>
            await service.CreateBlankAsync("RollbackProject", dir, null, null, null, "user");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inject fault*");

        // The directory that was app-created must be removed on rollback.
        Directory.Exists(dir).Should().BeFalse("rollback must remove app-created directory");
    }

    // =========================================================================
    // PC-06: RenameAsync updates name and returns true for existing project
    // =========================================================================
    [Fact]
    public async Task RenameAsync_UpdatesName_ReturnsTrue()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var service = BuildService(store);
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
        var service = BuildService(store);

        var result = await service.RenameAsync(ProjectId.New(), "New Name");

        result.Should().BeFalse();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

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

    /// <summary>
    /// IProjectStore wrapper that faults on InsertAsync to test rollback behavior.
    /// </summary>
    private sealed class FaultingProjectStore : IProjectStore
    {
        private readonly IProjectStore _inner;
        private readonly bool _throwOnInsert;

        public FaultingProjectStore(IProjectStore inner, bool throwOnInsert)
        {
            _inner = inner;
            _throwOnInsert = throwOnInsert;
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

        public Task UpdateWorkingDirectoryAsync(ProjectId id, string workingDirectory, string defaultBranch, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateWorkingDirectoryAsync(id, workingDirectory, defaultBranch, updatedAt, ct);

        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) =>
            _inner.TryBeginDeleteAsync(id, ct);

        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) =>
            _inner.DeleteAsync(id, ct);

        public Task UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdatePickupSettingsAsync(id, maxReadyPerHeartbeat, autopilot, autoApproveTools, updatedAt, ct);

        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateDefaultWorkflowAsync(id, workflowId, updatedAt, ct);

        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateActiveReviewPolicyAsync(id, policyName, updatedAt, ct);
        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateSandboxProfileAsync(id, sandboxProfile, updatedAt, ct);

        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateSourceBlueprintAsync(id, blueprintId, blueprintType, updatedAt, ct);

        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            _inner.UpdateAllowedWorkflowIdsAsync(id, allowedWorkflowIds, updatedAt, ct);
    }
}
