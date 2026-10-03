using System.Text.Json;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

[Collection("PostgreSQL")]
public sealed class RelayIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly string _schema = "test_" + Guid.NewGuid().ToString("N");
    private PostgresOutbox _outbox = null!;

    public RelayIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _outbox = new PostgresOutbox(_fixture.DataSource, _schema);
        await _outbox.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static OutboxEvent Message(string stream = "stream", string? payload = null) =>
        new(Guid.NewGuid(), stream, Guid.NewGuid().ToString("N"), "changed", 1,
            JsonDocument.Parse(payload ?? """{"nested":{"value":1}}""").RootElement.Clone(),
            DateTimeOffset.UtcNow);

    private async Task<StoredOutboxEvent> EnqueueAsync(OutboxEvent message)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var stored = await _outbox.EnqueueAsync(connection, transaction, message);
        await transaction.CommitAsync();
        return stored;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private Task<long> DeliveredAsync() =>
        ScalarAsync($"SELECT count(*) FROM \"{_schema}\".outbox_events WHERE delivered_at IS NOT NULL");

    private async Task ExpireAsync(Guid id)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE \"{_schema}\".outbox_events SET leased_until = clock_timestamp() - interval '1 second' WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task WaitForDatabaseConditionAsync(string sql)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ScalarAsync(sql) != 0) return;
            await Task.Delay(30);
        }
        Assert.Fail("Expected PostgreSQL lock wait was not observed.");
    }

    private static void AssertSameEvent(StoredOutboxEvent expected, StoredOutboxEvent actual)
    {
        Assert.Equal(expected.Message.Id, actual.Message.Id);
        Assert.Equal(expected.Message.StreamId, actual.Message.StreamId);
        Assert.Equal(expected.Message.IdempotencyKey, actual.Message.IdempotencyKey);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.True(JsonElement.DeepEquals(expected.Message.Payload, actual.Message.Payload));
    }

    [Fact]
    public async Task PublishesBeforeAcknowledgingAndPreservesEventIdentity()
    {
        var first = await EnqueueAsync(Message("first"));
        var second = await EnqueueAsync(Message("second", """{"second":true}"""));
        var published = new List<StoredOutboxEvent>();
        var relay = new OutboxRelay(_outbox, new Publisher(async (item, _) =>
        {
            Assert.Equal(published.Count, await DeliveredAsync());
            published.Add(item);
        }));

        var outcomes = await relay.RelayOnceAsync("worker", 2, TimeSpan.FromMinutes(1));

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.IsType<RelayOutcome.Acknowledged>(outcome));
        Assert.Equal(published.Select(item => item.Message.Id),
            outcomes.Select(outcome => outcome.Event.Message.Id));
        AssertSameEvent(first, Assert.Single(published, item => item.Message.Id == first.Message.Id));
        AssertSameEvent(second, Assert.Single(published, item => item.Message.Id == second.Message.Id));
        Assert.Equal(2, await DeliveredAsync());
        Assert.Empty(await relay.RelayOnceAsync("worker", 2, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublisherFailureDoesNotAcknowledgeIncludingUnrelatedCancellation(bool canceledException)
    {
        var stored = await EnqueueAsync(Message());
        var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        Exception failure = canceledException
            ? new OperationCanceledException(unrelated.Token)
            : new InvalidOperationException("transport failed");
        var relay = new OutboxRelay(_outbox, new Publisher((_, _) => Task.FromException(failure)));

        var outcome = Assert.IsType<RelayOutcome.PublishFailed>(
            Assert.Single(await relay.RelayOnceAsync("worker", 1, TimeSpan.FromMinutes(1))));
        AssertSameEvent(stored, outcome.Event);
        Assert.Same(failure, outcome.Failure);
        Assert.Equal(0, await DeliveredAsync());
        Assert.Equal(1, await ScalarAsync(
            $"SELECT count(*) FROM \"{_schema}\".outbox_events WHERE lease_token IS NOT NULL"));
    }

    [Fact]
    public async Task FailureInOneStreamDoesNotBlockAnIndependentStream()
    {
        var failed = await EnqueueAsync(Message("bad"));
        var succeeded = await EnqueueAsync(Message("good"));
        var published = new List<Guid>();
        var relay = new OutboxRelay(_outbox, new Publisher((item, _) =>
        {
            published.Add(item.Message.Id);
            return item.Message.Id == failed.Message.Id
                ? Task.FromException(new InvalidOperationException("bad stream"))
                : Task.CompletedTask;
        }));

        var outcomes = await relay.RelayOnceAsync("worker", 2, TimeSpan.FromMinutes(1));

        Assert.Equal(2, published.Count);
        Assert.Contains(outcomes, result => result is RelayOutcome.PublishFailed { Event.Message.Id: var id } && id == failed.Message.Id);
        Assert.Contains(outcomes, result => result is RelayOutcome.Acknowledged { Event.Message.Id: var id } && id == succeeded.Message.Id);
        Assert.Equal(1, await DeliveredAsync());
    }

    [Fact]
    public async Task CancellationBeforeInvocationDoesNotClaim()
    {
        await EnqueueAsync(Message());
        using var source = new CancellationTokenSource();
        source.Cancel();
        var relay = new OutboxRelay(_outbox, new Publisher((_, _) =>
        {
            Assert.Fail("Must not publish");
            return Task.CompletedTask;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => relay.RelayOnceAsync("worker", 1, TimeSpan.FromMinutes(1), source.Token));
        Assert.Equal(0, await ScalarAsync(
            $"SELECT count(*) FROM \"{_schema}\".outbox_events WHERE lease_token IS NOT NULL"));
    }

    [Fact]
    public async Task CancellationDuringPublishLeavesLeaseUnacknowledged()
    {
        await EnqueueAsync(Message());
        using var source = new CancellationTokenSource();
        var relay = new OutboxRelay(_outbox, new Publisher((_, token) =>
        {
            Assert.Equal(source.Token, token);
            source.Cancel();
            return Task.FromCanceled(token);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => relay.RelayOnceAsync("worker", 1, TimeSpan.FromMinutes(1), source.Token));
        Assert.Equal(0, await DeliveredAsync());
        Assert.Equal(1, await ScalarAsync(
            $"SELECT count(*) FROM \"{_schema}\".outbox_events WHERE lease_token IS NOT NULL"));
    }

    [Fact]
    public async Task CancellationAfterPublishStopsBeforeAcknowledgment()
    {
        await EnqueueAsync(Message());
        using var source = new CancellationTokenSource();
        var relay = new OutboxRelay(_outbox, new Publisher((_, token) =>
        {
            Assert.Equal(source.Token, token);
            source.Cancel();
            return Task.CompletedTask;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => relay.RelayOnceAsync("worker", 1, TimeSpan.FromMinutes(1), source.Token));
        Assert.Equal(0, await DeliveredAsync());
    }

    [Fact]
    public async Task CancellationWhileAcknowledgmentWaitsForRealRowLockDoesNotAcknowledge()
    {
        var stored = await EnqueueAsync(Message());
        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var rowLock = new NpgsqlCommand(
            $"SELECT id FROM \"{_schema}\".outbox_events WHERE id = @id FOR UPDATE", blocker, transaction);
        rowLock.Parameters.AddWithValue("id", stored.Message.Id);
        using var source = new CancellationTokenSource();
        var relay = new OutboxRelay(_outbox, new Publisher(async (_, _) =>
        {
            await rowLock.ExecuteScalarAsync();
        }));
        var pending = relay.RelayOnceAsync("worker", 1, TimeSpan.FromMinutes(1), source.Token);
        try
        {
            await WaitForDatabaseConditionAsync($"""
                SELECT count(*) FROM pg_stat_activity
                WHERE state = 'active' AND wait_event_type = 'Lock'
                    AND query LIKE '%"{_schema}".outbox_events%'
                """);
            Assert.False(pending.IsCompleted);
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await DeliveredAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrReplacedLeaseReportsFencedAcknowledgment(bool replaced)
    {
        var stored = await EnqueueAsync(Message());
        OutboxDelivery? replacement = null;
        var competitor = new PostgresOutbox(_fixture.DataSource, _schema);
        var relay = new OutboxRelay(_outbox, new Publisher(async (item, _) =>
        {
            await ExpireAsync(item.Message.Id);
            if (replaced)
                replacement = Assert.Single(await competitor.ClaimAsync("competitor", 1, TimeSpan.FromMinutes(1)));
        }));

        var outcome = Assert.IsType<RelayOutcome.AcknowledgmentFenced>(
            Assert.Single(await relay.RelayOnceAsync("original", 1, TimeSpan.FromMinutes(1))));
        AssertSameEvent(stored, outcome.Event);
        Assert.Equal(0, await DeliveredAsync());
        if (replaced)
        {
            Assert.NotNull(replacement);
            AssertSameEvent(stored, replacement.Event);
            Assert.True(await competitor.AcknowledgeAsync(stored.Message.Id, replacement.LeaseToken));
        }
        else
        {
            Assert.Single(await competitor.ClaimAsync("competitor", 1, TimeSpan.FromMinutes(1)));
        }
    }

    [Fact]
    public async Task ConcurrentRelaysClaimDisjointEventsAndAcknowledgeOnce()
    {
        var first = await EnqueueAsync(Message("one"));
        var second = await EnqueueAsync(Message("two"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<Guid>();
        var sync = new object();
        Task Publish(StoredOutboxEvent item, CancellationToken _)
        {
            lock (sync) attempts.Add(item.Message.Id);
            return Task.CompletedTask;
        }
        var workers = new[] { "one", "two" }.Select(async worker =>
        {
            var relay = new OutboxRelay(new PostgresOutbox(_fixture.DataSource, _schema), new Publisher(Publish));
            await gate.Task;
            return await relay.RelayOnceAsync(worker, 2, TimeSpan.FromMinutes(1));
        }).ToArray();
        gate.SetResult();

        var outcomes = (await Task.WhenAll(workers)).SelectMany(batch => batch).ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.All(outcomes, outcome => Assert.IsType<RelayOutcome.Acknowledged>(outcome));
        Assert.Equal(new[] { first.Message.Id, second.Message.Id }.Order(),
            attempts.Order());
        Assert.Equal(2, await DeliveredAsync());
        Assert.Empty(await _outbox.ClaimAsync("later", 2, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task SuccessiveBatchesPreservePerStreamOrder()
    {
        var messages = new[]
        {
            await EnqueueAsync(Message()),
            await EnqueueAsync(Message()),
            await EnqueueAsync(Message())
        };
        var published = new List<StoredOutboxEvent>();
        var relay = new OutboxRelay(_outbox, new Publisher((item, _) =>
        {
            published.Add(item);
            return Task.CompletedTask;
        }));

        foreach (var expected in messages)
        {
            var outcome = Assert.IsType<RelayOutcome.Acknowledged>(
                Assert.Single(await relay.RelayOnceAsync("worker", 3, TimeSpan.FromMinutes(1))));
            AssertSameEvent(expected, outcome.Event);
        }
        Assert.Equal(new long[] { 1, 2, 3 }, published.Select(item => item.Sequence));
        Assert.Empty(await relay.RelayOnceAsync("worker", 3, TimeSpan.FromMinutes(1)));
        Assert.Equal(3, await DeliveredAsync());
    }

    [Fact]
    public async Task RestartRedeliversPublishedEventButAtomicInboxPreventsDuplicateEffect()
    {
        var stored = await EnqueueAsync(Message(payload: """{"nested":{"value":42}}"""));
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                $"CREATE TABLE \"{_schema}\".effects (id text PRIMARY KEY, count integer NOT NULL); " +
                $"INSERT INTO \"{_schema}\".effects VALUES ('counter', 0)", connection);
            await command.ExecuteNonQueryAsync();
        }
        var attempts = new List<StoredOutboxEvent>();
        var admissions = new List<InboxAdmission>();
        using var stopped = new CancellationTokenSource();
        async Task Publish(StoredOutboxEvent item, CancellationToken _)
        {
            attempts.Add(item);
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var admission = await _outbox.AdmitAsync(connection, transaction, "consumer", item.Message.Id.ToString("D"));
            admissions.Add(admission);
            if (admission == InboxAdmission.Admitted)
            {
                await using var update = new NpgsqlCommand(
                    $"UPDATE \"{_schema}\".effects SET count = count + 1 WHERE id = 'counter'",
                    connection, transaction);
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync();
            if (attempts.Count == 1) stopped.Cancel();
        }
        var first = new OutboxRelay(_outbox, new Publisher(Publish));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.RelayOnceAsync("original", 1, TimeSpan.FromMinutes(1), stopped.Token));
        Assert.Equal(0, await DeliveredAsync());
        Assert.Equal(1, await ScalarAsync($"SELECT count FROM \"{_schema}\".effects WHERE id = 'counter'"));
        await ExpireAsync(stored.Message.Id);

        var restartedOutbox = new PostgresOutbox(_fixture.DataSource, _schema);
        await restartedOutbox.InitializeAsync();
        var restarted = new OutboxRelay(restartedOutbox, new Publisher(Publish));
        var outcome = Assert.IsType<RelayOutcome.Acknowledged>(
            Assert.Single(await restarted.RelayOnceAsync("restarted", 1, TimeSpan.FromMinutes(1))));

        Assert.Equal(new[] { InboxAdmission.Admitted, InboxAdmission.Duplicate }, admissions);
        Assert.Equal(2, attempts.Count);
        AssertSameEvent(stored, attempts[0]);
        AssertSameEvent(attempts[0], attempts[1]);
        AssertSameEvent(stored, outcome.Event);
        Assert.Equal(1, await ScalarAsync($"SELECT count FROM \"{_schema}\".effects WHERE id = 'counter'"));
        Assert.Equal(1, await DeliveredAsync());
    }

    private sealed class Publisher(Func<StoredOutboxEvent, CancellationToken, Task> publish) : IOutboxPublisher
    {
        public Task PublishAsync(StoredOutboxEvent storedEvent, CancellationToken cancellationToken = default) =>
            publish(storedEvent, cancellationToken);
    }
}
