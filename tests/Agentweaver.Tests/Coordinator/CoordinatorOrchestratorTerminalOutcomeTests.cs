using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorOrchestratorTerminalOutcomeTests : IAsyncDisposable
{
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly ServiceProvider _services;

    public CoordinatorOrchestratorTerminalOutcomeTests()
    {
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);
        var services = new ServiceCollection();
        services.AddScoped<IRunStore>(_ => _runStore);
        _services = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FailNoTeamAsync_LosingTerminalTransition_DoesNotAppendFailureOrCompleteStream()
    {
        var run = new Run
        {
            Id = RunId.New(),
            AgentName = "Coordinator",
            Status = RunStatus.InProgress,
            RepositoryPath = ".",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test no-team terminal ownership",
            SubmittingUser = "owner",
            StartedAt = DateTimeOffset.UtcNow,
            Origin = RunOrigin.Interactive,
        };
        await _runStore.InsertAsync(run);
        await _runStore.TrySetTerminalOutcomeForCurrentGenerationAsync(
            run.Id,
            RunStatus.Completed,
            EventTypes.RunCompleted,
            new { result = "another replica won" },
            DateTimeOffset.UtcNow,
            "another replica won");

        var streams = new RunStreamStore();
        var entry = streams.Create(run.Id.ToString(), run.SubmittingUser);
        var executor = new CoordinatorOrchestratorExecutor(
            new ThrowingWorkflowAgentFactory(),
            streams,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLoggerFactory.Instance,
            new FakeStoryIndependenceClassifier(),
            new FakeAssemblyGateCodeClassifier(),
            "gpt-5-mini",
            null,
            null);

        await executor.FailNoTeamAsync(run.Id.ToString(), CancellationToken.None);

        entry.GetSnapshotSince(0).Events.Should().NotContain(e => e.Type == EventTypes.RunFailed,
            "a replica that loses the generation-fenced terminal transition must not publish a stale failure");
        entry.IsCompleted.Should().BeFalse(
            "a replica that loses the terminal transition must not complete the live stream");
    }

    public async ValueTask DisposeAsync()
    {
        _services.Dispose();
        await _runDb.DisposeAsync();
    }

    private sealed class ThrowingWorkflowAgentFactory : IWorkflowAgentFactory
    {
        public IWorkflowTurnAgent CreateWorkerAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateRaiAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateRubberduckAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateBuildTestAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateScribeAgent() => throw new NotSupportedException();
    }
}
