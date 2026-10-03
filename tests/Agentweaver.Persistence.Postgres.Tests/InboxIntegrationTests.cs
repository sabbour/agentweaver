using System.Text.Json;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

[Collection("PostgreSQL")]
public sealed class InboxIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly string _schema = "test_" + Guid.NewGuid().ToString("N");
    private PostgresOutbox _store = null!;

    public InboxIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _store = new PostgresOutbox(_fixture.DataSource, _schema);
        await _store.InitializeAsync();
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"CREATE TABLE \"{_schema}\".domain_state (id text PRIMARY KEY, revision integer NOT NULL)", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".{table}", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<int> RevisionAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT revision FROM \"{_schema}\".domain_state WHERE id = 'aggregate'", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<InboxAdmission> ProcessAsync(
        PostgresOutbox store, string consumer, string message, bool enqueue = false)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var admission = await store.AdmitAsync(connection, transaction, consumer, message);
        if (admission == InboxAdmission.Admitted)
        {
            await using var update = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".domain_state (id, revision) VALUES ('aggregate', 1)
                ON CONFLICT (id) DO UPDATE SET revision = domain_state.revision + 1
                """, connection, transaction);
            await update.ExecuteNonQueryAsync();
            if (enqueue)
            {
                using var document = JsonDocument.Parse("""{"effect":true}""");
                await store.EnqueueAsync(connection, transaction,
                    new OutboxEvent(Guid.NewGuid(), "aggregate", consumer + ":" + message,
                        "processed", 1, document.RootElement, DateTimeOffset.UtcNow));
            }
        }
        await transaction.CommitAsync();
        return admission;
    }

    [Fact]
    public async Task AdmissionDomainStateAndOutboxCommitOnceAndSurviveReconstructedClient()
    {
        Assert.Equal(InboxAdmission.Admitted, await ProcessAsync(_store, "consumer", "message", true));
        var restarted = new PostgresOutbox(_fixture.DataSource, _schema);
        await restarted.InitializeAsync();
        Assert.Equal(InboxAdmission.Duplicate, await ProcessAsync(restarted, "consumer", "message", true));
        Assert.Equal(1, await RevisionAsync());
        Assert.Equal(1, await CountAsync("consumer_inbox_receipts"));
        Assert.Equal(1, await CountAsync("outbox_events"));
        Assert.Equal(2, await CountAsync("outbox_schema_migrations"));
        Assert.Single(await restarted.ClaimAsync("worker", 10, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ConsumerIdentityIsolatedWhileMessageIdentityIsStable()
    {
        Assert.Equal(InboxAdmission.Admitted, await ProcessAsync(_store, "one", "shared"));
        Assert.Equal(InboxAdmission.Admitted, await ProcessAsync(_store, "two", "shared"));
        Assert.Equal(InboxAdmission.Duplicate, await ProcessAsync(_store, "one", "shared"));
        Assert.Equal(InboxAdmission.Duplicate, await ProcessAsync(_store, "two", "shared"));
        Assert.Equal(2, await RevisionAsync());
        Assert.Equal(2, await CountAsync("consumer_inbox_receipts"));
    }

    [Fact]
    public async Task RollbackDiscardsReceiptDomainStateAndOutboxSoRetryAdmits()
    {
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            Assert.Equal(InboxAdmission.Admitted,
                await _store.AdmitAsync(connection, transaction, "consumer", "retry"));
            Assert.Equal(InboxAdmission.Duplicate,
                await _store.AdmitAsync(connection, transaction, "consumer", "retry"));
            await using var update = new NpgsqlCommand(
                $"INSERT INTO \"{_schema}\".domain_state VALUES ('aggregate', 1)", connection, transaction);
            await update.ExecuteNonQueryAsync();
            using var document = JsonDocument.Parse("""{"effect":true}""");
            await _store.EnqueueAsync(connection, transaction,
                new OutboxEvent(Guid.NewGuid(), "aggregate", "rollback", "processed", 1,
                    document.RootElement, DateTimeOffset.UtcNow));
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await CountAsync("consumer_inbox_receipts"));
        Assert.Equal(0, await CountAsync("domain_state"));
        Assert.Equal(0, await CountAsync("outbox_events"));
        Assert.Equal(InboxAdmission.Admitted, await ProcessAsync(_store, "consumer", "retry", true));
        Assert.Equal(1, await RevisionAsync());
        Assert.Equal(1, await CountAsync("outbox_events"));
    }

    [Fact]
    public async Task ConcurrentDuplicateWaitsForCommitThenSkipsDomainWork()
    {
        await using var firstConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        Assert.Equal(InboxAdmission.Admitted,
            await _store.AdmitAsync(firstConnection, firstTransaction, "consumer", "concurrent"));
        var contender = ProcessAsync(_store, "consumer", "concurrent", true);
        try
        {
            await WaitForBlockedInsertAsync();
            Assert.False(contender.IsCompleted);
            await using var update = new NpgsqlCommand(
                $"INSERT INTO \"{_schema}\".domain_state VALUES ('aggregate', 1)",
                firstConnection, firstTransaction);
            await update.ExecuteNonQueryAsync();
            await firstTransaction.CommitAsync();
            Assert.Equal(InboxAdmission.Duplicate, await contender.WaitAsync(TimeSpan.FromSeconds(8)));
        }
        finally
        {
            if (!contender.IsCompleted)
            {
                await firstTransaction.RollbackAsync();
                await contender.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        Assert.Equal(1, await RevisionAsync());
        Assert.Equal(1, await CountAsync("consumer_inbox_receipts"));
        Assert.Equal(0, await CountAsync("outbox_events"));
    }

    [Fact]
    public async Task ConcurrentDuplicateAdmitsAfterFirstTransactionRollsBack()
    {
        await using var firstConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        Assert.Equal(InboxAdmission.Admitted,
            await _store.AdmitAsync(firstConnection, firstTransaction, "consumer", "concurrent"));
        var contender = ProcessAsync(_store, "consumer", "concurrent", true);
        try
        {
            await WaitForBlockedInsertAsync();
            Assert.False(contender.IsCompleted);
        }
        finally
        {
            await firstTransaction.RollbackAsync();
        }
        Assert.Equal(InboxAdmission.Admitted, await contender.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(1, await RevisionAsync());
        Assert.Equal(1, await CountAsync("consumer_inbox_receipts"));
        Assert.Equal(1, await CountAsync("outbox_events"));
    }

    private async Task WaitForBlockedInsertAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT count(*) FROM pg_stat_activity
                WHERE state = 'active' AND wait_event_type = 'Lock' AND wait_event = 'transactionid'
                    AND query LIKE '%"{_schema}".consumer_inbox_receipts%'
                """, connection);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
                return;
            await Task.Delay(30);
        }
        Assert.Fail("The competing receipt insert did not wait on the open transaction.");
    }

    [Fact]
    public async Task CancellationAndInvalidInputsDoNotCreateReceipts()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var value in new[] { "", " \t", "\n" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => _store.AdmitAsync(connection, transaction, value, "message"));
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => _store.AdmitAsync(connection, transaction, "consumer", value));
        }
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.AdmitAsync(connection, transaction, null!, "message"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.AdmitAsync(connection, transaction, "consumer", null!));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.AdmitAsync(connection, transaction, "consumer", "message", canceled.Token));
        await transaction.RollbackAsync();

        await using var other = await _fixture.DataSource.OpenConnectionAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _store.AdmitAsync(other, transaction, "consumer", "message"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.AdmitAsync(null!, transaction, "consumer", "message"));
        Assert.Equal(0, await CountAsync("consumer_inbox_receipts"));
    }

    [Fact]
    public async Task CancellationWhileWaitingForDuplicateLeavesNoCommittedReceipt()
    {
        await using var firstConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        Assert.Equal(InboxAdmission.Admitted,
            await _store.AdmitAsync(firstConnection, firstTransaction, "consumer", "canceled"));

        await using var waitingConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var waitingTransaction = await waitingConnection.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = _store.AdmitAsync(waitingConnection, waitingTransaction,
            "consumer", "canceled", cancellation.Token);
        await WaitForBlockedInsertAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await waiting.WaitAsync(TimeSpan.FromSeconds(8)));
        await waitingTransaction.RollbackAsync();
        await firstTransaction.RollbackAsync();

        Assert.Equal(0, await CountAsync("consumer_inbox_receipts"));
        Assert.Equal(InboxAdmission.Admitted, await ProcessAsync(_store, "consumer", "canceled"));
        Assert.Equal(1, await RevisionAsync());
    }

    [Fact]
    public async Task MigrationUpgradesExistingOutboxWithoutChangingItsEvents()
    {
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand($"""
                DROP TABLE "{_schema}".consumer_inbox_receipts;
                DELETE FROM "{_schema}".outbox_schema_migrations WHERE version = 2;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
        using var document = JsonDocument.Parse("""{"original":true}""");
        var original = new OutboxEvent(Guid.NewGuid(), "legacy", "legacy-key",
            "legacy", 1, document.RootElement.Clone(), DateTimeOffset.UtcNow);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await _store.EnqueueAsync(connection, transaction, original);
            await transaction.CommitAsync();
        }
        var upgraded = new PostgresOutbox(_fixture.DataSource, _schema);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => upgraded.InitializeAsync()));
        Assert.Equal(2, await CountAsync("outbox_schema_migrations"));
        Assert.Equal(1, await CountAsync("outbox_events"));
        Assert.Equal(InboxAdmission.Admitted, await ProcessAsync(upgraded, "consumer", "legacy-key"));
        Assert.Equal(original.Id,
            Assert.Single(await upgraded.ClaimAsync("worker", 1, TimeSpan.FromMinutes(1))).Event.Message.Id);
    }
}
