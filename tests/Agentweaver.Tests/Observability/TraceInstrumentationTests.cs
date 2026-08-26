using System.Diagnostics;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Metrics;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// Regression tests for issue #546: an <c>execute_tool</c> span must be bounded by the SDK
    /// event timestamps (<c>ToolExecutionStartEvent.Timestamp</c> /
    /// <c>ToolExecutionCompleteEvent.Timestamp</c>), not by the wall-clock instant our
    /// single-consumer stream loop happens to observe the events. The GitHub Copilot SDK
    /// dispatches tool calls sequentially, so a sibling tool that blocks (e.g. a <c>web_fetch</c>
    /// waiting out its 5-minute HITL approval deadline) stalls delivery of every other tool's
    /// lifecycle events. Observation-time bounding therefore inflated innocent, near-instant tools
    /// (<c>list_decisions</c>, <c>get_memory</c>, <c>list_inbox</c>) to the same ~5-minute duration
    /// as the blocked <c>web_fetch</c> — the exact symptom seen in transaction trace
    /// <c>db469dc6-7dda-4464-8521-c0048a4e7398</c>.
    /// </summary>
    private static ActivityListener ListenToAgentweaverSource()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Agentweaver",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public void StartToolSpanCore_AnchorsStartToSdkTimestamp_NotObservationTime()
    {
        using var listener = ListenToAgentweaverSource();

        // The SDK stamped the tool's start 5 minutes before our loop observed the event.
        var sdkStart = DateTimeOffset.UtcNow.AddMinutes(-5);

        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "list_decisions", sdkStart);

        activity.Should().NotBeNull();
        activity!.StartTimeUtc.Should().BeCloseTo(sdkStart.UtcDateTime, TimeSpan.FromMilliseconds(50),
            "the span start must reflect the SDK ToolExecutionStartEvent.Timestamp, not 'now'");
        activity.Stop();
    }

    [Fact]
    public void StartToolSpanCore_NullTimestamp_FallsBackToObservationTime()
    {
        using var listener = ListenToAgentweaverSource();
        var before = DateTime.UtcNow;

        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "grep", startTime: null);

        activity.Should().NotBeNull();
        activity!.StartTimeUtc.Should().BeOnOrAfter(before.AddMilliseconds(-50),
            "with no SDK timestamp the span must fall back to observation-time bounding");
        activity.Stop();
    }

    [Fact]
    public void CompleteToolSpanCore_FastToolObservedLate_ReportsRealShortDuration()
    {
        using var listener = ListenToAgentweaverSource();

        // list_decisions really ran for ~20ms at T0, but its completion event was delivered to
        // our consumer loop only after the sibling web_fetch's 5-minute HITL wait unblocked the
        // stream. Anchoring both ends to the SDK timestamps must yield the true ~20ms duration.
        var sdkStart = DateTimeOffset.UtcNow.AddMinutes(-5);
        var sdkEnd = sdkStart.AddMilliseconds(20);

        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "list_decisions", sdkStart);
        activity.Should().NotBeNull();

        CopilotAIAgent.CompleteToolSpanCore(activity!, success: true, error: null, endTime: sdkEnd);

        activity!.Duration.Should().BeCloseTo(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50),
            "duration must reflect the tool's real execution window, not the 5 minutes it waited " +
            "to be observed behind a blocked sibling");
        activity.Duration.Should().BeLessThan(TimeSpan.FromMinutes(1),
            "a near-instant tool must never inherit the blocked sibling's ~5-minute duration");
    }

    [Fact]
    public void CompleteToolSpanCore_BlockedTool_ReportsItsOwnLongDuration()
    {
        using var listener = ListenToAgentweaverSource();

        // The genuinely blocked web_fetch: SDK start T0, SDK complete T0 + 5 min. Its span must
        // still show the real ~5-minute duration — the fix must not flatten legitimately slow tools.
        var sdkStart = DateTimeOffset.UtcNow.AddMinutes(-5);
        var sdkEnd = sdkStart.AddMinutes(5);

        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "web_fetch", sdkStart);
        activity.Should().NotBeNull();

        CopilotAIAgent.CompleteToolSpanCore(activity!, success: false, error: "URL fetch was denied by the operator.", endTime: sdkEnd);

        activity!.Duration.Should().BeCloseTo(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1),
            "the actually-blocked tool must keep its real ~5-minute duration");
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public void CompleteToolSpanCore_EndBeforeStart_ClampsToNonNegativeDuration()
    {
        using var listener = ListenToAgentweaverSource();

        var sdkStart = DateTimeOffset.UtcNow;
        var skewedEnd = sdkStart.AddMinutes(-1); // clock skew: completion "before" start

        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "get_memory", sdkStart);
        activity.Should().NotBeNull();

        CopilotAIAgent.CompleteToolSpanCore(activity!, success: true, error: null, endTime: skewedEnd);

        activity!.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero,
            "a backwards SDK timestamp must never produce a negative duration; it falls back to observation time");
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

    /// <summary>
    /// Regression test for the App Insights Gen AI trace view showing "No arguments recorded"
    /// for every native SDK tool call. <see cref="CopilotAIAgent.ConfigureToolSpanTags"/> tags the
    /// tool name/call id/agent name but never the call arguments; App Insights reads
    /// <c>gen_ai.tool.call.arguments</c> directly off the span, so it always rendered empty even
    /// though the Agentweaver UI's RunEvent-based trace panel (fixed by #850/#889) showed them
    /// fine. <see cref="CopilotAIAgent.EmitToolCallOnce"/> must tag the still-open <c>execute_tool</c>
    /// span with the (redacted) arguments in addition to emitting the <c>tool.call</c> RunEvent.
    /// </summary>
    [Fact]
    public void EmitToolCallOnce_SetsGenAiToolCallArgumentsOnSpan()
    {
        Activity? capturedSpan = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Agentweaver",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "execute_tool mock_search")
                    capturedSpan = activity;
            },
        };
        ActivitySource.AddActivityListener(listener);

        var agent = BuildAgent();
        agent.ObserveToolExecutionStarted(
            "call-args-1",
            "mock_search",
            JsonSerializer.SerializeToElement(new { query = "hello world" }),
            DateTimeOffset.UtcNow);

        capturedSpan.Should().NotBeNull("StartToolSpan must have opened an execute_tool span before EmitToolCallOnce ran");
        var argsTag = capturedSpan!.GetTagItem("gen_ai.tool.call.arguments") as string;
        argsTag.Should().NotBeNullOrEmpty("App Insights reads gen_ai.tool.call.arguments directly off the span");
        argsTag.Should().Contain("hello world");

        capturedSpan.Stop();
    }

    /// <summary>
    /// Regression test companion to <see cref="EmitToolCallOnce_SetsGenAiToolCallArgumentsOnSpan"/>:
    /// verifies <see cref="CopilotAIAgent.CompleteToolSpanCore"/> tags
    /// <c>gen_ai.tool.call.result</c> on the span (what App Insights reads for "Output") when a
    /// result is supplied, mirroring the existing coverage for the success/error/duration tags.
    /// </summary>
    [Fact]
    public void CompleteToolSpanCore_WithResult_SetsGenAiToolCallResultTag()
    {
        using var listener = ListenToAgentweaverSource();

        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "create");
        activity.Should().NotBeNull();

        CopilotAIAgent.CompleteToolSpanCore(
            activity!, success: true, error: null, endTime: null, toolResult: "{\"path\":\"file.txt\"}");

        activity!.GetTagItem("gen_ai.tool.call.result").Should().Be("{\"path\":\"file.txt\"}");
    }

    [Fact]
    public void CompleteToolSpanCore_NoResult_DoesNotSetGenAiToolCallResultTag()
    {
        using var listener = ListenToAgentweaverSource();

        var activity = CopilotAIAgent.StartToolSpanCore(turnActivity: null, "create");
        activity.Should().NotBeNull();

        CopilotAIAgent.CompleteToolSpanCore(activity!, success: true, error: null, endTime: null);

        activity!.GetTagItem("gen_ai.tool.call.result").Should().BeNull();
    }

    private static CopilotAIAgent BuildAgent()
    {
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        return new CopilotAIAgent(
            factory, new FixedInstallationScopeStub(), SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(), new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(), NullLogger<CopilotAIAgent>.Instance);
    }
}
