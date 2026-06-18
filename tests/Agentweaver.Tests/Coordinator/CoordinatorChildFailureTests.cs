using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly RunStreamStore _streamStore = new();
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunOrchestrator _orchestrator;

    public CoordinatorChildFailureTests()
    {
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

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

    [Theory]
    [InlineData(@"could not create worktree at C:\Users\asabbour\.local\share\agentweaver\worktrees\abc")]
    [InlineData("path /home/asabbour/.copilot/session-state/x rejected")]
    [InlineData("path /Users/asabbour/Git/scaffolders/y outside sandbox")]
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

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
    }
}
