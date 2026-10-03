using System.Text.Json;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

[Collection("PostgreSQL")]
public sealed class OutboxIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly string _schema = "test_" + Guid.NewGuid().ToString("N");
    private PostgresOutbox _outbox = null!;

    public OutboxIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _outbox = new PostgresOutbox(_fixture.DataSource, _schema);
        await _outbox.InitializeAsync();
    }

    public Task DisposeAsync() => DropSchemaAsync(_schema);

    private async Task DropSchemaAsync(string schema)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static OutboxEvent Message(
        string stream = "stream", string? key = null, Guid? id = null,
        string payload = """{"value":1}""", string type = "changed",
        int version = 1, DateTimeOffset? occurred = null)
    {
        using var document = JsonDocument.Parse(payload);
        return new OutboxEvent(id ?? Guid.NewGuid(), stream, key ?? Guid.NewGuid().ToString("N"),
            type, version, document.RootElement.Clone(), occurred ?? DateTimeOffset.UtcNow);
    }

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

    private Task<long> EventCountAsync() =>
        ScalarAsync($"SELECT count(*) FROM \"{_schema}\".outbox_events");

    private async Task CreateServiceTableAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"CREATE TABLE \"{_schema}\".service_state (id text PRIMARY KEY, revision integer NOT NULL)", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task StateAndEventCommitTogetherAndRollbackTogether()
    {
        await CreateServiceTableAsync();
        var committed = Message(key: "committed");
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var update = new NpgsqlCommand(
                $"INSERT INTO \"{_schema}\".service_state VALUES ('aggregate', 1)", connection, transaction);
            await update.ExecuteNonQueryAsync();
            await _outbox.EnqueueAsync(connection, transaction, committed);
            await transaction.CommitAsync();
        }
        Assert.Equal(1, await ScalarAsync($"SELECT revision FROM \"{_schema}\".service_state WHERE id = 'aggregate'"));
        Assert.Equal(1, await EventCountAsync());

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var update = new NpgsqlCommand(
                $"UPDATE \"{_schema}\".service_state SET revision = 2 WHERE id = 'aggregate'", connection, transaction);
            await update.ExecuteNonQueryAsync();
            await _outbox.EnqueueAsync(connection, transaction, Message(key: "rolled-back"));
            await transaction.RollbackAsync();
        }
        Assert.Equal(1, await ScalarAsync($"SELECT revision FROM \"{_schema}\".service_state WHERE id = 'aggregate'"));
        Assert.Equal(1, await EventCountAsync());
        Assert.Equal(2, (await EnqueueAsync(Message(key: "next"))).Sequence);
    }

    [Fact]
    public async Task RetryWithEquivalentJsonReturnsOriginalAndConflictsLeaveNoExtraEvent()
    {
        var original = Message(key: "key", payload: """{"a":1,"b":{"x":true}}""");
        var first = await EnqueueAsync(original);
        var equivalent = original with { Payload = Message(payload: """{"b":{"x":true},"a":1}""").Payload };
        var again = await EnqueueAsync(equivalent);
        Assert.Equal(first.Message.Id, again.Message.Id);
        Assert.Equal(first.Sequence, again.Sequence);

        foreach (var conflict in new[]
        {
            original with { Payload = Message(payload: """{"a":2,"b":{"x":true}}""").Payload },
            original with { EventType = "another" },
            original with { EventVersion = 2 },
            original with { StreamId = "different" },
            original with { Id = Guid.NewGuid() },
            original with { OccurredAt = original.OccurredAt.AddSeconds(1) },
            original with { IdempotencyKey = "different-key" }
        })
        {
            await Assert.ThrowsAsync<OutboxConflictException>(() => EnqueueAsync(conflict));
            Assert.Equal(1, await EventCountAsync());
        }
        Assert.Equal(2, (await EnqueueAsync(Message(key: "next"))).Sequence);
    }

    [Fact]
    public async Task ReturnedPayloadOutlivesSourceDocument()
    {
        using var source = JsonDocument.Parse("""{"nested":{"value":"owned"}}""");
        var message = Message() with { Payload = source.RootElement };
        var stored = await EnqueueAsync(message);
        source.Dispose();
        Assert.Equal("owned", stored.Message.Payload.GetProperty("nested").GetProperty("value").GetString());
        Assert.Equal(stored.Sequence, (await EnqueueAsync(
            message with { Payload = JsonDocument.Parse("""{"nested":{"value":"owned"}}""").RootElement.Clone() })).Sequence);
    }

    [Fact]
    public async Task ConcurrentSameKeyCreatesOneDurableEvent()
    {
        var message = Message(key: "race");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = Enumerable.Range(0, 8).Select(async _ =>
        {
            await gate.Task;
            return await EnqueueAsync(message);
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(contenders);
        Assert.All(results, result =>
        {
            Assert.Equal(message.Id, result.Message.Id);
            Assert.Equal(1, result.Sequence);
        });
        Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task ConcurrentStreamWritersAssignContiguousUniqueSequences()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, 16).Select(async index =>
        {
            await gate.Task;
            return await EnqueueAsync(Message(stream: "hot", key: "key-" + index));
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(writers);
        Assert.Equal(Enumerable.Range(1, 16).Select(n => (long)n),
            results.Select(result => result.Sequence).Order());
        Assert.Equal(16, await EventCountAsync());
    }

    [Fact]
    public async Task ClaimsAreDisjointAndBlockedByEarlierUndeliveredSequence()
    {
        var first = await EnqueueAsync(Message(stream: "ordered", occurred: DateTimeOffset.UtcNow.AddDays(1)));
        var second = await EnqueueAsync(Message(stream: "ordered", occurred: DateTimeOffset.UtcNow.AddDays(-1)));
        var independent = await EnqueueAsync(Message(stream: "independent"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new[] { "worker-a", "worker-b" }.Select(async worker =>
        {
            await gate.Task;
            return await _outbox.ClaimAsync(worker, 2, TimeSpan.FromMinutes(1));
        }).ToArray();
        gate.SetResult();
        var batches = await Task.WhenAll(workers);
        var deliveries = batches.SelectMany(batch => batch).ToArray();
        Assert.Equal(2, deliveries.Length);
        Assert.Equal(2, deliveries.Select(d => d.Event.Message.Id).Distinct().Count());
        Assert.Contains(deliveries, d => d.Event.Message.Id == first.Message.Id);
        Assert.Contains(deliveries, d => d.Event.Message.Id == independent.Message.Id);
        Assert.DoesNotContain(deliveries, d => d.Event.Message.Id == second.Message.Id);
        Assert.Empty(await _outbox.ClaimAsync("other", 10, TimeSpan.FromMinutes(1)));

        var firstClaim = Assert.Single(deliveries, d => d.Event.Message.Id == first.Message.Id);
        Assert.True(await _outbox.AcknowledgeAsync(first.Message.Id, firstClaim.LeaseToken));
        var next = Assert.Single(await _outbox.ClaimAsync("other", 10, TimeSpan.FromMinutes(1)));
        Assert.Equal(second.Message.Id, next.Event.Message.Id);
        Assert.Equal(2, next.Event.Sequence);
        Assert.True(await _outbox.AcknowledgeAsync(second.Message.Id, next.LeaseToken));
        var independentClaim = Assert.Single(deliveries, d => d.Event.Message.Id == independent.Message.Id);
        Assert.True(await _outbox.AcknowledgeAsync(independent.Message.Id, independentClaim.LeaseToken));
        Assert.Empty(await _outbox.ClaimAsync("other", 10, TimeSpan.FromMinutes(1)));
        Assert.False(await _outbox.AcknowledgeAsync(first.Message.Id, firstClaim.LeaseToken));
    }

    [Fact]
    public async Task RestartPreservesLeaseAndReclaimFencesObsoleteAcknowledgment()
    {
        var first = await EnqueueAsync(Message());
        var second = await EnqueueAsync(Message());
        var leased = Assert.Single(await _outbox.ClaimAsync("original", 1, TimeSpan.FromMinutes(1)));
        var restarted = new PostgresOutbox(_fixture.DataSource, _schema);
        await restarted.InitializeAsync();
        Assert.Empty(await restarted.ClaimAsync("restarted", 2, TimeSpan.FromMinutes(1)));

        // Set DB lease expiry instead of relying on scheduler timing for this race.
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                $"UPDATE \"{_schema}\".outbox_events SET leased_until = clock_timestamp() - interval '1 second' WHERE id = @id",
                connection);
            command.Parameters.AddWithValue("id", first.Message.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.False(await restarted.AcknowledgeAsync(first.Message.Id, leased.LeaseToken));
        var renewed = Assert.Single(await restarted.ClaimAsync("restarted", 2, TimeSpan.FromMinutes(1)));
        Assert.Equal(first.Message.Id, renewed.Event.Message.Id);
        Assert.NotEqual(leased.LeaseToken, renewed.LeaseToken);
        Assert.False(await _outbox.AcknowledgeAsync(first.Message.Id, leased.LeaseToken));
        Assert.False(await restarted.AcknowledgeAsync(Guid.NewGuid(), renewed.LeaseToken));
        Assert.True(await restarted.AcknowledgeAsync(first.Message.Id, renewed.LeaseToken));
        var successor = Assert.Single(await _outbox.ClaimAsync("original", 1, TimeSpan.FromMinutes(1)));
        Assert.Equal(second.Message.Id, successor.Event.Message.Id);
    }

    [Fact]
    public async Task GenuineShortLeaseExpiresAndCanBeReclaimed()
    {
        var message = await EnqueueAsync(Message());
        var first = Assert.Single(await _outbox.ClaimAsync("first", 1, TimeSpan.FromMilliseconds(150)));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        IReadOnlyList<OutboxDelivery> retry = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            retry = await _outbox.ClaimAsync("retry", 1, TimeSpan.FromSeconds(10));
            if (retry.Count != 0) break;
            await Task.Delay(30);
        }
        var reclaimed = Assert.Single(retry);
        Assert.Equal(message.Message.Id, reclaimed.Event.Message.Id);
        Assert.NotEqual(first.LeaseToken, reclaimed.LeaseToken);
        Assert.False(await _outbox.AcknowledgeAsync(message.Message.Id, first.LeaseToken));
        Assert.True(await _outbox.AcknowledgeAsync(message.Message.Id, reclaimed.LeaseToken));
    }

    [Fact]
    public async Task AcknowledgmentRechecksExpiryAfterWaitingForUnchangedRowLock()
    {
        var stored = await EnqueueAsync(Message());
        var delivery = Assert.Single(await _outbox.ClaimAsync("worker", 1, TimeSpan.FromSeconds(3)));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var rowLock = new NpgsqlCommand(
            $"SELECT id FROM \"{_schema}\".outbox_events WHERE id = @id FOR UPDATE",
            connection, transaction);
        rowLock.Parameters.AddWithValue("id", stored.Message.Id);
        await rowLock.ExecuteScalarAsync();
        var acknowledgment = _outbox.AcknowledgeAsync(stored.Message.Id, delivery.LeaseToken);
        bool acknowledged;
        try
        {
            await WaitForDatabaseConditionAsync($"""
                SELECT count(*) FROM pg_stat_activity
                WHERE state = 'active' AND wait_event_type = 'Lock'
                    AND query LIKE '%"{_schema}".outbox_events%'
                """);
            await WaitForDatabaseConditionAsync($"""
                SELECT count(*) FROM "{_schema}".outbox_events
                WHERE leased_until <= clock_timestamp()
                """);
            Assert.False(acknowledgment.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync();
            acknowledged = await acknowledgment.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(acknowledged);
        Assert.Equal(0, await ScalarAsync(
            $"SELECT count(*) FROM \"{_schema}\".outbox_events WHERE delivered_at IS NOT NULL"));
    }

    private async Task WaitForDatabaseConditionAsync(string sql)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ScalarAsync(sql) != 0) return;
            await Task.Delay(30);
        }
        Assert.Fail("The expected PostgreSQL condition was not observed before the deadline.");
    }

    [Fact]
    public async Task SchemasAreIsolatedDespiteOverlappingKeysAndStreamNames()
    {
        var otherSchema = "test_" + Guid.NewGuid().ToString("N");
        try
        {
            var other = new PostgresOutbox(_fixture.DataSource, otherSchema);
            await other.InitializeAsync();
            var message = Message(stream: "shared", key: "same-key");
            await EnqueueAsync(message);
            await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var stored = await other.EnqueueAsync(connection, transaction,
                    message with { Id = Guid.NewGuid() });
                await transaction.CommitAsync();
                Assert.Equal(1, stored.Sequence);
            }
            Assert.Equal(1, await EventCountAsync());
            Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM \"{otherSchema}\".outbox_events"));
            Assert.Single(await other.ClaimAsync("other", 10, TimeSpan.FromMinutes(1)));
            Assert.Single(await _outbox.ClaimAsync("this", 10, TimeSpan.FromMinutes(1)));
        }
        finally
        {
            await DropSchemaAsync(otherSchema);
        }
    }

    [Fact]
    public async Task RepeatedAndConcurrentInitializationPreservesCommittedData()
    {
        var persisted = await EnqueueAsync(Message());
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => new PostgresOutbox(_fixture.DataSource, _schema).InitializeAsync()));
        await _outbox.InitializeAsync();
        Assert.Equal(1, await EventCountAsync());
        Assert.Equal(2, (await EnqueueAsync(Message())).Sequence);
        Assert.Equal(persisted.Message.Id,
            Assert.Single(await _outbox.ClaimAsync("worker", 1, TimeSpan.FromMinutes(1))).Event.Message.Id);
    }

    [Fact]
    public async Task ConcurrentFirstInitializationAppliesMigrationExactlyOnce()
    {
        var newSchema = "test_" + Guid.NewGuid().ToString("N");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(_ => new PostgresOutbox(_fixture.DataSource, newSchema).InitializeAsync()));
            Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM \"{newSchema}\".outbox_schema_migrations"));
            var store = new PostgresOutbox(_fixture.DataSource, newSchema);
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            Assert.Equal(1, (await store.EnqueueAsync(connection, transaction, Message())).Sequence);
            await transaction.CommitAsync();
        }
        finally
        {
            await DropSchemaAsync(newSchema);
        }
    }

    [Fact]
    public async Task RefusesUnknownNewerSchemaVersionWithoutErasingData()
    {
        var persisted = await EnqueueAsync(Message());
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO \"{_schema}\".outbox_schema_migrations (version) VALUES (999)", connection);
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => _outbox.InitializeAsync());
        Assert.Equal(1, await EventCountAsync());
        Assert.Equal(persisted.Message.Id,
            Assert.Single(await _outbox.ClaimAsync("worker", 1, TimeSpan.FromMinutes(1))).Event.Message.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("public")]
    [InlineData("pg_catalog")]
    [InlineData("information_schema")]
    [InlineData("pg_hidden")]
    [InlineData("Bad")]
    [InlineData("1bad")]
    [InlineData("safe\n")]
    [InlineData("quoted\"; DROP SCHEMA public; --")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void RejectsUnsafeSchemas(string schema) =>
        Assert.ThrowsAny<ArgumentException>(() => new PostgresOutbox(_fixture.DataSource, schema));

    [Fact]
    public async Task RejectsBadClaimsAndBadEnqueueTransactions()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _outbox.ClaimAsync("", 1, TimeSpan.FromMinutes(1)));
        foreach (var size in new[] { 0, -1, 1001 })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => _outbox.ClaimAsync("worker", size, TimeSpan.FromMinutes(1)));
        foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)) })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => _outbox.ClaimAsync("worker", 1, duration));

        await using var closed = await _fixture.DataSource.OpenConnectionAsync();
        await using var closedTransaction = await closed.BeginTransactionAsync();
        await closed.CloseAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _outbox.EnqueueAsync(closed, closedTransaction, Message()));

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var another = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _outbox.EnqueueAsync(another, transaction, Message()));
        await transaction.RollbackAsync();
        Assert.Equal(0, await EventCountAsync());
    }

    [Fact]
    public async Task RejectsMalformedPayloadAndMissingRequiredMessageFields()
    {
        var message = Message();
        foreach (var invalid in new[]
        {
            message with { Payload = default },
            message with { Payload = JsonDocument.Parse("[]").RootElement.Clone() },
            message with { Payload = JsonDocument.Parse("null").RootElement.Clone() },
            message with { StreamId = "" },
            message with { IdempotencyKey = "" },
            message with { EventType = "" },
            message with { EventVersion = 0 }
        })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => EnqueueAsync(invalid));
            Assert.Equal(0, await EventCountAsync());
        }
    }
}
