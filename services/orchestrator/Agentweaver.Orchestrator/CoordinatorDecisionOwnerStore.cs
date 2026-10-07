using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class CoordinatorDecisionOwnerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _runs;
    private readonly string _sessions;
    private readonly string _decisions;
    private readonly string _gates;
    private readonly string _grants;
    private readonly string _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly CoordinatorRunSelectionContextStore _runSelectionContexts;

    public CoordinatorDecisionOwnerStore(
        NpgsqlDataSource dataSource,
        string schema,
        CoordinatorRunSelectionContextStore runSelectionContexts,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(runSelectionContexts);
        if (schema is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                schema, "^[a-z][a-z0-9_]{0,62}\\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));

        _dataSource = dataSource;
        var quotedSchema = $"\"{schema}\"";
        _runs = $"{quotedSchema}.accepted_runs";
        _sessions = $"{quotedSchema}.coordination_sessions";
        _decisions = $"{quotedSchema}.coordinator_decisions";
        _gates = $"{quotedSchema}.coordinator_gates";
        _grants = $"{quotedSchema}.executable_action_grants";
        _outbox = $"{quotedSchema}.coordinator_decision_outbox";
        _runSelectionContexts = runSelectionContexts;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CoordinatorDecisionCurrentState> InitializeRootAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        ValidateInput(actor, identity, selection);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        OwnerBinding binding;
        await using (var ownerLock = CreateOwnerBindingCommand(
                         connection,
                         transaction,
                         actor,
                         identity,
                         selection.Authorization.TenantId,
                         forUpdate: true))
        await using (var reader = await ownerLock.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "coordinator_session_unavailable", StatusCodes.Status404NotFound);
            binding = ReadOwnerBinding(
                reader, HashSelection(selection.Selection), selection.Authorization.TenantId);
        }

        var expectedBinding = CoordinatorDecisionBinding.Create(
            actor, identity, selection, binding.RunFence);
        var latest = await ReadLatestDecisionAsync(
            connection, transaction, identity, cancellationToken).ConfigureAwait(false);
        if (latest is not null)
        {
            var existing = await RestoreEnvelopeAsync(
                latest.Envelope, expectedBinding, selection.Selection, cancellationToken).ConfigureAwait(false);
            if (!existing.IsValid ||
                !string.Equals(
                    latest.Envelope.AcceptedSelectionHash,
                    expectedBinding.AcceptedSelectionHash,
                    StringComparison.Ordinal))
                throw InvalidPersistedState();
            ValidateStateActor(existing.State!, actor);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CoordinatorDecisionCurrentState(
                existing.State!,
                latest.StateVersion,
                expectedBinding.AcceptedSelectionHash);
        }

        var state = CoordinatorDecisionState.Create(expectedBinding.Fence);
        var envelope = CoordinatorDecisionStateEnvelope.Capture(state, expectedBinding);
        var decisionId = Guid.NewGuid();
        const string requestId = "root-initialization";
        const string actionKind = "run.initialize";
        const long stateVersion = 1;
        var commandHash = ComputeCommandHash(new
        {
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            expectedBinding.AcceptedSelectionHash,
            expectedBinding.Fence
        });
        var payload = JsonSerializer.Serialize(new PersistedDecisionPayload(
            envelope, true, [], JsonSerializer.SerializeToElement(new { initialized = true })), JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_decisions}
                (project_id, run_id, session_id, request_id, decision_id,
                 actor_issuer, actor_subject, execution_fence, state_version,
                 action_kind, idempotency_key, command_hash, decision_state, decision)
            VALUES
                (@project, @run, @session, @request, @decision,
                 @issuer, @subject, @fence, @version,
                 @action, @idempotency, @command_hash, 'accepted', @payload)
            """, connection, transaction))
        {
            AddIdentity(insert, identity);
            insert.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, state.Fence);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
            insert.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, actionKind);
            insert.Parameters.AddWithValue(
                "idempotency", NpgsqlDbType.Varchar, "owner-root-init-" + Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("command_hash", NpgsqlDbType.Char, commandHash);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertOutboxEventAsync(
            connection,
            transaction,
            identity,
            decisionId,
            "decision.accepted",
            new
            {
                requestId,
                actionKind,
                executionFence = state.Fence,
                stateVersion
            },
            cancellationToken).ConfigureAwait(false);
        await IssueTransitionGrantsAsync(
            connection,
            transaction,
            actor,
            identity,
            selection,
            expectedBinding,
            state,
            decisionId,
            requestId,
            stateVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CoordinatorDecisionCurrentState(state, stateVersion, expectedBinding.AcceptedSelectionHash);
    }

    public async Task<CoordinatorDecisionCurrentState> ReadCurrentAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        ValidateInput(actor, identity, selection);
        var currentSelectionHash = HashSelection(selection.Selection);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        OwnerBinding binding;
        await using (var command = CreateOwnerBindingCommand(
                         connection,
                         transaction: null,
                         actor,
                         identity,
                         selection.Authorization.TenantId,
                         forUpdate: false))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "coordinator_session_unavailable", StatusCodes.Status404NotFound);
            binding = ReadOwnerBinding(
                reader, currentSelectionHash, selection.Authorization.TenantId);
        }
        var latest = await ReadLatestDecisionAsync(
            connection, transaction: null, identity, cancellationToken).ConfigureAwait(false);
        var expectedBinding = CoordinatorDecisionBinding.Create(
            actor, identity, selection, binding.RunFence);
        var current = await RehydrateCurrentAsync(
            expectedBinding, latest, selection.Selection, cancellationToken).ConfigureAwait(false);
        await ValidatePendingGateRowsAsync(
            connection, identity, actor, current, cancellationToken).ConfigureAwait(false);
        return current;
    }

    public async Task<int> ReadRegisteredChildCountAsync(
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.ProjectId) ||
            string.IsNullOrWhiteSpace(identity.RunId) ||
            string.IsNullOrWhiteSpace(identity.SessionId))
            throw new ArgumentException("A valid run session identity is required.", nameof(identity));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadRegisteredChildCountAsync(
            connection, transaction: null, identity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoordinatorDecisionPersistedResult> PersistTransitionAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        long expectedStateVersion,
        string idempotencyKey,
        string requestId,
        string actionKind,
        string commandHash,
        CoordinatorDecisionState nextState,
        bool transitionAccepted,
        ImmutableArray<WorkflowValidationIssue> issues,
        WorkPlanRunSelectionContext? confirmedSelectionContext,
        WorkPlanRunSelectionContext? candidateSelectionContext,
        object? validatedTransitionValue,
        CancellationToken cancellationToken,
        PendingCoordinatorRunSelectionBinding? candidateRunSelectionBinding = null)
    {
        ValidateInput(actor, identity, selection);
        ArgumentNullException.ThrowIfNull(nextState);
        ValidateIdempotency(idempotencyKey, nameof(idempotencyKey));
        ValidateIdempotency(requestId, nameof(requestId));
        ValidateIdempotency(actionKind, nameof(actionKind));
        if (expectedStateVersion < 0 ||
            commandHash.Length != 64 ||
            !commandHash.All(Uri.IsHexDigit) ||
            nextState.Fence <= 0)
            throw new CoordinationException("coordinator_decision_invalid", StatusCodes.Status400BadRequest);
        if (candidateRunSelectionBinding is not null &&
            (!transitionAccepted || candidateSelectionContext is null))
            throw new CoordinationException("coordinator_decision_invalid", StatusCodes.Status400BadRequest);

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        OwnerBinding binding;
        await using (var ownerLock = CreateOwnerBindingCommand(
                         connection,
                         transaction,
                         actor,
                         identity,
                         selection.Authorization.TenantId,
                         forUpdate: true))
        await using (var reader = await ownerLock.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "coordinator_session_unavailable", StatusCodes.Status404NotFound);
            binding = ReadOwnerBinding(reader, selectionHash, selection.Authorization.TenantId);
        }
        var expectedBinding = CoordinatorDecisionBinding.Create(
            actor, identity, selection, binding.RunFence);

        var existing = await ReadByIdempotencyAsync(
            connection,
            transaction,
            actor,
            identity,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RequestId != requestId ||
                existing.ActionKind != actionKind ||
                existing.CommandHash != commandHash)
                throw new CoordinationException(
                    "coordinator_decision_idempotency_conflict", StatusCodes.Status409Conflict);
            var existingState = await RestoreEnvelopeAsync(
                existing.Envelope, expectedBinding, selection.Selection, cancellationToken).ConfigureAwait(false);
            if (!existingState.IsValid ||
                !string.Equals(
                    existing.Envelope.AcceptedSelectionHash,
                    selectionHash,
                    StringComparison.Ordinal))
                throw InvalidPersistedState();
            ValidateStateActor(existingState.State!, actor);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CoordinatorDecisionPersistedResult(
                existing.DecisionId,
                existing.StateVersion,
                existing.DecisionState == "accepted",
                existingState.State!,
                existing.Issues,
                existing.TransitionValue);
        }

        var previous = await ReadLatestDecisionAsync(
            connection, transaction, identity, cancellationToken).ConfigureAwait(false);
        var previousEnvelope = previous?.Envelope;
        var stateVersion = (previous?.StateVersion ?? 0) + 1;
        if ((previous?.StateVersion ?? 0) != expectedStateVersion)
            throw new CoordinationException("coordinator_decision_stale", StatusCodes.Status409Conflict);
        if (nextState.Fence != binding.RunFence)
            throw new CoordinationException("coordinator_decision_stale_fence", StatusCodes.Status409Conflict);

        var mergedEnvelope = CaptureEnvelope(
            nextState,
            expectedBinding,
            previousEnvelope,
            confirmedSelectionContext,
            candidateSelectionContext);
        var restored = await RestoreEnvelopeAsync(
            mergedEnvelope,
            expectedBinding,
            selection.Selection,
            cancellationToken,
            candidateRunSelectionBinding?.Context).ConfigureAwait(false);
        if (!restored.IsValid)
            throw new CoordinationException("coordinator_decision_invalid", StatusCodes.Status409Conflict);
        var newState = restored.State!;

        var maxChildren = CoordinatorWorkflowCatalog.ReadMaxChildren(selection.Selection.Snapshot);
        var registeredChildren = await ReadRegisteredChildCountAsync(
            connection, transaction, identity, cancellationToken).ConfigureAwait(false);
        var plannedChildren = newState.CandidateWorkPlan?.Plan.Items.Length
                              ?? newState.ConfirmedWorkPlan?.Plan.Items.Length
                              ?? 0;
        if (registeredChildren + plannedChildren > maxChildren)
            throw new CoordinationException(
                "run_child_limit_exceeded", StatusCodes.Status409Conflict);

        if (candidateRunSelectionBinding is not null)
            await _runSelectionContexts.PersistBindingAsync(
                connection,
                transaction,
                selection.Selection,
                newState.Fence,
                candidateRunSelectionBinding,
                cancellationToken).ConfigureAwait(false);

        var payload = new PersistedDecisionPayload(
            mergedEnvelope,
            transitionAccepted,
            issues.IsDefault ? [] : issues,
            validatedTransitionValue is null
                ? null
                : JsonSerializer.SerializeToElement(validatedTransitionValue, JsonOptions));
        var decisionId = Guid.NewGuid();
        var decisionState = transitionAccepted ? "accepted" : "rejected";
        var serializedPayload = JsonSerializer.Serialize(payload, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_decisions}
                (project_id, run_id, session_id, request_id, decision_id,
                 actor_issuer, actor_subject, execution_fence, state_version,
                 action_kind, idempotency_key, command_hash, decision_state, decision)
            VALUES
                (@project, @run, @session, @request, @decision,
                 @issuer, @subject, @fence, @version,
                 @action, @idempotency, @command_hash, @state, @payload)
            """, connection, transaction))
        {
            AddIdentity(insert, identity);
            insert.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, newState.Fence);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
            insert.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, actionKind);
            insert.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
            insert.Parameters.AddWithValue("command_hash", NpgsqlDbType.Char, commandHash);
            insert.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, decisionState);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, serializedPayload);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateGateRowsAsync(
            connection,
            transaction,
            actor,
            identity,
            previousEnvelope?.PendingGate,
            newState,
            idempotencyKey,
            stateVersion,
            cancellationToken).ConfigureAwait(false);
        await InsertOutboxEventAsync(
            connection,
            transaction,
            identity,
            decisionId,
            transitionAccepted ? "decision.accepted" : "decision.rejected",
            new
            {
                requestId,
                actionKind,
                executionFence = newState.Fence,
                stateVersion
            },
            cancellationToken).ConfigureAwait(false);
        await SupersedeCurrentGrantsAsync(
            connection, transaction, identity, cancellationToken).ConfigureAwait(false);
        if (transitionAccepted)
            await IssueTransitionGrantsAsync(
                connection,
                transaction,
                actor,
                identity,
                selection,
                expectedBinding,
                newState,
                decisionId,
                requestId,
                stateVersion,
                cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CoordinatorDecisionPersistedResult(
            decisionId,
            stateVersion,
            transitionAccepted,
            newState,
            issues.IsDefault ? [] : issues,
            payload.TransitionValue);
    }

    public static string ComputeCommandHash<T>(T command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions)));

    private static CoordinatorDecisionStateEnvelope CaptureEnvelope(
        CoordinatorDecisionState state,
        CoordinatorDecisionBinding binding,
        CoordinatorDecisionStateEnvelope? previous,
        WorkPlanRunSelectionContext? confirmedSelectionContext,
        WorkPlanRunSelectionContext? candidateSelectionContext)
    {
        var captured = CoordinatorDecisionStateEnvelope.Capture(
            state,
            binding,
            confirmedSelectionContext,
            candidateSelectionContext);
        if (captured.ConfirmedWorkPlan is not null &&
            captured.ConfirmedSelectionContext is null)
        {
            if (previous?.ConfirmedWorkPlan is not null &&
                SamePlan(previous.ConfirmedWorkPlan, captured.ConfirmedWorkPlan))
                captured = captured with
                {
                    ConfirmedSelectionContext = previous.ConfirmedSelectionContext
                };
            else if (previous?.CandidateWorkPlan is not null &&
                     SamePlan(previous.CandidateWorkPlan, captured.ConfirmedWorkPlan))
                captured = captured with
                {
                    ConfirmedSelectionContext = previous.CandidateSelectionContext
                };
        }

        if (captured.CandidateWorkPlan is not null &&
            captured.CandidateSelectionContext is null &&
            previous?.CandidateWorkPlan is not null &&
            SamePlan(previous.CandidateWorkPlan, captured.CandidateWorkPlan))
            captured = captured with
            {
                CandidateSelectionContext = previous.CandidateSelectionContext
            };
        return captured;
    }

    private static bool SamePlan(WorkPlan first, WorkPlan second) =>
        string.Equals(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonSerializer.Serialize(second, JsonOptions),
            StringComparison.Ordinal);

    private async Task UpdateGateRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        CoordinatorGateRequest? previousGate,
        CoordinatorDecisionState state,
        string idempotencyKey,
        long stateVersion,
        CancellationToken cancellationToken)
    {
        var nextGate = state.PendingGate;
        if (previousGate is not null &&
            (nextGate is null ||
             !string.Equals(previousGate.RequestId, nextGate.RequestId, StringComparison.Ordinal)))
        {
            var receipt = state.DecisionReceipts.FirstOrDefault(candidate =>
                string.Equals(candidate.RequestId, previousGate.RequestId, StringComparison.Ordinal));
            if (receipt is not null)
            {
                var gateState = receipt.Kind == CoordinatorGateKind.Question
                    ? "answered"
                    : receipt.ChoiceId == CoordinatorGateChoices.Approve ? "approved" : "rejected";
                var response = JsonSerializer.Serialize(receipt, JsonOptions);
                await using var resolve = new NpgsqlCommand($"""
                    UPDATE {_gates}
                    SET gate_state = @state, resolved_by_issuer = @issuer,
                        resolved_by_subject = @subject, response = @response,
                        state_version = @version, updated_at = clock_timestamp()
                    WHERE project_id = @project AND run_id = @run AND request_id = @request
                      AND gate_state = 'pending'
                    """, connection, transaction);
                AddIdentity(resolve, identity);
                resolve.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, previousGate.RequestId);
                resolve.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, gateState);
                resolve.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
                resolve.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
                resolve.Parameters.AddWithValue("response", NpgsqlDbType.Jsonb, response);
                resolve.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
                if (await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new CoordinationException("coordinator_gate_stale", StatusCodes.Status409Conflict);
            }
            else
            {
                await using var cancel = new NpgsqlCommand($"""
                    UPDATE {_gates}
                    SET gate_state = 'cancelled', state_version = @version,
                        updated_at = clock_timestamp()
                    WHERE project_id = @project AND run_id = @run AND request_id = @request
                      AND gate_state = 'pending'
                    """, connection, transaction);
                AddIdentity(cancel, identity);
                cancel.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, previousGate.RequestId);
                cancel.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
                if (await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new CoordinationException("coordinator_gate_stale", StatusCodes.Status409Conflict);
            }
        }

        if (nextGate is null ||
            previousGate is not null &&
            string.Equals(previousGate.RequestId, nextGate.RequestId, StringComparison.Ordinal))
            return;

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_gates}
                (project_id, run_id, session_id, request_id, gate_kind, gate_state,
                 gate, allowed_choices, allow_free_form, created_by_issuer,
                 created_by_subject, execution_fence, state_version, idempotency_key)
            VALUES
                (@project, @run, @session, @request, @kind, 'pending',
                 @gate, @choices, @freeform, @issuer,
                 @subject, @fence, @version, @idempotency)
            """, connection, transaction);
        AddIdentity(insert, identity);
        insert.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, nextGate.RequestId);
        insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, GetGateKind(nextGate.Kind));
        insert.Parameters.AddWithValue("gate", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(nextGate, JsonOptions));
        insert.Parameters.AddWithValue(
            "choices", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(nextGate.AllowedChoices, JsonOptions));
        insert.Parameters.AddWithValue("freeform", NpgsqlDbType.Boolean, nextGate.AllowsFreeform);
        insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, nextGate.Fence);
        insert.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, stateVersion);
        insert.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertOutboxEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        Guid decisionId,
        string eventKind,
        object payload,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_outbox}
                (event_id, project_id, run_id, session_id, decision_id, event_kind, event)
            VALUES (@event, @project, @run, @session, @decision, @kind, @payload)
            """, connection, transaction);
        AddIdentity(insert, identity);
        insert.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, Guid.NewGuid());
        insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
        insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, eventKind);
        insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(payload, JsonOptions));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SupersedeCurrentGrantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {_grants}
            SET is_current = false, grant_state = 'superseded'
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND is_current AND grant_state = 'active'
            """, connection, transaction);
        AddIdentity(command, identity);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task IssueTransitionGrantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CoordinatorDecisionBinding binding,
        CoordinatorDecisionState state,
        Guid decisionId,
        string requestId,
        long stateVersion,
        CancellationToken cancellationToken)
    {
        var roleRevision = GetCurrentRoleRevision(
            selection.Authorization, identity.ProjectId, "acceptRunSelection");
        foreach (var intent in GetLegalActionIntents(state))
        {
            if (!CoordinatorTypedActionIds.All.Contains(intent.ActionId))
                throw new CoordinationException(
                    "coordinator_action_not_registered", StatusCodes.Status409Conflict);
            var grantId = Guid.NewGuid().ToString("N");
            const string revision = "1";
            const string purpose = "coordinator.typed-decision";
            var expiresAt = _timeProvider.GetUtcNow().AddMinutes(5);
            await using (var insert = new NpgsqlCommand($"""
                INSERT INTO {_grants}
                    (project_id, run_id, grant_id, revision, is_current, grant_state,
                     issuer, actor_id, tenant_id, session_id, step_id, action_ids, purpose,
                     project_revision, project_configuration_revision, platform_runtime_revision,
                     context_revision, accepted_selection_hash, membership_revision, role_revision,
                     execution_fence, expires_at, source_state_version, source_decision_id, source_request_id)
                VALUES
                    (@project, @run, @grant, @revision, true, 'active',
                     @issuer, @actor, @tenant, @session, @step, @actions, @purpose,
                     @projectRevision, @configurationRevision, @platformRevision,
                     @contextRevision, @selectionHash, @membershipRevision, @roleRevision,
                     @fence, @expiresAt, @stateVersion, @decision, @request)
                """, connection, transaction))
            {
                AddIdentity(insert, identity);
                insert.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grantId);
                insert.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
                insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
                insert.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actor.Subject);
                insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
                insert.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, intent.StepId);
                insert.Parameters.AddWithValue(
                    "actions", NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(new[] { intent.ActionId }, JsonOptions));
                insert.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, purpose);
                insert.Parameters.AddWithValue(
                    "projectRevision", NpgsqlDbType.Bigint, binding.ProjectRevision);
                insert.Parameters.AddWithValue(
                    "configurationRevision", NpgsqlDbType.Bigint, binding.ProjectConfigurationRevision);
                insert.Parameters.AddWithValue(
                    "platformRevision", NpgsqlDbType.Bigint, binding.PlatformRuntimeRevision);
                insert.Parameters.AddWithValue(
                    "contextRevision", NpgsqlDbType.Varchar, binding.ContextRevision);
                insert.Parameters.AddWithValue(
                    "selectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
                insert.Parameters.AddWithValue(
                    "membershipRevision", NpgsqlDbType.Bigint, selection.Authorization.MembershipRevision);
                insert.Parameters.AddWithValue("roleRevision", NpgsqlDbType.Bigint, roleRevision);
                insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.Fence);
                insert.Parameters.AddWithValue("expiresAt", NpgsqlDbType.TimestampTz, expiresAt);
                insert.Parameters.AddWithValue("stateVersion", NpgsqlDbType.Bigint, stateVersion);
                insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
                insert.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertOutboxEventAsync(
                connection,
                transaction,
                identity,
                decisionId,
                "grant.issued." + grantId,
                new
                {
                    grantId,
                    revision,
                    actorId = actor.Subject,
                    tenantId = binding.TenantId,
                    sessionId = identity.SessionId,
                    stepId = intent.StepId,
                    actionId = intent.ActionId,
                    purpose,
                    sourceRequestId = requestId,
                    sourceStateVersion = stateVersion,
                    executionFence = binding.Fence,
                    expiresAt
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static ImmutableArray<ActionGrantIntent> GetLegalActionIntents(
        CoordinatorDecisionState state)
    {
        if (state.PendingGate is not null)
            return [];
        if (state.OutcomeSpec is null)
            return [new ActionGrantIntent(CoordinatorTypedActionIds.ProposeOutcomeSpec, "outcome")];
        if (!state.OutcomeConfirmed)
            return [];
        if (state.SelectedWorkflow is null || !state.WorkflowConfirmed)
            return [new ActionGrantIntent(CoordinatorTypedActionIds.SelectWorkflow, "workflow")];
        if (!state.CanDecompose)
            return [];
        if (state.ConfirmedWorkPlan is null)
            return [new ActionGrantIntent(CoordinatorTypedActionIds.ProposeWorkPlan, "work-plan")];
        if (!state.CanDispatch)
            return [];

        var intents = ImmutableArray.CreateBuilder<ActionGrantIntent>();
        intents.Add(new ActionGrantIntent(CoordinatorTypedActionIds.ReviseWorkPlan, "work-plan"));
        foreach (var step in state.SelectedWorkflow.Definition.Steps
                     .Where(step => step.Mode == WorkflowStepMode.Platform && step.PlatformGate is not null))
            intents.Add(new ActionGrantIntent(CoordinatorTypedActionIds.RequestAssembly, step.Id));
        return intents.ToImmutable();
    }

    private async Task<int> ReadRegisteredChildCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT count(*)::integer
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
              AND parent_session_id IS NOT NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long GetCurrentRoleRevision(
        ProjectsAuthorizationContext authorization,
        string projectId,
        string permission)
    {
        var revisions = authorization.EffectiveAuthority
            .Where(entry => entry.ResourceType == "project" && entry.ResourceId == projectId)
            .SelectMany(entry => entry.Permissions)
            .Where(grant => grant.Permission == permission)
            .Select(grant => grant.RoleRevision)
            .Distinct()
            .ToArray();
        if (revisions.Length != 1 || revisions[0] < 1)
            throw new CoordinationException(
                "run_selection_permission_denied", StatusCodes.Status403Forbidden);
        return revisions[0];
    }

    private async Task<CoordinatorDecisionPersistedRecord?> ReadByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT decision_id, request_id, action_kind, command_hash, decision_state,
                   state_version, decision::text
            FROM {_decisions}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND actor_issuer = @issuer AND actor_subject = @subject
              AND idempotency_key = @idempotency
            """, connection, transaction);
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDecisionRecord(reader)
            : null;
    }

    private async Task<CoordinatorDecisionPersistedRecord?> ReadLatestDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT decision_id, request_id, action_kind, command_hash, decision_state,
                   state_version, decision::text
            FROM {_decisions}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            ORDER BY state_version DESC
            LIMIT 1
            """, connection, transaction);
        AddIdentity(command, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDecisionRecord(reader)
            : null;
    }

    private async Task ValidatePendingGateRowsAsync(
        NpgsqlConnection connection,
        SessionIdentity identity,
        CoordinationActor actor,
        CoordinatorDecisionCurrentState current,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_id, gate::text, execution_fence, state_version,
                   created_by_issuer, created_by_subject
            FROM {_gates}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND gate_state = 'pending'
            """, connection);
        AddIdentity(command, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        CoordinatorGateRequest? persistedGate = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                persistedGate = JsonSerializer.Deserialize<CoordinatorGateRequest>(
                    reader.GetString(1), JsonOptions);
            }
            catch (JsonException)
            {
                throw InvalidPersistedState();
            }

            if (persistedGate is null ||
                reader.GetString(0) != persistedGate.RequestId ||
                reader.GetInt64(2) != current.State.Fence ||
                reader.GetInt64(3) < 1 ||
                reader.GetInt64(3) > current.StateVersion ||
                reader.GetString(4) != actor.Issuer ||
                reader.GetString(5) != actor.Subject ||
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw InvalidPersistedState();
        }

        if ((current.State.PendingGate is null) != (persistedGate is null) ||
            persistedGate is not null &&
            !string.Equals(
                JsonSerializer.Serialize(current.State.PendingGate, JsonOptions),
                JsonSerializer.Serialize(persistedGate, JsonOptions),
                StringComparison.Ordinal))
            throw InvalidPersistedState();
    }

    private static CoordinatorDecisionPersistedRecord ReadDecisionRecord(NpgsqlDataReader reader)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<PersistedDecisionPayload>(reader.GetString(6), JsonOptions)
                ?? throw new JsonException("The persisted coordinator decision is empty.");
            return new CoordinatorDecisionPersistedRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3).TrimEnd(),
                reader.GetString(4),
                reader.GetInt64(5),
                payload.Envelope,
                payload.Issues.IsDefault ? [] : payload.Issues,
                payload.TransitionValue);
        }
        catch (JsonException)
        {
            throw InvalidPersistedState();
        }
    }

    private async Task<CoordinatorDecisionCurrentState> RehydrateCurrentAsync(
        CoordinatorDecisionBinding expectedBinding,
        CoordinatorDecisionPersistedRecord? latest,
        EffectiveRunSelection selection,
        CancellationToken cancellationToken)
    {
        if (latest is null)
            return new CoordinatorDecisionCurrentState(
                CoordinatorDecisionState.Create(expectedBinding.Fence),
                0,
                expectedBinding.AcceptedSelectionHash);
        var restored = await RestoreEnvelopeAsync(
            latest.Envelope, expectedBinding, selection, cancellationToken).ConfigureAwait(false);
        if (!restored.IsValid ||
            restored.State!.Fence != expectedBinding.Fence ||
            latest.StateVersion < 1)
            throw InvalidPersistedState();
        return new CoordinatorDecisionCurrentState(
            restored.State,
            latest.StateVersion,
            expectedBinding.AcceptedSelectionHash);
    }

    private async Task<CoordinatorDecisionStateEnvelopeRestoreResult> RestoreEnvelopeAsync(
        CoordinatorDecisionStateEnvelope envelope,
        CoordinatorDecisionBinding expectedBinding,
        EffectiveRunSelection selection,
        CancellationToken cancellationToken,
        WorkPlanRunSelectionContext? preparedSelectionContext = null)
    {
        var requiresBinding = envelope.ConfirmedSelectionContext?.IsolationProviderBinding is not null ||
                              envelope.CandidateSelectionContext?.IsolationProviderBinding is not null;
        if (!requiresBinding)
            return envelope.Restore(expectedBinding);

        var trustedContext = preparedSelectionContext ??
                             await _runSelectionContexts.ReadAsync(
                                 selection, expectedBinding.Fence, cancellationToken).ConfigureAwait(false);
        var trustedBinding = trustedContext?.IsolationProviderBinding;
        if (trustedContext is null || trustedBinding is null ||
            !MatchesSelectionContext(envelope.ConfirmedSelectionContext, trustedContext, expectedBinding.RunId) ||
            !MatchesSelectionContext(envelope.CandidateSelectionContext, trustedContext, expectedBinding.RunId))
            return envelope.Restore(expectedBinding);

        PinnedProviderBinding? ResolveBinding(PinnedProviderBindingEnvelope persisted) =>
            persisted.Matches(trustedBinding, expectedBinding.RunId) ? trustedBinding : null;

        return envelope.Restore(expectedBinding, ResolveBinding);
    }

    private static bool MatchesSelectionContext(
        WorkPlanSelectionContextEnvelope? persisted,
        WorkPlanRunSelectionContext trusted,
        string runId)
    {
        if (persisted is null)
            return true;
        if (persisted.Roles.IsDefault ||
            !string.Equals(
                JsonSerializer.Serialize(persisted.Roles, JsonOptions),
                JsonSerializer.Serialize(trusted.Roles, JsonOptions),
                StringComparison.Ordinal))
            return false;
        return persisted.IsolationProviderBinding is null ||
               trusted.IsolationProviderBinding is { } binding &&
               persisted.IsolationProviderBinding.Matches(binding, runId);
    }

    private static void ValidateStateActor(CoordinatorDecisionState state, CoordinationActor actor)
    {
        if ((state.PendingGate is { } gate &&
             !string.Equals(gate.AuthorizedActorId, actor.Subject, StringComparison.Ordinal)) ||
            state.DecisionReceipts.Any(receipt =>
                !string.Equals(receipt.ActorId, actor.Subject, StringComparison.Ordinal)))
            throw InvalidPersistedState();
    }

    private NpgsqlCommand CreateOwnerBindingCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        bool forUpdate)
    {
        var lockClause = forUpdate ? "FOR UPDATE OF r, s" : string.Empty;
        var command = new NpgsqlCommand($"""
            SELECT r.tenant_id, r.execution_fence, r.execution_state, r.accepted_selection_hash,
                   s.execution_fence, s.writer_issuer, s.writer_subject, s.lifecycle_state
            FROM {_runs} AS r
            INNER JOIN {_sessions} AS s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
            WHERE r.project_id = @project AND r.run_id = @run AND s.session_id = @session
              AND r.tenant_id = @tenant AND s.parent_session_id IS NULL
              AND s.writer_issuer = @issuer AND s.writer_subject = @subject
            {lockClause}
            """, connection, transaction);
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        return command;
    }

    private static OwnerBinding ReadOwnerBinding(
        NpgsqlDataReader reader,
        string expectedSelectionHash,
        string expectedTenantId)
    {
        var tenantId = reader.GetString(0);
        var runFence = reader.GetInt64(1);
        var runState = reader.GetString(2);
        var selectionHash = reader.GetString(3).TrimEnd();
        var sessionFence = reader.GetInt64(4);
        var lifecycle = reader.GetString(7);
        if (tenantId != expectedTenantId ||
            runFence <= 0 ||
            sessionFence != runFence ||
            lifecycle != "active" ||
            runState == "completed" ||
            selectionHash.Length != 64 ||
            !string.Equals(selectionHash, expectedSelectionHash, StringComparison.Ordinal))
            throw new CoordinationException("coordinator_session_stale", StatusCodes.Status409Conflict);
        return new OwnerBinding(runFence, selectionHash, tenantId);
    }

    private static void ValidateInput(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        if (!Uri.TryCreate(actor.Issuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(issuer.AbsoluteUri, actor.Issuer, StringComparison.Ordinal))
            throw new CoordinationException("coordinator_actor_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(actor.Subject, nameof(actor.Subject));
        CoordinationIdentity.ValidateIdentity(identity.ProjectId, nameof(identity.ProjectId));
        CoordinationIdentity.ValidateIdentity(identity.RunId, nameof(identity.RunId));
        CoordinationIdentity.ValidateIdentity(identity.SessionId, nameof(identity.SessionId));
        var effective = selection.Selection;
        var authority = selection.Authorization;
        if (effective.ProjectId != identity.ProjectId ||
            effective.RunId != identity.RunId ||
            effective.Snapshot.ValueKind != JsonValueKind.Object ||
            effective.ProjectRevision < 1 ||
            effective.ProjectConfigurationRevision < 1 ||
            effective.PlatformRuntimeRevision < 1 ||
            string.IsNullOrWhiteSpace(effective.ContextRevision) ||
            authority.ContractVersion != 1 ||
            authority.Issuer != actor.Issuer ||
            authority.ActorId != actor.Subject ||
            string.IsNullOrWhiteSpace(authority.TenantId) ||
            authority.BoundProjectId != identity.ProjectId ||
            authority.BoundRunId != identity.RunId ||
            authority.MembershipRevision < 1 ||
            authority.EffectiveAuthority.IsDefault ||
            !authority.EffectiveAuthority.Any(entry =>
                entry.ResourceType == "project" &&
                entry.ResourceId == identity.ProjectId &&
                !entry.Permissions.IsDefault &&
                entry.Permissions.Any(permission =>
                    permission.Permission == "acceptRunSelection" &&
                    permission.RoleRevision > 0)))
            throw new CoordinationException("coordinator_selection_invalid", StatusCodes.Status409Conflict);
    }

    private static string HashSelection(EffectiveRunSelection selection) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Snapshot.GetRawText())));

    private static void AddIdentity(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
    }

    private static void ValidateIdempotency(string value, string name)
    {
        CoordinationIdentity.ValidateIdentity(value, name);
        if (value.Length > 128)
            throw new CoordinationException("coordinator_decision_invalid", StatusCodes.Status400BadRequest);
    }

    private static string GetGateKind(CoordinatorGateKind kind) => kind switch
    {
        CoordinatorGateKind.OutcomeConfirmation => "outcome",
        CoordinatorGateKind.GeneratedWorkflowConfirmation => "workflow_first_use",
        CoordinatorGateKind.WorkPlanConfirmation => "work_plan",
        CoordinatorGateKind.ScopeChangeConfirmation => "scope_change",
        CoordinatorGateKind.Question => "question",
        CoordinatorGateKind.Approval => "approval",
        _ => throw new CoordinationException("coordinator_gate_invalid", StatusCodes.Status400BadRequest)
    };

    private static CoordinationException InvalidPersistedState() =>
        new("coordinator_state_invalid", StatusCodes.Status503ServiceUnavailable);

    private sealed record OwnerBinding(long RunFence, string SelectionHash, string TenantId);
    private sealed record ActionGrantIntent(string ActionId, string StepId);

    private sealed record PersistedDecisionPayload(
        CoordinatorDecisionStateEnvelope Envelope,
        bool TransitionAccepted,
        ImmutableArray<WorkflowValidationIssue> Issues,
        JsonElement? TransitionValue);

    private sealed record CoordinatorDecisionPersistedRecord(
        Guid DecisionId,
        string RequestId,
        string ActionKind,
        string CommandHash,
        string DecisionState,
        long StateVersion,
        CoordinatorDecisionStateEnvelope Envelope,
        ImmutableArray<WorkflowValidationIssue> Issues,
        JsonElement? TransitionValue);
}

internal sealed record CoordinatorDecisionCurrentState(
    CoordinatorDecisionState State,
    long StateVersion,
    string SelectionHash);

internal sealed record CoordinatorDecisionPersistedResult(
    Guid DecisionId,
    long StateVersion,
    bool TransitionAccepted,
    CoordinatorDecisionState State,
    ImmutableArray<WorkflowValidationIssue> Issues,
    JsonElement? TransitionValue);
