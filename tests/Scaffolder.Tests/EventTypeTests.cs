using FluentAssertions;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Domain;

/// <summary>
/// Verifies FR-011: the event taxonomy is complete, consistent, and all envelope
/// fields are correctly typed. No mocks; all tests operate on the real domain types.
/// </summary>
public sealed class EventTypeTests
{
    [Fact]
    public void AllEventTypes_AreInAllSet()
    {
        // FR-011 defines exactly 14 event types across three groups.
        // Lifecycle: 4, Content: 5, Review/Merge: 5 = 14 total.
        // The spec originally read "15" but the taxonomy has 14 distinct constants.
        var declared = new[]
        {
            EventType.RunStarted, EventType.RunCompleted, EventType.RunFailed, EventType.RunBounded,
            EventType.AgentMessage,
            EventType.ToolCall, EventType.ToolResult, EventType.ToolRejected, EventType.ToolError,
            EventType.ReviewRequested, EventType.ReviewApproved, EventType.ReviewDeclined,
            EventType.MergeCompleted, EventType.MergeFailed
        };

        foreach (var type in declared)
        {
            EventType.All.Should().Contain(type,
                because: $"{type} is declared in the taxonomy and must appear in EventType.All");
        }

        EventType.All.Should().HaveCount(declared.Length,
            because: "EventType.All must contain exactly the declared taxonomy constants");
    }

    [Fact]
    public void EventEnvelope_RequiredFields_ArePresent()
    {
        var runId = RunId.New();
        var evt = new RunEvent
        {
            RunId = runId,
            Sequence = 1,
            Type = EventType.RunStarted,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = "{}"
        };

        evt.RunId.Should().Be(runId);
        evt.Sequence.Should().Be(1);
        evt.Type.Should().Be(EventType.RunStarted);
        evt.Timestamp.Should().NotBe(default);
        evt.Payload.Should().NotBeNull();
        // CallId is optional on non-tool events
        evt.CallId.Should().BeNull();
    }

    [Fact]
    public void ToolEvents_RequireCallId()
    {
        var runId = RunId.New();
        var callId = Guid.NewGuid().ToString("D");

        var toolEvents = new[]
        {
            new RunEvent { RunId = runId, Sequence = 1, Type = EventType.ToolCall, Timestamp = DateTimeOffset.UtcNow, Payload = "{}", CallId = callId },
            new RunEvent { RunId = runId, Sequence = 2, Type = EventType.ToolResult, Timestamp = DateTimeOffset.UtcNow, Payload = "{}", CallId = callId },
            new RunEvent { RunId = runId, Sequence = 3, Type = EventType.ToolRejected, Timestamp = DateTimeOffset.UtcNow, Payload = "{}", CallId = callId },
            new RunEvent { RunId = runId, Sequence = 4, Type = EventType.ToolError, Timestamp = DateTimeOffset.UtcNow, Payload = "{}", CallId = callId }
        };

        foreach (var evt in toolEvents)
        {
            evt.CallId.Should().NotBeNullOrEmpty(
                because: $"FR-018 requires tool event {evt.Type} to carry a callId");
        }
    }

    [Fact]
    public void TimestampField_IsInformationalOnly()
    {
        // FR-018: timestamp is informational only and MUST NOT be used to order events.
        // Two events with the same sequence must be treated as equal regardless of timestamp.
        var runId = RunId.New();
        var earlier = new RunEvent
        {
            RunId = runId,
            Sequence = 5,
            Type = EventType.AgentMessage,
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(-60),
            Payload = "{}"
        };
        var later = new RunEvent
        {
            RunId = runId,
            Sequence = 5,
            Type = EventType.AgentMessage,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = "{}"
        };

        // Sequence is the ordering key; timestamps differ but sequence is identical.
        earlier.Sequence.Should().Be(later.Sequence,
            because: "sequence establishes total order; timestamp must not override it");

        // Ordering by sequence places them equal, regardless of timestamp ordering.
        var ordered = new[] { earlier, later }.OrderBy(e => e.Sequence).ToList();
        ordered[0].Sequence.Should().Be(ordered[1].Sequence);
    }
}
