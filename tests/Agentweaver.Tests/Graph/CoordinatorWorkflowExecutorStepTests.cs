using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Graph;

/// <summary>
/// Unit tests for the live per-executor <c>workflow.step</c> translation that makes the workflow
/// graph dynamic. The watch loop maps MAF executor-lifecycle events (ExecutorInvoked/Completed/
/// Failed) to <c>workflow.step</c> events via
/// <see cref="RunWatchLoopService.TryBuildExecutorStepEvent"/>, using the executorId -> render
/// metadata map captured when the run's workflow is built. The class name carries "Coordinator" so
/// it runs under the coordinator-filtered suite (the child assemble-ready node is the concrete
/// beneficiary of this translation).
/// </summary>
public sealed class CoordinatorWorkflowExecutorStepTests : IClassFixture<CoordinatorWebApplicationFactory>
{
    private readonly CoordinatorWebApplicationFactory _factory;

    public CoordinatorWorkflowExecutorStepTests(CoordinatorWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private RunWorkflowFactory Factory =>
        _factory.Services.GetRequiredService<RunWorkflowFactory>();

    private static JsonElement ToJson(object payload) =>
        JsonSerializer.SerializeToElement(payload);

    // ── Translation function ──────────────────────────────────────────────────

    [Theory]
    [InlineData("started")]
    [InlineData("completed")]
    [InlineData("failed")]
    public void TranslateAssembleReady_EmitsStepWithTimestamp(string status)
    {
        var meta = new ExecutorNodeMeta("assemble-ready", "Assemble-ready", Hidden: false);

        var payload = RunWatchLoopService.TryBuildExecutorStepEvent(meta, status);

        payload.Should().NotBeNull("the assemble-ready terminal has no dedicated self-emitter and must light up live");
        var json = ToJson(payload!);
        json.GetProperty("step").GetString().Should().Be("assemble-ready",
            "payload.step must equal the descriptor node id the frontend keys on");
        json.GetProperty("status").GetString().Should().Be(status);
        json.GetProperty("label").GetString().Should().Be("Assemble-ready");
        json.GetProperty("timestamp_utc").GetString().Should().NotBeNullOrEmpty(
            "timestamp_utc drives the per-node live elapsed timer");
        DateTimeOffset.TryParse(json.GetProperty("timestamp_utc").GetString(), out _).Should().BeTrue(
            "timestamp_utc must be a valid ISO 8601 instant");
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("rai")]
    [InlineData("merge")]
    [InlineData("scribe")]
    [InlineData("review")]
    public void TranslateDedicatedNodes_ReturnNull(string logicalNodeId)
    {
        // These nodes own their workflow.step lifecycle via richer dedicated emitters; the generic
        // MAF-event translator must not double-emit / clobber their statuses.
        var meta = new ExecutorNodeMeta(logicalNodeId, logicalNodeId, Hidden: false);

        RunWatchLoopService.TryBuildExecutorStepEvent(meta, "started").Should().BeNull();
        RunWatchLoopService.TryBuildExecutorStepEvent(meta, "completed").Should().BeNull();
    }

    [Fact]
    public void TranslateHiddenPlumbing_ReturnsNull()
    {
        var meta = new ExecutorNodeMeta("review-adapter", "Review adapter", Hidden: true);

        RunWatchLoopService.TryBuildExecutorStepEvent(meta, "completed").Should().BeNull(
            "hidden plumbing nodes are dropped from the descriptor and must not emit status");
    }

    [Fact]
    public void TranslateNullMeta_ReturnsNull()
    {
        RunWatchLoopService.TryBuildExecutorStepEvent(null, "started").Should().BeNull();
    }

    [Fact]
    public void Translate_IncludesOptionalMessage_WhenProvided()
    {
        var meta = new ExecutorNodeMeta("assemble-ready", "Assemble-ready", Hidden: false);

        var payload = RunWatchLoopService.TryBuildExecutorStepEvent(meta, "started", "Preparing child result for assembly");

        var json = ToJson(payload!);
        json.GetProperty("message").GetString().Should().Be("Preparing child result for assembly");
    }

    [Fact]
    public void Translate_OmitsMessage_WhenNotProvided()
    {
        var meta = new ExecutorNodeMeta("assemble-ready", "Assemble-ready", Hidden: false);

        var payload = RunWatchLoopService.TryBuildExecutorStepEvent(meta, "started");

        var json = ToJson(payload!);
        json.TryGetProperty("message", out _).Should().BeFalse(
            "message is optional and must be omitted when not supplied so consumers can rely on its absence");
    }

    // ── Executor-id -> render metadata map (the watch loop's lookup) ────────────

    [Fact]
    public void ChildExecutorMetaMap_MapsAssembleReadyExecutorIdToLogicalNode()
    {
        var map = Factory.BuildExecutorMetaForTest(isChild: true);

        // MAF reports executorId "child-assemble-ready"; it must resolve to the rendered node id
        // "assemble-ready" so payload.step lines up with the descriptor.
        map.Should().ContainKey("child-assemble-ready");
        map["child-assemble-ready"].LogicalNodeId.Should().Be("assemble-ready");
        map["child-assemble-ready"].DisplayLabel.Should().Be("Assemble-ready");
        map["child-assemble-ready"].Hidden.Should().BeFalse();

        // The child pipeline's agent executor is present and maps to its logical node
        // (it self-emits, so the translator skips it — but the lookup must still resolve).
        map["agent-turn"].LogicalNodeId.Should().Be("agent");
    }

    [Fact]
    public void ChildExecutorMetaMap_DrivesLiveAssembleReadyEvent()
    {
        // End-to-end of the watch loop's lookup -> translate path for the gap node: resolve the MAF
        // executor id, then translate started/completed into workflow.step events.
        var map = Factory.BuildExecutorMetaForTest(isChild: true);
        var meta = map["child-assemble-ready"];

        var started = RunWatchLoopService.TryBuildExecutorStepEvent(meta, "started");
        var completed = RunWatchLoopService.TryBuildExecutorStepEvent(meta, "completed");

        ToJson(started!).GetProperty("step").GetString().Should().Be("assemble-ready");
        ToJson(started!).GetProperty("status").GetString().Should().Be("started");
        ToJson(completed!).GetProperty("step").GetString().Should().Be("assemble-ready");
        ToJson(completed!).GetProperty("status").GetString().Should().Be("completed");
    }

    [Fact]
    public void FullExecutorMetaMap_SkipsAllDedicatedNodes()
    {
        // The full pipeline's only non-hidden nodes are agent/rai/review/merge/scribe — all dedicated
        // — so the generic translator emits nothing for it (no behavior change to the full pipeline).
        var map = Factory.BuildExecutorMetaForTest(isChild: false);

        foreach (var meta in map.Values)
        {
            RunWatchLoopService.TryBuildExecutorStepEvent(meta, "started").Should().BeNull(
                $"full-pipeline node '{meta.LogicalNodeId}' is hidden or has a dedicated emitter");
        }
    }
}
