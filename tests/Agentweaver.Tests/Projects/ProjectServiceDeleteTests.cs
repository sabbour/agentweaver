using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Tests for ProjectService.DeleteAsync: CAS gate, run cancellation, and
/// record-only delete semantics.
/// </summary>
public sealed class ProjectServiceDeleteTests : IAsyncDisposable
{
    private readonly string _testRoot;

    public ProjectServiceDeleteTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-svc-del-{Guid.NewGuid():N}");
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

    private string NewDir()
    {
        var path = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProjectService BuildService(
        IProjectStore store,
        IProjectWorkspaceProvider? workspace = null)
    {
        workspace ??= new LocalFilesystemWorkspaceProvider();
        return new ProjectService(
            store, workspace, new NoOpGitInitializer(),
            new InMemoryGitHubTokenStore(), new FixedInstallationScopeProvider(),
            NullLogger<ProjectService>.Instance);
    }

    private static async Task<Project> CreateProjectAsync(
        ProjectService svc, string dir)
    {
        return await svc.CreateBlankAsync(
            "Delete Test Project", dir, null, null, null, "test-user");
    }

    private static Run MakeActiveRun(ProjectId projectId, RunStatus status = RunStatus.InProgress) => new()
    {
        Id                = RunId.New(),
        RepositoryPath    = "dummy-repo",
        OriginatingBranch = "main",
        ModelSource       = ModelSource.GitHubCopilot,
        Task              = "test task",
        SubmittingUser    = "test-user",
        Status            = status,
        StartedAt         = DateTimeOffset.UtcNow,
        ProjectId         = projectId,
    };

    // =========================================================================
    // PD-01: DeleteAsync returns false when project is already Deleting
    // =========================================================================
    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenAlreadyDeleting()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var runStore     = new SqliteRunStore(testDb.Db);
        var registry     = new RunWorkflowRegistry();
        var svc          = BuildService(projectStore);
        var dir          = NewDir();

        var project = await CreateProjectAsync(svc, dir);

        // First delete transitions to Deleting and returns true.
        var first = await svc.DeleteAsync(project.Id, runStore, registry);
        first.Should().BeTrue();

        // Trying to delete again (project record was removed by first delete's cleanup)
        // returns false because TryBeginDeleteAsync finds no active record.
        var second = await svc.DeleteAsync(project.Id, runStore, registry);
        second.Should().BeFalse();
    }

    // =========================================================================
    // PD-02: TryBeginDeleteAsync sets state to Deleting in DB
    // =========================================================================
    [Fact]
    public async Task TryBeginDeleteAsync_SetsStateToDeleting()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var svc          = BuildService(projectStore);
        var dir          = NewDir();

        var project = await CreateProjectAsync(svc, dir);

        // Directly test the CAS gate
        var result = await projectStore.TryBeginDeleteAsync(project.Id);
        result.Should().BeTrue();

        var retrieved = await projectStore.GetAsync(project.Id);
        retrieved!.State.Should().Be(ProjectState.Deleting);
    }

    // =========================================================================
    // PD-03: DeleteAsync cancels non-terminal runs (transitions to Failed)
    // =========================================================================
    [Fact]
    public async Task DeleteAsync_CancelsNonTerminalRuns()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var runStore     = new SqliteRunStore(testDb.Db);
        var registry     = new RunWorkflowRegistry();
        var svc          = BuildService(projectStore);
        var dir          = NewDir();

        var project = await CreateProjectAsync(svc, dir);

        // Insert a non-terminal run associated with this project
        var run = MakeActiveRun(project.Id, RunStatus.InProgress);
        await runStore.InsertAsync(run);

        await svc.DeleteAsync(project.Id, runStore, registry);

        var cancelledRun = await runStore.GetAsync(run.Id);
        cancelledRun!.Status.Should().Be(RunStatus.Failed,
            "non-terminal runs must be cancelled to Failed when their project is deleted");
    }

    // =========================================================================
    // PD-04: DeleteAsync removes the project record after cancelling runs
    // =========================================================================
    [Fact]
    public async Task DeleteAsync_RemovesProjectRecord()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var runStore     = new SqliteRunStore(testDb.Db);
        var registry     = new RunWorkflowRegistry();
        var svc          = BuildService(projectStore);
        var dir          = NewDir();

        var project = await CreateProjectAsync(svc, dir);

        await svc.DeleteAsync(project.Id, runStore, registry);

        var result = await projectStore.GetAsync(project.Id);
        result.Should().BeNull("project record must be removed after delete");
    }

    // =========================================================================
    // PD-05: DeleteAsync returns false for unknown project id
    // =========================================================================
    [Fact]
    public async Task DeleteAsync_ReturnsFalse_ForUnknownProject()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var runStore     = new SqliteRunStore(testDb.Db);
        var registry     = new RunWorkflowRegistry();
        var svc          = BuildService(projectStore);

        var result = await svc.DeleteAsync(ProjectId.New(), runStore, registry);

        result.Should().BeFalse();
    }

    // =========================================================================
    // PD-06: Runs in terminal states are NOT re-transitioned
    // =========================================================================
    [Fact]
    public async Task DeleteAsync_DoesNotAffectTerminalRuns()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var runStore     = new SqliteRunStore(testDb.Db);
        var registry     = new RunWorkflowRegistry();
        var svc          = BuildService(projectStore);
        var dir          = NewDir();

        var project = await CreateProjectAsync(svc, dir);

        // Insert a run that is already terminal (Merged)
        var run = MakeActiveRun(project.Id, RunStatus.Merged);
        await runStore.InsertAsync(run);

        await svc.DeleteAsync(project.Id, runStore, registry);

        var terminalRun = await runStore.GetAsync(run.Id);
        terminalRun!.Status.Should().Be(RunStatus.Merged,
            "terminal runs must not be re-transitioned by delete");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

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
}
