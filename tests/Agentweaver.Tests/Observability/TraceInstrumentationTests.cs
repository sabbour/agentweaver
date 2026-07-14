using System.Diagnostics;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Metrics;
using FluentAssertions;

namespace Agentweaver.Tests.Observability;

/// <summary>
/// Unit tests for issue #166 gen-AI trace instrumentation:
///   - <see cref="CopilotAIAgent.ConfigureToolSpanTags"/> stamps the gen AI semantic-convention
///     attributes on an <c>execute_tool</c> child span so the transaction-trace tree can classify
///     and render tool calls.
///   - <see cref="AppInsightsMetricsService.ClassifySpanType"/> maps span attributes to the
///     invoke-agent / llm / tool node types the UI renders.
/// </summary>
public sealed class TraceInstrumentationTests
{
    [Fact]
    public void AggregateRunAgentBreakdown_MergesAgentNamesIgnoringCase()
    {
        var breakdown = AppInsightsMetricsService.AggregateRunAgentBreakdown([
            new AgentUsageBreakdownDto
            {
                AgentName = "coordinator",
                InvocationCount = 2,
                TotalTokens = 0,
                TotalNanoAiu = 120,
            },
            new AgentUsageBreakdownDto
            {
                AgentName = "Coordinator",
                InvocationCount = 3,
                TotalTokens = 0,
                TotalNanoAiu = 180,
            },
        ]);

        breakdown.Should().ContainSingle();
        breakdown[0].AgentName.Should().Be("coordinator");
        breakdown[0].InvocationCount.Should().Be(5);
        breakdown[0].TotalNanoAiu.Should().Be(300);
    }

    [Fact]
    public void ConfigureToolSpanTags_StampsGenAiToolAttributes()
    {
        using var activity = new Activity("execute_tool mock_search");
        activity.Start();

        CopilotAIAgent.ConfigureToolSpanTags(activity, "mock_search", "call-1", "child-agent-1", "run-42");

        activity.GetTagItem("agentweaver.span.kind").Should().Be("tool_call");
        activity.GetTagItem("gen_ai.operation.name").Should().Be("execute_tool");
        activity.GetTagItem("gen_ai.tool.name").Should().Be("mock_search");
        activity.GetTagItem("tool.call.id").Should().Be("call-1");
        activity.GetTagItem("gen_ai.agent.name").Should().Be("child-agent-1");
        activity.GetTagItem("run_id").Should().Be("run-42");

        activity.Stop();
    }

    [Fact]
    public void ConfigureToolSpanTags_OmitsAgentNameWhenBlank()
    {
        using var activity = new Activity("execute_tool grep");
        activity.Start();

        CopilotAIAgent.ConfigureToolSpanTags(activity, "grep", "call-2", agentName: "  ", runId: "run-7");

        activity.GetTagItem("gen_ai.agent.name").Should().BeNull();
        activity.GetTagItem("gen_ai.tool.name").Should().Be("grep");

        activity.Stop();
    }

    [Theory]
    [InlineData("tool_call", null, null, null, "tool")]
    [InlineData(null, "mock_search", null, null, "tool")]
    [InlineData(null, null, "execute_tool", null, "tool")]
    [InlineData("agent_turn", null, "chat", "gpt-4o", "invoke-agent")]
    [InlineData(null, null, "chat", "gpt-4o", "llm")]
    [InlineData(null, null, null, "gpt-4o", "llm")]
    [InlineData(null, null, null, null, "invoke-agent")]
    public void ClassifySpanType_MapsAttributesToNodeType(
        string? spanKind, string? toolName, string? operationName, string? model, string expected)
    {
        AppInsightsMetricsService.ClassifySpanType(spanKind, toolName, operationName, model)
            .Should().Be(expected);
    }
}
