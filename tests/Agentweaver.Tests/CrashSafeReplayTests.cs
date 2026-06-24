using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Runtime;

/// <summary>
/// Verifies crash-safe durability and replay guarantees of <see cref="SqliteRunEventStream"/>:
///
/// <list type="bullet">
///   <item>Per-append SQLite write-through: the event is on disk before AppendAsync returns.</item>
///   <item>Full restart replay: a brand-new instance against the same DB file replays all events.</item>
///   <item>Cursor resume: SubscribeAsync(fromSequence=K) returns only events with Sequence > K.</item>
/// </list>
/// </summary>
public sealed class CrashSafeReplayTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly string _memoryDbPath;

    public CrashSafeReplayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-crashsafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // SqliteRunEventStream derives memory.db from the directory containing Database:Path.
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "agentweaver.db"),
            })
            .Build();

        _memoryDbPath = Path.Combine(_dir, "memory.db");
        CreateRunEventsTable(_memoryDbPath);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Durability guarantee
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AppendAsync succeeds → immediately drop in-memory state (new instance = simulated crash) →
    /// the event must already be in the SQLite file.
    /// </summary>
    [Fact]
    public async Task AppendAsync_WritesToSqlite_BeforeReturning()
    {
        const string runId = "dur-run-1";
        var stream = new SqliteRunEventStream(_config);

        await stream.AppendAsync(runId, new RunEvent(0, "agent.message.delta", new { delta = "hello" }));

        // Read directly from SQLite — bypasses all in-memory state.
        var rows = ReadRawRows(runId);
        rows.Should().HaveCount(1, "the event must be durable before AppendAsync returns");
        rows[0].eventType.Should().Be("agent.message.delta");
    }

    /// <summary>
    /// AppendAsync three events → immediately destroy the instance → open a fresh instance
    /// against the same DB file → all three rows must be present and ordered.
    /// </summary>
    [Fact]
    public async Task AppendAsync_MultipleEvents_AllDurableImmediately()
    {
        const string runId = "dur-run-2";
        var stream = new SqliteRunEventStream(_config);

        await stream.AppendAsync(runId, new RunEvent(0, "event.a", new { }));
        await stream.AppendAsync(runId, new RunEvent(0, "event.b", new { }));
        await stream.AppendAsync(runId, new RunEvent(0, EventTypes.RunCompleted, new { }));

        // Drop in-memory state: allocate a fresh instance (simulates process death after last write).
        _ = new SqliteRunEventStream(_config);

        var rows = ReadRawRows(runId);
        rows.Should().HaveCount(3);
        rows.Select(r => r.sequence).Should().BeInAscendingOrder();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Restart simulation
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Append N events via a producer instance → simulate restart (new instance, zero in-memory
    /// state) → SubscribeAsync(fromSequence=0) must replay all N events, in order, with no gaps
    /// and no duplicates.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task SimulatedRestart_SubscribeAsync_ReplaysAllEventsInOrder(int n)
    {
        var runId = $"restart-run-{n}";
        var producer = new SqliteRunEventStream(_config);

        for (var i = 1; i <= n; i++)
        {
            var type = i == n ? EventTypes.RunCompleted : "event.step";
            await producer.AppendAsync(runId, new RunEvent(0, type, new { step = i }));
        }

        // Simulate crash: brand-new instance — all channels and in-memory state discarded.
        var afterRestart = new SqliteRunEventStream(_config);

        var replayed = new List<RunEvent>();
        await foreach (var evt in afterRestart.SubscribeAsync(runId, 0))
            replayed.Add(evt);

        replayed.Should().HaveCount(n, "all {0} events must be replayed", n);
        replayed.Select(e => e.Sequence).Should().BeInAscendingOrder("sequences must be monotonic");
        replayed.Select(e => e.Sequence).Should().OnlyHaveUniqueItems("no duplicates");
        replayed[0].Sequence.Should().Be(1, "sequence starts at 1");
        replayed[^1].Sequence.Should().Be(n, "no gaps");
        replayed[^1].Type.Should().Be(EventTypes.RunCompleted);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cursor resume (Last-Event-ID semantics)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribe from sequence K → only events with Sequence &gt; K are returned.
    /// Verifies the SSE Last-Event-ID resume contract.
    /// </summary>
    [Theory]
    [InlineData(0, new[] { 1, 2, 3, 4, 5 })]  // from start
    [InlineData(1, new[] { 2, 3, 4, 5 })]       // resume after first
    [InlineData(3, new[] { 4, 5 })]              // resume mid-stream
    [InlineData(4, new[] { 5 })]                 // resume at penultimate
    public async Task SubscribeAsync_FromCursor_ReturnsOnlyEventsAfterCursor(int fromSequence, int[] expected)
    {
        var runId = $"cursor-run-{fromSequence}";

        // Use a fresh instance per sub-test to avoid channel state cross-contamination.
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(0, "a", new { }));
        await stream.AppendAsync(runId, new RunEvent(0, "b", new { }));
        await stream.AppendAsync(runId, new RunEvent(0, "c", new { }));
        await stream.AppendAsync(runId, new RunEvent(0, "d", new { }));
        await stream.AppendAsync(runId, new RunEvent(0, EventTypes.RunCompleted, new { }));

        // Simulate reconnect after restart: new instance, cursor driven by Last-Event-ID.
        var reconnect = new SqliteRunEventStream(_config);

        var seen = new List<int>();
        await foreach (var evt in reconnect.SubscribeAsync(runId, fromSequence))
            seen.Add(evt.Sequence);

        seen.Should().Equal(expected, "only events with Sequence > {0} must be delivered", fromSequence);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static void CreateRunEventsTable(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "RunEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                "RunId" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RunEvents_RunId_Sequence" ON "RunEvents" ("RunId", "Sequence");
            """;
        cmd.ExecuteNonQuery();
    }

    private List<(int sequence, string eventType)> ReadRawRows(string runId)
    {
        using var conn = new SqliteConnection($"Data Source={_memoryDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Sequence", "EventType" FROM "RunEvents"
            WHERE "RunId" = $runId ORDER BY "Sequence";
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(int, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        return rows;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
