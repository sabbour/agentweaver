extern alias agenthost;
using System.Text.Json;
using System.Threading.Channels;
using agenthost::Agentweaver.AgentHost;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agentweaver.Tests.AgentHost;

/// <summary>
/// spec-018 P1.5 — pod-side A2A bridge (<see cref="A2ATurnBridgeAgent"/>) unit tests.
/// Proves the bridge-IN (decode <c>IsRevision</c> from the setup DataPart) and bridge-OUT
/// (emit each RunEvent as a DataContent in the streaming response) wire behavior, plus the
/// mTLS-skip endpoint scheme selection.
/// </summary>
public sealed class A2ATurnBridgeAgentTests
{
    private static DataContent EncodeSetup(bool isRevision)
    {
        var setup = new AgentSetupParams
        {
            WorkingDirectory = "/workspace",
            RepositoryPath = "/workspace",
            RunId = "run-123",
            IsRevision = isRevision,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(
            setup, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return new DataContent(json, AgentSetupParams.MediaType);
    }

    private static List<ChatMessage> BuildTurnMessage(string task, bool isRevision) =>
    [
        new(ChatRole.User, new List<AIContent> { EncodeSetup(isRevision), new TextContent(task) }),
    ];

    /// <summary>A no-op inner agent: only backs DelegatingAIAgent; never invoked by StreamTurnAsync.</summary>
    private sealed class NoOpInnerAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession? session, JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Fake runner that records the turn args and emits one RunEvent mid-turn.</summary>
    private sealed class FakeTurnRunner : IPodTurnRunner
    {
        private ChannelWriter<RunEvent>? _writer;
        public List<(string Task, bool IsRevision)> Calls { get; } = [];
        public RunEvent EventToEmit { get; init; } = new(1, "agent.delta", new { text = "hi" });
        public string ReturnText { get; init; } = "final-text";

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _writer = streamWriter;

        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            Calls.Add((task, isRevision));
            _writer!.TryWrite(EventToEmit);
            return Task.FromResult(ReturnText);
        }

        public Task ForceStopTurnAsync() => Task.CompletedTask;
    }

    private static A2ATurnBridgeAgent CreateBridge(IPodTurnRunner runner) =>
        new(new NoOpInnerAgent(), runner, NullLogger<A2ATurnBridgeAgent>.Instance);

    private sealed class CancellableTurnRunner : IPodTurnRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) { }
        public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
        public Task ForceStopTurnAsync() => Task.CompletedTask;
    }

    private sealed class CancellationIgnoringTurnRunner : IPodTurnRunner
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ForceStopped { get; private set; }

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) { }
        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _completion.Task;
        }
        public Task ForceStopTurnAsync()
        {
            ForceStopped = true;
            _completion.TrySetResult("");
            return Task.CompletedTask;
        }
    }

    /// <summary>Runner that optionally emits a RunEvent, then aborts by throwing.</summary>
    private sealed class ThrowingTurnRunner : IPodTurnRunner
    {
        private ChannelWriter<RunEvent>? _writer;
        public RunEvent? PreFailureEvent { get; init; }
        public Exception Failure { get; init; } = new InvalidOperationException("boom");

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _writer = streamWriter;

        public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            if (PreFailureEvent is not null)
            {
                await _writer!.WriteAsync(PreFailureEvent, cancellationToken);
            }

            throw Failure;
        }

        public Task ForceStopTurnAsync() => Task.CompletedTask;
    }

    private static List<RunEvent> DecodeRunFailedEvents(IEnumerable<AgentResponseUpdate> updates) =>
        updates
            .SelectMany(u => u.Contents)
            .OfType<DataContent>()
            .Where(d => string.Equals(d.MediaType, RunEventDataPartCodec.MediaType, StringComparison.OrdinalIgnoreCase))
            .Select(RunEventDataPartCodec.TryDecodeRunEvent)
            .Where(e => e is not null && string.Equals(e.Type, EventTypes.RunFailed, StringComparison.Ordinal))
            .Select(e => e!)
            .ToList();

    [Fact]
    public async Task StreamTurnAsync_TurnAbortsWithoutStructuredFailure_EmitsSyntheticRunFailed()
    {
        // #267 regression: a pod turn that aborts WITHOUT emitting a structured RunFailed must still
        // hand the worker a structured terminal (agent_turn_internal_error) so the worker recovers a
        // real errorCode from the stream instead of a bare, context-free "Received: None".
        var runner = new ThrowingTurnRunner { Failure = new InvalidOperationException("internal boom") };
        var bridge = CreateBridge(runner);

        var updates = new List<AgentResponseUpdate>();
        Func<Task> act = async () =>
        {
            await foreach (var update in bridge.StreamTurnAsync(BuildTurnMessage("go", isRevision: false), default))
            {
                updates.Add(update);
            }
        };

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("internal boom", "the original turn exception must still propagate");

        var runFailed = DecodeRunFailedEvents(updates);
        runFailed.Should().ContainSingle("a synthetic structured terminal must be emitted");
        JsonSerializer.Serialize(runFailed[0].Payload).Should().Contain("agent_turn_internal_error");
    }

    [Fact]
    public async Task StreamTurnAsync_TurnAbortsAfterStructuredFailure_DoesNotDoubleEmit()
    {
        // If the pod already emitted its own structured RunFailed, the bridge must NOT overwrite it
        // with a generic agent_turn_internal_error — the worker's last-seen structured reason must
        // remain the specific one the pod reported.
        var structured = new RunEvent(1, EventTypes.RunFailed, new
        {
            message = "shell hard deadline",
            errorCode = "shell_execution_timeout",
            retryable = true,
        });
        var runner = new ThrowingTurnRunner
        {
            PreFailureEvent = structured,
            Failure = new InvalidOperationException("internal boom"),
        };
        var bridge = CreateBridge(runner);

        var updates = new List<AgentResponseUpdate>();
        Func<Task> act = async () =>
        {
            await foreach (var update in bridge.StreamTurnAsync(BuildTurnMessage("go", isRevision: false), default))
            {
                updates.Add(update);
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();

        var runFailed = DecodeRunFailedEvents(updates);
        runFailed.Should().ContainSingle("the pod's own structured RunFailed must not be duplicated");
        var payload = JsonSerializer.Serialize(runFailed[0].Payload);
        payload.Should().Contain("shell_execution_timeout");
        payload.Should().NotContain("agent_turn_internal_error");
    }

    [Fact]
    public void ExtractTurn_DecodesIsRevisionAndTask_FromSetupDataPart()
    {
        var (task, isRevision) = A2ATurnBridgeAgent.ExtractTurn(BuildTurnMessage("do the task", isRevision: true));

        task.Should().Be("do the task");
        isRevision.Should().BeTrue();
    }

    [Fact]
    public void ExtractTurn_DefaultsIsRevisionFalse_WhenNoSetupPart()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new List<AIContent> { new TextContent("fresh task") }),
        };

        var (task, isRevision) = A2ATurnBridgeAgent.ExtractTurn(messages);

        task.Should().Be("fresh task");
        isRevision.Should().BeFalse();
    }

    [Fact]
    public async Task StreamTurnAsync_ForwardsIsRevision_ToRunner()
    {
        var runner = new FakeTurnRunner();
        var bridge = CreateBridge(runner);

        await foreach (var _ in bridge.StreamTurnAsync(BuildTurnMessage("revise it", isRevision: true), default))
        {
            // drain
        }

        runner.Calls.Should().ContainSingle();
        runner.Calls[0].Should().Be(("revise it", true));
    }

    [Fact]
    public async Task StreamTurnAsync_EmitsRunEvent_AsDataContent()
    {
        var runner = new FakeTurnRunner
        {
            EventToEmit = new RunEvent(1, "agent.task", new { text = "working" }),
        };
        var bridge = CreateBridge(runner);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in bridge.StreamTurnAsync(BuildTurnMessage("go", isRevision: false), default))
        {
            updates.Add(update);
        }

        var dataParts = updates
            .SelectMany(u => u.Contents)
            .OfType<DataContent>()
            .Where(d => string.Equals(d.MediaType, RunEventDataPartCodec.MediaType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        dataParts.Should().ContainSingle("the single emitted RunEvent must surface as a DataPart");

        var decoded = RunEventDataPartCodec.TryDecodeRunEvent(dataParts[0]);
        decoded.Should().NotBeNull();
        decoded!.Type.Should().Be("agent.task");
    }

    [Fact]
    public async Task StreamTurnAsync_EmitsFinalAssistantText_AfterEvents()
    {
        var runner = new FakeTurnRunner { ReturnText = "all done" };
        var bridge = CreateBridge(runner);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in bridge.StreamTurnAsync(BuildTurnMessage("go", isRevision: false), default))
        {
            updates.Add(update);
        }

        updates.Any(u => u.Text == "all done").Should().BeTrue();
    }

    [Fact]
    public async Task StreamTurnAsync_ConsumerCancellation_CancelsAndJoinsTurn()
    {
        var runner = new CancellableTurnRunner();
        var bridge = new A2ATurnBridgeAgent(
            new NoOpInnerAgent(), runner, workspaceManager: null, runtimeState: null,
            NullLogger<A2ATurnBridgeAgent>.Instance, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();
        await using var enumerator = bridge.StreamTurnAsync(BuildTurnMessage("go", false), cts.Token).GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();
        await runner.Started.Task;

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        runner.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task StreamTurnAsync_CancellationIgnoringTurn_IsForceStoppedAfterDrainBound()
    {
        var runner = new CancellationIgnoringTurnRunner();
        var bridge = new A2ATurnBridgeAgent(
            new NoOpInnerAgent(), runner, workspaceManager: null, runtimeState: null,
            NullLogger<A2ATurnBridgeAgent>.Instance, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();
        await using var enumerator = bridge.StreamTurnAsync(BuildTurnMessage("go", false), cts.Token).GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();
        await runner.Started.Task;

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        runner.ForceStopped.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "https")]
    [InlineData(false, "http")]
    public void AgentHostEndpoint_SchemeAndUrl_HonorRequireMtls(bool requireMtls, string expectedScheme)
    {
        AgentHostEndpoint.Scheme(requireMtls).Should().Be(expectedScheme);

        var url = AgentHostEndpoint.Build(requireMtls, "10.0.0.5", 8088, "/a2a/agent");
        url.Should().Be($"{expectedScheme}://10.0.0.5:8088/a2a/agent");
    }

    [Fact]
    public void AgentSetupParams_TryDecode_RoundTripsIsRevision()
    {
        var decoded = AgentSetupParams.TryDecode(EncodeSetup(isRevision: true));

        decoded.Should().NotBeNull();
        decoded!.IsRevision.Should().BeTrue();
        decoded.RunId.Should().Be("run-123");
    }

    [Fact]
    public void AgentSetupParams_TryDecode_ReturnsNull_ForWrongMediaType()
    {
        var content = new DataContent(new byte[] { 1, 2, 3 }, "application/octet-stream");

        AgentSetupParams.TryDecode(content).Should().BeNull();
    }
}
