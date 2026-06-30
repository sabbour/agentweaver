using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Unit tests for <see cref="SqliteTokenUsageStore"/> covering persist/query correctness,
/// idempotent inserts, cross-run aggregation, time-range filtering, and per-project grouping.
/// Each test uses an isolated SQLite file via <see cref="TestSqliteDb"/> with no mocks.
/// </summary>
public sealed class SqliteTokenUsageStoreTests
{
    // =========================================================================
    // TU-01: RecordAsync writes a complete row that round-trips through GetRunUsageAsync.
    // =========================================================================
    [Fact]
    public async Task RecordAsync_StoresRecord()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteTokenUsageStore(testDb.Db);

        var runId = Guid.NewGuid().ToString();
        var record = new TokenUsageRecord
        {
            Id = $"{runId}:1",
            RunId = runId,
            ModelId = "gpt-4o",
            InputTokens = 100,
            OutputTokens = 50,
            TotalNanoAiu = 5_000_000_000L,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        await store.RecordAsync(record);

        var summary = await store.GetRunUsageAsync(runId);

        summary.InputTokens.Should().Be(100);
        summary.OutputTokens.Should().Be(50);
        summary.TotalTokens.Should().Be(150);
        summary.TotalNanoAiu.Should().Be(5_000_000_000L);
        summary.ByModel.Should().HaveCount(1);
        summary.ByModel[0].ModelId.Should().Be("gpt-4o");
        summary.ByModel[0].InputTokens.Should().Be(100);
        summary.ByModel[0].OutputTokens.Should().Be(50);
    }

    // =========================================================================
    // TU-02: Duplicate id is silently ignored; exactly one row survives.
    // =========================================================================
    [Fact]
    public async Task RecordAsync_IdempotentOnDuplicateId()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteTokenUsageStore(testDb.Db);

        var runId = Guid.NewGuid().ToString();
        var record = new TokenUsageRecord
        {
            Id = $"{runId}:1",
            RunId = runId,
            ModelId = "gpt-4o",
            InputTokens = 200,
            OutputTokens = 80,
            TotalNanoAiu = 1_000_000_000L,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        await store.RecordAsync(record);
        await store.RecordAsync(record);

        var summary = await store.GetRunUsageAsync(runId);

        // If INSERT OR IGNORE works correctly only one row exists, so totals are not doubled.
        summary.InputTokens.Should().Be(200);
        summary.OutputTokens.Should().Be(80);
        summary.TotalTokens.Should().Be(280);
        summary.ByModel.Should().HaveCount(1,
            "the duplicate insert must be silently discarded");
    }

    // =========================================================================
    // TU-03: GetRunUsageAsync aggregates totals and produces a per-model breakdown.
    // =========================================================================
    [Fact]
    public async Task GetRunUsageAsync_AggregatesTotals()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteTokenUsageStore(testDb.Db);

