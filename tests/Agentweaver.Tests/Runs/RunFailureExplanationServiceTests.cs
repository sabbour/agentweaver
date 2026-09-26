using Agentweaver.Api.Execution;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Runs;

public sealed class RunFailureExplanationServiceTests
{
    [Fact]
    public async Task GetAsync_IdentifiesTheActualEffectivePermissionGate()
    {
        await using var sqlite = await TestSqliteDb.CreateAsync();
        var run = CreateRun();
        await new SqliteRunStore(sqlite.Db).InsertAsync(run);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
        await using var memory = new MemoryDbContext(options);
        await memory.Database.EnsureCreatedAsync();

        var binding = EffectivePermissionBinding.Create(
            run.Id.ToString(),
            run.LifecycleGeneration,
            "current-project-sandbox-policy",
            $"project:{run.ProjectId}",
            new SandboxPolicy
            {
                RepositoryPath = run.RepositoryPath,
            }) with
        {
            AllowedOperations = [EffectivePermissionOperations.WorkspaceRead],
        };
        var timestamp = DateTimeOffset.Parse("2026-09-26T12:00:10Z");
        var terminalEvent = StructuredRunFailureTerminal.NormalizeFailure(new RunEvent(
            4,
            EventTypes.RunFailed,
            new
            {
                errorCode = "coordinator_execution_failed",
                retryable = false,
                toolCallId = "call-denied",
            },
            timestamp));
        var events = new[]
        {
            new RunEvent(1, EventTypes.PermissionBindingBound, binding, timestamp.AddSeconds(-3)),
            new RunEvent(2, EventTypes.ToolCall, new { callId = "call-denied", name = "apply_patch" }, timestamp.AddSeconds(-2)),
            new RunEvent(3, EventTypes.RunDegraded, new
            {
                reason = $"Operation denied: '{EffectivePermissionOperations.WorkspaceWrite}'",
                callId = "call-denied",
                toolName = "apply_patch",
                permissionAttempt = 1,
                permissionBindingId = binding.BindingId,
                permissionBindingVersion = binding.Version,
                permissionSource = binding.Source,
            }, timestamp.AddSeconds(-1)),
            terminalEvent,
        };
        memory.RunEvents.Add(new RunEventRecord
        {
            RunId = run.Id.ToString(),
            Sequence = 4,
            EventType = EventTypes.RunFailed,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(terminalEvent.Payload),
            CreatedAt = timestamp.UtcDateTime,
        });
        await memory.SaveChangesAsync();

        var eventStream = new FixedEventStream(events);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = sqlite.FilePath })
            .Build();
        var identityReader = new ExecutionIdentityReader(
            memory,
            sqlite.Db,
            configuration,
            eventStream,
            new FixedPermissionBindingProvider(binding));
        var service = new RunFailureExplanationService(
            new RunTerminalDiagnosticReader(memory),
            identityReader,
            eventStream,
            NullLogger<RunFailureExplanationService>.Instance);

        var result = await service.GetAsync(run, CancellationToken.None);

        result.Should().NotBeNull();
        result!.DenialGate.Should().NotBeNull();
        result.DenialGate!.Gate.Should().Be("effective_permission");
        result.DenialGate.Capability.Should().Be(EffectivePermissionOperations.WorkspaceWrite);
        result.DenialGate.PermissionBindingId.Should().Be(binding.BindingId);
        result.SupportedInterpretations.Should().ContainSingle(item => item.Code == "attributable_denial");
        result.NextActions.Should().ContainSingle(item => item.Kind == "authorization_or_configuration_repair");
        result.EvidenceReferences.Should().Contain(item => item.ToolCallId == "call-denied");
    }

    private static Run CreateRun() => new()
    {
        Id = RunId.Parse("00000000-0000-0000-0000-000000001403"),
        RepositoryPath = @"C:\repo",
        OriginatingBranch = "dev",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "write a file",
        SubmittingUser = "principal-123",
        Status = RunStatus.Failed,
        StartedAt = DateTimeOffset.Parse("2026-09-26T12:00:00Z"),
        EndedAt = DateTimeOffset.Parse("2026-09-26T12:00:10Z"),
        ProjectId = ProjectId.Parse("00000000-0000-0000-0000-000000001400"),
        AgentName = "Tank",
    };

    private sealed class FixedPermissionBindingProvider(EffectivePermissionBinding binding)
        : IEffectivePermissionBindingProvider
    {
        public Task<EffectivePermissionBinding> ResolveAsync(
            string runId,
            string repositoryPath,
            EffectivePermissionBinding? ceiling = null,
            CancellationToken ct = default) =>
            Task.FromResult(binding);

        public Task<EffectivePermissionBinding> ResolveForInspectionAsync(
            string runId,
            string repositoryPath,
            CancellationToken ct = default) =>
            Task.FromResult(binding);
    }

    private sealed class FixedEventStream(IReadOnlyList<RunEvent> events) : IRunEventStream
    {
        public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<RunEvent> SubscribeAsync(
            string runId,
            int fromSequence = 0,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask CompleteAsync(string runId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public Task<IReadOnlyList<RunEvent>> GetPersistedEventsAsync(
            string runId,
            int fromSequence = 0,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunEvent>>(
                events.Where(evt => evt.Sequence > fromSequence).OrderBy(evt => evt.Sequence).ToArray());
    }
}
