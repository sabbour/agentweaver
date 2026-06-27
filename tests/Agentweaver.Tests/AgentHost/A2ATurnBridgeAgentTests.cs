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
    }

    private static A2ATurnBridgeAgent CreateBridge(FakeTurnRunner runner) =>
        new(new NoOpInnerAgent(), runner, NullLogger<A2ATurnBridgeAgent>.Instance);

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
