using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Runtime;

public sealed class RuntimeContextMetricsTests
{
    [Fact]
    public void BothCopilotRunners_ShareAnIdenticallyShapedCompositionRecord()
    {
        var declarations = Declarations();
        var context = "charter\n\n---\n\n## Available Skills\n\n- metadata\n  Full instructions: `.agentweaver/skills/review/SKILL.md`";

        var persistentRunner = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", "inspect implementation", context,
            AgentBasePrompt.Compose(context, ["report_intent", "safe_tool"]), declarations);
        var oneShotRunner = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", "inspect implementation", context,
            AgentBasePrompt.Compose(context, ["report_intent", "safe_tool"]), declarations);

        persistentRunner.Should().BeEquivalentTo(oneShotRunner);
        persistentRunner.GetType().GetProperties().Select(property => property.Name).Should().Equal(
            "Provider", "RunId", "ProjectId", "BaseCharacters", "RunContextCharacters",
            "SkillCharacters", "SeparatorCharacters", "TaskCharacters", "ToolDeclarationCharacters",
            "SkillDeliveryMode", "TotalCharacters", "EstimatedTokens");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneShotRunner_EmitsExactlyOneRedactedSystemPromptEvent(bool includeMemoryGuidance)
    {
        const string secret = "super-secret-token";
        var emitted = new List<(string Type, object Payload)>();
        var tools = includeMemoryGuidance
            ? new[] { "record_memory", "unsafe_tool" }
            : new[] { "unsafe_tool" };
        var promptComposition = GitHubCopilotAgentRunner.ComposePrompt(
            $"context {secret}",
            tools);

        GitHubCopilotAgentRunner.EmitRuntimeContext(
            (type, payload) => emitted.Add((type, payload)),
            "run-123",
            "project-456",
            $"task {secret}",
            $"context {secret}\n\n---\n\n## Available Skills\n\n- {secret}\n  Full instructions: `.agentweaver/skills/review/SKILL.md`",
            promptComposition,
            Declarations());

        emitted.Count(item => item.Type == EventTypes.AgentSystemPrompt).Should().Be(1);
        emitted.Count(item => item.Type == EventTypes.AgentRuntimeContext).Should().Be(1);
        var systemPrompt = emitted.Single(item => item.Type == EventTypes.AgentSystemPrompt).Payload;
        systemPrompt.Should().BeOfType<AgentSystemPromptMetadata>()
            .Which.CallableMemoryGuidanceIncluded.Should().Be(includeMemoryGuidance);
        JsonSerializer.Serialize(systemPrompt).Should().NotContain(secret)
            .And.NotContain("unsafe_tool");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentRunner_EmitsExactlyOneSystemPromptEventWithCompositionDecision(bool includeMemoryGuidance)
    {
        var events = Channel.CreateUnbounded<RunEvent>();
        var agent = new CopilotAIAgent(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new FixedGitHubCopilotCapabilityCredentialProvider()),
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance);
        agent.SetTurnStreamWriter(events.Writer);
        var toolNames = includeMemoryGuidance ? new[] { "record_memory" } : Array.Empty<string>();
        var promptComposition = CopilotAIAgent.ComposePrompt("charter canary", toolNames);

        agent.EmitRuntimeContext("task canary", promptComposition);

        var emitted = new List<RunEvent>();
        while (events.Reader.TryRead(out var evt))
            emitted.Add(evt);

        emitted.Count(evt => evt.Type == EventTypes.AgentSystemPrompt).Should().Be(1);
        emitted.Count(evt => evt.Type == EventTypes.AgentRuntimeContext).Should().Be(1);
        emitted.Single(evt => evt.Type == EventTypes.AgentSystemPrompt).Payload
            .Should().BeOfType<AgentSystemPromptMetadata>()
            .Which.CallableMemoryGuidanceIncluded.Should().Be(includeMemoryGuidance);
        JsonSerializer.Serialize(emitted.Single(evt => evt.Type == EventTypes.AgentSystemPrompt).Payload)
            .Should().NotContain("canary")
            .And.NotContain("record_memory");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothPromptCompositionPaths_ReuseTheCallableMemoryGuidanceDecision(bool includeMemoryGuidance)
    {
        var tools = includeMemoryGuidance ? new[] { "record_memory" } : Array.Empty<string>();

        var persistent = CopilotAIAgent.ComposePrompt("charter", tools);
        var oneShot = GitHubCopilotAgentRunner.ComposePrompt("charter", tools);

        persistent.CallableMemoryGuidanceIncluded.Should().Be(includeMemoryGuidance);
        oneShot.CallableMemoryGuidanceIncluded.Should().Be(includeMemoryGuidance);
        persistent.Content.Contains("## Project memory and coordination", StringComparison.Ordinal)
            .Should().Be(includeMemoryGuidance);
        oneShot.Content.Contains("## Project memory and coordination", StringComparison.Ordinal)
            .Should().Be(includeMemoryGuidance);
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("## Available Skills\n\n- metadata\n  Full instructions: `.agentweaver/skills/review/SKILL.md`", "file")]
    [InlineData("## Available Skills\n\n- metadata\n  Full instructions (inlined — no on-disk SKILL.md available for this run):\n\n  instructions", "inline")]
    [InlineData("## Available Skills\n\n- one\n  Full instructions: `.agentweaver/skills/review/SKILL.md`\n- two\n  Full instructions (inlined — no on-disk SKILL.md available for this run):\n\n  instructions", "mixed")]
    public void Compose_ClassifiesAssignedSkillDeliveryWithoutRecordingContent(string? context, string expectedMode)
    {
        var metrics = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", "task", context,
            AgentBasePrompt.Compose(context, []), Declarations());

        metrics.SkillDeliveryMode.Should().Be(expectedMode);
    }

    [Fact]
    public void Compose_SectionSizesAndDocumentedEstimateMatchExactAssembly()
    {
        var declarations = Declarations();
        var context = "charter\n\n---\n\n## Available Skills\n\n- metadata\n  Full instructions: `.agentweaver/skills/review/SKILL.md`";
        var metrics = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", "do work", context,
            AgentBasePrompt.Compose(context, ["report_intent", "safe_tool"]), declarations);

        metrics.TotalCharacters.Should().Be(
            metrics.BaseCharacters + metrics.RunContextCharacters + metrics.SkillCharacters +
            metrics.SeparatorCharacters + metrics.TaskCharacters + metrics.ToolDeclarationCharacters);
        metrics.EstimatedTokens.Should().Be((metrics.TotalCharacters + 3) / 4);
        metrics.ToolDeclarationCharacters.Should().Be(JsonSerializer.Serialize(declarations).Length);
        metrics.SeparatorCharacters.Should().Be("\n\n".Length + "\n\n---\n\n".Length);
    }

    [Fact]
    public void Compose_PreservesCorrelationAndRedactsAllInputContent()
    {
        const string secret = "super-secret-token";
        var context = $"context {secret}\n\n---\n\n## Available Skills\n\n- {secret}\n  Full instructions (inlined — no on-disk SKILL.md available for this run):\n\n  {secret}";
        var metrics = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", $"task {secret}",
            context, AgentBasePrompt.Compose(context, ["unsafe_tool"]), Declarations());

        metrics.RunId.Should().Be("run-123");
        metrics.ProjectId.Should().Be("project-456");
        var serialized = JsonSerializer.Serialize(metrics);
        serialized.Should().NotContain(secret)
            .And.NotContain("unsafe_tool")
            .And.NotContain("task ")
            .And.NotContain("context ");
    }

    [Fact]
    public async Task OperatorAssistant_EmitsExactScalarOnlyRuntimeContextForResolvedProvider()
    {
        const string secret = "operator-secret-canary";
        var request = new OperatorAssistantRequest(
            ConversationId: "conversation-123",
            Message: $"inspect {secret}",
            CallerUser: $"user-{secret}",
            GitHubLogin: secret,
            ProjectId: "project-456",
            RunId: "conversation-123",
            ModelId: null,
            AgentDefinition: $"operator definition {secret}",
            McpBrokerToken: secret,
            History: []);
        var declarations = Declarations();
        var sink = new RecordingPromptSink();
        var systemPrompt = OperatorAssistantAgent.BuildSystemPromptForTests(
            request.AgentDefinition,
            declarations.Count,
            request.ProjectId,
            request.RunId);

        var metrics = await OperatorAssistantAgent.EmitPromptMetadataAsync(
            request,
            systemPrompt,
            declarations,
            ModelSource.GitHubCopilot,
            sink,
            CancellationToken.None);

        metrics.Provider.Should().Be("copilot");
        metrics.RunId.Should().Be("conversation-123");
        metrics.ProjectId.Should().Be("project-456");
        metrics.RunContextCharacters.Should().Be(0);
        metrics.SkillCharacters.Should().Be(0);
        metrics.SeparatorCharacters.Should().Be(0);
        metrics.SkillDeliveryMode.Should().Be("none");
        metrics.TotalCharacters.Should().Be(
            metrics.BaseCharacters + metrics.TaskCharacters + metrics.ToolDeclarationCharacters);
        metrics.EstimatedTokens.Should().Be((metrics.TotalCharacters + 3) / 4);
        JsonSerializer.Serialize(metrics).Should().NotContain(secret)
            .And.NotContain("safe_tool");
        sink.Events.Should().ContainSingle().Which.Should().BeEquivalentTo((metrics, false));
    }

    [Fact]
    public async Task OperatorAssistant_ByokPromptMetadataUsesCanonicalProviderWithoutConfigurationSecrets()
    {
        const string providerSecret = "private-byok-provider-secret";
        var request = new OperatorAssistantRequest(
            ConversationId: "conversation-byok",
            Message: "inspect deployment",
            CallerUser: "user-1",
            GitHubLogin: null,
            ProjectId: "project-1",
            RunId: "conversation-byok",
            ModelId: "private-model-name",
            AgentDefinition: "You are the operator.",
            McpBrokerToken: providerSecret,
            History: []);
        var sink = new RecordingPromptSink();
        var declarations = Declarations();
        var systemPrompt = OperatorAssistantAgent.BuildSystemPromptForTests(
            request.AgentDefinition,
            declarations.Count,
            request.ProjectId,
            request.RunId);

        var metrics = await OperatorAssistantAgent.EmitPromptMetadataAsync(
            request,
            systemPrompt,
            declarations,
            ModelSource.Byok,
            sink,
            CancellationToken.None);

        metrics.Provider.Should().Be("byok");
        sink.Events.Should().ContainSingle().Which.Should().BeEquivalentTo((metrics, false));
        JsonSerializer.Serialize(sink.Events).Should().NotContain(providerSecret)
            .And.NotContain("private-model-name")
            .And.NotContain("safe_tool");
    }

    private static List<AIFunctionDeclaration> Declarations() =>
    [AIFunctionFactory.Create((string value) => value, "safe_tool")];

    private sealed class RecordingPromptSink : IOperatorAssistantTurnSink
    {
        public List<(AgentRuntimeContextMetrics Metrics, bool GuidanceIncluded)> Events { get; } = [];

        public ValueTask OnPromptMetadataAsync(
            AgentRuntimeContextMetrics runtimeContext,
            bool callableMemoryGuidanceIncluded,
            CancellationToken ct)
        {
            Events.Add((runtimeContext, callableMemoryGuidanceIncluded));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnAssistantTextDeltaAsync(string delta, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnToolCallAsync(string toolName, string? argumentsJson, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnToolResultAsync(string toolName, bool success, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnMcpBrokerTokenRefreshRequiredAsync(CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> OnApprovalRequiredAsync(
            string requestId,
            string toolName,
            string? argumentsJson,
            CancellationToken ct) =>
            ValueTask.FromResult(true);
    }
}