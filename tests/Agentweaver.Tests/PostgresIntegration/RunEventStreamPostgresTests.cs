using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class RunEventStreamPostgresTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task AppendAsync_ConcurrentSameRunAcrossIndependentStreams_AssignsUniqueContiguousSequences()
    {
        var runId = "run-events-pg-" + Guid.NewGuid().ToString("N");
        const int workerCount = 8;
        const int eventsPerWorker = 15;

        var providers = new List<ServiceProvider>();
        var streams = new List<EfRunEventStream>();
        try
        {
            for (var i = 0; i < workerCount; i++)
            {
                var services = new ServiceCollection();
                services.AddDbContextFactory<MemoryDbContext>(opts =>
                    opts.UseNpgsql(pg.ConnectionString,
                        n => n.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
                services.AddLogging();
                var provider = services.BuildServiceProvider();
                providers.Add(provider);
                streams.Add(new EfRunEventStream(provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>()));
            }

            var start = new Barrier(workerCount);
            var workers = streams.Select((stream, worker) => Task.Run(async () =>
            {
                start.SignalAndWait();
                for (var index = 0; index < eventsPerWorker; index++)
                {
                    _ = await stream.AppendAsync(runId, new RunEvent(0, EventTypes.ToolCall, new
                    {
                        worker,
                        index,
                        requestId = $"{worker:D2}-{index:D2}",
                    })).ConfigureAwait(false);
                }
            })).ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);

            var verifier = new EfRunEventStream(pg.Factory);
            _ = await verifier.AppendAsync(runId, new RunEvent(0, EventTypes.RunCompleted, new { })).ConfigureAwait(false);

            await using var db = await pg.CreateDbContextAsync().ConfigureAwait(false);
            var rows = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId)
                .OrderBy(e => e.Sequence)
                .ToListAsync()
                .ConfigureAwait(false);

            var expectedCount = (workerCount * eventsPerWorker) + 1;
            rows.Should().HaveCount(expectedCount);
            rows.Select(r => r.Sequence).Should().Equal(Enumerable.Range(1, expectedCount));
            rows.Select(r => r.Sequence).Distinct().Should().HaveCount(expectedCount);

            var replayed = new List<RunEvent>();
            await foreach (var evt in new EfRunEventStream(pg.Factory).SubscribeAsync(runId, 0))
                replayed.Add(evt);

            replayed.Should().HaveCount(expectedCount);
            replayed.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, expectedCount));
            replayed[^1].Type.Should().Be(EventTypes.RunCompleted);
        }
        finally
        {
            foreach (var provider in providers)
                provider.Dispose();
        }
    }

    [PostgresFact]
    public async Task AppendAsync_ExplicitSequence_DuplicateIdenticalEvent_IsIdempotent()
    {
        var runId = "run-events-pg-idempotent-" + Guid.NewGuid().ToString("N");
        var stream = new EfRunEventStream(pg.Factory);

        _ = await stream.AppendAsync(runId, new RunEvent(11, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = true,
        })).ConfigureAwait(false);

        _ = await stream.AppendAsync(runId, new RunEvent(11, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = true,
        })).ConfigureAwait(false);

        await using var db = await pg.CreateDbContextAsync().ConfigureAwait(false);
        var rows = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Sequence)
            .ToListAsync()
            .ConfigureAwait(false);

        rows.Should().ContainSingle();
        rows[0].Sequence.Should().Be(11);
        rows[0].EventType.Should().Be(EventTypes.ToolResult);
    }

    [PostgresFact]
    public async Task AppendAsync_ExplicitSequence_DuplicateDifferentPayload_Throws()
    {
        var runId = "run-events-pg-mismatch-" + Guid.NewGuid().ToString("N");
        var stream = new EfRunEventStream(pg.Factory);

        _ = await stream.AppendAsync(runId, new RunEvent(5, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = true,
        })).ConfigureAwait(false);

        var act = async () => _ = await stream.AppendAsync(runId, new RunEvent(5, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = false,
        })).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit sequence collision*")
            .ConfigureAwait(false);
    }
}
