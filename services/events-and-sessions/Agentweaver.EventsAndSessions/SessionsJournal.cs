using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

public sealed class PostgresSessionsJournal : ISessionsJournal
{
    public const int MaximumPageSize = 200;
    public const int MaximumSubscriptionEvents = 10_000;
    public const int MaximumSubscriptionSeconds = 300;
    private const string InboxConsumer = "events-and-sessions.session-events";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _sessions;
    private readonly string _providerBindings;
    private readonly string _runStreams;
    private readonly string _events;
    private readonly string _objectReferences;
    private readonly PostgresOutbox _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _referenceRetention;

    public PostgresSessionsJournal(
        NpgsqlDataSource dataSource,
        PostgresSessionsProviderOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _dataSource = dataSource;
        _schema = $"\"{options.Schema}\"";
        _sessions = $"{_schema}.sessions";
        _providerBindings = $"{_schema}.session_provider_bindings";
        _runStreams = $"{_schema}.session_run_streams";
        _events = $"{_schema}.session_events";
        _objectReferences = $"{_schema}.session_object_references";
        _outbox = new PostgresOutbox(dataSource, options.Schema);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds);
        _referenceRetention = TimeSpan.FromDays(options.ReferenceRetentionDays);
    }

    public async Task<SessionRecord> CreateSessionAsync(
        ClaimsPrincipal principal,
        string sessionId,
        SessionProviderBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var scope = RequireScope(principal);
        var identity = scope.ForSession(sessionId);
        if (binding.ProjectId != identity.ProjectId || binding.RunId != identity.RunId)
            throw new SessionAccessDeniedException("The pinned provider belongs to a different project or run.");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insertBinding = new NpgsqlCommand($"""
            INSERT INTO {_providerBindings}
                (project_id, run_id, provider_id, adapter_version, options_schema_version,
                 options_revision, resource_id, resource_generation, negotiated_capabilities)
            VALUES
                (@project, @run, @provider, @version, @schema_version,
                 @revision, @resource, @generation, @capabilities)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection, transaction))
        {
            AddBindingIdentity(insertBinding, binding);
            insertBinding.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, binding.ProviderId);
            insertBinding.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, binding.AdapterVersion.ToString());
            insertBinding.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, binding.OptionsSchemaVersion);
            insertBinding.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, binding.OptionsRevision);
            insertBinding.Parameters.AddWithValue("resource", NpgsqlDbType.Varchar, binding.ResourceId);
            insertBinding.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, binding.ResourceGeneration);
            var capabilities = JsonSerializer.Serialize(
                binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal), JsonOptions);
            insertBinding.Parameters.AddWithValue("capabilities", NpgsqlDbType.Jsonb, capabilities);
            await insertBinding.ExecuteNonQueryAsync(cancellationToken);
        }

        var storedBinding = await ReadProviderBindingAsync(
            connection, transaction, identity.ProjectId, identity.RunId, lockRow: true, cancellationToken);
        if (storedBinding is null)
            throw new InvalidOperationException("A provider binding was not persisted for the session run.");
        if (!binding.Matches(storedBinding))
            throw new SessionProviderBindingConflictException(
                "The run already has a different immutable Sessions provider binding.");

        await using (var insertRunStream = new NpgsqlCommand($"""
            INSERT INTO {_runStreams} (project_id, run_id)
            VALUES (@project, @run)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection, transaction))
        {
            insertRunStream.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
            insertRunStream.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
            await insertRunStream.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_sessions} (session_id, project_id, run_id)
            VALUES (@session, @project, @run)
            ON CONFLICT (project_id, run_id, session_id) DO NOTHING
            RETURNING created_at
            """, connection, transaction);
        AddIdentity(insert, identity);
        await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var created = reader.GetFieldValue<DateTimeOffset>(0);
            await reader.DisposeAsync();
            var runPosition = await ReadRunPositionAsync(
                connection, transaction, identity.ProjectId, identity.RunId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SessionRecord(identity, created, runPosition);
        }
        await reader.DisposeAsync();

        var existing = await ReadSessionAsync(connection, transaction, identity, lockRow: false, cancellationToken);
        if (existing is null)
            throw new InvalidOperationException("A session identity conflict occurred without a stored session.");
        if (existing.Identity != identity)
            throw new SessionAccessDeniedException("The session belongs to a different project or run.");
        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    public async Task<SessionProviderBinding> GetProviderBindingAsync(
        ClaimsPrincipal principal,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var identity = RequireScope(principal).ForSession(sessionId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var session = await ReadSessionAsync(connection, null, identity, lockRow: false, cancellationToken);
        RequireOwnedSession(session, identity);
        var binding = await ReadProviderBindingAsync(
            connection, null, identity.ProjectId, identity.RunId, lockRow: false, cancellationToken);
        return binding ?? throw new InvalidOperationException(
            "A stored session does not have its immutable provider binding.");
    }

    public async Task<SessionProviderBinding> GetRunProviderBindingAsync(
        ClaimsPrincipal principal,
        string projectId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireScope(principal);
        RequireRequestedRun(scope, projectId, runId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var binding = await ReadProviderBindingAsync(
            connection, null, projectId, runId, lockRow: false, cancellationToken);
        return binding ?? throw new SessionNotFoundException("The project run does not exist.");
    }

    public async Task<SessionAppendResult> AppendAsync(
        ClaimsPrincipal principal,
        string sessionId,
        AppendSessionEvent input,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireScope(principal);
        var identity = scope.ForSession(sessionId);
        ValidateInput(input);
        if (input.Payload is PolicyEvaluationSessionPayload)
            throw new SessionAccessDeniedException(
                "Policy evaluation events require trusted Orchestrator Core writer provenance.");
        return await AppendCoreAsync(identity, input, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SessionAppendResult> AppendPolicyEvaluationAsync(
        ClaimsPrincipal principal,
        string sessionId,
        PolicyEvaluationReceiptView receipt,
        Func<CancellationToken, Task> validateBeforeCommit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(validateBeforeCommit);
        var identity = RequireScope(principal).ForSession(sessionId);
        var subjects = principal.FindAll("sub").Take(2).ToArray();
        var issuers = principal.FindAll("iss").Take(2).ToArray();
        if (receipt.ReceiptId == Guid.Empty ||
            receipt.Identity != identity ||
            subjects.Length != 1 ||
            issuers.Length != 1 ||
            receipt.Evidence.ActorId != subjects[0].Value ||
            receipt.Issuer != issuers[0].Value)
            throw new SessionAccessDeniedException(
                "The policy receipt does not match the authenticated session caller.");

        var input = new AppendSessionEvent(
            receipt.ReceiptId,
            SessionsContractVersions.CurrentSchemaVersion,
            SessionsContractVersions.PolicyEvaluationEventVersion,
            receipt.Evidence);
        ValidateInput(input);
        return await AppendCoreAsync(
            identity, input, cancellationToken, validateBeforeCommit).ConfigureAwait(false);
    }

    private async Task<SessionAppendResult> AppendCoreAsync(
        SessionIdentity identity,
        AppendSessionEvent input,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateBeforeCommit = null)
    {
        var canonicalInput = CreateCanonicalInput(identity, input);
        var payload = JsonSerializer.SerializeToElement<SessionEventPayload>(input.Payload, JsonOptions);
        var references = SessionEventPayloadValidation.ValidateAndGetReferences(input.Payload);
        var scopedEvent = CreateScopedEventIdentity(identity, input.EventId);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var session = await ReadSessionAsync(connection, transaction, identity, lockRow: false, cancellationToken);
        if (session is null)
            throw new SessionNotFoundException("The session does not exist.");
        if (session.Identity != identity)
            throw new SessionAccessDeniedException("The session belongs to a different project or run.");

        var admission = await _outbox.AdmitAsync(
            connection, transaction, InboxConsumer, scopedEvent.MessageId, cancellationToken);
        if (admission == InboxAdmission.Duplicate)
        {
            var duplicate = await ReadEventByIdAsync(
                connection, transaction, identity.ProjectId, identity.RunId, input.EventId, cancellationToken);
            if (duplicate is null)
                throw new InvalidOperationException("An inbox receipt exists without its session event.");
            await using var compare = new NpgsqlCommand($"""
                SELECT canonical_input = @input FROM {_events}
                WHERE project_id = @project AND run_id = @run AND event_id = @event
                """, connection, transaction);
            compare.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
            compare.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
            compare.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, canonicalInput.GetRawText());
            compare.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, input.EventId);
            if (await compare.ExecuteScalarAsync(cancellationToken) is not true)
                throw new SessionEventConflictException(
                    "The event identity was already used for a different immutable event.");
            if (validateBeforeCommit is not null)
                await validateBeforeCommit(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken);
            return new SessionAppendResult(duplicate, IsDuplicate: true);
        }

        var position = await AdvanceRunPositionAsync(
            connection, transaction, identity.ProjectId, identity.RunId, cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();
        var occurredAt = new DateTimeOffset(utcNow.Ticks - utcNow.Ticks % 10, TimeSpan.Zero);
        var retainedUntil = occurredAt + _referenceRetention;
        var storedReferences = references.Select(reference => new StoredSessionObjectReference(
            reference,
            new SessionObjectRetention(scopedEvent.RetentionOwnerId, retainedUntil)))
            .ToImmutableArray();
        var envelope = new SessionEventEnvelope(
            input.SchemaVersion, input.EventVersion, input.EventId, identity,
            position, occurredAt, input.Payload, storedReferences);
        var referencesJson = JsonSerializer.SerializeToElement(storedReferences, JsonOptions);

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_events}
                (event_id, project_id, run_id, session_id, position, schema_version, event_version,
                 event_kind, occurred_at, payload, canonical_input, object_references)
            VALUES
                (@event, @project, @run, @session, @position, @schema_version, @event_version,
                 @kind, @occurred, @payload, @canonical, @references)
            """, connection, transaction))
        {
            AddIdentity(insert, identity);
            insert.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, input.EventId);
            insert.Parameters.AddWithValue("position", NpgsqlDbType.Bigint, position);
            insert.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, input.SchemaVersion);
            insert.Parameters.AddWithValue("event_version", NpgsqlDbType.Integer, input.EventVersion);
            insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar,
                SessionEventPayloadValidation.KindOf(input.Payload).ToString());
            insert.Parameters.AddWithValue("occurred", NpgsqlDbType.TimestampTz, occurredAt);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload.GetRawText());
            insert.Parameters.AddWithValue("canonical", NpgsqlDbType.Jsonb, canonicalInput.GetRawText());
            insert.Parameters.AddWithValue("references", NpgsqlDbType.Jsonb, referencesJson.GetRawText());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var reference in storedReferences)
        {
            await using var insertReference = new NpgsqlCommand($"""
                INSERT INTO {_objectReferences}
                    (project_id, run_id, event_id, object_key, purpose, byte_length, retention_owner_id, retain_until)
                VALUES (@project, @run, @event, @key, @purpose, @length, @owner, @retain)
                """, connection, transaction);
            insertReference.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
            insertReference.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
            insertReference.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, input.EventId);
            insertReference.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, reference.Reference.Key.Value);
            insertReference.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, reference.Reference.Purpose);
            insertReference.Parameters.AddWithValue("length", NpgsqlDbType.Bigint,
                (object?)reference.Reference.ByteLength ?? DBNull.Value);
            insertReference.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, reference.Retention.OwnerId);
            insertReference.Parameters.AddWithValue("retain", NpgsqlDbType.TimestampTz, reference.Retention.RetainUntil);
            await insertReference.ExecuteNonQueryAsync(cancellationToken);
        }

        var outboxPayload = JsonSerializer.SerializeToElement(envelope, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            scopedEvent.OutboxId,
            $"sessions/{identity.ProjectId}/{identity.RunId}",
            scopedEvent.MessageId,
            $"sessions.event.{SessionEventPayloadValidation.KindOf(input.Payload)}",
            input.EventVersion,
            outboxPayload,
            occurredAt), cancellationToken);

        if (validateBeforeCommit is not null)
            await validateBeforeCommit(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return new SessionAppendResult(envelope, IsDuplicate: false);
    }

    internal async Task<SessionEventEnvelope> AppendAddressedMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AddressedMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);
        if (connection.State != System.Data.ConnectionState.Open ||
            !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied open connection.", nameof(transaction));
        if (message.Sender.ProjectId != message.Recipient.ProjectId ||
            message.MessageId == Guid.Empty ||
            message.ThreadSequence < 1)
            throw new ArgumentException("The addressed message journal identity is invalid.", nameof(message));

        var session = await ReadSessionAsync(
            connection, transaction, message.Sender, lockRow: false, cancellationToken).ConfigureAwait(false);
        RequireOwnedSession(session, message.Sender);

        var payload = new AddressedMessageSessionPayload(
            message.MessageId,
            message.Sender,
            message.Recipient,
            message.ThreadId,
            message.ThreadSequence,
            message.Purpose);
        var input = new AppendSessionEvent(
            message.MessageId,
            SessionsContractVersions.CurrentSchemaVersion,
            SessionsContractVersions.CurrentEventVersion,
            payload);
        ValidateInput(input);
        var canonicalInput = CreateCanonicalInput(message.Sender, input);
        var admission = await _outbox.AdmitAsync(
            connection,
            transaction,
            InboxConsumer,
            CreateScopedEventIdentity(message.Sender, message.MessageId).MessageId,
            cancellationToken).ConfigureAwait(false);
        if (admission == InboxAdmission.Duplicate)
        {
            var duplicate = await ReadEventByIdAsync(
                connection, transaction, message.Sender.ProjectId, message.Sender.RunId,
                message.MessageId, cancellationToken).ConfigureAwait(false);
            if (duplicate is null)
                throw new InvalidOperationException("An inbox receipt exists without its addressed-message event.");
            await using var compare = new NpgsqlCommand($"""
                SELECT canonical_input = @input FROM {_events}
                WHERE project_id = @project AND run_id = @run AND event_id = @event
                """, connection, transaction);
            compare.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, message.Sender.ProjectId);
            compare.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, message.Sender.RunId);
            compare.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, canonicalInput.GetRawText());
            compare.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, message.MessageId);
            if (await compare.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new SessionEventConflictException(
                    "The addressed-message event identity was already used for different content.");
            return duplicate;
        }

        var position = await AdvanceRunPositionAsync(
            connection,
            transaction,
            message.Sender.ProjectId,
            message.Sender.RunId,
            cancellationToken).ConfigureAwait(false);
        var occurredAt = NormalizeTimestamp(message.CreatedAt);
        var envelope = new SessionEventEnvelope(
            SessionsContractVersions.CurrentSchemaVersion,
            SessionsContractVersions.CurrentEventVersion,
            message.MessageId,
            message.Sender,
            position,
            occurredAt,
            payload,
            []);
        var payloadJson = JsonSerializer.SerializeToElement<SessionEventPayload>(payload, JsonOptions);
        var referencesJson = JsonSerializer.SerializeToElement(
            ImmutableArray<StoredSessionObjectReference>.Empty, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_events}
                (event_id, project_id, run_id, session_id, position, schema_version, event_version,
                 event_kind, occurred_at, payload, canonical_input, object_references)
            VALUES
                (@event, @project, @run, @session, @position, @schema_version, @event_version,
                 @kind, @occurred, @payload, @canonical, @references)
            """, connection, transaction))
        {
            AddIdentity(insert, message.Sender);
            insert.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, message.MessageId);
            insert.Parameters.AddWithValue("position", NpgsqlDbType.Bigint, position);
            insert.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, envelope.SchemaVersion);
            insert.Parameters.AddWithValue("event_version", NpgsqlDbType.Integer, envelope.EventVersion);
            insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, SessionEventKind.AddressedMessage.ToString());
            insert.Parameters.AddWithValue("occurred", NpgsqlDbType.TimestampTz, occurredAt);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson.GetRawText());
            insert.Parameters.AddWithValue("canonical", NpgsqlDbType.Jsonb, canonicalInput.GetRawText());
            insert.Parameters.AddWithValue("references", NpgsqlDbType.Jsonb, referencesJson.GetRawText());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return envelope;
    }

    public async Task<SessionEventPage> ReplayAsync(
        ClaimsPrincipal principal,
        SessionEventPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = RequireScope(principal);
        var identity = scope.ForSession(request.SessionId);
        ValidatePageRequest(request);
        var afterPosition = DecodeCursor(request.Cursor, identity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var session = await ReadSessionAsync(connection, null, identity, lockRow: false, cancellationToken);
        RequireOwnedSession(session, identity);

        return await ReadEventPageAsync(
            connection, scope, identity.SessionId, afterPosition, request.Limit, request.Cursor, cancellationToken);
    }

    public async Task<SessionEventPage> ReplayRunAsync(
        ClaimsPrincipal principal,
        SessionRunEventPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = RequireScope(principal);
        RequireRequestedRun(scope, request.ProjectId, request.RunId);
        ValidatePageRequest(request.Limit);
        var afterPosition = DecodeRunCursor(request.Cursor, scope);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        _ = await ReadRunPositionAsync(
            connection, null, scope.ProjectId, scope.RunId, cancellationToken);
        return await ReadEventPageAsync(
            connection, scope, null, afterPosition, request.Limit, request.Cursor, cancellationToken);
    }

    private async Task<SessionEventPage> ReadEventPageAsync(
        NpgsqlConnection connection,
        SessionRunScope scope,
        string? sessionId,
        long afterPosition,
        int limit,
        string? requestCursor,
        CancellationToken cancellationToken)
    {
        var sessionFilter = sessionId is null ? string.Empty : "AND session_id = @session";
        await using var command = new NpgsqlCommand($"""
            SELECT event_id, project_id, run_id, session_id, position, schema_version, event_version,
                occurred_at, payload, object_references
            FROM {_events}
            WHERE project_id = @project AND run_id = @run AND position > @after
                {sessionFilter}
            ORDER BY position
            LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, scope.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, scope.RunId);
        if (sessionId is not null)
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, afterPosition);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit + 1);
        var events = new List<SessionEventEnvelope>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                events.Add(ReadEvent(reader));

        var hasMore = events.Count > limit;
        if (hasMore)
            events.RemoveAt(events.Count - 1);
        var immutable = events.ToImmutableArray();
        var next = immutable.Length == 0
            ? requestCursor
            : sessionId is null
                ? EncodeRunCursor(scope, immutable[^1].Position)
                : EncodeCursor(scope.ForSession(sessionId), immutable[^1].Position);
        return new SessionEventPage(immutable, next, hasMore);
    }

    public async IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
        ClaimsPrincipal principal,
        SessionSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSubscriptionBounds(request.MaximumEvents, request.MaximumDurationSeconds);

        var cursor = request.Cursor;
        var emitted = 0;
        var deadline = _timeProvider.GetUtcNow().AddSeconds(request.MaximumDurationSeconds);
        while (emitted < request.MaximumEvents && _timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReplayAsync(principal,
                new SessionEventPageRequest(request.SessionId, cursor, Math.Min(100, request.MaximumEvents - emitted)),
                cancellationToken);
            if (page.Events.Length > 0)
            {
                foreach (var item in page.Events)
                {
                    yield return new SessionEventDelivery(
                        item, EncodeCursor(item.Identity, item.Position));
                    emitted++;
                    if (emitted >= request.MaximumEvents)
                        yield break;
                }
                cursor = page.NextCursor;
                continue;
            }
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
        }
    }

    public async IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
        ClaimsPrincipal principal,
        SessionRunSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = RequireScope(principal);
        RequireRequestedRun(scope, request.ProjectId, request.RunId);
        ValidateSubscriptionBounds(request.MaximumEvents, request.MaximumDurationSeconds);

        var cursor = request.Cursor;
        var emitted = 0;
        var deadline = _timeProvider.GetUtcNow().AddSeconds(request.MaximumDurationSeconds);
        while (emitted < request.MaximumEvents && _timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReplayRunAsync(principal,
                new SessionRunEventPageRequest(
                    scope.ProjectId, scope.RunId, cursor,
                    Math.Min(100, request.MaximumEvents - emitted)),
                cancellationToken);
            if (page.Events.Length > 0)
            {
                foreach (var item in page.Events)
                {
                    yield return new SessionEventDelivery(
                        item, EncodeRunCursor(scope, item.Position));
                    emitted++;
                    if (emitted >= request.MaximumEvents)
                        yield break;
                }
                cursor = page.NextCursor;
                continue;
            }
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
        }
    }

    private async Task<SessionRecord?> ReadSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT s.project_id, s.run_id, s.session_id, s.created_at, r.last_position
            FROM {_sessions} AS s
            JOIN {_runStreams} AS r USING (project_id, run_id)
            WHERE s.project_id = @project AND s.run_id = @run AND s.session_id = @session
            {(lockRow ? "FOR UPDATE OF s" : string.Empty)}
            """, connection, transaction);
        AddIdentity(command, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new SessionRecord(
            new SessionIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt64(4));
    }

    private async Task<SessionProviderBinding?> ReadProviderBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT provider_id, adapter_version, options_schema_version, options_revision,
                resource_id, resource_generation, negotiated_capabilities
            FROM {_providerBindings}
            WHERE project_id = @project AND run_id = @run
            {(lockRow ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var capabilities = JsonSerializer.Deserialize<ImmutableHashSet<string>>(
            reader.GetString(6), JsonOptions)
            ?? throw new InvalidOperationException("A stored provider capability set is invalid.");
        return new SessionProviderBinding(
            projectId,
            runId,
            reader.GetString(0),
            Version.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            capabilities);
    }

    private async Task<long> ReadRunPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT last_position FROM {_runStreams}
            WHERE project_id = @project AND run_id = @run
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long position
            ? position
            : throw new SessionNotFoundException("The project run does not exist.");
    }

    private async Task<long> AdvanceRunPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {_runStreams}
            SET last_position = last_position + 1
            WHERE project_id = @project AND run_id = @run
            RETURNING last_position
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long position
            ? position
            : throw new SessionNotFoundException("The project run does not exist.");
    }

    private async Task<SessionEventEnvelope?> ReadEventByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT event_id, project_id, run_id, session_id, position, schema_version, event_version,
                occurred_at, payload, object_references
            FROM {_events}
            WHERE project_id = @project AND run_id = @run AND event_id = @event
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private static SessionEventEnvelope ReadEvent(NpgsqlDataReader reader)
    {
        var identity = new SessionIdentity(reader.GetString(1), reader.GetString(2), reader.GetString(3));
        var payload = JsonSerializer.Deserialize<SessionEventPayload>(reader.GetString(8), JsonOptions)
            ?? throw new InvalidOperationException("A stored session event payload is invalid.");
        var references = JsonSerializer.Deserialize<ImmutableArray<StoredSessionObjectReference>>(
            reader.GetString(9), JsonOptions);
        return new SessionEventEnvelope(
            reader.GetInt32(5), reader.GetInt32(6), reader.GetGuid(0), identity,
            reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(7), payload, references);
    }

    private static void ValidateInput(AppendSessionEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.EventId == Guid.Empty)
            throw new ArgumentException("A stable non-empty event identity is required.", nameof(input));
        if (input.SchemaVersion != SessionsContractVersions.CurrentSchemaVersion ||
            !SessionEventPayloadValidation.SupportsEventVersion(input.EventVersion, input.Payload))
            throw new SessionContractVersionException("The event contract version is not supported.");
        _ = SessionEventPayloadValidation.ValidateAndGetReferences(input.Payload);
    }

    private static JsonElement CreateCanonicalInput(SessionIdentity identity, AppendSessionEvent input) =>
        JsonSerializer.SerializeToElement(new CanonicalInput(
            input.SchemaVersion, input.EventVersion, input.EventId,
            identity.ProjectId, identity.RunId, identity.SessionId, input.Payload), JsonOptions);

    private static ScopedEventIdentity CreateScopedEventIdentity(SessionIdentity identity, Guid eventId)
    {
        var identityBytes = Encoding.UTF8.GetBytes(
            $"{identity.ProjectId}\0{identity.RunId}\0{eventId:D}");
        var digest = SHA256.HashData(identityBytes);
        var scopedId = Convert.ToHexString(digest);
        return new ScopedEventIdentity(
            new Guid(digest.AsSpan(0, 16)),
            $"sessions:{scopedId}",
            scopedId);
    }

    private static void ValidatePageRequest(SessionEventPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePageRequest(request.Limit);
    }

    private static void ValidatePageRequest(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumPageSize);
    }

    private static void ValidateSubscriptionBounds(int maximumEvents, int maximumDurationSeconds)
    {
        if (maximumEvents is < 1 or > MaximumSubscriptionEvents)
            throw new ArgumentOutOfRangeException(nameof(maximumEvents),
                "Live subscriptions are bounded to 10,000 events.");
        if (maximumDurationSeconds is < 1 or > MaximumSubscriptionSeconds)
            throw new ArgumentOutOfRangeException(nameof(maximumDurationSeconds),
                "Live subscriptions are bounded to five minutes.");
    }

    private static long DecodeCursor(string? cursor, SessionIdentity identity) =>
        DecodeCursor(cursor, identity.ProjectId, identity.RunId, identity.SessionId);

    private static long DecodeRunCursor(string? cursor, SessionRunScope scope) =>
        DecodeCursor(cursor, scope.ProjectId, scope.RunId, sessionId: null);

    private static long DecodeCursor(
        string? cursor, string projectId, string runId, string? sessionId)
    {
        if (string.IsNullOrEmpty(cursor))
            return 0;
        if (cursor.Length > 2048)
            throw new ArgumentException("The replay cursor is invalid.", nameof(cursor));
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var data = JsonSerializer.Deserialize<CursorData>(Convert.FromBase64String(encoded), JsonOptions);
            if (data is null || data.Position < 0 || data.ProjectId != projectId ||
                data.RunId != runId || data.SessionId != sessionId)
                throw new ArgumentException("The replay cursor does not belong to this event stream.", nameof(cursor));
            return data.Position;
        }
        catch (FormatException)
        {
            throw new ArgumentException("The replay cursor is invalid.", nameof(cursor));
        }
        catch (JsonException)
        {
            throw new ArgumentException("The replay cursor is invalid.", nameof(cursor));
        }
    }

    private static string EncodeCursor(SessionIdentity identity, long position)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new CursorData(identity.ProjectId, identity.RunId, identity.SessionId, position), JsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string EncodeRunCursor(SessionRunScope scope, long position)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new CursorData(scope.ProjectId, scope.RunId, null, position), JsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static SessionRunScope RequireScope(ClaimsPrincipal principal)
    {
        return RequireScope(principal, out _);
    }

    private static SessionRunScope RequireScope(ClaimsPrincipal principal, out string? actorId)
    {
        if (!SessionIdentityClaims.TryGetScope(principal, out var scope, out actorId) || scope is null)
            throw new SessionAuthenticationException();
        return scope.Value;
    }

    private static void RequireRequestedRun(SessionRunScope scope, string projectId, string runId)
    {
        if (scope.ProjectId != projectId || scope.RunId != runId)
            throw new SessionAccessDeniedException("The requested event stream belongs to a different project or run.");
    }

    private static void RequireOwnedSession(SessionRecord? session, SessionIdentity identity)
    {
        if (session is null)
            throw new SessionNotFoundException("The session does not exist.");
        if (session.Identity != identity)
            throw new SessionAccessDeniedException("The session belongs to a different project or run.");
    }

    private static void AddIdentity(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
    }

    private static void AddBindingIdentity(NpgsqlCommand command, SessionProviderBinding binding)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private sealed record CursorData(string ProjectId, string RunId, string? SessionId, long Position);

    private sealed record ScopedEventIdentity(Guid OutboxId, string MessageId, string RetentionOwnerId);

    private sealed record CanonicalInput(
        int SchemaVersion,
        int EventVersion,
        Guid EventId,
        string ProjectId,
        string RunId,
        string SessionId,
        SessionEventPayload Payload);
}
