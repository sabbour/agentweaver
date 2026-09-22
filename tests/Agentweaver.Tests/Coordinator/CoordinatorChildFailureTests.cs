using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="RunOrchestrator.MarkChildRunFailedAsync"/> (Feature 008 Defect B). When
/// <c>StartChildRunAsync</c> throws BEFORE it can persist the child run row (e.g. worktree creation
/// fails), the dispatched subtask would otherwise carry a childRunId that <c>GET /api/runs/{id}</c>
/// cannot find — an empty execution log. This method must leave a retrievable terminal FAILED run row
/// and a non-empty execution log (a persisted RunFailed event).
///
/// Real stores, no mocks (Principle VII): a real <see cref="SqliteRunStore"/> for the run row and a
/// real EF <see cref="MemoryDbContext"/> (in-memory SQLite) for the persisted events.
/// </summary>
public sealed class CoordinatorChildFailureTests : IAsyncDisposable
{
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunOrchestrator _orchestrator;
    private readonly List<string> _tempDirs = [];

    public CoordinatorChildFailureTests()
    {
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        services.AddDbContextFactory<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        var memoryConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MemoryContext:MaxTokens"] = "1",
            })
            .Build();
        services.AddSingleton<IConfiguration>(memoryConfiguration);
        services.AddScoped(sp => new MemoryContextCompiler(
            sp.GetRequiredService<MemoryDbContext>(), memoryConfiguration));
        var secrets = new InMemorySecretStore();
        services.AddSingleton<ISecretStore>(secrets);
        services.AddScoped<GitHubConnectionsPersistenceStore>();
        services.AddScoped<ByokProviderConfigurationService>();
        services.AddScoped<EffectiveModelProviderResolver>();
        services.AddSingleton<IRunEventStream, EfRunEventStream>();
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.Database.EnsureCreated();
            db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                EntraObjectId = "platform-admin",
                CredentialReference = "copilot-app-platform-default-context-budget-test",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        secrets.SetSecretAsync(
            "copilot-app-platform-default-context-budget-test",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "signed-in",
                accessToken = "test-token",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                githubLogin = "test-user",
            })).GetAwaiter().GetResult();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _streamStore = new RunStreamStore(_provider.GetRequiredService<IRunEventStream>());

        _orchestrator = new RunOrchestrator(
            _runStore,
            _streamStore,
            worktreeManager: null!,
            workflowFactory: null!,
            registry: null!,
            watchLoop: null!,
            _scopeFactory,
            configuration: null!,
            NullLogger<RunOrchestrator>.Instance);
    }

    [Fact]
    public async Task PreStartFailure_PersistsRetrievableFailedRun_AndRunFailedEvent()
    {
        var childRun = NewChildRun();

        // Simulate StartChildRunAsync throwing during worktree creation (before any InsertAsync).
        await _orchestrator.MarkChildRunFailedAsync(
            childRun, new InvalidOperationException("worktree creation failed"), default);

        // The child run row is now retrievable and terminal (FAILED), carrying the error message.
        var fetched = await _runStore.GetAsync(childRun.Id);
        fetched.Should().NotBeNull("the failed child must be retrievable via GET /api/runs/{id}");
        fetched!.Status.Should().Be(RunStatus.Failed);
        fetched.EndedAt.Should().NotBeNull();
        fetched.Result.Should().Contain("worktree creation failed");

        // The execution log is non-empty: a RunFailed event was recorded on the stream...
        var runId = childRun.Id.ToString();
        var streamEvents = _streamStore.Get(runId)!.GetSnapshotSince(0).Events;
        streamEvents.Should().Contain(e => e.Type == EventTypes.RunFailed);
        _streamStore.Get(runId)!.IsCompleted.Should().BeTrue("the failed child stream is terminal");

        // ...and persisted to RunEvents so the log survives stream eviction.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.RunEvents.AnyAsync(e => e.RunId == runId && e.EventType == EventTypes.RunFailed))
            .Should().BeTrue("the RunFailed event must be persisted for retrieval after eviction");
    }

    [Fact]
    public async Task WhenRunRowAlreadyInserted_TransitionsItToFailed_WithoutThrowing()
    {
        var childRun = NewChildRun();

        // A later failure path: the InProgress row was already inserted before the throw.
        await _runStore.InsertAsync(childRun);

        await _orchestrator.MarkChildRunFailedAsync(
            childRun, new InvalidOperationException("workflow start boom"), default);

        var fetched = await _runStore.GetAsync(childRun.Id);
        fetched!.Status.Should().Be(RunStatus.Failed, "an existing InProgress row must be terminalized");
        fetched.Result.Should().Contain("workflow start boom");
    }

    [Fact]
    public async Task DispatchFailureFallback_WhenTerminalCasLoses_DoesNotDuplicateRunFailed()
    {
        var childRun = NewChildRun();
        await _runStore.InsertAsync(childRun);
        (await _runStore.TrySetTerminalStatusAsync(
            childRun.Id, RunStatus.Failed, DateTimeOffset.UtcNow, "already_failed", default))
            .Should().BeTrue();

        var entry = _streamStore.Create(childRun.Id.ToString(), childRun.SubmittingUser);
        entry.RecordNext(EventTypes.RunFailed, new { reason = "already_failed" });
        _streamStore.Complete(childRun.Id.ToString());

        await _orchestrator.MarkChildRunFailedAsync(
            childRun, new InvalidOperationException("late dispatch failure"), default);

        entry.GetSnapshotSince(0).Events.Should().ContainSingle(e => e.Type == EventTypes.RunFailed);
        entry.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task StartChildRunAsync_WhenLaunchFailsAfterWorktreeCreation_CleansUpChildWorktree()
    {
        var (repoPath, worktreesBase) = CreateRepository();
        var manager = BuildWorktreeManager(worktreesBase);
        var orchestrator = new RunOrchestrator(
            _runStore,
            _streamStore,
            manager,
            workflowFactory: null!,
            registry: null!,
            watchLoop: null!,
            _scopeFactory,
            configuration: null!,
            NullLogger<RunOrchestrator>.Instance);
        var childRun = NewChildRun() with { RepositoryPath = repoPath, OriginatingBranch = "main" };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            orchestrator.StartChildRunAsync(childRun, default));

        var expectedWorktreePath = Path.Combine(worktreesBase, childRun.Id.ToString());
        Directory.Exists(expectedWorktreePath).Should().BeFalse(
            "failed child launch must remove the per-child worktree it just created");
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("child")]
    [InlineData("reserved")]
    public async Task MandatoryContextBudgetFailureBeforeWorkflowStart_TerminalizesRunAndCompletesStream(
        string launchKind)
    {
        var (repoPath, worktreesBase) = CreateRepository();
        var manager = BuildWorktreeManager(worktreesBase);
        var orchestrator = new RunOrchestrator(
            _runStore,
            _streamStore,
            manager,
            workflowFactory: null!,
            registry: null!,
            watchLoop: null!,
            _scopeFactory,
            configuration: null!,
            NullLogger<RunOrchestrator>.Instance);
        var projectId = ProjectId.New();
        var run = NewChildRun() with
        {
            RepositoryPath = repoPath,
            OriginatingBranch = "main",
            ProjectId = projectId,
            ParentRunId = launchKind == "child" ? RunId.New().ToString() : null,
            Status = launchKind == "reserved" ? RunStatus.Pending : RunStatus.InProgress,
        };

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.Decisions.Add(new Decision
            {
                ProjectId = projectId.ToString(),
                AgentName = run.AgentName!,
                Type = "architectural",
                Status = "active",
                Title = "Mandatory boundary",
                Content = new string('d', 128),
                TrustState = MemoryTrustStates.Approved,
                SourceKind = MemorySourceKinds.Run,
                SourceIdentity = "run:context-budget",
                ApprovedBy = "human:alice",
                ApprovedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        if (launchKind == "reserved")
            await _runStore.InsertAsync(run);

        Func<Task> start = launchKind switch
        {
            "normal" => () => orchestrator.StartRunAsync(run, CancellationToken.None),
            "child" => () => orchestrator.StartChildRunAsync(run, CancellationToken.None),
            "reserved" => () => orchestrator.StartReservedProjectRunAsync(run, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(launchKind)),
        };

        await start.Should().ThrowAsync<MandatoryContextBudgetExceededException>();

        var persisted = await _runStore.GetAsync(run.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(RunStatus.Failed);
        persisted.EndedAt.Should().NotBeNull();
        persisted.Result.Should().Contain(nameof(MandatoryContextBudgetExceededException));

        var entry = _streamStore.Get(run.Id.ToString());
        entry.Should().NotBeNull();
        entry!.IsCompleted.Should().BeTrue();
        entry.GetSnapshotSince(0).Events.Should().ContainSingle(e =>
            e.Type == EventTypes.RunFailed
            && System.Text.Json.JsonSerializer.Serialize(e.Payload)
                .Contains("mandatory_context_budget_exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SteeringRevision_MandatoryContextBudgetFailure_TerminalizesReopenedStream()
    {
        var projectId = ProjectId.New();
        var run = NewChildRun() with
        {
            ProjectId = projectId,
            WorktreePath = "existing-worktree",
            WorktreeBranch = "agentweaver/existing",
        };
        await _runStore.InsertAsync(run);
        await SeedMandatoryDecisionAsync(projectId, run.AgentName!);

        var entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);
        entry.RecordNext(EventTypes.RunCompleted, new { result = "prior turn complete" });
        _streamStore.Complete(run.Id.ToString());
        _streamStore.Reopen(run.Id.ToString());

        var act = () => _orchestrator.StartRevisionAsync(
            run, "redirect to the corrected task", CancellationToken.None, isChild: true);

        await act.Should().ThrowAsync<MandatoryContextBudgetExceededException>();

        (await _runStore.GetAsync(run.Id))!.Status.Should().Be(RunStatus.Failed);
        entry.IsCompleted.Should().BeTrue();
        entry.GetSnapshotSince(0).Events.Should().ContainSingle(e =>
            e.Type == EventTypes.RunFailed
            && System.Text.Json.JsonSerializer.Serialize(e.Payload)
                .Contains("mandatory_context_budget_exceeded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"could not create worktree at C:\Users\asabbour\.local\share\agentweaver\worktrees\abc")]
    [InlineData("path /home/asabbour/.copilot/session-state/x rejected")]
    [InlineData("path /Users/asabbour/Git/agentweaver/y outside sandbox")]
    public void RedactFailureReason_MasksUserHomePaths(string message)
    {
        var reason = RunOrchestrator.RedactFailureReason(new InvalidOperationException(message));

        reason.Should().Contain("<redacted>");
        reason.Should().NotContain("asabbour", "the OS login name must not leak into a persisted, user-visible log");
        reason.Should().StartWith("InvalidOperationException", "the exception type prefixes the reason");
    }

    [Fact]
    public void RedactFailureReason_CapsLength()
    {
        var reason = RunOrchestrator.RedactFailureReason(new Exception(new string('x', 5000)));
        reason.Length.Should().BeLessThanOrEqualTo(520, "the reason is length-capped before persistence");
    }

    private static Run NewChildRun() => new()
    {
        Id = RunId.New(),
        RepositoryPath = "child-repo",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "do the subtask",
        SubmittingUser = "alice",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        AgentName = "morpheus",
        ParentRunId = RunId.New().ToString(),
        SubtaskId = "7",
    };

    private async Task SeedMandatoryDecisionAsync(ProjectId projectId, string agentName)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.Decisions.Add(new Decision
        {
            ProjectId = projectId.ToString(),
            AgentName = agentName,
            Type = "architectural",
            Status = "active",
            Title = "Mandatory boundary",
            Content = new string('d', 128),
            TrustState = MemoryTrustStates.Approved,
            SourceKind = MemorySourceKinds.Run,
            SourceIdentity = "run:context-budget",
            ApprovedBy = "human:alice",
            ApprovedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private (string RepoPath, string WorktreesBase) CreateRepository()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"aw-child-failure-repo-{Guid.NewGuid():N}");
        var worktreesBase = Path.Combine(Path.GetTempPath(), $"aw-child-failure-wt-{Guid.NewGuid():N}");
        _tempDirs.Add(repoPath);
        _tempDirs.Add(worktreesBase);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "init");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("init", sig, sig);
        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");
        Commands.Checkout(repo, repo.Head.Tip);

        return (repoPath, worktreesBase);
    }

    private static WorktreeManager BuildWorktreeManager(string worktreesBase)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = worktreesBase,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@test.com",
            })
            .Build();
        return new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
