extern alias agenthost;
using System.Text.Json;
using System.Threading.Channels;
using agenthost::Agentweaver.AgentHost;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agentweaver.Tests.AgentHost;

/// <summary>
/// #336 regression: the pod-side A2A bridge (<see cref="A2ATurnBridgeAgent"/>) must apply the
/// per-turn <see cref="AgentSetupParams"/> the worker delivers on every turn — the assembled
/// system prompt (charter/memory/assigned skills) and project/agent identity — onto the pod's
/// agent BEFORE the turn runs. A warm pod's startup <c>SetupAsync</c> only applies static
/// pod-environment context, so without this the per-run skills/memory never reach the agent in
/// <c>pod-per-run</c> mode (the deployed staging execution mode).
/// </summary>
public sealed class A2ATurnBridgePerTurnContextTests
{
    private const string BaseManifestContext =
        "SANDBOX TOOL MANIFEST\nThe following tools are pre-installed in this sandbox.";
    private const string ComplianceToken = "COMPLIANCE-TOKEN-7f3a9c";

    private static string SkillsSystemPrompt() =>
        $"Charter: ship it.\n\n{SkillPromptMarkers.SectionHeading}\n\n" +
        $"### Compliance Skill\nAlways begin every response with the token {ComplianceToken}.";

    private static DataContent EncodeSetup(
        string? systemPromptContext,
        string? projectId,
        string? agentName,
        bool isRevision = false)
    {
        var setup = new AgentSetupParams
        {
            WorkingDirectory = "/workspace",
            RepositoryPath = "/workspace",
            RunId = "run-336",
            SystemPromptContext = systemPromptContext,
            ProjectId = projectId,
            AgentName = agentName,
            IsRevision = isRevision,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(
            setup, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return new DataContent(json, AgentSetupParams.MediaType);
    }

    private static List<ChatMessage> BuildTurnMessage(
        string task, string? systemPromptContext, string? projectId, string? agentName, bool isRevision = false) =>
    [
        new(ChatRole.User, new List<AIContent>
        {
            EncodeSetup(systemPromptContext, projectId, agentName, isRevision),
            new TextContent(task),
        }),
    ];

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

    /// <summary>Records the per-turn context applied to the agent and the order relative to the turn.</summary>
    private sealed class RecordingTurnRunner : IPodTurnRunner
    {
        private ChannelWriter<RunEvent>? _writer;
        public List<(string? SystemPromptContext, string? ProjectId, string? AgentName)> Applied { get; } = [];
        public bool ContextAppliedBeforeTurn { get; private set; }

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _writer = streamWriter;

        public bool ApplyPerTurnContext(string? systemPromptContext, string? projectId, string? agentName)
        {
            Applied.Add((systemPromptContext, projectId, agentName));
            return true;
        }

        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            ContextAppliedBeforeTurn = Applied.Count > 0;
            _writer?.TryWrite(new RunEvent(1, "agent.delta", new { text = "hi" }));
            return Task.FromResult("done");
        }

        public Task ForceStopTurnAsync() => Task.CompletedTask;
    }

    private static A2ATurnBridgeAgent CreateBridge(IPodTurnRunner runner, AgentHostRuntimeState? runtimeState) =>
        new(new NoOpInnerAgent(), runner, workspaceManager: null, runtimeState: runtimeState,
            NullLogger<A2ATurnBridgeAgent>.Instance);

    private static async Task DrainAsync(A2ATurnBridgeAgent bridge, IEnumerable<ChatMessage> messages)
    {
        await foreach (var _ in bridge.StreamTurnAsync(messages, default))
        {
            // drain
        }
    }

    [Fact]
    public async Task StreamTurnAsync_AppliesPerTurnSkills_LayeredOnPodBaseContext_BeforeRunningTurn()
    {
        // The exact #336 scenario: a skill assigned to "Rogers" whose instructions require the
        // compliance token. The composed prompt arrives per-turn in AgentSetupParams and MUST be
        // applied to the agent (layered on the pod's static manifest) before the turn runs.
        var runtimeState = new AgentHostRuntimeState();
        runtimeState.SetPodBaseSystemPromptContext(BaseManifestContext);

        var runner = new RecordingTurnRunner();
        var bridge = CreateBridge(runner, runtimeState);

        await DrainAsync(bridge, BuildTurnMessage(
            "do the task", SkillsSystemPrompt(), projectId: "proj-1", agentName: "Rogers"));

        runner.Applied.Should().ContainSingle("the per-turn context must be applied exactly once");
        var applied = runner.Applied[0];

        applied.ProjectId.Should().Be("proj-1");
        applied.AgentName.Should().Be("Rogers");
        applied.SystemPromptContext.Should().Contain("SANDBOX TOOL MANIFEST",
            "the static pod-environment context must be preserved, not replaced");
        applied.SystemPromptContext.Should().Contain(SkillPromptMarkers.SectionHeading,
            "the assigned-skills section heading must reach the agent");
        applied.SystemPromptContext.Should().Contain(ComplianceToken,
            "the assigned skill's instructions must reach the agent's assembled prompt (#336)");
        SkillPromptMarkers.ContainsSkillContext(applied.SystemPromptContext).Should().BeTrue();

        runner.ContextAppliedBeforeTurn.Should().BeTrue(
            "context must be applied before the turn executes, not after");
    }

    [Fact]
    public async Task StreamTurnAsync_WithoutPodBaseContext_DeliversPerTurnContextVerbatim()
    {
        // Non-warm / no recorded base context: the per-turn context is delivered as-is.
        var runner = new RecordingTurnRunner();
        var bridge = CreateBridge(runner, runtimeState: null);

        await DrainAsync(bridge, BuildTurnMessage(
            "go", SkillsSystemPrompt(), projectId: "proj-2", agentName: "Tank"));

        runner.Applied.Should().ContainSingle();
        runner.Applied[0].SystemPromptContext.Should().Contain(ComplianceToken);
        runner.Applied[0].SystemPromptContext.Should().NotContain("SANDBOX TOOL MANIFEST");
        runner.Applied[0].AgentName.Should().Be("Tank");
    }

    [Fact]
    public void MergeSystemPromptContext_LayersBaseThenPerTurn()
    {
        var merged = A2ATurnBridgeAgent.MergeSystemPromptContext(BaseManifestContext, SkillsSystemPrompt());

        merged.Should().NotBeNull();
        merged!.IndexOf("SANDBOX TOOL MANIFEST", StringComparison.Ordinal)
            .Should().BeLessThan(merged.IndexOf(ComplianceToken, StringComparison.Ordinal),
                "the pod-environment context must come first, then the per-run context");
        merged.Should().Contain("\n\n");
    }

    [Theory]
    [InlineData(null, "turn-only", "turn-only")]
    [InlineData("base-only", null, "base-only")]
    [InlineData(null, null, null)]
    [InlineData("", "  ", null)]
    public void MergeSystemPromptContext_HandlesMissingSides(string? baseCtx, string? turnCtx, string? expected)
    {
        A2ATurnBridgeAgent.MergeSystemPromptContext(baseCtx, turnCtx).Should().Be(expected);
    }

    [Fact]
    public void ExtractTurnWithSetup_DecodesFullPerTurnSetup()
    {
        var (task, setup) = A2ATurnBridgeAgent.ExtractTurnWithSetup(BuildTurnMessage(
            "the task", SkillsSystemPrompt(), projectId: "proj-9", agentName: "Rogers", isRevision: true));

        task.Should().Be("the task");
        setup.Should().NotBeNull();
        setup!.ProjectId.Should().Be("proj-9");
        setup.AgentName.Should().Be("Rogers");
        setup.IsRevision.Should().BeTrue();
        setup.SystemPromptContext.Should().Contain(ComplianceToken);
    }
}
