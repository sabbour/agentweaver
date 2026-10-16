using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

internal sealed class AddressedMessageException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class MessagingProviderUnavailableException(string message) : Exception(message);

internal sealed class MessagingProviderBindingConflictException(string message) : Exception(message);

internal sealed class PostgresAddressedMessageStore
{
    private const string AckInboxConsumer = "events-and-sessions.addressed-message-ack";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _sessions;
    private readonly string _threads;
    private readonly string _messages;
    private readonly string _providerBindings;
    private readonly PostgresOutbox _outbox;
    private readonly PostgresSessionsJournal _journal;
    private readonly PostgresSessionsProviderOptions _resourceOptions;
    private readonly NativePostgresMessagingProviderOptions _messagingOptions;
    private readonly NativePostgresMessagingProvider _provider;
    private readonly ProviderResolver _resolver;
    private readonly string _identityIssuer;
    private readonly TimeProvider _timeProvider;

    public PostgresAddressedMessageStore(
        NpgsqlDataSource dataSource,
        PostgresSessionsProviderOptions resourceOptions,
        NativePostgresMessagingProviderOptions messagingOptions,
        NativePostgresMessagingProvider provider,
        ProviderResolver resolver,
        PostgresSessionsJournal journal,
        string identityIssuer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(resourceOptions);
        ArgumentNullException.ThrowIfNull(messagingOptions);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityIssuer);
        resourceOptions.Validate();
        messagingOptions.Validate();
        _dataSource = dataSource;
        var schema = $"\"{resourceOptions.Schema}\"";
        _sessions = $"{schema}.sessions";
        _threads = $"{schema}.addressed_message_threads";
        _messages = $"{schema}.addressed_messages";
        _providerBindings = $"{schema}.messaging_provider_bindings";
        _outbox = new PostgresOutbox(dataSource, resourceOptions.Schema);
        _journal = journal;
        _resourceOptions = resourceOptions;
        _messagingOptions = messagingOptions;
        _provider = provider;
        _resolver = resolver;
        _identityIssuer = identityIssuer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AddressedMessageSendResult> SendAsync(
        ClaimsPrincipal principal,
        AddressedMessageDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = AddressedMessageValidation.ValidateAndNormalize(draft);
        var scope = RequireSenderScope(principal, normalized.Sender);
        var identity = CreateIdentityMetadata(principal);
        var canonicalInput = CreateCanonicalInput(normalized);

        await using (var lookup = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var existing = await FindByIdempotencyAsync(
                lookup, null, normalized.Sender, identity, normalized.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
                return await ExistingOrConflictAsync(
                    lookup, null, normalized, identity, canonicalInput, existing, cancellationToken)
                    .ConfigureAwait(false);
        }

        var now = NormalizeTimestamp(_timeProvider.GetUtcNow());
        var expiresAt = NormalizeTimestamp(normalized.ExpiresAt ?? now.AddDays(1));
        if (expiresAt <= now ||
            expiresAt > now.AddDays(_messagingOptions.MaximumMessageLifetimeDays))
            throw new AddressedMessageException("invalid_expiry");

        var senderBinding = await ResolveBindingAsync(normalized.Sender.RunId, cancellationToken)
            .ConfigureAwait(false);
        var recipientBinding = normalized.Recipient.RunId == normalized.Sender.RunId
            ? senderBinding
            : await ResolveBindingAsync(normalized.Recipient.RunId, cancellationToken)
                .ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var raced = await FindByIdempotencyAsync(
            connection, transaction, normalized.Sender, identity,
            normalized.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (raced is not null)
        {
            var result = await ExistingOrConflictAsync(
                connection, transaction, normalized, identity, canonicalInput, raced, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        await PersistProviderBindingAsync(
            connection, transaction, scope.ProjectId, normalized.Sender.RunId,
            senderBinding, cancellationToken).ConfigureAwait(false);
        if (normalized.Recipient.RunId != normalized.Sender.RunId)
            await PersistProviderBindingAsync(
                connection, transaction, scope.ProjectId, normalized.Recipient.RunId,
                recipientBinding, cancellationToken).ConfigureAwait(false);
        await RequireSessionAsync(connection, transaction, normalized.Sender, cancellationToken).ConfigureAwait(false);
        await RequireSessionAsync(connection, transaction, normalized.Recipient, cancellationToken).ConfigureAwait(false);

        var (threadId, sequence, createdThread) = await AllocateThreadSequenceAsync(
            connection, transaction, normalized, cancellationToken).ConfigureAwait(false);
        var message = new AddressedMessageEnvelope(
            Guid.NewGuid(),
            normalized.Sender,
            normalized.Recipient,
            threadId,
            normalized.ReplyToId,
            normalized.IdempotencyKey,
            sequence,
            normalized.SenderFence,
            normalized.RecipientFence,
            ClaimFence: 0,
            normalized.DeliveryMode,
            normalized.Purpose,
            normalized.Kind,
            normalized.RequestId,
            normalized.ReplyCorrelationId,
            normalized.UserQuote,
            normalized.CoordinatorInstructions,
            normalized.Payload.Clone(),
            AddressedMessageStatus.Accepted,
            now,
            expiresAt,
            PresentedAt: null,
            AcknowledgedAt: null,
            FailureReason: null,
            identity,
            ToProviderMetadata(senderBinding));

        var inserted = await InsertMessageAsync(
            connection, transaction, message, canonicalInput, cancellationToken).ConfigureAwait(false);
        if (!inserted)
        {
            await RevertThreadSequenceAsync(
                connection, transaction, normalized.Sender.ProjectId, threadId, sequence,
                createdThread, cancellationToken).ConfigureAwait(false);
            var existing = await FindByIdempotencyAsync(
                connection, transaction, normalized.Sender, identity,
                normalized.IdempotencyKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "An addressed-message uniqueness conflict had no stored message.");
            var result = await ExistingOrConflictAsync(
                connection, transaction, normalized, identity, canonicalInput, existing, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        await _journal.AppendAddressedMessageAsync(
            connection, transaction, message, cancellationToken).ConfigureAwait(false);
        var eventPayload = JsonSerializer.SerializeToElement(message, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            message.MessageId,
            $"addressed-messages/{message.Recipient.ProjectId}/{message.Recipient.RunId}",
            OutboxIdempotencyKey(identity, normalized.Sender, normalized.IdempotencyKey),
            "sessions.addressed_message",
            SessionsContractVersions.CurrentEventVersion,
            eventPayload,
            now), cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AddressedMessageSendResult(message, IsDuplicate: false);
    }

    public async Task<AddressedMessageClaim?> ClaimNextAtTurnBoundaryAsync(
        SessionIdentity recipient,
        long currentFence,
        string owner,
        CancellationToken cancellationToken = default)
    {
        if (currentFence < 1)
            throw new ArgumentOutOfRangeException(nameof(currentFence));
        if (!IsToken(owner, 128))
            throw new AddressedMessageException("invalid_claim_owner");

        await ExpireDueAsync(1000, cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var fenceStale = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = @undeliverable,
                failure_reason = @stale,
                claim_owner = NULL,
                claimed_until = NULL
            WHERE project_id = @project AND recipient_run_id = @run AND recipient_session_id = @session
                AND recipient_fence <> @fence
                AND expires_at > clock_timestamp()
                AND status IN (@accepted, @claimed, @delivered)
            """, connection, transaction))
        {
            AddIdentity(fenceStale, recipient);
            fenceStale.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, currentFence);
            fenceStale.Parameters.AddWithValue("undeliverable", NpgsqlDbType.Varchar,
                AddressedMessageStatus.Undeliverable.ToString());
            fenceStale.Parameters.AddWithValue("stale", NpgsqlDbType.Varchar,
                AddressedMessageFailureReason.StaleFence.ToString());
            AddPendingStatusParameters(fenceStale);
            await fenceStale.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Guid? messageId;
        DateTimeOffset? leaseExpiresAt;
        long claimFence;
        await using (var existingClaim = new NpgsqlCommand($"""
            SELECT message_id, claimed_until, claim_fence
            FROM {_messages}
            WHERE project_id = @project AND recipient_run_id = @run
                AND recipient_session_id = @session AND recipient_fence = @fence
                AND claim_owner = @owner AND claimed_until > clock_timestamp()
                AND expires_at > clock_timestamp() AND purpose <> @progress
                AND status IN (@claimed, @delivered)
            ORDER BY created_at, thread_id, thread_sequence
            LIMIT 1 FOR UPDATE
            """, connection, transaction))
        {
            AddIdentity(existingClaim, recipient);
            existingClaim.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, currentFence);
            existingClaim.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, owner);
            existingClaim.Parameters.AddWithValue("claimed", NpgsqlDbType.Varchar,
                AddressedMessageStatus.Claimed.ToString());
            existingClaim.Parameters.AddWithValue("delivered", NpgsqlDbType.Varchar,
                AddressedMessageStatus.Delivered.ToString());
            existingClaim.Parameters.AddWithValue("progress", NpgsqlDbType.Varchar,
                AddressedMessagePurpose.Progress.ToString());
            await using var reader = await existingClaim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messageId = reader.GetGuid(0);
                leaseExpiresAt = reader.GetFieldValue<DateTimeOffset>(1);
                claimFence = reader.GetInt64(2);
            }
            else
            {
                messageId = null;
                leaseExpiresAt = null;
                claimFence = 0;
            }
        }

        if (messageId is null)
        {
            await using var claim = new NpgsqlCommand($"""
                WITH candidate AS (
                    SELECT m.project_id, m.message_id
                    FROM {_messages} AS m
                    WHERE m.project_id = @project
                        AND m.recipient_run_id = @run
                        AND m.recipient_session_id = @session
                        AND m.recipient_fence = @fence
                        AND m.expires_at > clock_timestamp()
                        AND m.purpose <> @progress
                        AND m.status IN (@accepted, @claimed, @delivered)
                        AND (m.claimed_until IS NULL OR m.claimed_until <= clock_timestamp())
                        AND NOT EXISTS (
                            SELECT 1 FROM {_messages} AS earlier
                            WHERE earlier.project_id = m.project_id
                                AND earlier.thread_id = m.thread_id
                                AND earlier.thread_sequence < m.thread_sequence
                                AND earlier.purpose <> @progress
                                AND earlier.status IN (@accepted, @claimed, @delivered)
                                AND earlier.expires_at > clock_timestamp()
                        )
                    ORDER BY CASE m.delivery_mode WHEN @immediate THEN 0 ELSE 1 END,
                        m.created_at, m.thread_id, m.thread_sequence
                    LIMIT 1
                    FOR UPDATE OF m SKIP LOCKED
                )
                UPDATE {_messages} AS m
                SET status = @claimed,
                    claim_owner = @owner,
                    claimed_until = clock_timestamp() + @lease,
                    claim_fence = claim_fence + 1
                FROM candidate
                WHERE m.project_id = candidate.project_id AND m.message_id = candidate.message_id
                RETURNING m.message_id, m.claimed_until, m.claim_fence
                """, connection, transaction);
            AddIdentity(claim, recipient);
            claim.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, currentFence);
            claim.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, owner);
            claim.Parameters.AddWithValue("lease", NpgsqlDbType.Interval,
                TimeSpan.FromSeconds(_messagingOptions.ClaimLeaseSeconds));
            claim.Parameters.AddWithValue("accepted", NpgsqlDbType.Varchar,
                AddressedMessageStatus.Accepted.ToString());
            claim.Parameters.AddWithValue("claimed", NpgsqlDbType.Varchar,
                AddressedMessageStatus.Claimed.ToString());
            claim.Parameters.AddWithValue("delivered", NpgsqlDbType.Varchar,
                AddressedMessageStatus.Delivered.ToString());
            claim.Parameters.AddWithValue("immediate", NpgsqlDbType.Varchar,
                AddressedMessageDeliveryMode.Immediate.ToString());
            claim.Parameters.AddWithValue("progress", NpgsqlDbType.Varchar,
                AddressedMessagePurpose.Progress.ToString());
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            messageId = reader.GetGuid(0);
            leaseExpiresAt = reader.GetFieldValue<DateTimeOffset>(1);
            claimFence = reader.GetInt64(2);
        }

        var message = await ReadMessageAsync(
            connection, transaction, recipient.ProjectId, messageId!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A claimed addressed message disappeared.");
        if (message.ClaimFence != claimFence)
            throw new InvalidOperationException("The addressed-message claim fence changed unexpectedly.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AddressedMessageClaim(message, owner, leaseExpiresAt!.Value);
    }

    public async Task<AddressedMessageEnvelope> PresentAsync(
        SessionIdentity recipient,
        long currentFence,
        Guid messageId,
        string owner,
        long claimFence,
        CancellationToken cancellationToken = default)
    {
        if (currentFence < 1 || messageId == Guid.Empty || claimFence < 1 || !IsToken(owner, 128))
            throw new AddressedMessageException("invalid_claim");
        var message = await GetAsync(recipient.ProjectId, messageId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Recipient != recipient ||
            message.RecipientFence != currentFence || message.ClaimFence != claimFence)
            throw new AddressedMessageException("claim_lost");
        return await PresentAsync(
            new AddressedMessageClaim(message, owner, message.ExpiresAt), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AddressedMessageEnvelope> PresentAsync(
        AddressedMessageClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var message = claim.Message;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = @delivered, presented_at = COALESCE(presented_at, clock_timestamp())
            WHERE project_id = @project AND message_id = @message
                AND recipient_run_id = @run AND recipient_session_id = @session
                AND status IN (@claimed, @delivered)
                AND claim_owner = @owner AND claim_fence = @claim_fence
                AND claimed_until > clock_timestamp() AND expires_at > clock_timestamp()
            """, connection, transaction);
        update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, message.Recipient.ProjectId);
        update.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, message.Recipient.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, message.Recipient.SessionId);
        update.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, message.MessageId);
        update.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, claim.Owner);
        update.Parameters.AddWithValue("claim_fence", NpgsqlDbType.Bigint, message.ClaimFence);
        update.Parameters.AddWithValue("claimed", NpgsqlDbType.Varchar, AddressedMessageStatus.Claimed.ToString());
        update.Parameters.AddWithValue("delivered", NpgsqlDbType.Varchar, AddressedMessageStatus.Delivered.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new AddressedMessageException("claim_lost");
        var presented = await ReadMessageAsync(
            connection, transaction, message.Recipient.ProjectId, message.MessageId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A presented addressed message disappeared.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return presented;
    }

    public async Task<AddressedMessageEnvelope> AcknowledgeAsync(
        SessionIdentity recipient,
        Guid messageId,
        string owner,
        long claimFence,
        CancellationToken cancellationToken = default,
        long? currentFence = null)
    {
        if (messageId == Guid.Empty || !IsToken(owner, 128) || claimFence < 1 ||
            currentFence is < 1)
            throw new AddressedMessageException("invalid_acknowledgment");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var admission = await _outbox.AdmitAsync(
            connection,
            transaction,
            AckInboxConsumer,
            $"{recipient.ProjectId}:{recipient.RunId}:{recipient.SessionId}:{messageId:N}",
            cancellationToken).ConfigureAwait(false);
        var current = await ReadMessageAsync(
            connection, transaction, recipient.ProjectId, messageId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Recipient != recipient)
            throw new AddressedMessageException("message_unavailable");
        if (admission == InboxAdmission.Duplicate)
        {
            if (current.Status != AddressedMessageStatus.Acknowledged)
                throw new InvalidOperationException("An acknowledgment inbox receipt has no acknowledged message.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        await using var update = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = @acknowledged,
                acknowledged_at = clock_timestamp(),
                claim_owner = NULL,
                claimed_until = NULL
            WHERE project_id = @project AND message_id = @message
                AND recipient_run_id = @run AND recipient_session_id = @session
                AND status = @delivered AND claim_owner = @owner AND claim_fence = @claim_fence
                AND (@current_fence IS NULL OR recipient_fence = @current_fence)
                AND claimed_until > clock_timestamp() AND expires_at > clock_timestamp()
            """, connection, transaction);
        update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, recipient.ProjectId);
        update.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, recipient.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
        update.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
        update.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, owner);
        update.Parameters.AddWithValue("claim_fence", NpgsqlDbType.Bigint, claimFence);
        update.Parameters.AddWithValue(
            "current_fence", NpgsqlDbType.Bigint, (object?)currentFence ?? DBNull.Value);
        update.Parameters.AddWithValue("delivered", NpgsqlDbType.Varchar, AddressedMessageStatus.Delivered.ToString());
        update.Parameters.AddWithValue("acknowledged", NpgsqlDbType.Varchar,
            AddressedMessageStatus.Acknowledged.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new AddressedMessageException("message_not_delivered");
        var acknowledged = await ReadMessageAsync(
            connection, transaction, recipient.ProjectId, messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An acknowledged addressed message disappeared.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return acknowledged;
    }

    public async Task<AddressedMessageEnvelope> MarkUndeliverableAsync(
        SessionIdentity recipient,
        Guid messageId,
        AddressedMessageFailureReason reason,
        CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty || !Enum.IsDefined(reason))
            throw new AddressedMessageException("invalid_failure_reason");
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = @undeliverable,
                failure_reason = @reason,
                claim_owner = NULL,
                claimed_until = NULL
            WHERE project_id = @project AND message_id = @message
                AND recipient_run_id = @run AND recipient_session_id = @session
                AND status IN (@accepted, @claimed, @delivered)
            """, connection, transaction);
        update.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, recipient.ProjectId);
        update.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, recipient.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
        update.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
        update.Parameters.AddWithValue("undeliverable", NpgsqlDbType.Varchar,
            AddressedMessageStatus.Undeliverable.ToString());
        update.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, reason.ToString());
        AddPendingStatusParameters(update);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var message = await ReadMessageAsync(
            connection, transaction, recipient.ProjectId, messageId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Recipient != recipient ||
            (changed == 0 && (message.Status != AddressedMessageStatus.Undeliverable ||
                message.FailureReason != reason)))
            throw new AddressedMessageException("message_unavailable");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return message;
    }

    public async Task<AddressedMessageEnvelope?> GetAsync(
        string projectId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (messageId == Guid.Empty)
            throw new ArgumentException("A message identity is required.", nameof(messageId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadMessageAsync(connection, null, projectId, messageId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> ExpireDueAsync(
        int batchSize = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 1000);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH due AS (
                SELECT project_id, message_id
                FROM {_messages}
                WHERE expires_at <= clock_timestamp()
                    AND status IN (@accepted, @claimed, @delivered)
                ORDER BY expires_at, message_id
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {_messages} AS m
            SET status = @expired,
                claim_owner = NULL,
                claimed_until = NULL
            FROM due
            WHERE m.project_id = due.project_id AND m.message_id = due.message_id
            """, connection);
        AddPendingStatusParameters(command);
        command.Parameters.AddWithValue("batch", NpgsqlDbType.Integer, batchSize);
        command.Parameters.AddWithValue("expired", NpgsqlDbType.Varchar, AddressedMessageStatus.Expired.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AddressedMessageSendResult> ExistingOrConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AddressedMessageDraft request,
        AddressedMessageIdentityMetadata identity,
        JsonElement canonicalInput,
        AddressedMessageEnvelope existing,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT canonical_input = @input
                AND (@expires IS NULL OR expires_at = @expires)
            FROM {_messages}
            WHERE project_id = @project AND message_id = @message
                AND sender_issuer = @issuer AND sender_identity_hash = @subject_hash
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, request.Sender.ProjectId);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, existing.MessageId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, identity.Issuer);
        command.Parameters.AddWithValue("subject_hash", NpgsqlDbType.Varchar, identity.SubjectHash);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, canonicalInput.GetRawText());
        command.Parameters.AddWithValue("expires", NpgsqlDbType.TimestampTz,
            request.ExpiresAt is { } expires ? NormalizeTimestamp(expires) : DBNull.Value);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new AddressedMessageException("idempotency_conflict");
        return new AddressedMessageSendResult(existing, IsDuplicate: true);
    }

    private async Task<AddressedMessageEnvelope?> FindByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity sender,
        AddressedMessageIdentityMetadata identity,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            {MessageSelect}
            WHERE m.project_id = @project AND m.sender_issuer = @issuer
                AND m.sender_run_id = @run AND m.sender_session_id = @session
                AND m.sender_identity_hash = @subject_hash AND m.idempotency_key = @key
            """, connection, transaction);
        AddIdentity(command, sender);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, identity.Issuer);
        command.Parameters.AddWithValue("subject_hash", NpgsqlDbType.Char, identity.SubjectHash);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    private async Task<AddressedMessageEnvelope?> ReadMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            {MessageSelect}
            WHERE m.project_id = @project AND m.message_id = @message
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    private async Task<(Guid ThreadId, long Sequence, bool CreatedThread)> AllocateThreadSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AddressedMessageDraft request,
        CancellationToken cancellationToken)
    {
        var threadId = request.ThreadId;
        if (request.ReplyToId is { } replyToId)
        {
            var replyTo = await ReadMessageAsync(
                connection, transaction, request.Sender.ProjectId, replyToId, cancellationToken).ConfigureAwait(false);
            if (replyTo is null ||
                replyTo.Status != AddressedMessageStatus.Acknowledged ||
                replyTo.Sender != request.Recipient ||
                replyTo.Recipient != request.Sender ||
                (replyTo.RequestId is not null &&
                    !string.Equals(replyTo.RequestId, request.ReplyCorrelationId, StringComparison.Ordinal)) ||
                (replyTo.RequestId is null && request.ReplyCorrelationId is not null) ||
                (threadId is not null && threadId != replyTo.ThreadId))
                throw new AddressedMessageException("reply_unavailable");
            threadId = replyTo.ThreadId;
        }

        if (threadId is null)
            threadId = Guid.NewGuid();
        else if (request.ReplyToId is null)
        {
            await using var participant = new NpgsqlCommand($"""
                SELECT EXISTS (
                        SELECT 1 FROM {_threads}
                        WHERE project_id = @project AND thread_id = @thread
                    ),
                    EXISTS (
                        SELECT 1 FROM {_messages}
                        WHERE project_id = @project AND thread_id = @thread
                            AND (
                                (sender_run_id = @sender_run AND sender_session_id = @sender_session
                                    AND recipient_run_id = @recipient_run AND recipient_session_id = @recipient_session)
                                OR
                                (sender_run_id = @recipient_run AND sender_session_id = @recipient_session
                                    AND recipient_run_id = @sender_run AND recipient_session_id = @sender_session)
                            )
                    )
                """, connection, transaction);
            AddThreadParticipants(participant, request, threadId.Value);
            bool threadExists;
            bool hasParticipant;
            await using (var reader = await participant.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("An addressed-message thread check returned no row.");
                threadExists = reader.GetBoolean(0);
                hasParticipant = reader.GetBoolean(1);
            }
            if (threadExists && !hasParticipant)
                throw new AddressedMessageException("thread_unavailable");
        }

        var createdThread = false;
        await using (var insertThread = new NpgsqlCommand($"""
            INSERT INTO {_threads} (project_id, thread_id)
            VALUES (@project, @thread)
            ON CONFLICT (project_id, thread_id) DO NOTHING
            RETURNING last_sequence
            """, connection, transaction))
        {
            insertThread.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, request.Sender.ProjectId);
            insertThread.Parameters.AddWithValue("thread", NpgsqlDbType.Uuid, threadId.Value);
            createdThread = await insertThread.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long;
        }

        await using var advance = new NpgsqlCommand($"""
            UPDATE {_threads}
            SET last_sequence = last_sequence + 1
            WHERE project_id = @project AND thread_id = @thread
            RETURNING last_sequence
            """, connection, transaction);
        advance.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, request.Sender.ProjectId);
        advance.Parameters.AddWithValue("thread", NpgsqlDbType.Uuid, threadId.Value);
        var value = await advance.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long sequence
            ? (threadId.Value, sequence, createdThread)
            : throw new InvalidOperationException("An addressed-message thread could not be sequenced.");
    }

    private async Task RevertThreadSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        Guid threadId,
        long sequence,
        bool createdThread,
        CancellationToken cancellationToken)
    {
        var sql = createdThread
            ? $"DELETE FROM {_threads} WHERE project_id = @project AND thread_id = @thread AND last_sequence = @sequence"
            : $"UPDATE {_threads} SET last_sequence = last_sequence - 1 WHERE project_id = @project AND thread_id = @thread AND last_sequence = @sequence";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("thread", NpgsqlDbType.Uuid, threadId);
        command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, sequence);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("An uncommitted addressed-message sequence could not be restored.");
    }

    private async Task<bool> InsertMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AddressedMessageEnvelope message,
        JsonElement canonicalInput,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_messages}
                (project_id, message_id, sender_run_id, sender_session_id, recipient_run_id,
                 recipient_session_id, sender_issuer, sender_identity_hash, thread_id, reply_to_id,
                 idempotency_key, thread_sequence, sender_fence, recipient_fence, claim_fence,
                 delivery_mode, purpose, kind, request_id, reply_correlation_id, user_quote,
                 coordinator_instructions, payload, canonical_input, status, created_at, expires_at)
            VALUES
                (@project, @message, @sender_run, @sender_session, @recipient_run,
                 @recipient_session, @issuer, @subject_hash, @thread, @reply_to,
                 @key, @sequence, @sender_fence, @recipient_fence, @claim_fence,
                 @mode, @purpose, @kind, @request, @correlation, @quote,
                 @instructions, @payload, @canonical, @status, @created, @expires)
            ON CONFLICT (project_id, sender_run_id, sender_session_id,
                sender_issuer, sender_identity_hash, idempotency_key) DO NOTHING
            RETURNING message_id
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, message.Sender.ProjectId);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, message.MessageId);
        command.Parameters.AddWithValue("sender_run", NpgsqlDbType.Varchar, message.Sender.RunId);
        command.Parameters.AddWithValue("sender_session", NpgsqlDbType.Varchar, message.Sender.SessionId);
        command.Parameters.AddWithValue("recipient_run", NpgsqlDbType.Varchar, message.Recipient.RunId);
        command.Parameters.AddWithValue("recipient_session", NpgsqlDbType.Varchar, message.Recipient.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, message.Identity.Issuer);
        command.Parameters.AddWithValue("subject_hash", NpgsqlDbType.Varchar, message.Identity.SubjectHash);
        command.Parameters.AddWithValue("thread", NpgsqlDbType.Uuid, message.ThreadId);
        command.Parameters.AddWithValue("reply_to", NpgsqlDbType.Uuid, (object?)message.ReplyToId ?? DBNull.Value);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, message.IdempotencyKey);
        command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, message.ThreadSequence);
        command.Parameters.AddWithValue("sender_fence", NpgsqlDbType.Bigint, message.SenderFence);
        command.Parameters.AddWithValue("recipient_fence", NpgsqlDbType.Bigint, message.RecipientFence);
        command.Parameters.AddWithValue("claim_fence", NpgsqlDbType.Bigint, message.ClaimFence);
        command.Parameters.AddWithValue("mode", NpgsqlDbType.Varchar, message.DeliveryMode.ToString());
        command.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, message.Purpose.ToString());
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, message.Kind.ToString());
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)message.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", NpgsqlDbType.Varchar,
            (object?)message.ReplyCorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("quote", NpgsqlDbType.Text, (object?)message.UserQuote ?? DBNull.Value);
        command.Parameters.AddWithValue("instructions", NpgsqlDbType.Text,
            (object?)message.CoordinatorInstructions ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, message.Payload.GetRawText());
        command.Parameters.AddWithValue("canonical", NpgsqlDbType.Jsonb, canonicalInput.GetRawText());
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, message.Status.ToString());
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, message.CreatedAt);
        command.Parameters.AddWithValue("expires", NpgsqlDbType.TimestampTz, message.ExpiresAt);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid;
    }

    private async Task PersistProviderBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        PinnedProviderBinding binding,
        CancellationToken cancellationToken)
    {
        var capabilities = JsonSerializer.Serialize(
            binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal), JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_providerBindings}
                (project_id, run_id, provider_id, adapter_version, options_schema_version,
                 options_revision, resource_id, resource_generation, negotiated_capabilities)
            VALUES (@project, @run, @provider, @version, @schema_version,
                    @revision, @resource, @generation, @capabilities)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection, transaction))
        {
            AddProjectRun(insert, projectId, runId);
            insert.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, binding.ProviderId);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, binding.AdapterVersion.ToString());
            insert.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, binding.OptionsSchemaVersion);
            insert.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, binding.OptionsRevision);
            insert.Parameters.AddWithValue("resource", NpgsqlDbType.Varchar, binding.Resource.ResourceId);
            insert.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, binding.Resource.Generation);
            insert.Parameters.AddWithValue("capabilities", NpgsqlDbType.Jsonb, capabilities);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var query = new NpgsqlCommand($"""
            SELECT provider_id, adapter_version, options_schema_version, options_revision,
                resource_id, resource_generation, negotiated_capabilities
            FROM {_providerBindings}
            WHERE project_id = @project AND run_id = @run
            FOR UPDATE
            """, connection, transaction);
        AddProjectRun(query, projectId, runId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A Messaging provider binding was not persisted.");
        var storedCapabilities = JsonSerializer.Deserialize<ImmutableHashSet<string>>(
            reader.GetString(6), JsonOptions)
            ?? throw new InvalidOperationException("A stored Messaging capability set is invalid.");
        if (reader.GetString(0) != binding.ProviderId ||
            Version.Parse(reader.GetString(1)) != binding.AdapterVersion ||
            reader.GetInt32(2) != binding.OptionsSchemaVersion ||
            reader.GetString(3) != binding.OptionsRevision ||
            reader.GetString(4) != binding.Resource.ResourceId ||
            reader.GetInt64(5) != binding.Resource.Generation ||
            !storedCapabilities.SetEquals(binding.NegotiatedCapabilities))
            throw new MessagingProviderBindingConflictException(
                "The run already has a different immutable Messaging provider binding.");
    }

    private async Task<PinnedProviderBinding> ResolveBindingAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var result = await _provider.ResolveNegotiateAndPinAsync(
            _resolver,
            new ProviderResolutionRequest(
                ProviderSeam.Messaging,
                ProjectOverrideId: null,
                NativePostgresMessagingProvider.AdapterVersion,
                NativePostgresMessagingProvider.OptionsSchemaVersion,
                AddressedMessageCapabilities.All),
            _messagingOptions,
            _resourceOptions,
            _dataSource,
            runId,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
            throw new MessagingProviderUnavailableException(
                result.Error?.Message ?? "The configured Messaging provider could not be selected.");
        if (result.Value.RunId != runId)
            throw new SessionAccessDeniedException("The Messaging provider binding belongs to another run.");
        return result.Value;
    }

    private async Task RequireSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_sessions}
                WHERE project_id = @project AND run_id = @run AND session_id = @session
            )
            """, connection, transaction);
        AddIdentity(command, identity);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new SessionNotFoundException("The addressed-message session does not exist.");
    }

    private static void AddThreadParticipants(
        NpgsqlCommand command,
        AddressedMessageDraft request,
        Guid threadId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, request.Sender.ProjectId);
        command.Parameters.AddWithValue("thread", NpgsqlDbType.Uuid, threadId);
        command.Parameters.AddWithValue("sender_run", NpgsqlDbType.Varchar, request.Sender.RunId);
        command.Parameters.AddWithValue("sender_session", NpgsqlDbType.Varchar, request.Sender.SessionId);
        command.Parameters.AddWithValue("recipient_run", NpgsqlDbType.Varchar, request.Recipient.RunId);
        command.Parameters.AddWithValue("recipient_session", NpgsqlDbType.Varchar, request.Recipient.SessionId);
    }

    private static void AddPendingStatusParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("accepted", NpgsqlDbType.Varchar, AddressedMessageStatus.Accepted.ToString());
        command.Parameters.AddWithValue("claimed", NpgsqlDbType.Varchar, AddressedMessageStatus.Claimed.ToString());
        command.Parameters.AddWithValue("delivered", NpgsqlDbType.Varchar, AddressedMessageStatus.Delivered.ToString());
    }

    private static void AddIdentity(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
    }

    private static void AddProjectRun(NpgsqlCommand command, string projectId, string runId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
    }

    private AddressedMessageIdentityMetadata CreateIdentityMetadata(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(SessionIdentityClaims.Subject)?.Value;
        if (!Guid.TryParseExact(subject, "D", out _))
            throw new SessionAuthenticationException();
        var subjectHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{_identityIssuer}\0{subject}")));
        return new AddressedMessageIdentityMetadata(_identityIssuer, subjectHash, ContractVersion: 1);
    }

    private static SessionRunScope RequireSenderScope(
        ClaimsPrincipal principal,
        SessionIdentity sender)
    {
        if (!SessionIdentityClaims.TryGetScope(principal, out var scope) || scope is null)
            throw new SessionAuthenticationException();
        var current = scope.Value;
        if (current.ProjectId != sender.ProjectId || current.RunId != sender.RunId)
            throw new SessionAccessDeniedException("The sender session is outside the authenticated project run.");
        return current;
    }

    private static AddressedMessageIdentityMetadata CreateIdentityMetadata(
        string issuer,
        string subjectHash) =>
        new(issuer, subjectHash, ContractVersion: 1);

    private static JsonElement CreateCanonicalInput(AddressedMessageDraft draft) =>
        JsonSerializer.SerializeToElement(draft with { ExpiresAt = null }, JsonOptions);

    private static string OutboxIdempotencyKey(
        AddressedMessageIdentityMetadata identity,
        SessionIdentity sender,
        string idempotencyKey) =>
        "addressed-message:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{sender.ProjectId}\0{sender.RunId}\0{sender.SessionId}\0" +
            $"{identity.Issuer}\0{identity.SubjectHash}\0{idempotencyKey}")));

    private static AddressedMessageProviderMetadata ToProviderMetadata(PinnedProviderBinding binding)
    {
        var capabilities = binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var resourceHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(binding.Resource.ResourceId)))[..24];
        return new AddressedMessageProviderMetadata(
            binding.ProviderId,
            binding.AdapterVersion.ToString(),
            binding.OptionsSchemaVersion,
            binding.OptionsRevision,
            resourceHash,
            binding.Resource.Generation,
            capabilities);
    }

    private static AddressedMessageEnvelope ReadMessage(NpgsqlDataReader reader)
    {
        var sender = new SessionIdentity(reader.GetString(1), reader.GetString(2), reader.GetString(3));
        var recipient = new SessionIdentity(reader.GetString(1), reader.GetString(4), reader.GetString(5));
        using var payload = JsonDocument.Parse(reader.GetString(20));
        var capabilities = JsonSerializer.Deserialize<ImmutableArray<string>>(reader.GetString(35), JsonOptions);
        if (capabilities.IsDefault)
            throw new InvalidOperationException("A stored Messaging capability set is invalid.");
        return new AddressedMessageEnvelope(
            reader.GetGuid(0),
            sender,
            recipient,
            reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            Enum.Parse<AddressedMessageDeliveryMode>(reader.GetString(13), ignoreCase: true),
            Enum.Parse<AddressedMessagePurpose>(reader.GetString(14), ignoreCase: true),
            Enum.Parse<AddressedMessageKind>(reader.GetString(15), ignoreCase: true),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            payload.RootElement.Clone(),
            Enum.Parse<AddressedMessageStatus>(reader.GetString(21), ignoreCase: true),
            reader.GetFieldValue<DateTimeOffset>(22),
            reader.GetFieldValue<DateTimeOffset>(23),
            reader.IsDBNull(24) ? null : reader.GetFieldValue<DateTimeOffset>(24),
            reader.IsDBNull(25) ? null : reader.GetFieldValue<DateTimeOffset>(25),
            reader.IsDBNull(26)
                ? null
                : Enum.Parse<AddressedMessageFailureReason>(reader.GetString(26), ignoreCase: true),
            CreateIdentityMetadata(reader.GetString(28), reader.GetString(27)),
            new AddressedMessageProviderMetadata(
                reader.GetString(29),
                reader.GetString(30),
                reader.GetInt32(31),
                reader.GetString(32),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reader.GetString(33))))[..24],
                reader.GetInt64(34),
                capabilities));
    }

    private string MessageSelect => $"""
        SELECT m.message_id, m.project_id, m.sender_run_id, m.sender_session_id,
            m.recipient_run_id, m.recipient_session_id, m.thread_id, m.reply_to_id,
            m.idempotency_key, m.thread_sequence, m.sender_fence, m.recipient_fence, m.claim_fence,
            m.delivery_mode, m.purpose, m.kind, m.request_id, m.reply_correlation_id,
            m.user_quote, m.coordinator_instructions, m.payload, m.status, m.created_at, m.expires_at,
            m.presented_at, m.acknowledged_at, m.failure_reason, m.sender_identity_hash, m.sender_issuer,
            p.provider_id, p.adapter_version, p.options_schema_version, p.options_revision,
            p.resource_id, p.resource_generation, p.negotiated_capabilities
        FROM {_messages} AS m
        JOIN {_providerBindings} AS p
            ON p.project_id = m.project_id AND p.run_id = m.sender_run_id
        """;

    private static bool IsToken(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
