using Agentweaver.Api.Execution;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.AspNetCore;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Runs;

public sealed class RunFailureExplanationServiceTests
{
    [Theory]
    [InlineData("coordinator_outcome_spec_draft_stalled")]
    [InlineData("mandatory_context_budget_exceeded")]
    public void Sanitizer_PreservesCanonicalTerminalCodes(string code)
    {
        var result = RunFailureDiagnosticSanitizer.Sanitize(new RunTerminalDiagnosticResponse
        {
            Code = code,
            Message = "untrusted",
            Component = "coordinator",
            Timestamp = DateTimeOffset.Parse("2026-09-26T12:00:00Z"),
            Retryable = false,
        });

        result.Code.Should().Be(code);
        result.Message.Should().NotContain("agent_turn_internal_error");
    }

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

    [Fact]
    public async Task GetAsync_ClassifiesRecoveredToolErrorsAsFactsWithoutClaimingCausality()
    {
        var run = CreateRun();
        var timestamp = DateTimeOffset.Parse("2026-09-26T12:00:10Z");
        var result = await ExplainAsync(run,
        [
            new RunEvent(2, EventTypes.ToolError, new { callId = "call-recovered" }, timestamp.AddSeconds(-2)),
            new RunEvent(3, EventTypes.ToolResult, new { callId = "call-recovered" }, timestamp.AddSeconds(-1)),
            FailureEvent(4, "coordinator_execution_failed", timestamp),
        ]);

        result.ObservedFacts.Should().ContainSingle(item => item.Code == "tool_error_recovered");
        result.Unknowns.Should().NotContain(item =>
            item.Code == "tool_error_causality_unknown"
            && item.EvidenceReferenceIds.Contains("event-2"));
        result.Unknowns.Should().ContainSingle(item => item.Code == "root_cause_not_attributable");
        result.Completeness.Should().Be("partial",
            "missing execution identity is explicitly represented rather than inferred");
    }

    [Fact]
    public async Task GetAsync_ReportsConflictingSuccessfulToolEvidenceAsUnknown()
    {
        var run = CreateRun();
        var timestamp = DateTimeOffset.Parse("2026-09-26T12:00:10Z");
        var result = await ExplainAsync(run,
        [
            new RunEvent(1, EventTypes.ToolCall, new { callId = "call-conflict", name = "apply_patch" }, timestamp.AddSeconds(-2)),
            new RunEvent(2, EventTypes.ToolResult, new { callId = "call-conflict" }, timestamp.AddSeconds(-1)),
            FailureEvent(3, "coordinator_execution_failed", timestamp, "call-conflict"),
        ]);

        result.DenialGate.Should().BeNull();
        result.Unknowns.Should().ContainSingle(item => item.Code == "conflicting_tool_evidence")
            .Which.EvidenceReferenceIds.Should().Contain(["event-3", "decision-2"]);
        result.SupportedInterpretations.Should().NotContain(item => item.Code == "attributable_denial");
        result.Completeness.Should().Be("partial");
    }

    [Fact]
    public async Task GetAsync_RetainsTerminalEvidenceWhenEventTelemetryIsUnavailable()
    {
        var run = CreateRun();
        var timestamp = DateTimeOffset.Parse("2026-09-26T12:00:10Z");
        var terminal = FailureEvent(1, "mandatory_context_budget_exceeded", timestamp);

        var result = await ExplainAsync(run, [terminal], eventStream: new ThrowingEventStream());

        result.Code.Should().Be("mandatory_context_budget_exceeded");
        result.ObservedFacts.Should().BeEmpty();
        result.Unknowns.Should().Contain(item => item.Code == "terminal_event_reference_unavailable");
        result.EvidenceSources.Single(source => source.Name == "durable_run_events")
            .Should().Match<DiagnosticEvidenceSource>(source =>
                source.Availability == "collection_error"
                && source.Completeness == "unavailable");
        result.Completeness.Should().Be("partial");
    }

