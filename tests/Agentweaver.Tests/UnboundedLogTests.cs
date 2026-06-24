using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Runtime;

/// <summary>
/// Verifies US3: the 10k cap is gone — appending more than 10,000 events via
/// <see cref="IRunEventStream.AppendAsync"/> produces a complete, ordered log with no
/// truncation and no silent drops.
/// </summary>
public sealed class UnboundedLogTests : IDisposable
{
    private const int EventCount = 10_001;

    private readonly string _dir;
    private readonly IConfiguration _config;

    public UnboundedLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-unbounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "agentweaver.db"),
            })
            .Build();

        CreateRunEventsTable(Path.Combine(_dir, "memory.db"));
    }

    private static void CreateRunEventsTable(string memoryDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={memoryDbPath}");
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

    [Fact]
    public async Task AppendMoreThan10k_AllEventsRecorded_NoneDropped()
    {
        var runId = "run-unbounded";
        var stream = new SqliteRunEventStream(_config);

        for (var i = 1; i <= EventCount; i++)
            await stream.AppendAsync(runId, new RunEvent(i, "agent.message.delta", new { delta = $"chunk-{i}" }));

        await stream.AppendAsync(runId, new RunEvent(EventCount + 1, EventTypes.RunCompleted, new { }));

        var replayed = new List<RunEvent>();
        await foreach (var evt in stream.SubscribeAsync(runId, fromSequence: 0))
            replayed.Add(evt);

        replayed.Should().HaveCount(EventCount + 1, "no event must be truncated — the 10k cap is gone");
        replayed.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, EventCount + 1),
            "events must be replayed in strict monotonic order with no gaps");
    }

    [Fact]
    public async Task AppendMoreThan10k_OrderingIsCorrect()
    {
        var runId = "run-ordering";
        var stream = new SqliteRunEventStream(_config);

        for (var i = 1; i <= EventCount; i++)
            await stream.AppendAsync(runId, new RunEvent(i, "agent.message.delta", new { seq = i }));

        await stream.AppendAsync(runId, new RunEvent(EventCount + 1, EventTypes.RunCompleted, new { }));

        var sequences = new List<int>();
        await foreach (var evt in stream.SubscribeAsync(runId, fromSequence: 0))
            sequences.Add(evt.Sequence);

        sequences.Should().BeInAscendingOrder("sequences must be strictly monotonic");
        sequences[^1].Should().Be(EventCount + 1, "last event must be the terminal");
        sequences[0].Should().Be(1, "first event must be sequence 1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort; pooled handles may linger */ }
    }
}
