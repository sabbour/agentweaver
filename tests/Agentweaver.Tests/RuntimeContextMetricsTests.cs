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
            ["report_intent", "safe_tool"], declarations);
        var oneShotRunner = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", "inspect implementation", context,
            ["report_intent", "safe_tool"], declarations);

        persistentRunner.Should().BeEquivalentTo(oneShotRunner);
        persistentRunner.GetType().GetProperties().Select(property => property.Name).Should().Equal(
            "Provider", "RunId", "ProjectId", "BaseCharacters", "RunContextCharacters",
            "SkillCharacters", "SeparatorCharacters", "TaskCharacters", "ToolDeclarationCharacters",
            "SkillDeliveryMode", "TotalCharacters", "EstimatedTokens");
    }

    [Fact]
    public void OneShotRunner_EmitsRedactedRuntimeContextEvent()
    {
        const string secret = "super-secret-token";
        var emitted = new List<(string Type, object Payload)>();

        GitHubCopilotAgentRunner.EmitRuntimeContext(
            (type, payload) => emitted.Add((type, payload)),
            "run-123",
            "project-456",
            $"task {secret}",
            $"context {secret}\n\n---\n\n## Available Skills\n\n- {secret}\n  Full instructions: `.agentweaver/skills/review/SKILL.md`",
            ["unsafe_tool"],
            Declarations());

        emitted.Should().ContainSingle().Which.Type.Should().Be(EventTypes.AgentRuntimeContext);
        JsonSerializer.Serialize(emitted[0].Payload).Should().NotContain(secret)
            .And.NotContain("unsafe_tool");
    }

    [Fact]
    public void PersistentRunner_EmitsRuntimeContextEventToItsTurnStream()
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

        agent.EmitRuntimeContext("inspect implementation");

        events.Reader.TryRead(out var emitted).Should().BeTrue();
        emitted!.Type.Should().Be(EventTypes.AgentRuntimeContext);
        emitted.Payload.Should().BeOfType<AgentRuntimeContextMetrics>();
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("## Available Skills\n\n- metadata\n  Full instructions: `.agentweaver/skills/review/SKILL.md`", "file")]
    [InlineData("## Available Skills\n\n- metadata\n  Full instructions (inlined — no on-disk SKILL.md available for this run):\n\n  instructions", "inline")]
    [InlineData("## Available Skills\n\n- one\n  Full instructions: `.agentweaver/skills/review/SKILL.md`\n- two\n  Full instructions (inlined — no on-disk SKILL.md available for this run):\n\n  instructions", "mixed")]
    public void Compose_ClassifiesAssignedSkillDeliveryWithoutRecordingContent(string? context, string expectedMode)
    {
        var metrics = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", "task", context, [], Declarations());

        metrics.SkillDeliveryMode.Should().Be(expectedMode);
    }

    [Fact]
    public void Compose_SectionSizesAndDocumentedEstimateMatchExactAssembly()
    {
        var declarations = Declarations();
        var context = "charter\n\n---\n\n## Available Skills\n\n- metadata\n  Full instructions: `.agentweaver/skills/review/SKILL.md`";
        var metrics = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", "do work", context,
            ["report_intent", "safe_tool"], declarations);

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
        var metrics = AgentRuntimeContextMetricsComposer.Compose(
            "copilot", "run-123", "project-456", $"task {secret}",
            $"context {secret}\n\n---\n\n## Available Skills\n\n- {secret}\n  Full instructions (inlined — no on-disk SKILL.md available for this run):\n\n  {secret}",
            ["unsafe_tool"], Declarations());

        metrics.RunId.Should().Be("run-123");
        metrics.ProjectId.Should().Be("project-456");
        var serialized = JsonSerializer.Serialize(metrics);
        serialized.Should().NotContain(secret)
            .And.NotContain("unsafe_tool")
            .And.NotContain("task ")
            .And.NotContain("context ");
    }

    private static List<AIFunctionDeclaration> Declarations() =>
    [AIFunctionFactory.Create((string value) => value, "safe_tool")];
}