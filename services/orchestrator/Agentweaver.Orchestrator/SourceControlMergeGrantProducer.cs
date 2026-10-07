using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal static class SourceControlMergeGrantProducer
{
    public const string ActionId = "source_control.merge";
    public const string Purpose = "source-control.merge";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static async Task TrackApprovalRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string intentsTable,
        string decisionsTable,
        CoordinationActor actor,
        SessionIdentity identity,
        CoordinatorDecisionBinding binding,
        CoordinatorDecisionState state,
        Guid decisionId,
        long stateVersion,
        CancellationToken cancellationToken)
    {
        var gate = state.PendingGate;
        if (gate?.Kind != CoordinatorGateKind.Approval ||
            gate.Fence != binding.Fence ||
            gate.AuthorizedActorId != actor.Subject ||
            !IsStableIdentifier(gate.SubjectId))
            return;

        await using var command = new NpgsqlCommand($"""
            UPDATE {intentsTable} AS i
            SET approval_request_decision_id = @decision,
                approval_request_state_version = @stateVersion,
                updated_at = clock_timestamp()
            WHERE i.project_id = @project AND i.run_id = @run
              AND i.intent_id = @intent AND i.session_id = @session
              AND i.approval_request_id = @request
              AND i.intent_state = 'approval_pending'
              AND i.approval_request_decision_id IS NULL
              AND i.issuer = @issuer AND i.actor_id = @actor AND i.tenant_id = @tenant
              AND i.accepted_selection_hash = @selectionHash
              AND i.execution_fence = @fence
              AND i.source_state_version + 1 = @stateVersion
              AND EXISTS (
                  SELECT 1 FROM {decisionsTable} AS source_decision
                  WHERE source_decision.project_id = i.project_id
                    AND source_decision.run_id = i.run_id
                    AND source_decision.decision_id = i.source_decision_id
                    AND source_decision.state_version = i.source_state_version
                    AND source_decision.decision_state = 'accepted')
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, gate.SubjectId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, gate.RequestId);
        command.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
        command.Parameters.AddWithValue("stateVersion", NpgsqlDbType.Bigint, stateVersion);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        command.Parameters.AddWithValue(
            "selectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.Fence);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task IssueApprovedIntentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string intentsTable,
        string pinsTable,
        string decisionsTable,
        string grantsTable,
        string outboxTable,
        Func<string, SourceControlRepositoryPin> restoreRepositoryPin,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CoordinatorDecisionBinding binding,
        CoordinatorDecisionState state,
        Guid decisionId,
        string requestId,
        long stateVersion,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (state.DecisionReceipts.IsDefaultOrEmpty)
            return;
        ArgumentNullException.ThrowIfNull(restoreRepositoryPin);

        foreach (var receipt in state.DecisionReceipts.Where(receipt =>
                     receipt.Kind == CoordinatorGateKind.Approval &&
                     receipt.FreeformAnswer is null))
        {
            var intent = await ReadPendingIntentAsync(
                connection,
                transaction,
                intentsTable,
                pinsTable,
                decisionsTable,
                identity,
                receipt.SubjectId,
                restoreRepositoryPin,
                cancellationToken).ConfigureAwait(false);
            if (intent is null)
                continue;

            if (intent.ApprovalRequestId != receipt.RequestId)
                continue;
            ValidateIntentBinding(
                intent,
                actor,
                identity,
                selection,
                binding,
                state,
                decisionId,
                requestId,
                stateVersion,
                receipt);

            if (receipt.ChoiceId == CoordinatorGateChoices.Reject)
            {
                await SetRejectedAsync(
                    connection, transaction, intentsTable, identity, intent.IntentId, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (receipt.ChoiceId != CoordinatorGateChoices.Approve)
                throw new CoordinationException(
                    "source_control_approval_invalid", StatusCodes.Status409Conflict);

            var approvedIntent = CreateApprovedIntent(intent, state, receipt);
            var approvalReceipt = JsonSerializer.Serialize(receipt, JsonOptions);
            await using (var updateIntent = new NpgsqlCommand($"""
                UPDATE {intentsTable}
                SET intent_state = 'approved', approval_decision_id = @decision,
                    approval_state_version = @stateVersion, approval_receipt = @receipt,
                    updated_at = clock_timestamp()
                WHERE project_id = @project AND run_id = @run AND intent_id = @intent
                  AND intent_state = 'approval_pending'
                """, connection, transaction))
            {
                AddScope(updateIntent, identity);
                updateIntent.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intent.IntentId);
                updateIntent.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
                updateIntent.Parameters.AddWithValue("stateVersion", NpgsqlDbType.Bigint, stateVersion);
                updateIntent.Parameters.AddWithValue("receipt", NpgsqlDbType.Jsonb, approvalReceipt);
                if (await updateIntent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new CoordinationException(
                        "source_control_approval_stale", StatusCodes.Status409Conflict);
            }

            var grantId = Guid.NewGuid().ToString("N");
            const string revision = "1";
            var expiresAt = timeProvider.GetUtcNow().AddMinutes(5);
            await using (var insertGrant = new NpgsqlCommand($"""
                INSERT INTO {grantsTable}
                    (project_id, run_id, grant_id, revision, is_current, grant_state,
                     issuer, actor_id, tenant_id, session_id, step_id, action_ids, purpose,
                     project_revision, project_configuration_revision, platform_runtime_revision,
                     context_revision, accepted_selection_hash, membership_revision, role_revision,
                     execution_fence, expires_at, source_state_version, source_decision_id,
                     source_request_id, source_control_intent_id)
                VALUES
                    (@project, @run, @grant, @revision, true, 'active',
                     @issuer, @actor, @tenant, @session, @step, @actions, @purpose,
                     @projectRevision, @configurationRevision, @platformRevision,
                     @contextRevision, @selectionHash, @membershipRevision, @roleRevision,
                     @fence, @expiresAt, @stateVersion, @decision,
                     @request, @intent)
                """, connection, transaction))
            {
                AddScope(insertGrant, identity);
                insertGrant.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grantId);
                insertGrant.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
                insertGrant.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
                insertGrant.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actor.Subject);
                insertGrant.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
                insertGrant.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
                insertGrant.Parameters.AddWithValue(
                    "step", NpgsqlDbType.Varchar, approvedIntent.WorkflowStepId);
                insertGrant.Parameters.AddWithValue(
                    "actions", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new[] { ActionId }, JsonOptions));
                insertGrant.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, Purpose);
                insertGrant.Parameters.AddWithValue(
                    "projectRevision", NpgsqlDbType.Bigint, binding.ProjectRevision);
                insertGrant.Parameters.AddWithValue(
                    "configurationRevision", NpgsqlDbType.Bigint, binding.ProjectConfigurationRevision);
                insertGrant.Parameters.AddWithValue(
                    "platformRevision", NpgsqlDbType.Bigint, binding.PlatformRuntimeRevision);
                insertGrant.Parameters.AddWithValue(
                    "contextRevision", NpgsqlDbType.Varchar, binding.ContextRevision);
                insertGrant.Parameters.AddWithValue(
                    "selectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
                insertGrant.Parameters.AddWithValue(
                    "membershipRevision", NpgsqlDbType.Bigint, selection.Authorization.MembershipRevision);
                insertGrant.Parameters.AddWithValue(
                    "roleRevision", NpgsqlDbType.Bigint, CurrentRoleRevision(selection, identity.ProjectId));
                insertGrant.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.Fence);
                insertGrant.Parameters.AddWithValue("expiresAt", NpgsqlDbType.TimestampTz, expiresAt);
                insertGrant.Parameters.AddWithValue("stateVersion", NpgsqlDbType.Bigint, stateVersion);
                insertGrant.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
                insertGrant.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
                insertGrant.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intent.IntentId);
                await insertGrant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var insertOutbox = new NpgsqlCommand($"""
                INSERT INTO {outboxTable}
                    (event_id, project_id, run_id, session_id, decision_id, event_kind, event)
                VALUES
                    (@event, @project, @run, @session, @decision, @kind, @payload)
                """, connection, transaction);
            AddScope(insertOutbox, identity);
            insertOutbox.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, Guid.NewGuid());
            insertOutbox.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            insertOutbox.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
            insertOutbox.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, "grant.issued." + grantId);
            insertOutbox.Parameters.AddWithValue(
                "payload",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(new
                {
                    grantId,
                    revision,
                    actorId = actor.Subject,
                    tenantId = binding.TenantId,
                    sessionId = identity.SessionId,
                    stepId = approvedIntent.WorkflowStepId,
                    actionId = ActionId,
                    purpose = Purpose,
                    sourceRequestId = requestId,
                    sourceStateVersion = stateVersion,
                    sourceControlIntentId = intent.IntentId,
                    executionFence = binding.Fence,
                    expiresAt
                }, JsonOptions));
            await insertOutbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<PendingMergeIntent?> ReadPendingIntentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string intentsTable,
        string pinsTable,
        string decisionsTable,
        SessionIdentity identity,
        string intentId,
        Func<string, SourceControlRepositoryPin> restoreRepositoryPin,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT i.intent_id, i.session_id, i.pin_id, i.issuer, i.actor_id, i.tenant_id,
                   i.accepted_selection_hash, i.project_revision, i.project_configuration_revision,
                   i.platform_runtime_revision, i.context_revision, i.execution_fence,
                   i.source_state_version, i.source_decision_id, i.source_request_id,
                   i.idempotency_key, i.workflow_id, i.definition_revision, i.work_plan_id,
                   i.assembly_request_id, i.workflow_step_id, i.approval_request_id,
                   i.pull_request_number, i.head_branch, i.head_sha, i.base_branch, i.base_sha,
                   i.merge_method, i.created_at, p.pin,
                   i.approval_request_decision_id, i.approval_request_state_version
            FROM {intentsTable} AS i
            INNER JOIN {pinsTable} AS p
              ON p.project_id = i.project_id AND p.run_id = i.run_id AND p.pin_id = i.pin_id
            INNER JOIN {decisionsTable} AS source_decision
              ON source_decision.project_id = i.project_id
             AND source_decision.run_id = i.run_id
             AND source_decision.decision_id = i.source_decision_id
             AND source_decision.state_version = i.source_state_version
             AND source_decision.decision_state = 'accepted'
            INNER JOIN {decisionsTable} AS approval_request_decision
              ON approval_request_decision.project_id = i.project_id
             AND approval_request_decision.run_id = i.run_id
             AND approval_request_decision.decision_id = i.approval_request_decision_id
             AND approval_request_decision.state_version = i.approval_request_state_version
             AND approval_request_decision.decision_state = 'accepted'
            WHERE i.project_id = @project AND i.run_id = @run AND i.intent_id = @intent
              AND i.intent_state = 'approval_pending'
            FOR UPDATE OF i
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var pin = restoreRepositoryPin(reader.GetString(29));
        return new PendingMergeIntent(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6).TrimEnd(),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetInt64(11),
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
            reader.GetString(27) switch
            {
                "merge" => SourceControlMergeMethod.Merge,
                "squash" => SourceControlMergeMethod.Squash,
                "rebase" => SourceControlMergeMethod.Rebase,
                _ => throw new CoordinationException(
                    "source_control_intent_contract_invalid", StatusCodes.Status503ServiceUnavailable)
            },
            reader.GetFieldValue<DateTimeOffset>(28),
            pin,
            reader.IsDBNull(30) ? null : reader.GetGuid(30),
            reader.IsDBNull(31) ? null : reader.GetInt64(31));
    }

    private static void ValidateIntentBinding(
        PendingMergeIntent intent,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CoordinatorDecisionBinding binding,
        CoordinatorDecisionState state,
        Guid decisionId,
        string requestId,
        long stateVersion,
        CoordinatorGateDecisionReceipt receipt)
    {
        if (intent.SessionId != identity.SessionId ||
            intent.Issuer != actor.Issuer ||
            intent.ActorId != actor.Subject ||
            intent.TenantId != binding.TenantId ||
            intent.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            intent.ProjectRevision != binding.ProjectRevision ||
            intent.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            intent.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            intent.ContextRevision != binding.ContextRevision ||
            intent.ExecutionFence != binding.Fence ||
            intent.ApprovalRequestDecisionId is null ||
            intent.ApprovalRequestStateVersion is null ||
            intent.SourceStateVersion + 1 != intent.ApprovalRequestStateVersion ||
            intent.ApprovalRequestStateVersion + 1 != stateVersion ||
            intent.SourceDecisionId == Guid.Empty ||
            intent.SourceRequestId != intent.AssemblyRequestId ||
            requestId != receipt.RequestId ||
            decisionId == intent.SourceDecisionId ||
            intent.Pin.AcceptedRun != new SourceControlAcceptedRunBinding(
                binding.Issuer,
                binding.Subject,
                binding.TenantId,
                binding.ProjectId,
                binding.RunId,
                binding.RootSessionId,
                binding.AcceptedSelectionHash,
                binding.ProjectRevision,
                binding.ProjectConfigurationRevision,
                binding.PlatformRuntimeRevision,
                binding.ContextRevision,
                binding.Fence) ||
            receipt.Kind != CoordinatorGateKind.Approval ||
            receipt.SubjectId != intent.IntentId ||
            receipt.RequestId != intent.ApprovalRequestId ||
            receipt.ActorId != actor.Subject ||
            receipt.Fence != binding.Fence ||
            receipt.FreeformAnswer is not null ||
            !state.DecisionReceipts.Contains(receipt) ||
            state.PendingGate is not null ||
            selection.Selection.ProjectId != binding.ProjectId ||
            selection.Selection.RunId != binding.RunId)
            throw new CoordinationException(
                "source_control_approval_binding_invalid", StatusCodes.Status409Conflict);
    }

    private static SourceControlMergeIntent CreateApprovedIntent(
        PendingMergeIntent intent,
        CoordinatorDecisionState state,
        CoordinatorGateDecisionReceipt receipt)
    {
        var assembly = CoordinatorDecisionFlow.RequestAssembly(
            state,
            new CoordinatorAssemblyRequest(
                intent.AssemblyRequestId,
                intent.WorkflowId,
                intent.DefinitionRevision,
                intent.WorkPlanId,
                intent.WorkflowStepId,
                WorkflowPlatformGate.Merge));
        if (!assembly.IsSuccess || assembly.Value is null)
            throw new CoordinationException(
                "source_control_merge_step_no_longer_legal", StatusCodes.Status409Conflict);
        var request = SourceControlMergeIntentRequest.Create(
            intent.IntentId,
            intent.ApprovalRequestId,
            intent.Pin.AcceptedRun,
            intent.Pin,
            intent.SourceStateVersion,
            state,
            assembly.Value,
            new SourceControlPullRequest(
                intent.PullRequestNumber,
                new Uri($"https://github.com/{intent.Pin.Repository.FullName}/pull/{intent.PullRequestNumber}"),
                "open",
                intent.HeadBranch,
                intent.HeadSha,
                intent.BaseBranch,
                intent.BaseSha,
                Merged: false,
                SourceControlPullRequestDisposition.Observed),
            intent.Method,
            intent.CreatedAt);
        return request.Approve(state, receipt);
    }

    private static async Task SetRejectedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string intentsTable,
        SessionIdentity identity,
        string intentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {intentsTable}
            SET intent_state = 'rejected', updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND intent_state = 'approval_pending'
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long CurrentRoleRevision(AuthorizedRunSelection selection, string projectId)
    {
        var revisions = selection.Authorization.EffectiveAuthority
            .Where(entry => entry.ResourceType == "project" && entry.ResourceId == projectId)
            .SelectMany(entry => entry.Permissions)
            .Where(permission => permission.Permission == "acceptRunSelection")
            .Select(permission => permission.RoleRevision)
            .Distinct()
            .ToArray();
        if (revisions.Length != 1 || revisions[0] < 1)
            throw new CoordinationException(
                "source_control_authority_unavailable", StatusCodes.Status403Forbidden);
        return revisions[0];
    }

    private static void AddScope(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
    }

    private static bool IsStableIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private sealed record PendingMergeIntent(
        string IntentId,
        string SessionId,
        string PinId,
        string Issuer,
        string ActorId,
        string TenantId,
        string AcceptedSelectionHash,
        long ProjectRevision,
        long ProjectConfigurationRevision,
        long PlatformRuntimeRevision,
        string ContextRevision,
        long ExecutionFence,
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
        DateTimeOffset CreatedAt,
        SourceControlRepositoryPin Pin,
        Guid? ApprovalRequestDecisionId,
        long? ApprovalRequestStateVersion);
}
