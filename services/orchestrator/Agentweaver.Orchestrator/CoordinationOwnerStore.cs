using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class CoordinationOwnerStore
{
    private const string IngressConsumer = "orchestrator.addressed-message-ingress";
    private const int EventVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _runs;
    private readonly string _sessions;
    private readonly string _messages;
    private readonly string _requests;
    private readonly string _notifications;
    private readonly PostgresOutbox _outbox;
    private readonly TimeProvider _timeProvider;

    public CoordinationOwnerStore(
        NpgsqlDataSource dataSource,
        string schema,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        _dataSource = dataSource;
        _schema = $"\"{schema}\"";
        _runs = $"{_schema}.accepted_runs";
        _sessions = $"{_schema}.coordination_sessions";
        _messages = $"{_schema}.coordination_messages";
        _requests = $"{_schema}.coordination_requests";
        _notifications = $"{_schema}.parent_notifications";
        _outbox = new PostgresOutbox(dataSource, schema);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AcceptedRoot> AcceptRootAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        var effectiveSelection = selection.Selection;
        if (selection.Authorization.ContractVersion != 1 ||
            selection.Authorization.Issuer != actor.Issuer ||
            selection.Authorization.ActorId != actor.Subject ||
            string.IsNullOrWhiteSpace(selection.Authorization.TenantId) ||
            selection.Authorization.BoundProjectId != effectiveSelection.ProjectId ||
            selection.Authorization.BoundRunId != effectiveSelection.RunId ||
            selection.Authorization.MembershipRevision < 1 ||
            selection.Authorization.EffectiveAuthority.IsDefault ||
            !selection.Authorization.EffectiveAuthority.Any(authority =>
                authority.ResourceType == "project" &&
                authority.ResourceId == effectiveSelection.ProjectId &&
                !authority.Permissions.IsDefault &&
                authority.Permissions.Any(permission =>
                    permission.Permission == "acceptRunSelection" &&
                    permission.RoleRevision > 0)))
            throw new CoordinationException(
                "run_selection_permission_denied", StatusCodes.Status403Forbidden);
        CoordinationIdentity.ValidateIdentity(sessionId, nameof(sessionId));
        ValidateSelection(effectiveSelection);

        var snapshot = effectiveSelection.Snapshot.GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_runs}
                (project_id, run_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject, tenant_id)
            VALUES (@project, @run, @selection, @hash, @issuer, @subject, @tenant)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection, transaction))
        {
            AddRunScope(insert, effectiveSelection.ProjectId, effectiveSelection.RunId);
            insert.Parameters.AddWithValue("selection", NpgsqlDbType.Jsonb, snapshot);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, selection.Authorization.TenantId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var bindTenant = new NpgsqlCommand($"""
            UPDATE {_runs}
            SET tenant_id = @tenant
            WHERE project_id = @project AND run_id = @run
              AND tenant_id IS NULL
              AND accepted_selection_hash = @hash
              AND accepted_by_issuer = @issuer
              AND accepted_by_subject = @subject
            """, connection, transaction))
        {
            AddRunScope(bindTenant, effectiveSelection.ProjectId, effectiveSelection.RunId);
            bindTenant.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, selection.Authorization.TenantId);
            bindTenant.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
            bindTenant.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            bindTenant.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            await bindTenant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var acceptedRun = await ReadAcceptedRunAsync(
            connection,
            transaction,
            effectiveSelection.ProjectId,
            effectiveSelection.RunId,
            forUpdate: true,
            cancellationToken)
            .ConfigureAwait(false);
        if (acceptedRun.SelectionHash != hash || acceptedRun.AcceptedIssuer != actor.Issuer ||
            acceptedRun.AcceptedSubject != actor.Subject ||
            acceptedRun.TenantId != selection.Authorization.TenantId)
            throw new CoordinationException("accepted_run_conflict", StatusCodes.Status409Conflict);

        var root = await ReadRootSessionAsync(
            connection,
            transaction,
            effectiveSelection.ProjectId,
            effectiveSelection.RunId,
            forUpdate: true,
            cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
        {
            await using var insertSession = new NpgsqlCommand($"""
                INSERT INTO {_sessions}
                    (project_id, run_id, session_id, parent_session_id, writer_issuer, writer_subject, execution_fence)
                VALUES (@project, @run, @session, NULL, @issuer, @subject, @fence)
                """, connection, transaction);
            AddRunScope(insertSession, effectiveSelection.ProjectId, effectiveSelection.RunId);
            insertSession.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
            insertSession.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insertSession.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insertSession.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, acceptedRun.Fence);
            await insertSession.ExecuteNonQueryAsync(cancellationToken);
            root = await ReadRootSessionAsync(
                connection,
                transaction,
                effectiveSelection.ProjectId,
                effectiveSelection.RunId,
                forUpdate: false,
                cancellationToken)
                .ConfigureAwait(false);
        }
        if (root is null || root.SessionId != sessionId ||
            root.WriterIssuer != actor.Issuer || root.WriterSubject != actor.Subject)
            throw new CoordinationException("root_session_conflict", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken);
        return new AcceptedRoot(
            effectiveSelection.ProjectId,
            effectiveSelection.RunId,
            root.SessionId,
            acceptedRun.Fence,
            acceptedRun.StateVersion,
            acceptedRun.LogicalTurnOrdinal,
            acceptedRun.ExecutionState);
    }

    public async Task<RegisteredChild> RegisterChildAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        string childSessionId,
        int maxChildren,
        int maxConcurrentChildren,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        CoordinationIdentity.ValidateIdentity(childSessionId, nameof(childSessionId));
        if (maxChildren is < 0 or > 100 ||
            maxConcurrentChildren is < 1 or > 32)
            throw new CoordinationException("run_child_limit_invalid", StatusCodes.Status502BadGateway);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var parentSession = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(parentSession, actor);
        RequireCurrentActiveSession(parentSession, run);
        if (parentSession.LifecycleState != "active")
            throw new CoordinationException("parent_session_unavailable", StatusCodes.Status409Conflict);

        var existing = await FindSessionAsync(
            connection, transaction, parent.ProjectId, parent.RunId, childSessionId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ParentSessionId != parent.SessionId ||
                existing.WriterIssuer != actor.Issuer || existing.WriterSubject != actor.Subject ||
                existing.LifecycleState != "active")
                throw new CoordinationException("child_session_conflict", StatusCodes.Status409Conflict);
            var requestId = await FindRequestIdAsync(
                connection, transaction, parent.ProjectId, parent.RunId,
                parent.SessionId, childSessionId, cancellationToken).ConfigureAwait(false);
            if (requestId is null)
                throw new InvalidOperationException("A registered child has no pending request.");
            await transaction.CommitAsync(cancellationToken);
            return new RegisteredChild(
                new SessionIdentity(parent.ProjectId, parent.RunId, childSessionId),
                parent.SessionId,
                requestId,
                existing.ExecutionFence);
        }

        await using (var countChildren = new NpgsqlCommand($"""
            SELECT count(*)::integer
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
              AND parent_session_id IS NOT NULL
            """, connection, transaction))
        {
            AddRunScope(countChildren, parent.ProjectId, parent.RunId);
            var currentChildren = Convert.ToInt32(
                await countChildren.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (currentChildren >= maxChildren)
                throw new CoordinationException(
                    "run_child_limit_exceeded", StatusCodes.Status409Conflict);
        }
        await using (var countActiveChildren = new NpgsqlCommand($"""
            SELECT count(*)::integer
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
              AND parent_session_id IS NOT NULL AND lifecycle_state = 'active'
            """, connection, transaction))
        {
            AddRunScope(countActiveChildren, parent.ProjectId, parent.RunId);
            var activeChildren = Convert.ToInt32(
                await countActiveChildren.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (activeChildren >= maxConcurrentChildren)
                throw new CoordinationException(
                    "run_concurrent_child_limit_exceeded", StatusCodes.Status409Conflict);
        }

        await using (var insertSession = new NpgsqlCommand($"""
            INSERT INTO {_sessions}
                (project_id, run_id, session_id, parent_session_id, writer_issuer, writer_subject, execution_fence)
            VALUES (@project, @run, @session, @parent, @issuer, @subject, @fence)
            """, connection, transaction))
        {
            AddRunScope(insertSession, parent.ProjectId, parent.RunId);
            insertSession.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, childSessionId);
            insertSession.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
            insertSession.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insertSession.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insertSession.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
            await insertSession.ExecuteNonQueryAsync(cancellationToken);
        }

        var requestIdCreated = Guid.NewGuid().ToString("N");
        await using (var insertRequest = new NpgsqlCommand($"""
            INSERT INTO {_requests}
                (project_id, run_id, request_id, sender_session_id, recipient_session_id)
            VALUES (@project, @run, @request, @sender, @recipient)
            """, connection, transaction))
        {
            AddRunScope(insertRequest, parent.ProjectId, parent.RunId);
            insertRequest.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestIdCreated);
            insertRequest.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, parent.SessionId);
            insertRequest.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, childSessionId);
            await insertRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RegisteredChild(
            new SessionIdentity(parent.ProjectId, parent.RunId, childSessionId),
            parent.SessionId,
            requestIdCreated,
            run.Fence);
    }

    public async Task<MessageRouteBinding> ValidateMessageRouteAsync(
        CoordinationActor actor,
        SessionIdentity sender,
        MessageRouteValidationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Message);
        var outbound = request.Message;
        if (outbound.OwnerMessageId == Guid.Empty || outbound.Sender != sender ||
            outbound.Recipient.ProjectId != sender.ProjectId ||
            outbound.Recipient.RunId != sender.RunId ||
            outbound.Sender.SessionId == outbound.Recipient.SessionId)
            throw new CoordinationException("owner_message_invalid", StatusCodes.Status400BadRequest);

        var persistedOutbound = await ReadOutboundMessageAsync(
            outbound.OwnerMessageId, cancellationToken).ConfigureAwait(false);
        if (!SameOutboundMessage(persistedOutbound, outbound))
            throw new CoordinationException("owner_message_mismatch", StatusCodes.Status409Conflict);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, sender.ProjectId, sender.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var stored = await ReadMessageAsync(
            connection, transaction, outbound.OwnerMessageId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null || stored.ProjectId != sender.ProjectId || stored.RunId != sender.RunId ||
            stored.SenderSessionId != sender.SessionId ||
            stored.RecipientSessionId != outbound.Recipient.SessionId ||
            stored.ThreadId != outbound.ThreadId ||
            stored.WriterIssuer != actor.Issuer || stored.WriterSubject != actor.Subject ||
            stored.Status is not ("pending" or "admitted"))
            throw new CoordinationException("owner_message_unavailable", StatusCodes.Status409Conflict);

        var source = await ReadSessionAsync(connection, transaction, sender, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        RequireWriter(source, actor);
        RequireCurrentActiveSession(source, run);
        if (stored.SenderFence != source.ExecutionFence || stored.SenderFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        var recipientIdentity = outbound.Recipient;
        var recipient = await ReadSessionAsync(
            connection, transaction, recipientIdentity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireCurrentActiveSession(recipient, run);
        if (stored.RecipientFence != recipient.ExecutionFence ||
            stored.RecipientFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        Guid? replyToMessageId = null;
        if (outbound.ReplyToId is { } replyTo)
        {
            var correlated = await HasReverseMessageAsync(
                connection, transaction, sender, recipientIdentity, replyTo,
                outbound.RequestId, outbound.ReplyCorrelationId, cancellationToken).ConfigureAwait(false);
            if (!correlated)
                throw new CoordinationException("reply_correlation_invalid", StatusCodes.Status409Conflict);
            replyToMessageId = await ReadAcknowledgedReplyMessageIdAsync(
                connection,
                transaction,
                sender,
                recipientIdentity,
                replyTo,
                outbound.RequestId,
                outbound.ReplyCorrelationId,
                cancellationToken).ConfigureAwait(false);
            if (replyToMessageId is null)
                throw new CoordinationException("reply_correlation_invalid", StatusCodes.Status409Conflict);
        }

        if (outbound.RequestId is { } requestId)
        {
            if (!await HasActiveRequestAsync(
                connection, transaction, sender.ProjectId, sender.RunId,
                sender.SessionId, recipientIdentity.SessionId, requestId, cancellationToken).ConfigureAwait(false) &&
                !await HasActiveRequestAsync(
                    connection, transaction, sender.ProjectId, sender.RunId,
                    recipientIdentity.SessionId, sender.SessionId, requestId, cancellationToken).ConfigureAwait(false))
                throw new CoordinationException("request_correlation_invalid", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MessageRouteBinding(
            sender,
            recipientIdentity,
            source.ExecutionFence,
            recipient.ExecutionFence,
            CoordinationIdentity.ClaimOwner(actor),
            replyToMessageId);
    }

    public async Task<CoordinationSessionBinding> GetSessionBindingAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        await transaction.CommitAsync(cancellationToken);
        return new CoordinationSessionBinding(
            identity,
            session.ExecutionFence,
            session.TurnState,
            session.StateVersion,
            session.PendingWake,
            CoordinationIdentity.ClaimOwner(actor));
    }

    public async Task<OutboxDelivery?> ClaimOutboundAsync(
        CoordinationActor actor,
        Guid ownerMessageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT writer_issuer, writer_subject
            FROM {_messages}
            WHERE message_id = @message
            """, connection);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, ownerMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new CoordinationException("message_not_found", StatusCodes.Status404NotFound);
        if (reader.GetString(0) != actor.Issuer || reader.GetString(1) != actor.Subject)
            throw new CoordinationException("message_writer_mismatch", StatusCodes.Status403Forbidden);
        await reader.DisposeAsync();
        return await _outbox.ClaimOneAsync(
            CoordinationIdentity.ClaimOwner(actor),
            ownerMessageId,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OwnerOutboundMessage> ReadOutboundMessageAsync(
        Guid ownerMessageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text
            FROM {_schema}.outbox_events
            WHERE id = @message AND event_type = 'orchestrator.addressed_message'
            """, connection);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, ownerMessageId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (json is null)
            throw new CoordinationException("message_outbox_not_found", StatusCodes.Status404NotFound);
        try
        {
            return JsonSerializer.Deserialize<OwnerOutboundMessage>(json, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new CoordinationException("message_outbox_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }

    public Task<bool> AcknowledgeOutboundAsync(
        OutboxDelivery delivery,
        CancellationToken cancellationToken) =>
        _outbox.AcknowledgeAsync(
            delivery.Event.Message.Id, delivery.LeaseToken, cancellationToken);

    public async Task<CoordinationMessageResult> SendMessageAsync(
        CoordinationActor actor,
        SessionIdentity sender,
        CoordinationMessageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        CoordinationIdentity.ValidateIdentity(request.RecipientSessionId, nameof(request.RecipientSessionId));
        var recipient = new SessionIdentity(sender.ProjectId, sender.RunId, request.RecipientSessionId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, sender.ProjectId, sender.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var source = await ReadSessionAsync(connection, transaction, sender, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireWriter(source, actor);
        RequireCurrentActiveSession(source, run);
        var target = await ReadSessionAsync(connection, transaction, recipient, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrentActiveSession(target, run);

        Guid? replyThreadId = null;
        if (request.ReplyToId is { } replyTo)
        {
            replyThreadId = await ReadReverseMessageThreadIdAsync(
                connection, transaction, sender, recipient, replyTo,
                request.RequestId, request.ReplyCorrelationId, cancellationToken).ConfigureAwait(false);
            if (replyThreadId is null ||
                (request.ThreadId is { } requestedThread && requestedThread != replyThreadId))
                throw new CoordinationException("reply_correlation_invalid", StatusCodes.Status409Conflict);
        }
        if (request.RequestId is { } correlatedRequestId &&
            !await HasActiveRequestAsync(
                connection, transaction, sender.ProjectId, sender.RunId,
                sender.SessionId, recipient.SessionId, correlatedRequestId, cancellationToken).ConfigureAwait(false) &&
            !await HasActiveRequestAsync(
                connection, transaction, sender.ProjectId, sender.RunId,
                recipient.SessionId, sender.SessionId, correlatedRequestId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("request_correlation_invalid", StatusCodes.Status409Conflict);

        var threadId = request.ThreadId ?? replyThreadId ?? Guid.NewGuid();
        AddressedMessageDraft draft;
        try
        {
            draft = AddressedMessageValidation.ValidateAndNormalize(new AddressedMessageDraft(
                sender,
                recipient,
                request.IdempotencyKey,
                request.DeliveryMode,
                request.Purpose,
                request.Kind,
                request.Payload,
                source.ExecutionFence,
                target.ExecutionFence,
                threadId,
                request.ReplyToId,
                request.RequestId,
                request.ReplyCorrelationId,
                request.UserQuote,
                request.CoordinatorInstructions));
        }
        catch (ArgumentException exception)
        {
            throw new CoordinationException("addressed_message_invalid", StatusCodes.Status400BadRequest, exception);
        }

        var canonicalRequest = JsonSerializer.SerializeToElement(
            draft with { SenderFence = 0, RecipientFence = 0, ThreadId = request.ThreadId }, JsonOptions);
        var idempotencyKey = draft.IdempotencyKey;
        var messageId = Guid.NewGuid();
        var outbound = new OwnerOutboundMessage(
            messageId,
            sender,
            recipient,
            idempotencyKey,
            draft.DeliveryMode,
            draft.Purpose,
            draft.Kind,
            draft.Payload,
            draft.SenderFence,
            draft.RecipientFence,
            threadId,
            draft.ReplyToId,
            draft.RequestId,
            draft.ReplyCorrelationId,
            draft.UserQuote,
            draft.CoordinatorInstructions);

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_messages}
                (project_id, run_id, message_id, sender_session_id, recipient_session_id,
                 writer_issuer, writer_subject, thread_id, sender_fence, recipient_fence,
                 idempotency_key, purpose, kind, delivery_mode, request_id, reply_to_id,
                 reply_correlation_id, canonical_request, payload)
            VALUES (@project, @run, @id, @sender, @recipient, @issuer, @subject, @thread,
                @senderFence, @recipientFence, @key, @purpose, @kind, @mode, @request,
                @replyTo, @replyCorrelation, @canonical, @payload)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddRunScope(insert, sender.ProjectId, sender.RunId);
            insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, messageId);
            insert.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
            insert.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("thread", NpgsqlDbType.Uuid, threadId);
            insert.Parameters.AddWithValue("senderFence", NpgsqlDbType.Bigint, draft.SenderFence);
            insert.Parameters.AddWithValue("recipientFence", NpgsqlDbType.Bigint, draft.RecipientFence);
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            insert.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, draft.Purpose.ToString());
            insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, draft.Kind.ToString());
            insert.Parameters.AddWithValue("mode", NpgsqlDbType.Varchar, draft.DeliveryMode.ToString());
            insert.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)draft.RequestId ?? DBNull.Value);
            insert.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, (object?)draft.ReplyToId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "replyCorrelation", NpgsqlDbType.Varchar, (object?)draft.ReplyCorrelationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("canonical", NpgsqlDbType.Jsonb, canonicalRequest.GetRawText());
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, draft.Payload.GetRawText());
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            if (inserted == 0)
            {
                var existing = await ReadExistingMessageAsync(
                    connection, transaction, sender, actor, idempotencyKey, canonicalRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null || !existing.CanonicalRequestMatches)
                    throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
                await transaction.CommitAsync(cancellationToken);
                return new CoordinationMessageResult(
                    existing.MessageId, existing.RecipientSessionId, existing.Status, existing.RequestId);
            }
        }

        var payload = JsonSerializer.SerializeToElement(outbound, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            messageId,
            $"coordination/{sender.ProjectId}/{sender.RunId}/{sender.SessionId}",
            $"{sender.SessionId}:{actor.Issuer}:{actor.Subject}:{idempotencyKey}",
            "orchestrator.addressed_message",
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

        if (draft.RequestId is { } requestId && draft.ReplyToId is null)
        {
            await using var bindRequest = new NpgsqlCommand($"""
                UPDATE {_requests}
                SET source_message_id = @message
                WHERE project_id = @project AND run_id = @run AND request_id = @request
                    AND ((sender_session_id = @sender AND recipient_session_id = @recipient)
                      OR (sender_session_id = @recipient AND recipient_session_id = @sender))
                    AND source_message_id IS NULL AND gate_state = 'pending'
                """, connection, transaction);
            AddRunScope(bindRequest, sender.ProjectId, sender.RunId);
            bindRequest.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
            bindRequest.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
            bindRequest.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
            bindRequest.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
            var bound = await bindRequest.ExecuteNonQueryAsync(cancellationToken);
            if (bound == 0)
                throw new CoordinationException("request_correlation_invalid", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CoordinationMessageResult(messageId, recipient.SessionId, "pending", draft.RequestId);
    }

    public async Task AdmitDeliveryAsync(
        CoordinationActor actor,
        DeliveryIngressRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var receipt = request.Admission ?? throw new CoordinationException(
            "delivery_receipt_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stored = await ReadMessageAsync(
            connection, transaction, request.OwnerMessageId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (stored is null || request.OwnerMessageId != receipt.OwnerMessageId ||
            stored.WriterIssuer != actor.Issuer || stored.WriterSubject != actor.Subject ||
            !ReceiptMatches(stored, receipt))
            throw new CoordinationException("delivery_receipt_mismatch", StatusCodes.Status409Conflict);

        var admission = await _outbox.AdmitAsync(
            connection, transaction, IngressConsumer, receipt.MessageId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (admission == InboxAdmission.Duplicate)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using (var updateMessage = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = 'admitted', events_message_id = @eventsMessage
            WHERE project_id = @project AND message_id = @ownerMessage AND status = 'pending'
            """, connection, transaction))
        {
            updateMessage.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, receipt.MessageId);
            updateMessage.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, stored.ProjectId);
            updateMessage.Parameters.AddWithValue("ownerMessage", NpgsqlDbType.Uuid, request.OwnerMessageId);
            if (await updateMessage.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new CoordinationException("delivery_state_conflict", StatusCodes.Status409Conflict);
        }

        if (stored.RequestId is not null && stored.ReplyToId is not null)
        {
            await using var updateRequest = new NpgsqlCommand($"""
                UPDATE {_requests}
                SET gate_state = 'input_available', updated_at = clock_timestamp()
                WHERE project_id = @project AND run_id = @run AND request_id = @request
                    AND gate_state = 'pending'
                """, connection, transaction);
            AddRunScope(updateRequest, stored.ProjectId, stored.RunId);
            updateRequest.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, stored.RequestId);
            await updateRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        var wakes = receipt.Purpose is AddressedMessagePurpose.NeedsInput or AddressedMessagePurpose.Error;
        if (wakes)
        {
            await using var wakeRecipient = new NpgsqlCommand($"""
                UPDATE {_sessions}
                SET pending_wake = true, state_version = state_version + 1
                WHERE project_id = @project AND run_id = @run AND session_id = @recipient
                    AND lifecycle_state = 'active'
                """, connection, transaction);
            AddRunScope(wakeRecipient, stored.ProjectId, stored.RunId);
            wakeRecipient.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, stored.RecipientSessionId);
            await wakeRecipient.ExecuteNonQueryAsync(cancellationToken);
        }

        var notificationKind = receipt.Purpose switch
        {
            AddressedMessagePurpose.Handoff => "handoff",
            AddressedMessagePurpose.NeedsInput => "needs_input",
            AddressedMessagePurpose.Error => "error",
            _ => null
        };
        if (notificationKind is not null)
        {
            await using var notification = new NpgsqlCommand($"""
                INSERT INTO {_notifications}
                    (project_id, run_id, notification_id, parent_session_id, child_session_id,
                     message_id, notification_kind, wakes_parent)
                SELECT @project, @run, @notification, recipient.session_id, sender.session_id,
                    @message, @kind, @wakes
                FROM {_sessions} AS sender
                JOIN {_sessions} AS recipient
                  ON recipient.project_id = sender.project_id AND recipient.run_id = sender.run_id
                 AND recipient.session_id = sender.parent_session_id
                WHERE sender.project_id = @project AND sender.run_id = @run
                    AND sender.session_id = @sender AND recipient.session_id = @recipient
                ON CONFLICT (project_id, message_id) DO NOTHING
                """, connection, transaction);
            AddRunScope(notification, stored.ProjectId, stored.RunId);
            notification.Parameters.AddWithValue("notification", NpgsqlDbType.Uuid, Guid.NewGuid());
            notification.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, request.OwnerMessageId);
            notification.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, notificationKind);
            notification.Parameters.AddWithValue("wakes", NpgsqlDbType.Boolean, wakes);
            notification.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, stored.SenderSessionId);
            notification.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, stored.RecipientSessionId);
            await notification.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TurnBoundaryResult?> ReadCompletedTurnBoundaryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        long executionFence,
        long requestStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (executionFence < 1 || requestStateVersion < 1)
            throw new CoordinationException("turn_fence_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != executionFence || run.Fence != executionFence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        if (session.TurnBoundaryRequestVersion != requestStateVersion ||
            session.TurnBoundaryResult is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var result = JsonSerializer.Deserialize<TurnBoundaryResult>(session.TurnBoundaryResult, JsonOptions);
        if (result is null || result.Session != identity || result.ExecutionState != "active")
            throw new InvalidOperationException("A persisted turn-boundary result is invalid.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<TurnBoundaryResult> AdvanceTurnBoundaryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        long expectedFence,
        long expectedStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (expectedFence < 1 || expectedStateVersion < 1)
            throw new CoordinationException("turn_fence_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (session.ExecutionFence != expectedFence || expectedFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        var resumedBoundary = session.TurnState == "presenting" &&
            session.TurnBoundaryRequestVersion == expectedStateVersion &&
            session.StateVersion > expectedStateVersion;
        var canStartBoundary = session.StateVersion == expectedStateVersion &&
            (session.TurnState == "idle" || (session.TurnState == "blocked" && session.PendingWake));
        if (!resumedBoundary && !canStartBoundary)
            throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);

        TurnBoundaryResult result;
        if (resumedBoundary)
        {
            result = new TurnBoundaryResult(
                identity, session.LogicalTurnOrdinal, session.StateVersion,
                "presenting", session.PendingWake, null);
        }
        else
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {_sessions}
                SET logical_turn_ordinal = logical_turn_ordinal + 1,
                    turn_state = 'presenting',
                    state_version = state_version + 1,
                    turn_boundary_request_version = @version,
                    turn_boundary_result = NULL
                WHERE project_id = @project AND run_id = @run AND session_id = @session
                    AND execution_fence = @fence AND state_version = @version
                    AND (turn_state = 'idle' OR (turn_state = 'blocked' AND pending_wake))
                    AND lifecycle_state = 'active'
                RETURNING logical_turn_ordinal, state_version, pending_wake
                """, connection, transaction);
            AddRunScope(update, identity.ProjectId, identity.RunId);
            update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, expectedFence);
            update.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, expectedStateVersion);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);
            result = new TurnBoundaryResult(
                identity,
                reader.GetInt64(0),
                reader.GetInt64(1),
                "presenting",
                reader.GetBoolean(2),
                null);
            await reader.DisposeAsync();
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TurnBoundaryResult> CompleteTurnBoundaryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        long executionFence,
        long requestStateVersion,
        long presentingStateVersion,
        AddressedMessageEnvelope? presentedMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (session.ExecutionFence != executionFence || executionFence != run.Fence ||
            session.TurnState != "presenting" ||
            session.TurnBoundaryRequestVersion != requestStateVersion ||
            presentingStateVersion <= requestStateVersion ||
            session.StateVersion < presentingStateVersion)
            throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);

        var pendingWake = session.StateVersion == requestStateVersion + 1
            ? false
            : session.PendingWake;
        var stateVersion = checked(session.StateVersion + 1);
        var notifications = await ReadParentNotificationsAsync(
            connection, transaction, identity, 50, cancellationToken).ConfigureAwait(false);
        var result = new TurnBoundaryResult(
            identity,
            session.LogicalTurnOrdinal,
            stateVersion,
            "active",
            pendingWake,
            presentedMessage,
            notifications.ToImmutableArray());
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET turn_state = 'active',
                state_version = @nextVersion,
                pending_wake = @pendingWake,
                turn_boundary_result = @result
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND execution_fence = @fence AND state_version = @version
                AND turn_state = 'presenting'
                AND turn_boundary_request_version = @requestVersion
                AND lifecycle_state = 'active'
            """, connection, transaction);
        AddRunScope(update, identity.ProjectId, identity.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        update.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, session.StateVersion);
        update.Parameters.AddWithValue("nextVersion", NpgsqlDbType.Bigint, stateVersion);
        update.Parameters.AddWithValue("requestVersion", NpgsqlDbType.Bigint, requestStateVersion);
        update.Parameters.AddWithValue("pendingWake", NpgsqlDbType.Boolean, pendingWake);
        update.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TurnBoundaryResult> FinishTurnAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        FinishTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionFence < 1 || request.ExpectedStateVersion < 1 ||
            !Enum.IsDefined(request.Completion))
            throw new CoordinationException("turn_completion_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (session.ExecutionFence != request.ExecutionFence || request.ExecutionFence != run.Fence ||
            session.StateVersion != request.ExpectedStateVersion || session.TurnState != "active")
            throw new CoordinationException("turn_completion_conflict", StatusCodes.Status409Conflict);

        var nextState = request.Completion switch
        {
            LogicalTurnCompletion.Idle => "idle",
            LogicalTurnCompletion.Blocked => "blocked",
            LogicalTurnCompletion.Completed => "completed",
            _ => throw new CoordinationException("turn_completion_invalid", StatusCodes.Status400BadRequest)
        };
        await using var update = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET turn_state = @state,
                lifecycle_state = CASE WHEN @state = 'completed' THEN 'completed' ELSE lifecycle_state END,
                state_version = state_version + 1
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND execution_fence = @fence AND state_version = @version AND turn_state = 'active'
            RETURNING logical_turn_ordinal, state_version, pending_wake
            """, connection, transaction);
        AddRunScope(update, identity.ProjectId, identity.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, request.ExecutionFence);
        update.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, request.ExpectedStateVersion);
        update.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, nextState);
        await using var reader = await update.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new CoordinationException("turn_completion_conflict", StatusCodes.Status409Conflict);
        var result = new TurnBoundaryResult(
            identity,
            reader.GetInt64(0),
            reader.GetInt64(1),
            nextState,
            reader.GetBoolean(2),
            null);
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task RecordPresentationAsync(
        CoordinationActor actor,
        SessionIdentity recipient,
        AddressedMessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Status != AddressedMessageStatus.Delivered ||
            envelope.Recipient != recipient || envelope.MessageId == Guid.Empty)
            throw new CoordinationException("presentation_receipt_invalid", StatusCodes.Status409Conflict);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, recipient.ProjectId, recipient.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, recipient, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (session.ExecutionFence != envelope.RecipientFence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using var update = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = 'presented'
            WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                AND recipient_session_id = @session AND recipient_fence = @fence
                AND status = 'admitted'
            """, connection, transaction);
        AddRunScope(update, recipient.ProjectId, recipient.RunId);
        update.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            await using var verify = new NpgsqlCommand($"""
                SELECT count(*) FROM {_messages}
                WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                    AND recipient_session_id = @session AND recipient_fence = @fence
                    AND status IN ('presented', 'acknowledged')
                """, connection, transaction);
            AddRunScope(verify, recipient.ProjectId, recipient.RunId);
            verify.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
            verify.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
            verify.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
            if ((long)(await verify.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
                throw new CoordinationException("presentation_receipt_mismatch", StatusCodes.Status409Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordAcknowledgementAsync(
        CoordinationActor actor,
        SessionIdentity recipient,
        AddressedMessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Status != AddressedMessageStatus.Acknowledged ||
            envelope.Recipient != recipient || envelope.MessageId == Guid.Empty)
            throw new CoordinationException("acknowledgment_receipt_invalid", StatusCodes.Status409Conflict);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, recipient.ProjectId, recipient.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, recipient, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (session.ExecutionFence != envelope.RecipientFence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using var update = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = 'acknowledged'
            WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                AND recipient_session_id = @session AND recipient_fence = @fence
                AND status IN ('admitted', 'presented')
            """, connection, transaction);
        AddRunScope(update, recipient.ProjectId, recipient.RunId);
        update.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            await using var verify = new NpgsqlCommand($"""
                SELECT count(*) FROM {_messages}
                WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                    AND recipient_session_id = @session AND recipient_fence = @fence
                    AND status = 'acknowledged'
                """, connection, transaction);
            AddRunScope(verify, recipient.ProjectId, recipient.RunId);
            verify.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
            verify.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
            verify.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
            if ((long)(await verify.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
                throw new CoordinationException("acknowledgment_receipt_mismatch", StatusCodes.Status409Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParentNotification>> ReadParentNotificationsAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > 100)
            throw new CoordinationException("notification_limit_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        var notifications = await ReadParentNotificationsAsync(
            connection, transaction, parent, limit, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return notifications;
    }

    private async Task<ImmutableArray<ParentNotification>> ReadParentNotificationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity parent,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT notification_id, parent_session_id, child_session_id, message_id,
                notification_kind, wakes_parent, created_at
            FROM {_notifications}
            WHERE project_id = @project AND run_id = @run AND parent_session_id = @parent
                AND acknowledged_at IS NULL
            ORDER BY created_at, notification_id
            LIMIT @limit
            """, connection, transaction);
        AddRunScope(command, parent.ProjectId, parent.RunId);
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var notifications = ImmutableArray.CreateBuilder<ParentNotification>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var purpose = reader.GetString(4) switch
            {
                "handoff" => AddressedMessagePurpose.Handoff,
                "needs_input" => AddressedMessagePurpose.NeedsInput,
                "error" => AddressedMessagePurpose.Error,
                _ => throw new InvalidOperationException("A parent notification kind is invalid.")
            };
            notifications.Add(new ParentNotification(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                purpose,
                reader.GetBoolean(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return notifications.ToImmutable();
    }

    public async Task AcknowledgeParentNotificationAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (notificationId == Guid.Empty)
            throw new CoordinationException("notification_id_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using var update = new NpgsqlCommand($"""
            UPDATE {_notifications}
            SET acknowledged_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND notification_id = @notification
                AND parent_session_id = @parent AND acknowledged_at IS NULL
            """, connection, transaction);
        AddRunScope(update, parent.ProjectId, parent.RunId);
        update.Parameters.AddWithValue("notification", NpgsqlDbType.Uuid, notificationId);
        update.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            await using var verify = new NpgsqlCommand($"""
                SELECT count(*) FROM {_notifications}
                WHERE project_id = @project AND run_id = @run AND notification_id = @notification
                    AND parent_session_id = @parent AND acknowledged_at IS NOT NULL
                """, connection, transaction);
            AddRunScope(verify, parent.ProjectId, parent.RunId);
            verify.Parameters.AddWithValue("notification", NpgsqlDbType.Uuid, notificationId);
            verify.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
            if ((long)(await verify.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
                throw new CoordinationException("notification_not_found", StatusCodes.Status404NotFound);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<AcceptedRunRow> ReadAcceptedRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT accepted_selection_hash, accepted_by_issuer, accepted_by_subject,
                execution_fence, logical_turn_ordinal, execution_state, state_version, tenant_id
            FROM {_runs}
            WHERE project_id = @project AND run_id = @run
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new CoordinationException("accepted_run_not_found", StatusCodes.Status404NotFound);
        return new AcceptedRunRow(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7));
    }

    private async Task<SessionRow> ReadSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(
            connection, transaction, identity.ProjectId, identity.RunId,
            identity.SessionId, forUpdate, cancellationToken).ConfigureAwait(false);
        return session ?? throw new CoordinationException("session_not_found", StatusCodes.Status404NotFound);
    }

    private async Task<SessionRow?> FindSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string sessionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, parent_session_id, writer_issuer, writer_subject,
                execution_fence, lifecycle_state, logical_turn_ordinal, turn_state, state_version, pending_wake,
                turn_boundary_request_version, turn_boundary_result
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11))
            : null;
    }

    private async Task<SessionRow?> ReadRootSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, parent_session_id, writer_issuer, writer_subject,
                execution_fence, lifecycle_state, logical_turn_ordinal, turn_state, state_version, pending_wake,
                turn_boundary_request_version, turn_boundary_result
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND parent_session_id IS NULL
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionRow(
                reader.GetString(0),
                null,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11))
            : null;
    }

    private async Task<string?> FindRequestIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string senderSessionId,
        string recipientSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_id FROM {_requests}
            WHERE project_id = @project AND run_id = @run
                AND sender_session_id = @sender AND recipient_session_id = @recipient
                AND gate_state = 'pending'
            ORDER BY created_at DESC LIMIT 1
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, senderSessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipientSessionId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<bool> HasActiveRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string senderSessionId,
        string recipientSessionId,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_requests}
                WHERE project_id = @project AND run_id = @run AND request_id = @request
                    AND sender_session_id = @sender AND recipient_session_id = @recipient
                    AND gate_state <> 'cancelled'
            )
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, senderSessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipientSessionId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<Guid?> ReadReverseMessageThreadIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        SessionIdentity recipient,
        Guid replyTo,
        string? requestId,
        string? replyCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT thread_id
            FROM {_messages}
            WHERE project_id = @project AND run_id = @run AND message_id = @replyTo
                AND sender_session_id = @recipient AND recipient_session_id = @sender
                AND (@request IS NULL OR request_id = @request)
                AND (@correlation IS NULL OR request_id = @correlation
                    OR reply_correlation_id = @correlation)
                AND status <> 'cancelled'
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, sender.ProjectId, sender.RunId);
        command.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, replyTo);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "correlation", NpgsqlDbType.Varchar, (object?)replyCorrelationId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetGuid(0) : null;
    }

    private async Task<bool> HasReverseMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        SessionIdentity recipient,
        Guid replyTo,
        string? requestId,
        string? replyCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_messages}
                WHERE project_id = @project AND run_id = @run AND message_id = @replyTo
                    AND sender_session_id = @recipient AND recipient_session_id = @sender
                    AND (@request IS NULL OR request_id = @request)
                    AND (@correlation IS NULL OR request_id = @correlation
                        OR reply_correlation_id = @correlation)
                    AND status <> 'cancelled'
            )
            """, connection, transaction);
        AddRunScope(command, sender.ProjectId, sender.RunId);
        command.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, replyTo);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "correlation", NpgsqlDbType.Varchar, (object?)replyCorrelationId ?? DBNull.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<Guid?> ReadAcknowledgedReplyMessageIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        SessionIdentity recipient,
        Guid replyTo,
        string? requestId,
        string? replyCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT events_message_id
            FROM {_messages}
            WHERE project_id = @project AND run_id = @run AND message_id = @replyTo
                AND sender_session_id = @recipient AND recipient_session_id = @sender
                AND status = 'acknowledged'
                AND (@request IS NULL OR request_id = @request)
                AND (@correlation IS NULL OR request_id = @correlation
                    OR reply_correlation_id = @correlation)
            """, connection, transaction);
        AddRunScope(command, sender.ProjectId, sender.RunId);
        command.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, replyTo);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "correlation", NpgsqlDbType.Varchar, (object?)replyCorrelationId ?? DBNull.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    private async Task<ExistingMessage?> ReadExistingMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        CoordinationActor actor,
        string idempotencyKey,
        JsonElement canonicalRequest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT message_id, recipient_session_id, status, request_id,
                canonical_request = @canonical
            FROM {_messages}
            WHERE project_id = @project AND run_id = @run AND sender_session_id = @sender
                AND writer_issuer = @issuer AND writer_subject = @subject AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, sender.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, sender.RunId);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        command.Parameters.AddWithValue("canonical", NpgsqlDbType.Jsonb, canonicalRequest.GetRawText());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ExistingMessage(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4))
            : null;
    }

    private async Task<StoredMessage?> ReadMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT project_id, run_id, sender_session_id, recipient_session_id, thread_id,
                writer_issuer, writer_subject, sender_fence, recipient_fence,
                purpose, kind, delivery_mode, request_id, reply_to_id, status
            FROM {_messages}
            WHERE message_id = @message
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredMessage(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetGuid(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt64(7), reader.GetInt64(8),
                Enum.Parse<AddressedMessagePurpose>(reader.GetString(9)),
                Enum.Parse<AddressedMessageKind>(reader.GetString(10)),
                Enum.Parse<AddressedMessageDeliveryMode>(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetGuid(13),
                reader.GetString(14))
            : null;
    }

    private static bool ReceiptMatches(StoredMessage stored, MessageAdmissionReceipt receipt) =>
        receipt.OwnerMessageId != Guid.Empty &&
        stored.ProjectId == receipt.Sender.ProjectId &&
        stored.RunId == receipt.Sender.RunId &&
        stored.ProjectId == receipt.Recipient.ProjectId &&
        stored.RunId == receipt.Recipient.RunId &&
        stored.SenderSessionId == receipt.Sender.SessionId &&
        stored.RecipientSessionId == receipt.Recipient.SessionId &&
        stored.ThreadId == receipt.ThreadId &&
        stored.SenderFence == receipt.SenderFence &&
        stored.RecipientFence == receipt.RecipientFence &&
        stored.Purpose == receipt.Purpose &&
        stored.RequestId == receipt.RequestId &&
        receipt.Status == AddressedMessageStatus.Accepted &&
        receipt.MessageId != Guid.Empty &&
        receipt.ThreadSequence > 0 &&
        receipt.Claim is null;

    private static bool SameOutboundMessage(
        OwnerOutboundMessage persisted,
        OwnerOutboundMessage supplied) =>
        persisted.OwnerMessageId == supplied.OwnerMessageId &&
        persisted.Sender == supplied.Sender &&
        persisted.Recipient == supplied.Recipient &&
        persisted.IdempotencyKey == supplied.IdempotencyKey &&
        persisted.DeliveryMode == supplied.DeliveryMode &&
        persisted.Purpose == supplied.Purpose &&
        persisted.Kind == supplied.Kind &&
        JsonElement.DeepEquals(persisted.Payload, supplied.Payload) &&
        persisted.SenderFence == supplied.SenderFence &&
        persisted.RecipientFence == supplied.RecipientFence &&
        persisted.ThreadId == supplied.ThreadId &&
        persisted.ReplyToId == supplied.ReplyToId &&
        persisted.RequestId == supplied.RequestId &&
        persisted.ReplyCorrelationId == supplied.ReplyCorrelationId &&
        persisted.UserQuote == supplied.UserQuote &&
        persisted.CoordinatorInstructions == supplied.CoordinatorInstructions;

    private static void RequireWriter(SessionRow session, CoordinationActor actor)
    {
        if (session.WriterIssuer != actor.Issuer || session.WriterSubject != actor.Subject)
            throw new CoordinationException("session_writer_mismatch", StatusCodes.Status403Forbidden);
    }

    private static void RequireCurrentActiveSession(SessionRow session, AcceptedRunRow run)
    {
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        if (session.LifecycleState != "active")
            throw new CoordinationException("session_unavailable", StatusCodes.Status409Conflict);
    }

    private static void ValidateSelection(EffectiveRunSelection selection)
    {
        if (selection.ProjectRevision < 1 || selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 || string.IsNullOrWhiteSpace(selection.ContextRevision) ||
            selection.Snapshot.ValueKind != JsonValueKind.Object)
            throw new CoordinationException("accepted_run_selection_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(selection.ProjectId, "project_id");
        CoordinationIdentity.ValidateIdentity(selection.RunId, "run_id");
    }

    private static void AddRunScope(NpgsqlCommand command, string projectId, string runId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
    }

    private static void ValidateSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 63 ||
            schema[0] is < 'a' or > 'z' ||
            schema.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_') ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
    }

    private sealed record AcceptedRunRow(
        string SelectionHash,
        string AcceptedIssuer,
        string AcceptedSubject,
        long Fence,
        long LogicalTurnOrdinal,
        string ExecutionState,
        long StateVersion,
        string TenantId);

    private sealed record SessionRow(
        string SessionId,
        string? ParentSessionId,
        string WriterIssuer,
        string WriterSubject,
        long ExecutionFence,
        string LifecycleState,
        long LogicalTurnOrdinal,
        string TurnState,
        long StateVersion,
        bool PendingWake,
        long? TurnBoundaryRequestVersion,
        string? TurnBoundaryResult);

    private sealed record ExistingMessage(
        Guid MessageId,
        string RecipientSessionId,
        string Status,
        string? RequestId,
        bool CanonicalRequestMatches);

    private sealed record StoredMessage(
        string ProjectId,
        string RunId,
        string SenderSessionId,
        string RecipientSessionId,
        Guid ThreadId,
        string WriterIssuer,
        string WriterSubject,
        long SenderFence,
        long RecipientFence,
        AddressedMessagePurpose Purpose,
        AddressedMessageKind Kind,
        AddressedMessageDeliveryMode DeliveryMode,
        string? RequestId,
        Guid? ReplyToId,
        string Status);
}
