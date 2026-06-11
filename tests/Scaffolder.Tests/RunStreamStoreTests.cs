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
    public void RevisionSequence_IsMonotonic_AcrossRevisionBoundary()
    {
        var store = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user");

        entry.RecordNext("agent.delta", new { delta = "a" });
        entry.RecordNext("agent.delta", new { delta = "b" });
        entry.RecordNext("review.requested", new { });

        entry.BumpGeneration();

        entry.RecordNext("revision.started", new { });
        entry.RecordNext("agent.delta", new { delta = "c" });

        var snapshot = entry.GetSnapshotSince(3);
        snapshot.Events.Select(e => e.Sequence).Should().Equal(4, 5);
        snapshot.Events.All(e => e.Sequence > 3).Should().BeTrue();
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
    public async Task AtCapRecordNext_RefreshesLastActiveAt_PreventingEviction()
    {
        // Fix 1: even when the MaxEventsPerRun cap is reached, RecordNext must refresh
        // LastActiveAt so the eviction sweep does not mistake a live at-cap entry for a stale one.
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");

        // Fill the entry to the hard cap.
        for (var i = 1; i <= RunStreamEntry.MaxEventsPerRun; i++)
            entry.Record(new RunEvent(i, "agent.delta", new { }));

        // Wait past the eviction window so LastActiveAt is stale.
        await Task.Delay(100);

        // An at-cap RecordNext call (no new event added) must still refresh LastActiveAt.
        entry.RecordNext("agent.delta", new { delta = "cap-hit-but-alive" });

        // Trigger the eviction sweep.
        var sweepId = Guid.NewGuid().ToString();
        store.Create(sweepId, "sweep");
        store.Complete(sweepId);

        // The entry was active (RecordNext was called), so it must NOT be evicted.
        store.Get(runId).Should().NotBeNull(
            "an at-cap entry whose RecordNext was called after the eviction window must not be evicted");
    }

    [Fact]
    public async Task TryMarkEvicted_IsAtomic_RecordNextBeforeSweepPreservesEntry()
    {
        // Fix 2: TryMarkEvicted atomically checks staleness and sets the eviction flag.
        // If RecordNext refreshes LastActiveAt before TryMarkEvicted runs, the entry
        // is kept alive — no TOCTOU window between the stale-check and TryRemove.
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-race");
        entry.RecordNext("agent.delta", new { delta = "initial" });

        // Let the entry become stale.
        await Task.Delay(100);

        // Simulate RecordNext arriving just before the sweep's atomic check.
        // TryMarkEvicted will see the refreshed LastActiveAt and return false → entry survives.
        entry.RecordNext("agent.delta", new { delta = "just-refreshed" });

        var sweepId = Guid.NewGuid().ToString();
        store.Create(sweepId, "sweep");
        store.Complete(sweepId);

        store.Get(runId).Should().NotBeNull(
            "an entry refreshed by RecordNext before TryMarkEvicted runs must not be evicted");
    }

    [Fact]
    public async Task TryMarkEvicted_PreventsSubsequentRecordNextFromResurrecting()
    {
        // Fix 2 (reverse): once TryMarkEvicted marks an entry, concurrent RecordNext calls
        // that arrive AFTER the mark are no-ops — the entry cannot be resurrected.
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-evict");
        entry.RecordNext("agent.delta", new { delta = "initial" });

        // Let the entry become stale and atomically mark it for eviction.
        await Task.Delay(100);
        var cutoff = DateTimeOffset.UtcNow;
        entry.TryMarkEvicted(cutoff).Should().BeTrue("stale entry must be marked for eviction");

        // A RecordNext arriving AFTER the mark must be a no-op — history must not grow.
        var historyBefore = entry.GetSnapshotSince(0).Events.Count;
        entry.RecordNext("agent.delta", new { delta = "attempted-after-eviction" });
        entry.GetSnapshotSince(0).Events.Count.Should().Be(historyBefore,
            "RecordNext on an evicted entry must not append new events");
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
    public async Task Eviction_SupressedWhenLastActiveAtIsRecent_EvenIfCreatedAtIsOld()
    {
        // Use a very short maxInProgressAge so the test does not need to wait 2 hours.
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));

        // Create a stale entry: record one event, then let it age past the window.
        var staleId = Guid.NewGuid().ToString();
        var staleEntry = store.Create(staleId, "user-stale");
        staleEntry.RecordNext("agent.delta", new { delta = "old" });

        // Create a live entry: will receive an event after the aging window closes,
        // so its LastActiveAt stays recent regardless of when it was created.
        var liveId = Guid.NewGuid().ToString();
        var liveEntry = store.Create(liveId, "user-live");
        liveEntry.RecordNext("agent.delta", new { delta = "initial" });

        // Allow both entries' LastActiveAt to become stale.
        await Task.Delay(100);

        // Refresh only the live entry's LastActiveAt — simulating ongoing revision activity.
        liveEntry.RecordNext("agent.delta", new { delta = "still active" });

        // Trigger the eviction sweep by completing an unrelated run.
        var sweepTriggerId = Guid.NewGuid().ToString();
        store.Create(sweepTriggerId, "user-sweep");
        store.Complete(sweepTriggerId);

        // Live entry must survive: its LastActiveAt was just updated.
        store.Get(liveId).Should().NotBeNull(
            "an entry with a recent LastActiveAt must not be evicted even if it was created long ago");

        // Stale entry must be evicted: its LastActiveAt exceeded maxInProgressAge.
        store.Get(staleId).Should().BeNull(
            "an entry whose LastActiveAt is older than maxInProgressAge must be swept");
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

    [Fact]
    public async Task TryBumpGeneration_ReturnsFalse_WhenEntryIsEvicted()
    {
        // Regression: BumpGeneration did not check _evicted, so a revision started against
        // a dead entry and all RecordNext calls were silently dropped.
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");
        entry.RecordNext("agent.delta", new { });

        await Task.Delay(100);
        var cutoff = DateTimeOffset.UtcNow;
        entry.TryMarkEvicted(cutoff).Should().BeTrue("stale entry must be marked evicted");

        var (success, _) = entry.TryBumpGeneration();
        success.Should().BeFalse("TryBumpGeneration on an evicted entry must return false");
    }

    [Fact]
    public async Task TryBumpGeneration_EvictedEntry_StoreRemoveAndCreate_ProducesLiveFreshEntry()
    {
        // Simulates the StartRevisionAsync recovery path: Get returns an evicted entry,
        // TryBumpGeneration detects it, Remove + Create produces a fresh live entry that
        // accepts RecordNext calls.
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");
        entry.RecordNext("agent.delta", new { });

        await Task.Delay(100);
        entry.TryMarkEvicted(DateTimeOffset.UtcNow).Should().BeTrue();

        var (success, _) = entry.TryBumpGeneration();
        success.Should().BeFalse("evicted entry must not allow BumpGeneration");

        // Simulate the recovery: remove dead entry, create a fresh one.
        store.Remove(runId);
        var freshEntry = store.Create(runId, "user-a");
        var (freshSuccess, freshGeneration) = freshEntry.TryBumpGeneration();

        freshSuccess.Should().BeTrue("fresh entry must not be evicted");
        freshGeneration.Should().Be(2, "generation starts at 1 and TryBumpGeneration increments it once");
        store.Get(runId).Should().BeSameAs(freshEntry, "fresh entry must be visible in the store");

        // Verify the fresh entry actually accepts events.
        freshEntry.RecordNext("revision.started", new { revision = 2 });
        freshEntry.GetSnapshotSince(0).Events.Should().HaveCount(1,
            "fresh entry must record events normally after recreation");
    }

    [Fact]
    public async Task ClearAwaitingReview_RefreshesLastActiveAt_PreventingImmediateEviction()
    {
        // Regression: ClearAwaitingReview did not refresh LastActiveAt. An entry that sat
        // awaiting review longer than maxInProgressAge became immediately evictable the moment
        // protection was cleared, before any revision event could be recorded.
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");
        entry.RecordNext("agent.delta", new { });
        entry.MarkAwaitingReview();

        // Let the entry age past the stale window while protected.
        await Task.Delay(100);

        // Clearing protection must atomically refresh LastActiveAt so the eviction sweep
        // triggered immediately after cannot evict the entry.
        var alive = entry.ClearAwaitingReview();
        alive.Should().BeTrue("non-evicted entry must return true");

        // Trigger the eviction sweep.
        var sweepId = Guid.NewGuid().ToString();
        store.Create(sweepId, "sweep");
        store.Complete(sweepId);

        store.Get(runId).Should().NotBeNull(
            "ClearAwaitingReview must refresh LastActiveAt so the entry survives the immediate post-review sweep");
    }

    [Fact]
    public void ClearAwaitingReview_ReturnsFalse_WhenEntryAlreadyEvicted()
    {
        var store = new RunStreamStore(maxInProgressAge: TimeSpan.FromMilliseconds(50));
        var runId = Guid.NewGuid().ToString();
        var entry = store.Create(runId, "user-a");
        entry.RecordNext("agent.delta", new { });
        entry.MarkAwaitingReview();

        // Manually evict (bypass IsAwaitingReview by clearing it first then forcing cutoff).
        entry.ClearAwaitingReview(); // clear protection
        entry.TryMarkEvicted(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)).Should().BeTrue();

        // ClearAwaitingReview on an already-evicted entry must return false.
        var alive = entry.ClearAwaitingReview();
        alive.Should().BeFalse("ClearAwaitingReview must return false for an evicted entry");
    }
}
