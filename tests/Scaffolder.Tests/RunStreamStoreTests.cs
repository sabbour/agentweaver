using FluentAssertions;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Runtime;

/// <summary>
/// Verifies Principle V (Observable Runs): a finished run's recorded event sequence
/// (the incremental agent.message.delta events) remains replayable after completion,
/// so a late or reconnecting client can watch the run as a stream rather than receiving
/// a single collapsed message.
/// </summary>
public sealed class RunStreamStoreTests
{
    [Fact]
    public void CompletedRun_RetainsDeltaSequence_ForReplay()
    {
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "Hello, ", messageId = "m1" }));
        entry.Record(new RunEvent(2, "agent.message.delta", new { delta = "world", messageId = "m1" }));
        entry.Record(new RunEvent(3, "run.completed", new { }));

        store.Complete(runId);

        var retained = store.Get(runId);
        retained.Should().NotBeNull();
        retained!.IsCompleted.Should().BeTrue();

        var snapshot = retained.GetSnapshotSince(0);
        snapshot.Events.Should().HaveCount(3);
        snapshot.IsCompleted.Should().BeTrue();
        snapshot.Events.Select(e => e.Type).Should().ContainInOrder(
            "agent.message.delta", "agent.message.delta", "run.completed");
    }

    [Fact]
    public void GetSnapshotSince_ReplaysOnlyEventsAfterLastSeen()
    {
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "a" }));
        entry.Record(new RunEvent(2, "agent.message.delta", new { delta = "b" }));
        entry.Record(new RunEvent(3, "agent.message.delta", new { delta = "c" }));
        store.Complete(runId);

        var snapshot = store.Get(runId)!.GetSnapshotSince(1);
        snapshot.Events.Select(e => e.Sequence).Should().Equal(2, 3);
    }

    [Fact]
    public void GetSnapshotSince_ReturnsCompletionFlag_Atomically()
    {
        // Verifies that the snapshot captures both events and completion state in one lock.
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "x" }));
        entry.Record(new RunEvent(2, "agent.message.delta", new { delta = "y" }));

        // Before completion
        var snap1 = entry.GetSnapshotSince(0);
        snap1.Events.Should().HaveCount(2);
        snap1.IsCompleted.Should().BeFalse();

        // Add final event and complete
        entry.Record(new RunEvent(3, "run.completed", new { }));
        entry.MarkCompleted();

        // After completion — snapshot from seq 2 must include the tail event and show completed
        var snap2 = entry.GetSnapshotSince(2);
        snap2.Events.Should().HaveCount(1);
        snap2.Events[0].Sequence.Should().Be(3);
        snap2.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void NoTailEventsLost_WhenCompletionHappensBetweenPolls()
    {
        // Simulates the race: events are written and completion is marked between two client polls.
        // The atomic snapshot must include both the tail events and the completion flag.
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "a" }));

        // Client reads up to seq 1
        var snap = entry.GetSnapshotSince(0);
        snap.Events.Should().HaveCount(1);
        snap.IsCompleted.Should().BeFalse();
        var lastSeen = 1;

        // Between polls: more events arrive and completion happens
        entry.Record(new RunEvent(2, "agent.message.delta", new { delta = "b" }));
        entry.Record(new RunEvent(3, "run.completed", new { }));
        entry.MarkCompleted();

        // Client polls again — must see tail events AND completion
        var snap2 = entry.GetSnapshotSince(lastSeen);
        snap2.Events.Select(e => e.Sequence).Should().Equal(2, 3);
        snap2.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentClients_BothReceiveFullOrderedSequence()
    {
        // Two clients read from the same entry concurrently — both must get the full
        // ordered event sequence without corruption or missing events.
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        const int totalEvents = 100;

        // Simulate a producer writing events with small delays
        var producer = Task.Run(async () =>
        {
            for (var i = 1; i <= totalEvents; i++)
            {
                entry.Record(new RunEvent(i, "agent.message.delta", new { delta = $"chunk-{i}" }));
                if (i % 10 == 0) await Task.Delay(1);
            }
            entry.MarkCompleted();
        });

        // Two concurrent consumers polling the entry
        async Task<List<int>> ConsumeAsync()
        {
            var seen = new List<int>();
            var lastSeen = 0;
            while (true)
            {
                var snapshot = entry.GetSnapshotSince(lastSeen);
                foreach (var evt in snapshot.Events)
                {
                    seen.Add(evt.Sequence);
                    if (evt.Sequence > lastSeen) lastSeen = evt.Sequence;
                }
                if (snapshot.IsCompleted) break;
                try { await entry.WaitForChangeAsync(CancellationToken.None); }
                catch (TimeoutException) { }
            }
            return seen;
        }

        var client1 = ConsumeAsync();
        var client2 = ConsumeAsync();

        await producer;
        var result1 = await client1;
        var result2 = await client2;

        // Both clients must have received all events in order
        result1.Should().Equal(Enumerable.Range(1, totalEvents));
        result2.Should().Equal(Enumerable.Range(1, totalEvents));
    }

    [Fact]
    public void Owner_IsStoredOnEntry()
    {
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "alice");

        entry.Owner.Should().Be("alice");
    }

    [Fact]
    public void MaxEventsPerRun_CapsHistory()
    {
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        for (var i = 1; i <= RunStreamEntry.MaxEventsPerRun + 100; i++)
            entry.Record(new RunEvent(i, "agent.message.delta", new { delta = "x" }));

        var snapshot = entry.GetSnapshotSince(0);
        snapshot.Events.Should().HaveCount(RunStreamEntry.MaxEventsPerRun);
    }

    [Fact]
    public async Task RecordedEvent_WakesWaitingClient_WithoutFullTimeout()
    {
        // A client blocked in WaitForChangeAsync must wake promptly when Record() is
        // called — well under the 1 s safety timeout.
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        var waitTask = entry.WaitForChangeAsync(CancellationToken.None);

        // Record an event after a tiny delay to prove the wake-up is from the event, not timeout
        await Task.Delay(50);
        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "x" }));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await waitTask;
        sw.Stop();

        // Must complete in well under 1 second (the timeout fallback). Allow 200 ms headroom.
        sw.ElapsedMilliseconds.Should().BeLessThan(200);
    }
}
