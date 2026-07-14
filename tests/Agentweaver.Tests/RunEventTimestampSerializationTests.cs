using System.Text.Json.Nodes;
using Agentweaver.Api.Endpoints;
using Agentweaver.Domain;
using FluentAssertions;
using Xunit;

namespace Agentweaver.Tests;

/// <summary>
/// Verifies the RunEvent.TimestampUtc stamped by RunStreamStore (RecordNext/Record) survives
/// serialization into the client-facing SSE/REST event shape via EndpointHelpers.StampTimestamp —
/// the fix for message timestamps showing "just now" and resetting on every re-render because the
/// frontend fell back to Date.now() at render time when no `timestamp_utc` key was present.
/// </summary>
public sealed class RunEventTimestampSerializationTests
{
    [Fact]
    public void StampTimestamp_AddsTimestampUtcKey_WhenPayloadHasNone()
    {
        var stamp = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var evt = new RunEvent(1, "coordinator.work_plan", new { plan = "build" }, stamp);

        var node = EndpointHelpers.StampTimestamp(evt);

        node.Should().BeOfType<JsonObject>();
        node["timestamp_utc"]!.GetValue<string>().Should().Be(stamp.ToString("O"));
        node["plan"]!.GetValue<string>().Should().Be("build");
    }

    [Fact]
    public void StampTimestamp_DoesNotOverwrite_ExistingEmitterTimestamp()
    {
        // Per-emitter payload timestamps (e.g. workflow.step's timestamp_utc set explicitly by the
        // caller) must be preserved exactly — the central stamp only fills gaps, never overrides.
        var stamp = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var emitterStamp = "2020-01-01T00:00:00.0000000+00:00";
        var evt = new RunEvent(1, "workflow.step", new { step = "review", timestamp_utc = emitterStamp }, stamp);

        var node = EndpointHelpers.StampTimestamp(evt);

        node["timestamp_utc"]!.GetValue<string>().Should().Be(emitterStamp);
    }

    [Fact]
    public void StampTimestamp_FallsBackToNow_WhenEventHasDefaultTimestamp()
    {
        // Legacy/direct RunEvent construction without going through RunStreamStore (e.g. the
        // legacy result-fallback path) still must not surface a missing/blank timestamp to the client.
        var evt = new RunEvent(1, "agent.message", new { content = "hi" });

        var before = DateTimeOffset.UtcNow;
        var node = EndpointHelpers.StampTimestamp(evt);
        var after = DateTimeOffset.UtcNow;

        var parsed = DateTimeOffset.Parse(node["timestamp_utc"]!.GetValue<string>());
        parsed.Should().BeOnOrAfter(before);
        parsed.Should().BeOnOrBefore(after);
    }
}
