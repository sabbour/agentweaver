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

    /// <summary>
    /// Regression tests for issue #200: <see cref="CopilotAIAgent.StartToolSpanCore"/> must parent
    /// every <c>execute_tool</c> span to the captured turn span explicitly, not to ambient
    /// <c>Activity.Current</c> — otherwise a tool span started while another tool span is still
    /// open (overlapping tool calls) would incorrectly nest under that other tool span instead of
    /// sitting as a sibling under the turn.
    /// </summary>
    [Fact]
    public void StartToolSpanCore_OverlappingToolCalls_BothParentToTurnSpan()
    {
        using var testSource = new ActivitySource("Agentweaver");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Agentweaver",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var turnActivity = testSource.StartActivity("Agentweaver model turn", ActivityKind.Client);
        turnActivity.Should().NotBeNull();

        // Simulate the exact overlap from the issue: StartA, then StartB before CompleteA. While
        // span A is open it *is* Activity.Current, reproducing the scenario that previously
        // caused mis-parenting.
        var spanA = CopilotAIAgent.StartToolSpanCore(turnActivity, "toolA");
        var spanB = CopilotAIAgent.StartToolSpanCore(turnActivity, "toolB");

        spanA.Should().NotBeNull();
        spanB.Should().NotBeNull();

        spanA!.ParentSpanId.Should().Be(turnActivity!.SpanId, "toolA must parent to the turn span");
        spanB!.ParentSpanId.Should().Be(turnActivity.SpanId, "toolB must parent to the turn span, not to the still-open toolA span");
        spanB.ParentSpanId.Should().NotBe(spanA.SpanId, "overlapping tool spans must not nest under each other");

        // Complete out of start order (CompleteB then CompleteA, per the issue's suggested test)
        // — parenting is fixed at StartActivity time so completion order must not matter.
        spanB.Stop();
        spanA.Stop();
    }

    [Fact]
    public void StartToolSpanCore_NoCapturedTurnActivity_FallsBackToAmbientParenting()
    {
        using var testSource = new ActivitySource("Agentweaver");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Agentweaver",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        // If no turn activity was captured (e.g. no listener was active when the turn started),
        // the fix must degrade gracefully to the original ambient-parent behavior rather than
        // throwing or refusing to create a span.
        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "toolA");

        activity.Should().NotBeNull();
        activity!.Stop();
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