    [Fact]
    public async Task GetAsync_UsesOnlyTheCurrentAttemptEvidence()
    {
        var run = CreateRun() with { LifecycleGeneration = 2 };
        var timestamp = DateTimeOffset.Parse("2026-09-26T12:00:10Z");
        var firstBinding = CreateBinding(run, attempt: 1);
        var secondBinding = CreateBinding(run, attempt: 2);
        var result = await ExplainAsync(run,
        [
            new RunEvent(1, EventTypes.PermissionBindingBound, firstBinding, timestamp.AddSeconds(-6)),
            new RunEvent(2, EventTypes.ToolError, new { callId = "old-attempt-call" }, timestamp.AddSeconds(-5)),
            FailureEvent(3, "coordinator_execution_failed", timestamp.AddSeconds(-4), "old-attempt-call"),
            new RunEvent(4, EventTypes.PermissionBindingBound, secondBinding, timestamp.AddSeconds(-3)),
            new RunEvent(5, EventTypes.ToolError, new { callId = "current-attempt-call" }, timestamp.AddSeconds(-2)),
            FailureEvent(6, "coordinator_execution_failed", timestamp, "current-attempt-call"),
        ], secondBinding);

        result.Attempt.Should().Be(2);
        result.EvidenceReferences.Should().Contain(item => item.ToolCallId == "current-attempt-call")
            .And.NotContain(item => item.ToolCallId == "old-attempt-call");
        result.Unknowns.Should().NotContain(item => item.Code == "attempt_boundary_unavailable");
    }

    [Fact]
    public async Task GetAsync_HandlesOutOfOrderEventsAndLabelsSyntheticCallIdsInFinalOutput()
    {
        var run = CreateRun();
        var timestamp = DateTimeOffset.Parse("2026-09-26T12:00:10Z");
        var result = await ExplainAsync(run,
        [
            FailureEvent(4, "coordinator_execution_failed", timestamp, "event-3"),
            new RunEvent(3, EventTypes.ToolError, new { }, timestamp.AddSeconds(-1)),
            new RunEvent(1, EventTypes.RunStarted, new { }, timestamp.AddSeconds(-3)),
        ]);

        result.ObservedFacts.Should().ContainSingle(item => item.Code == "terminal_failure_recorded");
        result.EvidenceReferences.Should().Contain(item =>
            item.Id == "event-4" && item.ToolCallId == "event-3");
        result.EvidenceReferences.Should().Contain(item =>
            item.Id == "decision-3" && item.ToolCallId == "event-3" && item.Synthetic);
        result.DenialGate.Should().BeNull();
        result.Unknowns.Should().Contain(item => item.Code == "root_cause_not_attributable");
    }

    private static async Task<RunTerminalDiagnosticResponse> ExplainAsync(
        Run run,
        IReadOnlyList<RunEvent> events,
        EffectivePermissionBinding? binding = null,
        IRunEventStream? eventStream = null)
    {
        await using var sqlite = await TestSqliteDb.CreateAsync();
        await new SqliteRunStore(sqlite.Db).InsertAsync(run);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
        await using var memory = new MemoryDbContext(options);
        await memory.Database.EnsureCreatedAsync();
        memory.RunEvents.AddRange(events
            .Where(evt => evt.Type == EventTypes.RunFailed)
            .Select(evt => new RunEventRecord
            {
                RunId = run.Id.ToString(),
                Sequence = evt.Sequence,
                EventType = evt.Type,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(evt.Payload),
                CreatedAt = evt.TimestampUtc.UtcDateTime,
            }));
        await memory.SaveChangesAsync();

        var stream = eventStream ?? new FixedEventStream(events);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = sqlite.FilePath })
            .Build();
        var identityReader = new ExecutionIdentityReader(
            memory,
            sqlite.Db,
            configuration,
            stream,
            new FixedPermissionBindingProvider(binding ?? CreateBinding(run, run.LifecycleGeneration)));
        var service = new RunFailureExplanationService(
            new RunTerminalDiagnosticReader(memory),
            identityReader,
            stream,
            NullLogger<RunFailureExplanationService>.Instance);

        return (await service.GetAsync(run, CancellationToken.None))!;
    }

    private static RunEvent FailureEvent(
        int sequence,
        string code,
        DateTimeOffset timestamp,
        string? toolCallId = null) =>
        StructuredRunFailureTerminal.NormalizeFailure(new RunEvent(
            sequence,
            EventTypes.RunFailed,
            new
            {
                errorCode = code,
                retryable = false,
                toolCallId,
            },
            timestamp));

    private static EffectivePermissionBinding CreateBinding(Run run, int attempt) =>
        EffectivePermissionBinding.Create(
            run.Id.ToString(),
            attempt,
            "current-project-sandbox-policy",
            $"project:{run.ProjectId}",
            new SandboxPolicy { RepositoryPath = run.RepositoryPath });

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

    private sealed class ThrowingEventStream : IRunEventStream
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
            throw new InvalidOperationException("telemetry unavailable");
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
                events.Where(evt => evt.Sequence > fromSequence).ToArray());
    }
}
