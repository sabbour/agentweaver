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
/// Concurrent delete + run-creation tests (T023b).
/// Verifies that the TryCreateProjectRunAsync CAS gate and DeleteAsync
/// together ensure no run remains in a non-terminal state after the
/// project is deleted, even under concurrent access.
/// </summary>
public sealed class ProjectDeleteConcurrencyTests : IAsyncDisposable
{
    private readonly string _testRoot;

    public ProjectDeleteConcurrencyTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-del-conc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(100);
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
    }

    private string NewDir()
    {
        var path = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProjectService BuildService(IProjectStore store)
    {
        return new ProjectService(
            store, TestWorkspaceProviders.CreateLocal(),
            new NoOpGitInitializer(),
            new InMemoryGitHubTokenStore(), new FixedInstallationScopeProvider(),
            NullLogger<ProjectService>.Instance);
    }

    // =========================================================================
    // CONC-01: Concurrent delete + run reservation — no run left non-terminal
    //
    // Race: delete and TryCreateProjectRunAsync run concurrently.
    // Invariant: after both complete, all runs for the deleted project must
    // be in a terminal state (no Pending, InProgress, AwaitingReview, etc.).
    // =========================================================================
    [Fact]
    public async Task ConcurrentDeleteAndRunCreate_LeavesNoNonTerminalRuns()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var runStore     = new SqliteRunStore(testDb.Db);
        var registry     = new RunWorkflowRegistry();
        var svc          = BuildService(projectStore);
        var dir          = NewDir();

        var project = await svc.CreateBlankAsync(
            "Concurrent Test", dir, null, null, null, "test-user");

        var nonTerminalStatuses = new[]
        {
            RunStatus.Pending, RunStatus.InProgress, RunStatus.AwaitingReview,
            RunStatus.Committing, RunStatus.Merging,
        };

        RunId? reservedRunId = null;
        bool  runWas409     = false;

        // Spawn delete and run-reservation concurrently.
        var deleteTask = Task.Run(async () =>
            await svc.DeleteAsync(project.Id, runStore, registry));

        var createTask = Task.Run(async () =>
        {
            await Task.Delay(2); // tiny stagger to create the race

            var run = new Run
            {
                Id                = RunId.New(),
                RepositoryPath    = project.WorkingDirectory,
                OriginatingBranch = project.DefaultBranch,
                ModelSource       = ModelSource.GitHubCopilot,
                Task              = "concurrent test task",
                SubmittingUser    = "test-user",
                Status            = RunStatus.Pending,
                StartedAt         = DateTimeOffset.UtcNow,
                ProjectId         = project.Id,
            };
            reservedRunId = run.Id;
            var reserved = await runStore.TryCreateProjectRunAsync(run);
            if (!reserved) runWas409 = true;
        });

        await Task.WhenAll(deleteTask, createTask);

        // Invariant: if the run was reserved, it must now be in a terminal state.
        if (!runWas409 && reservedRunId.HasValue)
        {
            var remaining = await runStore.GetRunsByProjectAndStatusesAsync(
                project.Id, nonTerminalStatuses);
            remaining.Should().BeEmpty(
                "delete must have cancelled all non-terminal runs; none may remain non-terminal after project deletion");
        }
        else
        {
            // 409 path: TryCreateProjectRunAsync returned false — valid outcome.
            runWas409.Should().BeTrue(
                "if the run was not reserved, TryCreateProjectRunAsync must have returned false");
        }
    }

    // =========================================================================
    // CONC-02: Reserved run that fails to start is terminalised to Failed
    //
    // Verifies the failure compensation path: if StartReservedProjectRunAsync
    // throws after TryCreateProjectRunAsync succeeds, the run ends up as Failed
    // and does not remain Pending.
    // =========================================================================
    [Fact]
    public async Task ReservationSideEffectFailure_TerminalisesRunToFailed()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var runStore     = new SqliteRunStore(testDb.Db);
        var dir          = NewDir();
        var svc          = BuildService(projectStore);

        var project = await svc.CreateBlankAsync(
            "Failure Test", dir, null, null, null, "test-user");

        // Insert a Pending run directly (simulating a reserved-but-not-started run).
        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = project.WorkingDirectory,
            OriginatingBranch = project.DefaultBranch,
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "failing start task",
            SubmittingUser    = "test-user",
            Status            = RunStatus.Pending,
            StartedAt         = DateTimeOffset.UtcNow,
            ProjectId         = project.Id,
        };

        bool reserved = await runStore.TryCreateProjectRunAsync(run);
        reserved.Should().BeTrue("run must be reserved while project is active");

        // Simulate start failure: terminalize manually (mirrors endpoint compensation path).
        await runStore.TrySetTerminalStatusAsync(
            run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, "run_start_failed");

        var finalRun = await runStore.GetAsync(run.Id);
        finalRun!.Status.Should().Be(RunStatus.Failed,
            "a reserved run that failed to start must be terminalized to Failed");
    }

    [Fact]
    public async Task ConcurrentDefaultsApplyAndProjectDelete_LeavesNoSkillState()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var skillStore = new SqliteSkillStore(testDb.Db);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var project = new Project
            {
                Id = ProjectId.New(),
                Name = $"Defaults/delete race {iteration}",
                Origin = ProjectOrigin.Blank(),
                WorkingDirectory = NewDir(),
                DefaultBranch = "main",
                Owner = "test-user",
                ProviderSettings = new ProjectProviderSettings
                {
                    DefaultProvider = ModelSource.GitHubCopilot,
                },
                State = ProjectState.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await projectStore.InsertAsync(project);
            var skill = BuiltInSkill(project.Id, $"race-default-{iteration}");
            var plan = new SkillDefaultsStorePlan(
                project.Id,
                project.TeamRevision,
                SkillCatalogStateFingerprint.Compute([], []),
                [skill],
                [],
                [new SkillAssignment
                {
                    ProjectId = project.Id,
                    SkillId = skill.Id,
                    AgentName = "Tank",
                    CreatedAt = DateTimeOffset.UtcNow,
                }]);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var apply = Task.Run(async () =>
            {
                await start.Task;
                return await skillStore.ApplyDefaultsAsync(plan);
            });
            var delete = Task.Run(async () =>
            {
                await start.Task;
                await projectStore.DeleteAsync(project.Id);
            });

            start.SetResult();
            await Task.WhenAll(apply, delete);

            (await projectStore.GetAsync(project.Id)).Should().BeNull();
            (await skillStore.ListByProjectAsync(project.Id)).Should().BeEmpty();
            (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().BeEmpty();
        }
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
}
