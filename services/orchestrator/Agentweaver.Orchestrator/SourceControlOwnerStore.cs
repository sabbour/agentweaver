using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class SourceControlOwnerStore(
    NpgsqlDataSource dataSource,
    string schema,
    ProviderCatalog providerCatalog,
    ProviderResolver providerResolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private static readonly TimeSpan MergeLockTimeout = TimeSpan.FromSeconds(30);

    private readonly string _schema = QuoteSchema(schema);
    private string Pins => $"{_schema}.source_control_repository_pins";
    private string Intents => $"{_schema}.source_control_merge_intents";
    private string Grants => $"{_schema}.executable_action_grants";

    public static string CreateIntentId(SessionIdentity identity, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            identity.ProjectId + "\0" + identity.RunId + "\0" + identity.SessionId + "\0" + idempotencyKey));
        return "source-merge-" + Convert.ToHexString(digest.AsSpan(0, 20)).ToLowerInvariant();
    }

    public static string CreateApprovalRequestId(string intentId) =>
        "source-approval-" + intentId;

    public static string CreateRepositoryPinId(SourceControlAcceptedRunBinding acceptedRun) =>
        "source-pin-" +
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(acceptedRun.ProjectId + "\0" + acceptedRun.RunId + "\0" +
                                   acceptedRun.AcceptedSelectionHash)).AsSpan(0, 20))
            .ToLowerInvariant();

    public async Task<SourceControlMergeIntentSnapshot?> FindMergeIntentByIdempotencyKeyAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction: null,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);

        var snapshot = await ReadIntentByIdempotencyKeyAsync(
            connection,
            transaction: null,
            identity,
            idempotencyKey,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is not null &&
            (snapshot.AcceptedRun.Issuer != actor.Issuer ||
             snapshot.AcceptedRun.Subject != actor.Subject ||
             snapshot.AcceptedRun.TenantId != selection.Authorization.TenantId ||
             snapshot.AcceptedRun.AcceptedSelectionHash != selectionHash ||
             snapshot.AcceptedRun.Fence != owner.RunFence))
            throw new CoordinationException(
                "source_control_intent_unavailable", StatusCodes.Status404NotFound);
        return snapshot;
    }

    public async Task<SourceControlMergeIntentSnapshot> PersistMergeIntentAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentRequest intentRequest,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(intentRequest);
        ValidateIdempotencyKey(idempotencyKey);
        if (identity.ProjectId != selection.Selection.ProjectId ||
            identity.RunId != selection.Selection.RunId ||
            intentRequest.AcceptedRun.ProjectId != identity.ProjectId ||
            intentRequest.AcceptedRun.RunId != identity.RunId ||
            intentRequest.AcceptedRun.RootSessionId != identity.SessionId ||
            intentRequest.AcceptedRun.Issuer != actor.Issuer ||
            intentRequest.AcceptedRun.Subject != actor.Subject)
            throw new CoordinationException(
                "source_control_intent_binding_invalid", StatusCodes.Status403Forbidden);

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentBinding(owner, selection, intentRequest.AcceptedRun);
        if (owner.StateVersion != intentRequest.SourceStateVersion ||
            owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_intent_stale_decision", StatusCodes.Status409Conflict);

        var pin = intentRequest.RepositoryPin;
        await PersistRepositoryPinRowAsync(
            connection, transaction, identity, pin, cancellationToken).ConfigureAwait(false);

        var storedPin = await ReadPinAsync(
            connection,
            transaction,
            identity,
            selectionHash,
            cancellationToken).ConfigureAwait(false);
        if (storedPin is null || !SamePin(storedPin, pin))
            throw new CoordinationException(
                "source_control_pin_conflict", StatusCodes.Status409Conflict);

        var pullRequest = intentRequest.PullRequest;
        var intentJson = JsonSerializer.Serialize(new
        {
            intentRequest.IntentId,
            intentRequest.ApprovalRequestId,
            intentRequest.SourceStateVersion,
            intentRequest.WorkflowId,
            intentRequest.DefinitionRevision,
            intentRequest.WorkPlanId,
            intentRequest.AssemblyRequestId,
            intentRequest.WorkflowStepId,
            intentRequest.PullRequest.Number,
            intentRequest.PullRequest.HeadBranch,
            intentRequest.PullRequest.HeadSha,
            intentRequest.PullRequest.BaseBranch,
            intentRequest.PullRequest.BaseSha,
            intentRequest.Method,
            intentRequest.CreatedAt
        }, JsonOptions);
        await using (var insertIntent = new NpgsqlCommand($"""
            INSERT INTO {Intents}
                (project_id, run_id, intent_id, session_id, pin_id, issuer, actor_id, tenant_id,
                 accepted_selection_hash, project_revision, project_configuration_revision,
                 platform_runtime_revision, context_revision, execution_fence,
                 source_state_version, source_decision_id, source_request_id, idempotency_key,
                 workflow_id, definition_revision, work_plan_id, assembly_request_id,
                 workflow_step_id, approval_request_id, pull_request_number,
                 head_branch, head_sha, base_branch, base_sha, merge_method,
                 intent_request, intent_state, created_at)
            VALUES
                (@project, @run, @intent, @session, @pin, @issuer, @actor, @tenant,
                 @selectionHash, @projectRevision, @configurationRevision,
                 @platformRevision, @contextRevision, @fence,
                 @sourceStateVersion, @sourceDecision, @sourceRequest, @idempotency,
                 @workflow, @definitionRevision, @workPlan, @assemblyRequest,
                 @workflowStep, @approvalRequest, @pullRequest,
                 @headBranch, @headSha, @baseBranch, @baseSha, @mergeMethod,
                 @intentRequest, 'approval_pending', @createdAt)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddScope(insertIntent, identity);
            insertIntent.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            AddAcceptedRun(insertIntent, intentRequest.AcceptedRun);
            insertIntent.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentRequest.IntentId);
            insertIntent.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
            insertIntent.Parameters.AddWithValue(
                "sourceStateVersion", NpgsqlDbType.Bigint, intentRequest.SourceStateVersion);
            insertIntent.Parameters.AddWithValue("sourceDecision", NpgsqlDbType.Uuid, owner.DecisionId);
            insertIntent.Parameters.AddWithValue(
                "sourceRequest", NpgsqlDbType.Varchar, intentRequest.AssemblyRequestId);
            insertIntent.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
            insertIntent.Parameters.AddWithValue("workflow", NpgsqlDbType.Varchar, intentRequest.WorkflowId);
            insertIntent.Parameters.AddWithValue(
                "definitionRevision", NpgsqlDbType.Varchar, intentRequest.DefinitionRevision);
            insertIntent.Parameters.AddWithValue("workPlan", NpgsqlDbType.Varchar, intentRequest.WorkPlanId);
            insertIntent.Parameters.AddWithValue(
                "assemblyRequest", NpgsqlDbType.Varchar, intentRequest.AssemblyRequestId);
            insertIntent.Parameters.AddWithValue(
                "workflowStep", NpgsqlDbType.Varchar, intentRequest.WorkflowStepId);
            insertIntent.Parameters.AddWithValue(
                "approvalRequest", NpgsqlDbType.Varchar, intentRequest.ApprovalRequestId);
            insertIntent.Parameters.AddWithValue("pullRequest", NpgsqlDbType.Bigint, pullRequest.Number);
            insertIntent.Parameters.AddWithValue(
                "headBranch", NpgsqlDbType.Varchar, pullRequest.HeadBranch);
            insertIntent.Parameters.AddWithValue("headSha", NpgsqlDbType.Varchar, pullRequest.HeadSha);
            insertIntent.Parameters.AddWithValue(
                "baseBranch", NpgsqlDbType.Varchar, pullRequest.BaseBranch);
            insertIntent.Parameters.AddWithValue("baseSha", NpgsqlDbType.Varchar, pullRequest.BaseSha);
            insertIntent.Parameters.AddWithValue(
                "mergeMethod", NpgsqlDbType.Varchar, ToDatabaseMergeMethod(intentRequest.Method));
            insertIntent.Parameters.AddWithValue("intentRequest", NpgsqlDbType.Jsonb, intentJson);
            insertIntent.Parameters.AddWithValue(
                "createdAt", NpgsqlDbType.TimestampTz, intentRequest.CreatedAt);
            await insertIntent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = await ReadIntentByIdempotencyKeyAsync(
            connection, transaction, identity, idempotencyKey, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (saved is null || !MatchesRequest(saved, actor, selection, intentRequest))
            throw new CoordinationException(
                "source_control_intent_idempotency_conflict", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<SourceControlMergeIntentSnapshot> ReadMergeIntentAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        string intentId,
        CancellationToken cancellationToken)
    {
        if (!IsIdentifier(intentId))
            throw new CoordinationException(
                "source_control_intent_invalid", StatusCodes.Status400BadRequest);
        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction: null,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);

        var snapshot = await ReadIntentByIdAsync(
            connection, transaction: null, identity, intentId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null ||
            snapshot.AcceptedRun.Issuer != actor.Issuer ||
            snapshot.AcceptedRun.Subject != actor.Subject ||
            snapshot.AcceptedRun.TenantId != selection.Authorization.TenantId ||
            snapshot.AcceptedRun.RootSessionId != identity.SessionId ||
            snapshot.AcceptedRun.AcceptedSelectionHash != selectionHash ||
            snapshot.AcceptedRun.Fence != owner.RunFence)
            throw new CoordinationException(
                "source_control_intent_unavailable", StatusCodes.Status404NotFound);
        return snapshot;
    }

    public async Task<SourceControlRepositoryPin> ReadRepositoryPinAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        var pin = await FindRepositoryPinAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        return pin ?? throw new CoordinationException(
            "source_control_pin_unavailable", StatusCodes.Status404NotFound);
    }

    public async Task<SourceControlRepositoryPin?> FindRepositoryPinAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction: null,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);

        var pin = await ReadPinAsync(
            connection, transaction: null, identity, selectionHash, cancellationToken)
            .ConfigureAwait(false);
        if (pin is not null)
            EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        return pin;
    }

    public async Task<SourceControlRepositoryPin> PersistRepositoryPinAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlRepositoryPin pin,
        long expectedStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (identity.ProjectId != selection.Selection.ProjectId ||
            identity.RunId != selection.Selection.RunId ||
            pin.AcceptedRun.ProjectId != identity.ProjectId ||
            pin.AcceptedRun.RunId != identity.RunId ||
            pin.AcceptedRun.RootSessionId != identity.SessionId ||
            pin.AcceptedRun.Issuer != actor.Issuer ||
            pin.AcceptedRun.Subject != actor.Subject)
            throw new CoordinationException(
                "source_control_pin_binding_invalid", StatusCodes.Status403Forbidden);

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        if (owner.StateVersion != expectedStateVersion || owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_pin_stale_decision", StatusCodes.Status409Conflict);

        await PersistRepositoryPinRowAsync(
            connection, transaction, identity, pin, cancellationToken).ConfigureAwait(false);
        var stored = await ReadPinAsync(
            connection, transaction, identity, selectionHash, cancellationToken).ConfigureAwait(false);
        if (stored is null || !SamePin(stored, pin))
            throw new CoordinationException(
                "source_control_pin_conflict", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<bool> RecordWebhookDeliveryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlRepositoryPin pin,
        GitHubWebhookEnvelope envelope,
        string payloadSha256,
        long expectedStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(envelope);
        if (payloadSha256.Length != 64 || !payloadSha256.All(Uri.IsHexDigit))
            throw new CoordinationException(
                "source_control_webhook_digest_invalid", StatusCodes.Status400BadRequest);

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);
        EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        if (owner.StateVersion != expectedStateVersion || owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_webhook_authority_changed", StatusCodes.Status409Conflict);

        var storedPin = await ReadPinAsync(
            connection, transaction, identity, selectionHash, cancellationToken).ConfigureAwait(false);
        if (storedPin is null ||
            !SamePin(storedPin, pin) ||
            !SameRepositoryIdentity(pin.Repository, envelope.Repository) ||
            pin.ProviderRepositoryId != envelope.ProviderRepositoryId)
            throw new CoordinationException(
                "source_control_webhook_binding_changed", StatusCodes.Status409Conflict);

        var inserted = false;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.source_control_webhook_deliveries
                (repository_owner, repository_name, delivery_id, event_name,
                 project_id, run_id, pin_id, accepted_selection_hash, execution_fence,
                 payload_sha256, delivery_state)
            VALUES
                (@owner, @repository, @delivery, @event,
                 @project, @run, @pin, @selectionHash, @fence,
                 @payloadHash, 'accepted')
            ON CONFLICT (repository_owner, repository_name, delivery_id) DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, identity);
            insert.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, pin.Repository.Owner);
            insert.Parameters.AddWithValue("repository", NpgsqlDbType.Varchar, pin.Repository.Name);
            insert.Parameters.AddWithValue("delivery", NpgsqlDbType.Varchar, envelope.DeliveryId);
            insert.Parameters.AddWithValue("event", NpgsqlDbType.Varchar, envelope.EventName);
            insert.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
            insert.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selectionHash);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, pin.AcceptedRun.Fence);
            insert.Parameters.AddWithValue("payloadHash", NpgsqlDbType.Char, payloadSha256);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }

        await using (var read = new NpgsqlCommand($"""
            SELECT project_id, run_id, pin_id, accepted_selection_hash,
                   execution_fence, payload_sha256, event_name, delivery_state
            FROM {_schema}.source_control_webhook_deliveries
            WHERE repository_owner = @owner AND repository_name = @repository
              AND delivery_id = @delivery
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, pin.Repository.Owner);
            read.Parameters.AddWithValue("repository", NpgsqlDbType.Varchar, pin.Repository.Name);
            read.Parameters.AddWithValue("delivery", NpgsqlDbType.Varchar, envelope.DeliveryId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != identity.ProjectId ||
                reader.GetString(1) != identity.RunId ||
                reader.GetString(2) != pin.PinId ||
                reader.GetString(3).TrimEnd() != selectionHash ||
                reader.GetInt64(4) != pin.AcceptedRun.Fence ||
                reader.GetString(5).TrimEnd() != payloadSha256 ||
                reader.GetString(6) != envelope.EventName ||
                reader.GetString(7) is not ("accepted" or "completed"))
                throw new CoordinationException(
                    "source_control_webhook_delivery_conflict", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return !inserted;
    }

    public async Task<SourceControlRepositoryMergeLock> AcquireRepositoryMergeLockAsync(
        SourceControlRepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var key = repository.FullName.ToLowerInvariant();
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using (var setLockTimeout = new NpgsqlCommand(
                             $"SET lock_timeout = '{(long)MergeLockTimeout.TotalMilliseconds}ms'",
                             connection))
            {
                await setLockTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = new NpgsqlCommand(
                             "SELECT pg_advisory_lock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                             connection)
                         {
                             CommandTimeout = checked((int)MergeLockTimeout.TotalSeconds + 5)
                         })
            {
                command.Parameters.AddWithValue("repository", NpgsqlDbType.Text, key);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var resetLockTimeout = new NpgsqlCommand("RESET lock_timeout", connection))
                await resetLockTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new SourceControlRepositoryMergeLock(connection, key);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new CoordinationException(
                "source_control_merge_lock_timeout",
                StatusCodes.Status503ServiceUnavailable,
                exception);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkMergeStartedAsync(
        SessionIdentity identity,
        SourceControlMergeIntentSnapshot intent,
        ExecutableActionGrantReference grantReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(grantReference);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents} AS i
            SET intent_state = 'merge_started', updated_at = clock_timestamp()
            WHERE i.project_id = @project AND i.run_id = @run AND i.intent_id = @intent
              AND i.session_id = @session AND i.intent_state = 'approved'
              AND i.approval_decision_id = @approvalDecision
              AND i.approval_state_version = @approvalVersion
              AND i.execution_fence = @fence
              AND i.accepted_selection_hash = @selectionHash
              AND EXISTS (
                  SELECT 1 FROM {_schema}.accepted_runs AS r
                  WHERE r.project_id = i.project_id AND r.run_id = i.run_id
                    AND r.execution_fence = i.execution_fence
                    AND r.accepted_selection_hash = i.accepted_selection_hash
                    AND r.execution_state <> 'completed')
              AND EXISTS (
                  SELECT 1 FROM {_schema}.coordination_sessions AS s
                  WHERE s.project_id = i.project_id AND s.run_id = i.run_id
                    AND s.session_id = i.session_id AND s.parent_session_id IS NULL
                    AND s.execution_fence = i.execution_fence AND s.lifecycle_state = 'active')
              AND EXISTS (
                  SELECT 1 FROM {_schema}.coordinator_decisions AS d
                  WHERE d.project_id = i.project_id AND d.run_id = i.run_id
                    AND d.decision_id = i.approval_decision_id
                    AND d.state_version = i.approval_state_version
                    AND d.decision_state = 'accepted')
              AND EXISTS (
                  SELECT 1 FROM {Grants} AS g
                  WHERE g.project_id = i.project_id AND g.run_id = i.run_id
                    AND g.source_control_intent_id = i.intent_id
                    AND g.grant_id = @grant AND g.revision = @revision
                    AND g.is_current AND g.grant_state = 'active'
                    AND g.expires_at > clock_timestamp())
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intent.IntentId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, intent.AcceptedRun.RootSessionId);
        command.Parameters.AddWithValue("approvalDecision", NpgsqlDbType.Uuid, intent.ApprovalDecisionId!.Value);
        command.Parameters.AddWithValue(
            "approvalVersion", NpgsqlDbType.Bigint, intent.ApprovalStateVersion!.Value);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, intent.AcceptedRun.Fence);
        command.Parameters.AddWithValue(
            "selectionHash", NpgsqlDbType.Char, intent.AcceptedRun.AcceptedSelectionHash);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grantReference.GrantId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, grantReference.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "source_control_intent_not_current", StatusCodes.Status409Conflict);
    }

    public async Task RecordPreEffectMergeConflictAsync(
        SessionIdentity identity,
        string intentId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = 'conflict', last_failure_code = @failureCode,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND intent_state = 'approved'
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        command.Parameters.AddWithValue("failureCode", NpgsqlDbType.Varchar, failureCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "source_control_intent_not_current", StatusCodes.Status409Conflict);
    }

    public async Task<bool> RecordStaleFenceMergeConflictAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentSnapshot expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.State != "approved" ||
            expected.ApprovalDecisionId is null ||
            expected.ApprovalStateVersion is null ||
            expected.GrantReference is null)
            return false;

        var acceptedRun = expected.AcceptedRun;
        var effective = selection.Selection;
        var selectionHash = HashSelection(effective);
        if (identity.ProjectId != effective.ProjectId ||
            identity.RunId != effective.RunId ||
            acceptedRun.Issuer != actor.Issuer ||
            acceptedRun.Subject != actor.Subject ||
            acceptedRun.TenantId != selection.Authorization.TenantId ||
            acceptedRun.ProjectId != identity.ProjectId ||
            acceptedRun.RunId != identity.RunId ||
            acceptedRun.RootSessionId != identity.SessionId ||
            acceptedRun.AcceptedSelectionHash != selectionHash ||
            acceptedRun.ProjectRevision != effective.ProjectRevision ||
            acceptedRun.ProjectConfigurationRevision != effective.ProjectConfigurationRevision ||
            acceptedRun.PlatformRuntimeRevision != effective.PlatformRuntimeRevision ||
            acceptedRun.ContextRevision != effective.ContextRevision)
            return false;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        OwnerBindingSnapshot owner;
        try
        {
            owner = await ReadOwnerBindingAsync(
                connection,
                transaction,
                actor,
                identity,
                selection.Authorization.TenantId,
                selectionHash,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CoordinationException exception) when (
            exception.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status409Conflict)
        {
            return false;
        }

        if (owner.ExecutionState == "completed" ||
            owner.SessionLifecycle != "active" ||
            owner.RunFence <= acceptedRun.Fence ||
            owner.StateVersion <= expected.ApprovalStateVersion.Value)
            return false;

        var stored = await ReadIntentByIdAsync(
            connection,
            transaction,
            identity,
            expected.IntentId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (stored is null ||
            stored.AcceptedRun != acceptedRun ||
            stored.ApprovalDecisionId != expected.ApprovalDecisionId ||
            stored.ApprovalStateVersion != expected.ApprovalStateVersion ||
            stored.ApprovalReceipt != expected.ApprovalReceipt)
            return false;

        var failureCode = "source_control_run_binding_changed";
        if (stored.State == "conflict" && stored.LastFailureCode == failureCode)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (stored.State != "approved" || stored.GrantReference is not null ||
            !await IsSupersededMergeGrantAsync(
                connection, transaction, identity, expected, cancellationToken).ConfigureAwait(false))
            return false;

        await using var update = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = 'conflict', last_failure_code = @failureCode,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND accepted_selection_hash = @selectionHash
              AND execution_fence = @fence
              AND approval_decision_id = @approvalDecision
              AND approval_state_version = @approvalVersion
              AND intent_state = 'approved'
            """, connection, transaction);
        AddScope(update, identity);
        update.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, expected.IntentId);
        update.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selectionHash);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, acceptedRun.Fence);
        update.Parameters.AddWithValue(
            "approvalDecision", NpgsqlDbType.Uuid, expected.ApprovalDecisionId.Value);
        update.Parameters.AddWithValue(
            "approvalVersion", NpgsqlDbType.Bigint, expected.ApprovalStateVersion.Value);
        update.Parameters.AddWithValue("failureCode", NpgsqlDbType.Varchar, failureCode);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            return false;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IsSupersededMergeGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        SourceControlMergeIntentSnapshot expected,
        CancellationToken cancellationToken)
    {
        var grant = expected.GrantReference
            ?? throw new ArgumentException("An approved merge intent must have a grant.", nameof(expected));
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {Grants} AS g
                WHERE g.project_id = @project AND g.run_id = @run
                  AND g.source_control_intent_id = @intent
                  AND g.grant_id = @grant AND g.revision = @revision
                  AND NOT g.is_current AND g.grant_state = 'superseded')
              AND NOT EXISTS (
                SELECT 1 FROM {Grants} AS g
                WHERE g.project_id = @project AND g.run_id = @run
                  AND g.source_control_intent_id = @intent AND g.is_current)
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, expected.IntentId);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grant.GrantId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, grant.Revision);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task RecordMergeOutcomeAsync(
        SessionIdentity identity,
        string intentId,
        string outcome,
        string? mergeSha,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        if (outcome is not ("merged" or "outcome_uncertain" or "conflict"))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = @outcome, merge_sha = @mergeSha,
                last_failure_code = @failureCode, updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND intent_state = 'merge_started'
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        command.Parameters.AddWithValue("outcome", NpgsqlDbType.Varchar, outcome);
        command.Parameters.AddWithValue(
            "mergeSha", NpgsqlDbType.Varchar, (object?)mergeSha ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "failureCode", NpgsqlDbType.Varchar, (object?)failureCode ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "source_control_merge_outcome_unavailable", StatusCodes.Status409Conflict);
    }

    public async Task MarkInterruptedMergeUncertainAsync(
        SessionIdentity identity,
        string intentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = 'outcome_uncertain',
                last_failure_code = 'process_interrupted',
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND intent_state = 'merge_started'
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceControlRepositoryPin?> ReadPinAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string selectionHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT pin FROM {Pins}
            WHERE project_id = @project AND run_id = @run
              AND accepted_selection_hash = @selectionHash
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selectionHash);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : RestorePin(json);
    }

    private async Task PersistRepositoryPinRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        SourceControlRepositoryPin pin,
        CancellationToken cancellationToken)
    {
        var pinJson = JsonSerializer.Serialize(CapturePin(pin), JsonOptions);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {Pins}
                (project_id, run_id, pin_id, session_id, issuer, actor_id, tenant_id,
                 accepted_selection_hash, project_revision, project_configuration_revision,
                 platform_runtime_revision, context_revision, execution_fence,
                 provider_id, provider_resource_id, provider_resource_generation,
                 repository_owner, repository_name, provider_repository_id,
                 api_secret_id, api_secret_version, checkout_secret_id, checkout_secret_version,
                 webhook_secret_id, webhook_secret_version, pin, created_at)
            VALUES
                (@project, @run, @pin, @session, @issuer, @actor, @tenant,
                 @selectionHash, @projectRevision, @configurationRevision,
                 @platformRevision, @contextRevision, @fence,
                 @provider, @resource, @generation,
                 @owner, @repository, @repositoryId,
                 @apiSecretId, @apiSecretVersion, @checkoutSecretId, @checkoutSecretVersion,
                 @webhookSecretId, @webhookSecretVersion, @pinJson, @createdAt)
            ON CONFLICT (project_id, run_id, accepted_selection_hash) DO NOTHING
            """, connection, transaction);
        AddScope(insert, identity);
        insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        AddAcceptedRun(insert, pin.AcceptedRun);
        insert.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
        insert.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, pin.ProviderBinding.ProviderId);
        insert.Parameters.AddWithValue(
            "resource", NpgsqlDbType.Varchar, pin.ProviderBinding.Resource.ResourceId);
        insert.Parameters.AddWithValue(
            "generation", NpgsqlDbType.Bigint, pin.ProviderBinding.Resource.Generation);
        insert.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, pin.Repository.Owner);
        insert.Parameters.AddWithValue("repository", NpgsqlDbType.Varchar, pin.Repository.Name);
        insert.Parameters.AddWithValue("repositoryId", NpgsqlDbType.Bigint, pin.ProviderRepositoryId);
        AddSecretReference(insert, "api", pin.ApiCredential.Secret);
        AddOptionalSecretReference(insert, "checkout", pin.CheckoutCredential?.Secret);
        AddOptionalSecretReference(insert, "webhook", pin.WebhookCredential?.Secret);
        insert.Parameters.AddWithValue("pinJson", NpgsqlDbType.Jsonb, pinJson);
        insert.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, pin.PinnedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceControlMergeIntentSnapshot?> ReadIntentByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string idempotencyKey,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockSuffix = forUpdate ? " FOR UPDATE OF i" : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT i.intent_id, i.session_id, i.pin_id, i.issuer, i.actor_id, i.tenant_id,
                   i.accepted_selection_hash, i.project_revision, i.project_configuration_revision,
                   i.platform_runtime_revision, i.context_revision, i.execution_fence,
                   i.source_state_version, i.source_decision_id, i.source_request_id,
                   i.idempotency_key, i.workflow_id, i.definition_revision, i.work_plan_id,
                   i.assembly_request_id, i.workflow_step_id, i.approval_request_id,
                   i.pull_request_number, i.head_branch, i.head_sha, i.base_branch, i.base_sha,
                   i.merge_method, i.intent_state, i.approval_decision_id,
                   i.approval_state_version, i.approval_receipt, i.merge_sha,
                   i.last_failure_code, i.created_at, p.pin,
                   grant_ref.grant_id, grant_ref.revision
            FROM {Intents} AS i
            INNER JOIN {Pins} AS p
              ON p.project_id = i.project_id AND p.run_id = i.run_id AND p.pin_id = i.pin_id
            LEFT JOIN LATERAL (
                SELECT g.grant_id, g.revision
                FROM {Grants} AS g
                WHERE g.project_id = i.project_id AND g.run_id = i.run_id
                  AND g.source_control_intent_id = i.intent_id
                  AND g.is_current AND g.grant_state = 'active'
                  AND g.expires_at > clock_timestamp()
                ORDER BY g.created_at DESC
                LIMIT 1
            ) AS grant_ref ON true
            WHERE i.project_id = @project AND i.run_id = @run
              AND i.idempotency_key = @idempotency
            {lockSuffix}
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadIntentSnapshot(reader)
            : null;
    }

    private async Task<SourceControlMergeIntentSnapshot?> ReadIntentByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string intentId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockSuffix = forUpdate ? " FOR UPDATE OF i" : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT i.intent_id, i.session_id, i.pin_id, i.issuer, i.actor_id, i.tenant_id,
                   i.accepted_selection_hash, i.project_revision, i.project_configuration_revision,
                   i.platform_runtime_revision, i.context_revision, i.execution_fence,
                   i.source_state_version, i.source_decision_id, i.source_request_id,
                   i.idempotency_key, i.workflow_id, i.definition_revision, i.work_plan_id,
                   i.assembly_request_id, i.workflow_step_id, i.approval_request_id,
                   i.pull_request_number, i.head_branch, i.head_sha, i.base_branch, i.base_sha,
                   i.merge_method, i.intent_state, i.approval_decision_id,
                   i.approval_state_version, i.approval_receipt, i.merge_sha,
                   i.last_failure_code, i.created_at, p.pin,
                   grant_ref.grant_id, grant_ref.revision
            FROM {Intents} AS i
            INNER JOIN {Pins} AS p
              ON p.project_id = i.project_id AND p.run_id = i.run_id AND p.pin_id = i.pin_id
            LEFT JOIN LATERAL (
                SELECT g.grant_id, g.revision
                FROM {Grants} AS g
                WHERE g.project_id = i.project_id AND g.run_id = i.run_id
                  AND g.source_control_intent_id = i.intent_id
                  AND g.is_current AND g.grant_state = 'active'
                ORDER BY g.created_at DESC
                LIMIT 1
            ) AS grant_ref ON true
            WHERE i.project_id = @project AND i.run_id = @run AND i.intent_id = @intent
            {lockSuffix}
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadIntentSnapshot(reader)
            : null;
    }

    private async Task<OwnerBindingSnapshot> ReadOwnerBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string selectionHash,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockSuffix = forUpdate ? " FOR UPDATE OF r, s" : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT r.accepted_selection_hash, r.accepted_by_issuer, r.accepted_by_subject,
                   COALESCE(r.tenant_id, ''), r.execution_fence, r.execution_state,
                   s.session_id, s.execution_fence, s.writer_issuer, s.writer_subject,
                   s.lifecycle_state, d.decision_id, d.state_version, d.decision_state
            FROM {_schema}.accepted_runs AS r
            INNER JOIN {_schema}.coordination_sessions AS s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
             AND s.session_id = @session AND s.parent_session_id IS NULL
            INNER JOIN LATERAL (
                SELECT decision_id, state_version, decision_state
                FROM {_schema}.coordinator_decisions
                WHERE project_id = r.project_id AND run_id = r.run_id AND session_id = s.session_id
                ORDER BY state_version DESC
                LIMIT 1
            ) AS d ON true
            WHERE r.project_id = @project AND r.run_id = @run
            {lockSuffix}
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "source_control_run_unavailable", StatusCodes.Status404NotFound);
        var binding = new OwnerBindingSnapshot(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetGuid(11),
            reader.GetInt64(12),
            reader.GetString(13));
        if (binding.AcceptedIssuer != actor.Issuer ||
            binding.AcceptedSubject != actor.Subject ||
            binding.TenantId != tenantId ||
            binding.AcceptedSelectionHash != selectionHash ||
            binding.RootSessionId != identity.SessionId ||
            binding.WriterIssuer != actor.Issuer ||
            binding.WriterSubject != actor.Subject ||
            binding.RunFence != binding.SessionFence)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        return binding;
    }

    private static void EnsureCurrentBinding(
        OwnerBindingSnapshot owner,
        AuthorizedRunSelection selection,
        SourceControlAcceptedRunBinding acceptedRun)
    {
        var effective = selection.Selection;
        if (owner.ExecutionState == "completed" ||
            owner.SessionLifecycle != "active" ||
            owner.DecisionState != "accepted" ||
            acceptedRun.AcceptedSelectionHash != owner.AcceptedSelectionHash ||
            acceptedRun.ProjectRevision != effective.ProjectRevision ||
            acceptedRun.ProjectConfigurationRevision != effective.ProjectConfigurationRevision ||
            acceptedRun.PlatformRuntimeRevision != effective.PlatformRuntimeRevision ||
            acceptedRun.ContextRevision != effective.ContextRevision ||
            acceptedRun.Fence != owner.RunFence ||
            acceptedRun.TenantId != selection.Authorization.TenantId ||
            acceptedRun.ProjectId != effective.ProjectId ||
            acceptedRun.RunId != effective.RunId ||
            acceptedRun.RootSessionId != owner.RootSessionId)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    private SourceControlMergeIntentSnapshot ReadIntentSnapshot(NpgsqlDataReader reader)
    {
        var pin = RestorePin(reader.GetString(35))
            ?? throw new CoordinationException(
                "source_control_pin_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        CoordinatorGateDecisionReceipt? approvalReceipt = null;
        if (!reader.IsDBNull(31))
        {
            approvalReceipt = JsonSerializer.Deserialize<CoordinatorGateDecisionReceipt>(
                reader.GetString(31), JsonOptions)
                ?? throw new CoordinationException(
                    "source_control_approval_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        }
        var grantReference = !reader.IsDBNull(36) && !reader.IsDBNull(37)
            ? new ExecutableActionGrantReference(reader.GetString(36), reader.GetString(37))
            : null;
        var acceptedRun = new SourceControlAcceptedRunBinding(
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            pin.AcceptedRun.ProjectId,
            pin.AcceptedRun.RunId,
            reader.GetString(1),
            reader.GetString(6).TrimEnd(),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetInt64(11));
        if (pin.AcceptedRun != acceptedRun)
            throw new CoordinationException(
                "source_control_intent_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        return new SourceControlMergeIntentSnapshot(
            reader.GetString(0),
            acceptedRun,
            pin,
            reader.GetInt64(12),
            reader.GetGuid(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetInt64(22),
            reader.GetString(23),
            reader.GetString(24),
            reader.GetString(25),
            reader.GetString(26),
            ParseDatabaseMergeMethod(reader.GetString(27)),
            reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetGuid(29),
            reader.IsDBNull(30) ? null : reader.GetInt64(30),
            approvalReceipt,
            reader.IsDBNull(32) ? null : reader.GetString(32),
            reader.IsDBNull(33) ? null : reader.GetString(33),
            reader.GetFieldValue<DateTimeOffset>(34),
            grantReference);
    }

    private static StoredRepositoryPin CapturePin(SourceControlRepositoryPin pin) =>
        new(
            pin.PinId,
            pin.AcceptedRun,
            pin.ProviderBinding.ProviderId,
            pin.ProviderBinding.AdapterVersion.ToString(),
            pin.ProviderBinding.OptionsSchemaVersion,
            pin.ProviderBinding.OptionsRevision,
            pin.ProviderBinding.Hosting,
            pin.ProviderBinding.Resource.ResourceId,
            pin.ProviderBinding.Resource.Generation,
            pin.ProviderBinding.NegotiatedCapabilities.Order(StringComparer.Ordinal).ToArray(),
            pin.Repository.Owner,
            pin.Repository.Name,
            CaptureRequiredCredential(pin.ApiCredential),
            CaptureOptionalCredential(pin.CheckoutCredential),
            CaptureOptionalCredential(pin.WebhookCredential),
            pin.ProviderRepositoryId,
            pin.DefaultBranch,
            pin.IsPrivate,
            pin.PinnedAt);

    private SourceControlRepositoryPin RestorePin(string json) =>
        RestorePin(json, providerCatalog, providerResolver);

    internal static SourceControlRepositoryPin RestorePin(
        string json,
        ProviderCatalog providerCatalog,
        ProviderResolver providerResolver)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(providerResolver);
        var stored = JsonSerializer.Deserialize<StoredRepositoryPin>(json, JsonOptions)
            ?? throw new CoordinationException(
                "source_control_pin_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        if (!Version.TryParse(stored.AdapterVersion, out var adapterVersion) ||
            stored.OptionsSchemaVersion < 1 ||
            stored.ResourceGeneration < 1 ||
            stored.NegotiatedCapabilities.Length == 0 ||
            stored.NegotiatedCapabilities.Any(string.IsNullOrWhiteSpace) ||
            stored.NegotiatedCapabilities.Distinct(StringComparer.Ordinal).Count() !=
                stored.NegotiatedCapabilities.Length)
            throw new CoordinationException(
                "source_control_pin_contract_invalid", StatusCodes.Status503ServiceUnavailable);

        var negotiatedCapabilities = stored.NegotiatedCapabilities
            .ToImmutableHashSet(StringComparer.Ordinal);
        var resolution = providerResolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            providerCatalog.TryGetDefault(ProviderSeam.SourceControl, out var defaultProviderId) &&
            string.Equals(defaultProviderId, stored.ProviderId, StringComparison.Ordinal)
                ? null
                : stored.ProviderId,
            adapterVersion!,
            stored.OptionsSchemaVersion,
            negotiatedCapabilities));
        var candidate = resolution.IsSuccess ? resolution.Value?.Candidate : null;
        if (candidate is null ||
            candidate.ProviderId != stored.ProviderId ||
            candidate.OptionsRevision != stored.OptionsRevision ||
            candidate.Hosting != stored.Hosting)
            throw new CoordinationException(
                "source_control_pin_provider_unavailable", StatusCodes.Status503ServiceUnavailable);

        var resource = new ProviderResourceRef(
            ProviderSeam.SourceControl,
            stored.ProviderId,
            stored.ResourceId,
            stored.ResourceGeneration);
        var negotiation = new ResourceNegotiation(resource, negotiatedCapabilities);
        var bindingResult = providerResolver.Pin(
            stored.AcceptedRun.RunId, candidate, stored.ResourceId, negotiation);
        var binding = bindingResult.IsSuccess ? bindingResult.Value : null;
        if (binding is null)
            throw new CoordinationException(
                "source_control_pin_provider_unavailable", StatusCodes.Status503ServiceUnavailable);

        return new SourceControlRepositoryPin(
            stored.PinId,
            stored.AcceptedRun,
            binding,
            new SourceControlRepositoryIdentity(stored.RepositoryOwner, stored.RepositoryName),
            RestoreCredential(stored.ApiCredential),
            stored.CheckoutCredential is null ? null : RestoreCredential(stored.CheckoutCredential),
            stored.WebhookCredential is null ? null : RestoreCredential(stored.WebhookCredential),
            stored.ProviderRepositoryId,
            stored.DefaultBranch,
            stored.IsPrivate,
            stored.PinnedAt);
    }

    private static StoredCredentialReference CaptureRequiredCredential(
        SourceControlCredentialReference credential) =>
        new(credential.Secret.Id, credential.Secret.Version, credential.Purpose);

    private static StoredCredentialReference? CaptureOptionalCredential(
        SourceControlCredentialReference? credential) =>
        credential is null
            ? null
            : new StoredCredentialReference(credential.Secret.Id, credential.Secret.Version, credential.Purpose);

    private static SourceControlCredentialReference RestoreCredential(
        StoredCredentialReference credential) =>
        new(new SecretRef(credential.SecretId, credential.SecretVersion), credential.Purpose);

    private static bool MatchesRequest(
        SourceControlMergeIntentSnapshot saved,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentRequest request) =>
        saved.IntentId == request.IntentId &&
        saved.ApprovalRequestId == request.ApprovalRequestId &&
        saved.AcceptedRun.Issuer == actor.Issuer &&
        saved.AcceptedRun.Subject == actor.Subject &&
        saved.AcceptedRun.TenantId == selection.Authorization.TenantId &&
        saved.AcceptedRun.AcceptedSelectionHash == request.AcceptedRun.AcceptedSelectionHash &&
        saved.AcceptedRun.Fence == request.AcceptedRun.Fence &&
        saved.Pin.PinId == request.RepositoryPin.PinId &&
        saved.SourceStateVersion == request.SourceStateVersion &&
        saved.WorkflowId == request.WorkflowId &&
        saved.DefinitionRevision == request.DefinitionRevision &&
        saved.WorkPlanId == request.WorkPlanId &&
        saved.AssemblyRequestId == request.AssemblyRequestId &&
        saved.WorkflowStepId == request.WorkflowStepId &&
        saved.PullRequestNumber == request.PullRequest.Number &&
        saved.HeadBranch == request.PullRequest.HeadBranch &&
        saved.HeadSha == request.PullRequest.HeadSha &&
        saved.BaseBranch == request.PullRequest.BaseBranch &&
        saved.BaseSha == request.PullRequest.BaseSha &&
        saved.Method == request.Method;

    private static bool SamePin(SourceControlRepositoryPin stored, SourceControlRepositoryPin proposed) =>
        stored.PinId == proposed.PinId &&
        stored.AcceptedRun == proposed.AcceptedRun &&
        SameProviderBinding(stored.ProviderBinding, proposed.ProviderBinding) &&
        SameRepositoryIdentity(stored.Repository, proposed.Repository) &&
        SameCredential(stored.ApiCredential, proposed.ApiCredential) &&
        SameCredential(stored.CheckoutCredential, proposed.CheckoutCredential) &&
        SameCredential(stored.WebhookCredential, proposed.WebhookCredential) &&
        stored.ProviderRepositoryId == proposed.ProviderRepositoryId &&
        stored.DefaultBranch == proposed.DefaultBranch &&
        stored.IsPrivate == proposed.IsPrivate &&
        stored.PinnedAt == proposed.PinnedAt;

    private static bool SameRepositoryIdentity(
        SourceControlRepositoryIdentity left,
        SourceControlRepositoryIdentity right) =>
        string.Equals(left.Owner, right.Owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static bool SameProviderBinding(
        PinnedProviderBinding left,
        PinnedProviderBinding right) =>
        left.RunId == right.RunId &&
        left.Seam == right.Seam &&
        left.ProviderId == right.ProviderId &&
        left.AdapterVersion == right.AdapterVersion &&
        left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
        left.OptionsRevision == right.OptionsRevision &&
        left.Hosting == right.Hosting &&
        left.Resource == right.Resource &&
        left.NegotiatedCapabilities.SetEquals(right.NegotiatedCapabilities);

    private static bool SameCredential(
        SourceControlCredentialReference? left,
        SourceControlCredentialReference? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.Purpose == right.Purpose &&
              left.Secret.Id == right.Secret.Id &&
              left.Secret.Version == right.Secret.Version;

    private static SourceControlMergeMethod ParseDatabaseMergeMethod(string value) =>
        value switch
        {
            "merge" => SourceControlMergeMethod.Merge,
            "squash" => SourceControlMergeMethod.Squash,
            "rebase" => SourceControlMergeMethod.Rebase,
            _ => throw new CoordinationException(
                "source_control_intent_contract_invalid", StatusCodes.Status503ServiceUnavailable)
        };

    private static string ToDatabaseMergeMethod(SourceControlMergeMethod value) =>
        value switch
        {
            SourceControlMergeMethod.Merge => "merge",
            SourceControlMergeMethod.Squash => "squash",
            SourceControlMergeMethod.Rebase => "rebase",
            _ => throw new CoordinationException(
                "source_control_merge_method_invalid", StatusCodes.Status400BadRequest)
        };

    private static void AddScope(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
    }

    private static void AddAcceptedRun(
        NpgsqlCommand command,
        SourceControlAcceptedRunBinding acceptedRun)
    {
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, acceptedRun.Issuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, acceptedRun.Subject);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, acceptedRun.TenantId);
        command.Parameters.AddWithValue(
            "selectionHash", NpgsqlDbType.Char, acceptedRun.AcceptedSelectionHash);
        command.Parameters.AddWithValue("projectRevision", NpgsqlDbType.Bigint, acceptedRun.ProjectRevision);
        command.Parameters.AddWithValue(
            "configurationRevision", NpgsqlDbType.Bigint, acceptedRun.ProjectConfigurationRevision);
        command.Parameters.AddWithValue(
            "platformRevision", NpgsqlDbType.Bigint, acceptedRun.PlatformRuntimeRevision);
        command.Parameters.AddWithValue(
            "contextRevision", NpgsqlDbType.Varchar, acceptedRun.ContextRevision);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, acceptedRun.Fence);
    }

    private static void AddSecretReference(NpgsqlCommand command, string prefix, SecretRef secret)
    {
        command.Parameters.AddWithValue(prefix + "SecretId", NpgsqlDbType.Varchar, secret.Id);
        command.Parameters.AddWithValue(prefix + "SecretVersion", NpgsqlDbType.Varchar, secret.Version);
    }

    private static void AddOptionalSecretReference(
        NpgsqlCommand command,
        string prefix,
        SecretRef? secret)
    {
        command.Parameters.AddWithValue(
            prefix + "SecretId", NpgsqlDbType.Varchar, (object?)secret?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue(
            prefix + "SecretVersion", NpgsqlDbType.Varchar, (object?)secret?.Version ?? DBNull.Value);
    }

    private static string HashSelection(EffectiveRunSelection selection) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Snapshot.GetRawText())));

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static void ValidateIdempotencyKey(string value)
    {
        if (!IsIdentifier(value))
            throw new CoordinationException(
                "source_control_idempotency_key_invalid", StatusCodes.Status400BadRequest);
    }

    private static string QuoteSchema(string value)
    {
        if (value is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                value, "^[a-z][a-z0-9_]{0,62}\\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            value is "public" or "pg_catalog" or "information_schema" ||
            value.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(value));
        return $"\"{value}\"";
    }

    private sealed record OwnerBindingSnapshot(
        string AcceptedSelectionHash,
        string AcceptedIssuer,
        string AcceptedSubject,
        string TenantId,
        long RunFence,
        string ExecutionState,
        string RootSessionId,
        long SessionFence,
        string WriterIssuer,
        string WriterSubject,
        string SessionLifecycle,
        Guid DecisionId,
        long StateVersion,
        string DecisionState);

    private sealed record StoredRepositoryPin(
        string PinId,
        SourceControlAcceptedRunBinding AcceptedRun,
        string ProviderId,
        string AdapterVersion,
        int OptionsSchemaVersion,
        string OptionsRevision,
        ProviderHostingPattern Hosting,
        string ResourceId,
        long ResourceGeneration,
        string[] NegotiatedCapabilities,
        string RepositoryOwner,
        string RepositoryName,
        StoredCredentialReference ApiCredential,
        StoredCredentialReference? CheckoutCredential,
        StoredCredentialReference? WebhookCredential,
        long ProviderRepositoryId,
        string DefaultBranch,
        bool IsPrivate,
        DateTimeOffset PinnedAt);

    private sealed record StoredCredentialReference(
        string SecretId,
        string SecretVersion,
        string Purpose);
}

internal sealed record SourceControlMergeIntentSnapshot(
    string IntentId,
    SourceControlAcceptedRunBinding AcceptedRun,
    SourceControlRepositoryPin Pin,
    long SourceStateVersion,
    Guid SourceDecisionId,
    string SourceRequestId,
    string IdempotencyKey,
    string WorkflowId,
    string DefinitionRevision,
    string WorkPlanId,
    string AssemblyRequestId,
    string WorkflowStepId,
    string ApprovalRequestId,
    long PullRequestNumber,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    string BaseSha,
    SourceControlMergeMethod Method,
    string State,
    Guid? ApprovalDecisionId,
    long? ApprovalStateVersion,
    CoordinatorGateDecisionReceipt? ApprovalReceipt,
    string? MergeSha,
    string? LastFailureCode,
    DateTimeOffset CreatedAt,
    ExecutableActionGrantReference? GrantReference);

internal sealed class SourceControlRepositoryMergeLock(
    NpgsqlConnection connection,
    string repositoryKey) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                connection);
            command.Parameters.AddWithValue("repository", NpgsqlDbType.Text, repositoryKey);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
