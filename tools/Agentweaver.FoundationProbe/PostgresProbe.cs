using System.Text.Json;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.FoundationProbe;

internal sealed class PostgresProbe(NpgsqlDataSource dataSource, PostgresOutbox outbox)
{
    public async Task<PostgresEvidence> RunAsync(
        ProbeTarget target,
        ProbeSource source,
        string nonce,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var roleCommand = new NpgsqlCommand("SELECT current_user", connection, transaction))
        {
            var currentRole = (string?)await roleCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentRole, target.Runtime.DatabaseRole, StringComparison.Ordinal))
                throw new ProbeException("postgres_role_mismatch");
        }

        var effectId = Guid.NewGuid();
        await using (var effectCommand = new NpgsqlCommand("""
            INSERT INTO foundation_probe.probe_effects
                (effect_id, nonce, source_sha, source_tree, runtime_role)
            VALUES (@id, @nonce, @source_sha, @source_tree, @role)
            """, connection, transaction))
        {
            effectCommand.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, effectId);
            effectCommand.Parameters.AddWithValue("nonce", NpgsqlDbType.Char, nonce);
            effectCommand.Parameters.AddWithValue("source_sha", NpgsqlDbType.Char, source.Sha);
            effectCommand.Parameters.AddWithValue("source_tree", NpgsqlDbType.Char, source.Tree);
            effectCommand.Parameters.AddWithValue("role", NpgsqlDbType.Name, target.Runtime.DatabaseRole);
            await effectCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var inbox = await outbox.AdmitAsync(
            connection, transaction, "foundation-probe", nonce, cancellationToken).ConfigureAwait(false);
        if (inbox != InboxAdmission.Admitted)
            throw new ProbeException("postgres_inbox_duplicate");

        var outboxId = Guid.NewGuid();
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            nonce,
            sourceSha = source.Sha,
            sourceTree = source.Tree,
        }));
        var stored = await outbox.EnqueueAsync(
            connection,
            transaction,
            new OutboxEvent(
                outboxId,
                $"foundation-probe:{nonce}",
                $"foundation-probe:{nonce}",
                "foundation.probe.completed",
                1,
                payloadDocument.RootElement,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (!await VerifyPostgresEffectsAsync(connection, nonce, effectId, outboxId, cancellationToken)
                .ConfigureAwait(false))
            throw new ProbeException("postgres_effect_verification_failed");

        return new PostgresEvidence(
            target.FoundationResources.PostgresServerId,
            target.FoundationResources.PostgresHost,
            target.Runtime.DatabaseName,
            target.Runtime.SchemaName,
            target.Runtime.DatabaseRole,
            effectId.ToString("D"),
            nonce,
            inbox.ToString(),
            stored.Message.Id.ToString("D"),
            stored.Sequence,
            TransactionCommitted: true);
    }

    private static async Task<bool> VerifyPostgresEffectsAsync(
        NpgsqlConnection connection,
        string nonce,
        Guid effectId,
        Guid outboxId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM foundation_probe.probe_effects
                    WHERE effect_id = @effect_id AND nonce = @nonce) = 1
                AND (SELECT count(*) FROM foundation_probe.consumer_inbox_receipts
                    WHERE consumer_id = 'foundation-probe' AND message_id = @nonce) = 1
                AND (SELECT count(*) FROM foundation_probe.outbox_events
                    WHERE id = @outbox_id AND idempotency_key = @idempotency_key) = 1
            """, connection);
        command.Parameters.AddWithValue("effect_id", NpgsqlDbType.Uuid, effectId);
        command.Parameters.AddWithValue("nonce", NpgsqlDbType.Char, nonce);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Uuid, outboxId);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, $"foundation-probe:{nonce}");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ProbeException("postgres_effect_verification_failed"));
    }
}
