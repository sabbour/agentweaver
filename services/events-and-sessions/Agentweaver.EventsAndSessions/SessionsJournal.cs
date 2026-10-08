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
    private const int CursorVersion = 1;
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
    private readonly string _forkLineage;
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
        _forkLineage = $"{_schema}.session_fork_lineage";
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
        if (SessionEventPayloadValidation.ValidateAndGetReferences(input.Payload).Any(
            reference => reference.Material is not null))
            throw new SessionAccessDeniedException("Session material requires its authenticated execution producer.");
        return await AppendCoreAsync(identity, input, cancellationToken).ConfigureAwait(false);
    }

    internal Task<SessionAppendResult> AppendMaterialAsync(
        ClaimsPrincipal principal, string sessionId, AppendSessionEvent input,
        Func<CancellationToken, Task> persistMaterial,
        Func<CancellationToken, Task> validateBeforeCommit,
        CancellationToken cancellationToken)
    {
        ValidateInput(input);
        if (input.Payload is not (TurnSessionPayload or CacheReferenceSessionPayload) ||
            SessionEventPayloadValidation.ValidateAndGetReferences(input.Payload) is not
                [{ Material: not null }])
            throw new ArgumentException("Only typed execution turn content or SDK cache material is accepted.");
        return AppendCoreAsync(RequireScope(principal).ForSession(sessionId), input,
            cancellationToken, validateBeforeCommit, persistMaterial);
    }

    internal async Task<SessionEventEnvelope> ReadMaterialEventAsync(
        ClaimsPrincipal principal, string sessionId, Guid eventId, CancellationToken cancellationToken)
    {
        var identity = RequireScope(principal).ForSession(sessionId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var envelope = await ReadEventByIdAsync(connection, null, identity.ProjectId, identity.RunId,
            eventId, cancellationToken).ConfigureAwait(false);
        if (envelope is null || envelope.Identity != identity)
            throw new SessionNotFoundException("The recorded session material event does not exist.");
        return envelope;
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
        Func<CancellationToken, Task>? validateBeforeCommit = null,
        Func<CancellationToken, Task>? persistMaterial = null)
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
            if (persistMaterial is not null)
                await persistMaterial(cancellationToken).ConfigureAwait(false);
            if (validateBeforeCommit is not null)
                await validateBeforeCommit(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken);
            return new SessionAppendResult(duplicate, IsDuplicate: true);
        }

        if (persistMaterial is not null)
            await persistMaterial(cancellationToken).ConfigureAwait(false);
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

    public async Task<SessionForkResult> ForkFromExplicitEventAsync(
        ClaimsPrincipal principal,
        string sourceSessionId,
        SessionForkRequest request,
        Func<CancellationToken, Task> validateAdmission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validateAdmission);
        var scope = RequireScope(principal, out var actorId);
        var source = scope.ForSession(sourceSessionId);
        var targetIdentity = scope.ForSession(request.TargetSessionId);
        ValidateForkRequest(source, targetIdentity, request);
        if (actorId is null)
            throw new SessionAuthenticationException();
        var sourcePosition = DecodeCursor(request.SourceCursor, source);
        if (sourcePosition < 1)
            throw new ArgumentException("A fork cursor must identify a committed event.", nameof(request));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sourceSession = await ReadSessionAsync(
            connection, transaction, source, lockRow: true, cancellationToken).ConfigureAwait(false);
        RequireOwnedSession(sourceSession, source);
        var binding = await ReadProviderBindingAsync(
            connection, transaction, source.ProjectId, source.RunId, lockRow: true, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null ||
            binding.ProviderId != NativePostgresSessionsProvider.ProviderId ||
            !binding.NegotiatedCapabilities.Contains(SessionsCapabilities.Fork))
            throw new SessionForkUnsupportedException(
                "The pinned Sessions provider does not support explicit-event forks.");

        var sourceEvent = await ReadEventByIdAsync(
            connection, transaction, source.ProjectId, source.RunId, request.SourceEventId, cancellationToken)
            .ConfigureAwait(false);
        if (sourceEvent is null || sourceEvent.Identity != source || sourceEvent.Position != sourcePosition)
            throw new SessionEventConflictException(
                "The source event and cursor do not identify the same committed event.");
        if (!SessionEventPayloadValidation.SupportsEventVersion(
                sourceEvent.EventVersion, sourceEvent.Payload))
            throw new SessionContractVersionException(
                "The committed source event version cannot be read by this Sessions journal.");

        var providerBindingHash = HashProviderBinding(binding);
        var commandHash = HashForkRequest(source, request, sourcePosition, providerBindingHash);
        var replay = await ReadExistingForkAsync(
            connection, transaction, source, actorId, request.IdempotencyKey, commandHash, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            await validateAdmission(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        var existingTarget = await ReadSessionAsync(
            connection, transaction, targetIdentity, lockRow: true, cancellationToken).ConfigureAwait(false);
        if (existingTarget is not null)
            throw new SessionEventConflictException("The target session identity is already in use.");

        DateTimeOffset createdAt;
        await using (var createTarget = new NpgsqlCommand($"""
            INSERT INTO {_sessions} (session_id, project_id, run_id)
            VALUES (@session, @project, @run)
            ON CONFLICT (project_id, run_id, session_id) DO NOTHING
            RETURNING created_at
            """, connection, transaction))
        {
            AddIdentity(createTarget, targetIdentity);
            await using var reader = await createTarget.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new SessionEventConflictException("The target session identity is already in use.");
            createdAt = reader.GetFieldValue<DateTimeOffset>(0);
        }

        var lineage = new SessionForkLineage(
            source,
            sourceEvent.EventId,
            sourceEvent.Position,
            CursorVersion,
            sourceEvent.SchemaVersion,
            sourceEvent.EventVersion,
            providerBindingHash,
            request.SourceCursor);
        await using (var insertLineage = new NpgsqlCommand($"""
            INSERT INTO {_forkLineage}
                (project_id, run_id, target_session_id, source_session_id, source_event_id,
                 source_position, cursor_version, source_schema_version, source_event_version,
                 provider_binding_hash, source_cursor, actor_subject, idempotency_key, command_hash)
            VALUES (@project, @run, @target, @source, @event, @position, @cursorVersion,
                @schemaVersion, @eventVersion, @bindingHash, @cursor, @actor, @key, @hash)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddIdentity(insertLineage, targetIdentity);
            insertLineage.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, targetIdentity.SessionId);
            insertLineage.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, source.SessionId);
            insertLineage.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, sourceEvent.EventId);
            insertLineage.Parameters.AddWithValue("position", NpgsqlDbType.Bigint, sourceEvent.Position);
            insertLineage.Parameters.AddWithValue("cursorVersion", NpgsqlDbType.Integer, CursorVersion);
            insertLineage.Parameters.AddWithValue("schemaVersion", NpgsqlDbType.Integer, sourceEvent.SchemaVersion);
            insertLineage.Parameters.AddWithValue("eventVersion", NpgsqlDbType.Integer, sourceEvent.EventVersion);
            insertLineage.Parameters.AddWithValue("bindingHash", NpgsqlDbType.Char, providerBindingHash);
            insertLineage.Parameters.AddWithValue("cursor", NpgsqlDbType.Varchar, request.SourceCursor);
            insertLineage.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actorId);
            insertLineage.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, request.IdempotencyKey);
            insertLineage.Parameters.AddWithValue("hash", NpgsqlDbType.Char, commandHash);
            if (await insertLineage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                var concurrentReplay = await ReadExistingForkAsync(
                    connection,
                    transaction,
                    source,
                    actorId,
                    request.IdempotencyKey,
                    commandHash,
                    cancellationToken).ConfigureAwait(false);
                if (concurrentReplay is null)
                    throw new SessionEventConflictException("The target session identity is already in use.");
                await validateAdmission(cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return concurrentReplay;
            }
        }

        var lastPosition = await ReadRunPositionAsync(
            connection, transaction, targetIdentity.ProjectId, targetIdentity.RunId, cancellationToken)
            .ConfigureAwait(false);
        await validateAdmission(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionForkResult(
            new SessionRecord(targetIdentity, createdAt, lastPosition),
            lineage,
            IsDuplicate: false);
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
        var queryText = sessionId is null
            ? $"""
                SELECT event_id, project_id, run_id, session_id, position, schema_version, event_version,
                    occurred_at, payload, object_references
                FROM {_events}
                WHERE project_id = @project AND run_id = @run AND position > @after
                ORDER BY position
                LIMIT @limit
                """
            : $"""
                WITH RECURSIVE visible_sessions(session_id, lower_position, upper_position) AS (
                    SELECT @session, COALESCE(fork.source_position, 0)::bigint, run.last_position
                    FROM {_runStreams} AS run
                    LEFT JOIN {_forkLineage} AS fork
                      ON fork.project_id = run.project_id AND fork.run_id = run.run_id
                     AND fork.target_session_id = @session
                    WHERE run.project_id = @project AND run.run_id = @run
                    UNION ALL
                    SELECT fork.source_session_id,
                        COALESCE(parent.source_position, 0)::bigint,
                        LEAST(visible.upper_position, fork.source_position)
                    FROM visible_sessions AS visible
                    JOIN {_forkLineage} AS fork
                      ON fork.project_id = @project AND fork.run_id = @run
                     AND fork.target_session_id = visible.session_id
                    LEFT JOIN {_forkLineage} AS parent
                      ON parent.project_id = @project AND parent.run_id = @run
                     AND parent.target_session_id = fork.source_session_id
                )
                SELECT event.event_id, event.project_id, event.run_id, event.session_id,
                    event.position, event.schema_version, event.event_version,
                    event.occurred_at, event.payload, event.object_references
                FROM {_events} AS event
                JOIN visible_sessions AS visible ON visible.session_id = event.session_id
                WHERE event.project_id = @project AND event.run_id = @run
                    AND event.position > GREATEST(@after, visible.lower_position)
                    AND event.position <= visible.upper_position
                ORDER BY event.position
                LIMIT @limit
                """;
        await using var command = new NpgsqlCommand(queryText, connection);
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
        var scope = RequireScope(principal);
        _ = scope.ForSession(request.SessionId);

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
                        item, EncodeCursor(scope.ForSession(request.SessionId), item.Position));
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
        NpgsqlTransaction? transaction,
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

    private static void ValidateForkRequest(
        SessionIdentity source,
        SessionIdentity target,
        SessionForkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (source == target || request.SourceEventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SourceCursor) || request.SourceCursor.Length > 2048 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 ||
            request.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("The explicit-event fork request is invalid.", nameof(request));
    }

    private async Task<SessionForkResult?> ReadExistingForkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity source,
        string actorId,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        StoredFork? stored;
        await using (var command = new NpgsqlCommand($"""
            SELECT target_session_id, source_session_id, source_event_id, source_position,
                cursor_version, source_schema_version, source_event_version, provider_binding_hash,
                source_cursor, command_hash
            FROM {_forkLineage}
            WHERE project_id = @project AND run_id = @run
                AND actor_subject = @actor AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, source.ProjectId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, source.RunId);
            command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actorId);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            stored = new StoredFork(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7).TrimEnd(),
                reader.GetString(8),
                reader.GetString(9).TrimEnd());
        }

        if (!string.Equals(stored.CommandHash, commandHash, StringComparison.Ordinal))
            throw new SessionEventConflictException(
                "The fork idempotency key was already used for a different source or target.");
        var targetIdentity = new SessionIdentity(source.ProjectId, source.RunId, stored.TargetSessionId);
        var target = await ReadSessionAsync(
            connection, transaction, targetIdentity, lockRow: false, cancellationToken).ConfigureAwait(false);
        if (target is null)
            throw new InvalidOperationException("A stored session fork has no target session.");
        var lineage = new SessionForkLineage(
            new SessionIdentity(source.ProjectId, source.RunId, stored.SourceSessionId),
            stored.SourceEventId,
            stored.SourcePosition,
            stored.CursorVersion,
            stored.SourceSchemaVersion,
            stored.SourceEventVersion,
            stored.ProviderBindingHash,
            stored.SourceCursor);
        return new SessionForkResult(target, lineage, IsDuplicate: true);
    }

    private static string HashForkRequest(
        SessionIdentity source,
        SessionForkRequest request,
        long sourcePosition,
        string providerBindingHash) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            source.ProjectId,
            source.RunId,
            source.SessionId,
            request.TargetSessionId,
            request.SourceEventId,
            request.SourceCursor,
            sourcePosition,
            CursorVersion,
            providerBindingHash
        }, JsonOptions)));

    private static string HashProviderBinding(SessionProviderBinding binding) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            binding.ProjectId,
            binding.RunId,
            binding.ProviderId,
            AdapterVersion = binding.AdapterVersion.ToString(),
            binding.OptionsSchemaVersion,
            binding.OptionsRevision,
            binding.ResourceId,
            binding.ResourceGeneration,
            NegotiatedCapabilities = binding.NegotiatedCapabilities
                .OrderBy(value => value, StringComparer.Ordinal)
        }, JsonOptions)));

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
            if (data is null || data.Version != CursorVersion || data.Position < 0 || data.ProjectId != projectId ||
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

    private sealed record CursorData(string ProjectId, string RunId, string? SessionId, long Position)
    {
        public int Version { get; init; } = CursorVersion;
    }

    private sealed record StoredFork(
        string TargetSessionId,
        string SourceSessionId,
        Guid SourceEventId,
        long SourcePosition,
        int CursorVersion,
        int SourceSchemaVersion,
        int SourceEventVersion,
        string ProviderBindingHash,
        string SourceCursor,
        string CommandHash);

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