        var runId = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;

        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{runId}:1",
            RunId = runId,
            ModelId = "gpt-4o",
            InputTokens = 300,
            OutputTokens = 100,
            TotalNanoAiu = 2_000_000_000L,
            RecordedAt = at,
        });
        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{runId}:2",
            RunId = runId,
            ModelId = "gpt-4o-mini",
            InputTokens = 50,
            OutputTokens = 20,
            TotalNanoAiu = 500_000_000L,
            RecordedAt = at,
        });

        var summary = await store.GetRunUsageAsync(runId);

        summary.TotalTokens.Should().Be(470, "300+100+50+20");
        summary.InputTokens.Should().Be(350);
        summary.OutputTokens.Should().Be(120);
        summary.TotalNanoAiu.Should().Be(2_500_000_000L);
        summary.ByModel.Should().HaveCount(2);

        var gpt4o = summary.ByModel.Single(m => m.ModelId == "gpt-4o");
        gpt4o.InputTokens.Should().Be(300);
        gpt4o.OutputTokens.Should().Be(100);

        var mini = summary.ByModel.Single(m => m.ModelId == "gpt-4o-mini");
        mini.InputTokens.Should().Be(50);
        mini.OutputTokens.Should().Be(20);
    }

    // =========================================================================
    // TU-04: GetWorkflowRunUsageAsync sums usage across multiple agent runs
    //         that share the same workflowRunId.
    // =========================================================================
    [Fact]
    public async Task GetWorkflowRunUsageAsync_SumsAcrossRuns()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteTokenUsageStore(testDb.Db);

        var workflowRunId = Guid.NewGuid().ToString();
        var runA = Guid.NewGuid().ToString();
        var runB = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;

        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{runA}:1",
            RunId = runA,
            WorkflowRunId = workflowRunId,
            ModelId = "gpt-4o",
            InputTokens = 400,
            OutputTokens = 150,
            TotalNanoAiu = 3_000_000_000L,
            RecordedAt = at,
        });
        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{runB}:1",
            RunId = runB,
            WorkflowRunId = workflowRunId,
            ModelId = "gpt-4o",
            InputTokens = 200,
            OutputTokens = 80,
            TotalNanoAiu = 1_500_000_000L,
            RecordedAt = at,
        });

        // Record from a different workflow run that must not be included.
        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            WorkflowRunId = Guid.NewGuid().ToString(),
            ModelId = "gpt-4o",
            InputTokens = 9999,
            OutputTokens = 9999,
            TotalNanoAiu = 9_999_000_000L,
            RecordedAt = at,
        });

        var summary = await store.GetWorkflowRunUsageAsync(workflowRunId);

        summary.InputTokens.Should().Be(600, "400+200 from the two child runs");
        summary.OutputTokens.Should().Be(230);
        summary.TotalTokens.Should().Be(830);
        summary.TotalNanoAiu.Should().Be(4_500_000_000L);
    }

    // =========================================================================
    // TU-05: GetProjectUsageAsync filters by time range.
    // =========================================================================
    [Fact]
    public async Task GetProjectUsageAsync_FiltersOnTimeRange()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteTokenUsageStore(testDb.Db);

        var projectId = Guid.NewGuid().ToString();
        var insideWindow = DateTimeOffset.UtcNow;
        var beforeWindow = insideWindow.AddDays(-10);
        var afterWindow = insideWindow.AddDays(10);

        // Record inside the query window.
        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ModelId = "gpt-4o",
            InputTokens = 100,
            OutputTokens = 40,
            TotalNanoAiu = 1_000_000_000L,
            RecordedAt = insideWindow,
        });

        // Record before the window — must be excluded.
        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ModelId = "gpt-4o",
            InputTokens = 9999,
            OutputTokens = 9999,
            TotalNanoAiu = 9_999_000_000L,
            RecordedAt = beforeWindow,
        });

        var from = insideWindow.AddSeconds(-1);
        var to = insideWindow.AddSeconds(1);

        var summary = await store.GetProjectUsageAsync(projectId, from, to);

        summary.InputTokens.Should().Be(100);
        summary.OutputTokens.Should().Be(40);
        summary.TotalTokens.Should().Be(140);
    }

    // =========================================================================
    // TU-06: GetAppUsageAsync groups results by project id.
    // =========================================================================
    [Fact]
    public async Task GetAppUsageAsync_GroupsByProject()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteTokenUsageStore(testDb.Db);

        var projectA = Guid.NewGuid().ToString();
        var projectB = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;

        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectA,
            ModelId = "gpt-4o",
            InputTokens = 200,
            OutputTokens = 80,
            TotalNanoAiu = 2_000_000_000L,
            RecordedAt = at,
        });
        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectB,
            ModelId = "gpt-4o-mini",
            InputTokens = 50,
            OutputTokens = 20,
            TotalNanoAiu = 500_000_000L,
            RecordedAt = at,
        });

        // Record with no project_id — must not appear in app-level grouping.
        await store.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ModelId = "gpt-4o",
            InputTokens = 1,
            OutputTokens = 1,
            TotalNanoAiu = 1L,
            RecordedAt = at,
        });

        var from = at.AddSeconds(-1);
        var to = at.AddSeconds(1);

        var result = await store.GetAppUsageAsync(from, to);

        result.Should().HaveCount(2, "only the two project-attributed records should appear");

        var entryA = result.Single(p => p.ProjectId == projectA);
        entryA.TotalTokens.Should().Be(280);
        entryA.ByModel.Should().HaveCount(1);

        var entryB = result.Single(p => p.ProjectId == projectB);
        entryB.TotalTokens.Should().Be(70);
        entryB.ByModel.Should().HaveCount(1);
        entryB.ByModel[0].ModelId.Should().Be("gpt-4o-mini");
    }
}
