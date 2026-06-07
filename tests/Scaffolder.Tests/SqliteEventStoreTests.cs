using FluentAssertions;
using Microsoft.Data.Sqlite;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Infrastructure;

/// <summary>
/// Verifies FR-022: the event log is durable, append-only, and per-run monotonic.
/// All tests use a real temp-file SQLite database; no mocks.
/// </summary>
public sealed class SqliteEventStoreTests : IAsyncLifetime
{
    private TestSqliteDb _testDb = null!;
    private SqliteEventStore _store = null!;

    public async Task InitializeAsync()
    {
        _testDb = await TestSqliteDb.CreateAsync();
        _store = new SqliteEventStore(_testDb.Db);
    }

    public async Task DisposeAsync() => await _testDb.DisposeAsync();

    private static RunEvent MakeEvent(RunId runId, string type = EventType.AgentMessage) => new()
    {
        RunId = runId,
        Sequence = 0,
        Type = type,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = "{}"
    };

    [Fact]
    public async Task Append_FirstEvent_GetsSequence1()
    {
        var runId = RunId.New();
        var evt = MakeEvent(runId);

        var result = await _store.AppendAsync(evt);

        result.Sequence.Should().Be(1);
    }

    [Fact]
    public async Task Append_MultipleEvents_SequenceIsMonotonic()
    {
        var runId = RunId.New();
        var sequences = new List<int>();

        for (var i = 0; i < 5; i++)
        {
            var result = await _store.AppendAsync(MakeEvent(runId));
            sequences.Add(result.Sequence);
        }

        sequences.Should().BeInAscendingOrder(because: "FR-019 requires per-run monotonic sequences");
        sequences.Should().OnlyHaveUniqueItems(because: "each event must get a distinct sequence number");
    }

    [Fact]
    public async Task ReadFromAsync_AfterSequence_ReturnsOnlyLaterEvents()
    {
        var runId = RunId.New();
        for (var i = 0; i < 5; i++)
        {
            await _store.AppendAsync(MakeEvent(runId));
        }

        var results = new List<RunEvent>();
        await foreach (var evt in _store.ReadFromAsync(runId, afterSequence: 3))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(2);
        results.Select(e => e.Sequence).Should().BeEquivalentTo(new[] { 4, 5 });
    }

    [Fact]
    public async Task AppendOnly_Trigger_PreventsDirectUpdate()
    {
        var runId = RunId.New();
        await _store.AppendAsync(MakeEvent(runId));

        await using var connection = await _testDb.Db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE run_events SET type = 'tampered' WHERE run_id = '{runId}';";

        var act = async () => await command.ExecuteNonQueryAsync();

        await act.Should().ThrowAsync<SqliteException>(
            because: "FR-022 requires append-only enforcement via DB trigger");
    }

    [Fact]
    public async Task ConcurrentAppend_TwoTasks_BothSucceed_SequencesAreUnique()
    {
        var runId = RunId.New();
        var results = new System.Collections.Concurrent.ConcurrentBag<RunEvent>();

        var task1 = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                results.Add(await _store.AppendAsync(MakeEvent(runId)));
            }
        });

        var task2 = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                results.Add(await _store.AppendAsync(MakeEvent(runId)));
            }
        });

        await Task.WhenAll(task1, task2);

        results.Should().HaveCount(10);
        results.Select(e => e.Sequence).Should().OnlyHaveUniqueItems(
            because: "concurrent appends must each receive a unique per-run sequence");
    }

    [Fact]
    public async Task ReadFromAsync_ReturnsEmpty_WhenNoEventsAfterCursor()
    {
        var runId = RunId.New();
        await _store.AppendAsync(MakeEvent(runId));
        await _store.AppendAsync(MakeEvent(runId));

        var results = new List<RunEvent>();
        await foreach (var evt in _store.ReadFromAsync(runId, afterSequence: 100))
        {
            results.Add(evt);
        }

        results.Should().BeEmpty(because: "there are no events after sequence 100");
    }
}
