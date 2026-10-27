using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Issues and reads executable-action grants and persists policy-evaluation receipts in PostgreSQL.
/// Each grant and receipt is bound to the accepted run, session writer, execution fence, and current selection authority.
/// </summary>
internal sealed class ExecutableActionGrantOwnerStore(
    NpgsqlDataSource dataSource,
    OrchestratorOptions options,
    TimeProvider timeProvider,
    ProjectsRunSelectionClient projects,
    IHttpContextAccessor httpContextAccessor) :
    IExecutableActionGrantOwnerLookup,
    IExecutableActionSourceReceiptWriter,
    IExecutableActionPolicyEvaluationReceiptWriter
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private const int MaximumIdLength = 256;
    private const string ToolInvocationEventType = "orchestrator.runtime.tool_invocation_admitted";
    private const string RootModelTurnAdmissionTable = "maf_root_model_turn_admissions";
    private readonly string _schema = $"\"{options.Schema}\"";
    private readonly PostgresOutbox _outbox = new(dataSource, options.Schema);
    private readonly RuntimeExecutionEvidenceLedgerStore _executionEvidence = new(options.Schema);
    private readonly RuntimeUsageSourceStore _runtimeSources = new(dataSource, options, timeProvider);

    /// <summary>Stores the exact generation request that may use the accepted coordinator root.</summary>
    internal async Task AdmitCoordinatorRootModelTurnAsync(
        HttpContext context,
        RuntimeRegistration registration,
        Guid requestId,
        string promptHash,
        long decisionStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registration);
        RuntimeContractValidation.Validate(registration);
        RuntimeContractValidation.ValidateHash(promptHash);
        var binding = registration.Binding;
        if (requestId == Guid.Empty || decisionStateVersion < 1 ||
            binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
            binding.WorkflowStepId is not null)
            throw new RuntimeAuthorizationException("runtime_action_binding_invalid");
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        if (actor.Issuer != binding.ActorIssuer || actor.Subject != binding.ActorId ||
            registration.ExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_action_actor_invalid");

        var authorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        var selectionHash = RuntimeContractValidation.Hash(
            Encoding.UTF8.GetBytes(authorized.Selection.Snapshot.GetRawText()));
        var roleRevisions = authorized.Authorization.EffectiveAuthority
            .Where(item => item.ResourceType == "project" && item.ResourceId == binding.ProjectId)
            .SelectMany(item => item.Permissions)
            .Where(item => item.Permission == "acceptRunSelection")
            .Select(item => item.RoleRevision).Distinct().Take(2).ToArray();
        if (roleRevisions.Length != 1 ||
            authorized.Authorization.ActorId != binding.ActorId ||
            authorized.Authorization.Issuer != binding.ActorIssuer ||
            authorized.Authorization.TenantId != binding.TenantId ||
            authorized.Selection.ProjectRevision != binding.ProjectRevision ||
            authorized.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            authorized.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            authorized.Selection.ProjectId != binding.ProjectId ||
            authorized.Selection.RunId != binding.RunId ||
            authorized.Selection.ContextRevision != binding.ContextRevision ||
            selectionHash != binding.AcceptedSelectionHash ||
            registration.ExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_action_selection_changed");
        var selectedModel = RuntimeAcceptedModelSelection.Read(authorized.Selection.Snapshot);
        var limits = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(authorized.Selection.Snapshot);
        if (selectedModel.Reference != binding.ModelSelectionReference ||
            selectedModel.SourceMode != binding.ModelSourceMode ||
            selectedModel.CredentialReference != binding.ModelCredentialReference ||
            selectedModel.ConnectionId != binding.ModelConnectionId ||
            selectedModel.ConnectionScope != binding.ModelConnectionScope ||
            selectedModel.ReasoningEffort != binding.ReasoningEffort ||
            limits.MaxModelTurns != binding.MaxModelTurns ||
            limits.MaxToolCalls != binding.MaxToolCalls)
            throw new RuntimeAuthorizationException("runtime_action_selection_changed");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var available = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection))
        {
            available.Parameters.AddWithValue(
                "table", NpgsqlDbType.Text, $"{options.Schema}.{RootModelTurnAdmissionTable}");
            if (!(bool)(await available.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var owner = await ReadCoordinatorRootDecisionAsync(
            connection, transaction, binding, cancellationToken).ConfigureAwait(false);
        if (owner.StateVersion != decisionStateVersion)
            throw new RuntimeAuthorizationException("runtime_model_turn_admission_stale");
        await RequireCoordinatorRootDecisionAsync(
            connection, transaction, owner.DecisionJson, registration, owner.StateVersion,
            requestId, promptHash, "model.turn", cancellationToken).ConfigureAwait(false);
        // Keep the run, root session, and accepted decision locked through the immutable admission write.
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.{RootModelTurnAdmissionTable}
                (project_id, run_id, root_session_id, request_id, actor_issuer, actor_id, tenant_id,
                 accepted_selection_hash, prompt_hash, execution_fence, decision_id, decision_state_version)
            VALUES
                (@project, @run, @session, @request, @issuer, @actor, @tenant,
                 @selectionHash, @prompt, @fence, @decision, @decisionVersion)
            ON CONFLICT (project_id, run_id, request_id) DO NOTHING
            """, connection, transaction))
        {
            BindRuntime(insert, binding);
            insert.Parameters["selectionHash"].Value = binding.AcceptedSelectionHash;
            insert.Parameters.AddWithValue("request", NpgsqlDbType.Uuid, requestId);
            insert.Parameters.AddWithValue("prompt", NpgsqlDbType.Char, promptHash);
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, owner.DecisionId);
            insert.Parameters.AddWithValue("decisionVersion", NpgsqlDbType.Bigint, owner.StateVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var exact = new NpgsqlCommand($"""
            SELECT root_session_id, actor_issuer, actor_id, tenant_id, accepted_selection_hash,
                   prompt_hash, execution_fence, decision_id, decision_state_version
            FROM {_schema}.{RootModelTurnAdmissionTable}
            WHERE project_id = @project AND run_id = @run AND request_id = @request
            """, connection, transaction))
        {
            exact.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            exact.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            exact.Parameters.AddWithValue("request", NpgsqlDbType.Uuid, requestId);
            await using var reader = await exact.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != binding.SessionId ||
                reader.GetString(1) != binding.ActorIssuer ||
                reader.GetString(2) != binding.ActorId ||
                reader.GetString(3) != binding.TenantId ||
                reader.GetString(4).Trim() != binding.AcceptedSelectionHash ||
                reader.GetString(5).Trim() != promptHash ||
                reader.GetInt64(6) != binding.ExecutionFence ||
                reader.GetGuid(7) != owner.DecisionId ||
                reader.GetInt64(8) != owner.StateVersion)
                throw new RuntimeAuthorizationException("runtime_model_turn_admission_conflict");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues a runtime action grant after validating the registration, accepted selection, and active child session.
    /// </summary>
    /// <param name="context">The authenticated request context used to reread run authority.</param>
    /// <param name="registration">The runtime registration and its immutable owner binding.</param>
    /// <param name="request">The runtime action request to authorize.</param>
    /// <param name="cancellationToken">Cancels database and authority reads.</param>
    /// <param name="reserveToolInvocation">When true, reserves a tool call under a run-scoped lock and enforces its limit.</param>
    /// <returns>The grant reference derived from the action event ID.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when registration, actor, decision, selection, or dispatch admission is stale or invalid.</exception>
    /// <remarks>Grant creation and any tool-invocation outbox reservation commit together, making retries idempotent by event ID and request hash.</remarks>
    internal async Task<ExecutableActionGrantReference> IssueRuntimeActionGrantAsync(
        HttpContext context, RuntimeRegistration registration, RuntimeActionRequest request,
        CancellationToken cancellationToken, bool reserveToolInvocation = false)
    {
        RuntimeActionContract.Validate(request);
        var binding = registration.Binding;
        var isCoordinatorRoot = binding.AgentId == RuntimeActionContract.CoordinatorRootStepId &&
            binding.WorkflowStepId is null;
        if ((binding.AgentId == RuntimeActionContract.CoordinatorRootStepId && !isCoordinatorRoot) ||
            (isCoordinatorRoot &&
             ((request.ActionId != "model.turn" &&
                !(request.IsToolInvocation && request.ActionId is ("tool.read" or "tool.write"))) ||
              (request.ActionId == "model.turn" && request.DispatchId != request.EventId))))
            throw new RuntimeAuthorizationException("runtime_action_binding_invalid");
        RuntimeActionContract.RequireNativeDispatchAdmission(binding, request);
        if (request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.RegistrationRevision != registration.Revision ||
            request.ExecutionFence != binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_action_binding_invalid");
        var step = RuntimeActionContract.RequireActionStepId(binding);
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        if (actor.Issuer != binding.ActorIssuer || actor.Subject != binding.ActorId)
            throw new RuntimeAuthorizationException("runtime_action_actor_invalid");
        var reference = new ExecutableActionGrantReference(request.EventId.ToString("N"), "1");
        var requestHash = RuntimeActionContract.Hash(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var requiresNumericEvidence =
            isCoordinatorRoot && request.ActionId == "model.turn" && binding.MaxModelTurns is not null ||
            reserveToolInvocation && request.IsToolInvocation && binding.MaxToolCalls is not null;
        var sendStateBefore = requiresNumericEvidence
            ? await RuntimeNumericLimitSendState.ReadAsync(
                connection, transaction, _schema, binding.ProjectId, binding.RunId,
                request.EventId, cancellationToken).ConfigureAwait(false)
            : null;
        if (reserveToolInvocation)
        {
            if (!request.IsToolInvocation)
                throw new RuntimeAuthorizationException("runtime_tool_invocation_invalid");
            await using var acquire = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('agentweaver.runtime.tools'), hashtext(@runScope))",
                connection, transaction);
            acquire.Parameters.AddWithValue("runScope", $"{binding.ProjectId}/{binding.RunId}");
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        Guid decisionId;
        long decisionVersion;
        string? rootDecisionJson = null;
        if (isCoordinatorRoot)
        {
            var owner = await ReadCoordinatorRootDecisionAsync(
                connection, transaction, binding, cancellationToken).ConfigureAwait(false);
            decisionId = owner.DecisionId;
            decisionVersion = owner.StateVersion;
            rootDecisionJson = owner.DecisionJson;
        }
        else
        {
            var ownerQuery = $"""
                SELECT d.decision_id, d.state_version, NULL::text
                FROM {_schema}.coordination_sessions s
                JOIN {_schema}.accepted_runs r ON r.project_id = s.project_id AND r.run_id = s.run_id
                JOIN {_schema}.coordinator_decisions d
                  ON d.project_id = s.project_id AND d.run_id = s.run_id AND d.session_id = s.root_session_id
                WHERE s.project_id = @project AND s.run_id = @run AND s.session_id = @session
                  AND s.writer_issuer = @issuer AND s.writer_subject = @actor
                  AND s.node_kind = 'child_work' AND s.lifecycle_state = 'active' AND s.turn_state = 'active'
                  AND s.execution_fence = @fence AND r.execution_fence = @fence
                  AND r.execution_state <> 'completed' AND r.tenant_id = @tenant
                  AND r.accepted_selection_hash = @selectionHash AND d.decision_state = 'accepted'
                ORDER BY d.state_version DESC LIMIT 1
                FOR SHARE OF s, r, d
                """;
            await using var current = new NpgsqlCommand(ownerQuery, connection, transaction);
            BindRuntime(current, binding);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RuntimeAuthorizationException("runtime_action_owner_unavailable");
            decisionId = reader.GetGuid(0);
            decisionVersion = reader.GetInt64(1);
        }
        if (isCoordinatorRoot && request.ActionId == "model.turn" && binding.MaxModelTurns is not null)
        {
            // Native preparation locks the root row before this execution lock; keep the same order here.
            await using var acquire = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('agentweaver.maf.execution'), hashtext(@scope))",
                connection, transaction);
            acquire.Parameters.AddWithValue("scope", JsonSerializer.Serialize(
                new[] { binding.ProjectId, binding.RunId, binding.SessionId }));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        // Authority is read after the owner row-lock wait, before grant publication.
        var authorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        var roleRevisions = authorized.Authorization.EffectiveAuthority
            .Where(item => item.ResourceType == "project" && item.ResourceId == binding.ProjectId)
            .SelectMany(item => item.Permissions)
            .Where(item => item.Permission == "acceptRunSelection")
            .Select(item => item.RoleRevision).Distinct().Take(2).ToArray();
        if (roleRevisions is not [var roleRevision] ||
            authorized.Authorization.ActorId != binding.ActorId ||
            authorized.Authorization.Issuer != binding.ActorIssuer ||
            authorized.Authorization.TenantId != binding.TenantId ||
            authorized.Selection.ProjectRevision != binding.ProjectRevision ||
            authorized.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            authorized.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            authorized.Selection.ProjectId != binding.ProjectId ||
            authorized.Selection.RunId != binding.RunId ||
            authorized.Selection.ContextRevision != binding.ContextRevision ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(authorized.Selection.Snapshot.GetRawText())) !=
                binding.AcceptedSelectionHash || registration.ExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_action_selection_changed");
        if (isCoordinatorRoot)
        {
            await RequireCoordinatorRootDecisionAsync(
                connection, transaction, rootDecisionJson, registration, decisionVersion,
                request.EventId, request.InputHash, request.ActionId, cancellationToken).ConfigureAwait(false);
            var selectedModel = RuntimeAcceptedModelSelection.Read(authorized.Selection.Snapshot);
            var limits = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(authorized.Selection.Snapshot);
            if (selectedModel.Reference != binding.ModelSelectionReference ||
                selectedModel.SourceMode != binding.ModelSourceMode ||
                selectedModel.CredentialReference != binding.ModelCredentialReference ||
                selectedModel.ConnectionId != binding.ModelConnectionId ||
                selectedModel.ConnectionScope != binding.ModelConnectionScope ||
                selectedModel.ReasoningEffort != binding.ReasoningEffort ||
                limits.MaxModelTurns != binding.MaxModelTurns ||
                limits.MaxToolCalls != binding.MaxToolCalls)
                throw new RuntimeAuthorizationException("runtime_action_selection_changed");
            if (request.ActionId == "model.turn")
            {
                var currentProjectSpendBudgets = await projects.ReadCurrentProjectSpendBudgetsAsync(
                    context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
                RequireCoordinatorRootSpendBudgetsSupported(
                    CoordinatorWorkflowCatalog.ReadSpendBudgets(authorized.Selection.Snapshot),
                    currentProjectSpendBudgets.Budgets);
            }
            if (request.ActionId == "model.turn" &&
                !await HasCoordinatorRootModelTurnAdmissionAsync(
                    connection, transaction, binding, request, decisionId, decisionVersion, cancellationToken)
                    .ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_model_turn_admission_required");
            if (request.ActionId == "model.turn" && binding.MaxModelTurns is { } maxModelTurns)
            {
                var observedCount = await RuntimeModelTurnReservationStore.CountRunWideAsync(
                        connection, transaction, _schema, binding.ProjectId, binding.RunId,
                        binding.SessionId, request.EventId, cancellationToken).ConfigureAwait(false);
                if (observedCount >= maxModelTurns)
                    throw new RuntimeNumericLimitDeniedException(
                        "runtime_model_turn_limit_exhausted", RuntimeNumericLimitKind.ModelTurns,
                        maxModelTurns, observedCount, decisionVersion,
                        sendStateBefore?.Hash ?? throw new RuntimeAuthorizationException(
                            "runtime_numeric_limit_send_state_unavailable"),
                        sendStateBefore?.IsEmpty ?? false);
            }
        }
        if (!isCoordinatorRoot && request.ActionId == "model.turn" && request.DispatchId is { } dispatchId)
        {
            await using var dispatch = new NpgsqlCommand($"""
                SELECT 1 FROM {_schema}.maf_execution_dispatches
                WHERE project_id = @project AND run_id = @run AND dispatch_id = @dispatch
                    AND child_session_id = @session AND runtime_instance_id = @runtime
                    AND registration_revision = @revision AND execution_fence = @fence
                    AND runtime_binding_hash = @binding AND prompt_hash = @prompt
                    AND dispatch_state IN ('prepared', 'sending')
                """, connection, transaction);
            BindRuntime(dispatch, binding);
            dispatch.Parameters.AddWithValue("dispatch", dispatchId);
            dispatch.Parameters.AddWithValue("runtime", registration.RuntimeInstanceId);
            dispatch.Parameters.AddWithValue("revision", registration.Revision);
            dispatch.Parameters.AddWithValue("binding", RuntimeContractValidation.RegistrationHash(registration));
            dispatch.Parameters.AddWithValue("prompt", request.InputHash);
            if (await dispatch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new RuntimeAuthorizationException("runtime_model_turn_admission_required");
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.executable_action_grants
                (project_id, run_id, grant_id, revision, is_current, grant_state,
                 issuer, actor_id, tenant_id, session_id, step_id, action_ids, purpose,
                 project_revision, project_configuration_revision, platform_runtime_revision,
                 context_revision, accepted_selection_hash, membership_revision, role_revision,
                 execution_fence, expires_at, source_state_version, source_decision_id, source_request_id)
            VALUES
                (@project, @run, @grant, '1', true, 'active',
                 @issuer, @actor, @tenant, @session, @step, @actions, @purpose,
                 @projectRevision, @configurationRevision, @platformRevision,
                 @contextRevision, @selectionHash, @membershipRevision, @roleRevision,
                 @fence, @expiresAt, @stateVersion, @decision, @requestHash)
            ON CONFLICT (project_id, run_id, grant_id, revision) DO NOTHING
            """, connection, transaction))
        {
            BindRuntime(insert, binding);
            insert.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, reference.GrantId);
            insert.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, step);
            insert.Parameters.AddWithValue("actions", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new[] { request.ActionId }));
            insert.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, RuntimeActionContract.Purpose);
            insert.Parameters.AddWithValue("projectRevision", NpgsqlDbType.Bigint, binding.ProjectRevision);
            insert.Parameters.AddWithValue("configurationRevision", NpgsqlDbType.Bigint, binding.ProjectConfigurationRevision);
            insert.Parameters.AddWithValue("platformRevision", NpgsqlDbType.Bigint, binding.PlatformRuntimeRevision);
            insert.Parameters.AddWithValue("contextRevision", NpgsqlDbType.Varchar, binding.ContextRevision);
            insert.Parameters.AddWithValue("membershipRevision", NpgsqlDbType.Bigint, authorized.Authorization.MembershipRevision);
            insert.Parameters.AddWithValue("roleRevision", NpgsqlDbType.Bigint, roleRevision);
            insert.Parameters.AddWithValue("expiresAt", NpgsqlDbType.TimestampTz, registration.ExpiresAt);
            insert.Parameters.AddWithValue("stateVersion", NpgsqlDbType.Bigint, decisionVersion);
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
            insert.Parameters.AddWithValue("requestHash", NpgsqlDbType.Varchar, requestHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var exact = new NpgsqlCommand($"""
            SELECT source_request_id FROM {_schema}.executable_action_grants
            WHERE project_id = @project AND run_id = @run AND grant_id = @grant AND revision = '1'
            """, connection, transaction))
        {
            exact.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            exact.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            exact.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, reference.GrantId);
            if ((string?)await exact.ExecuteScalarAsync(cancellationToken) != requestHash)
                throw new RuntimeAuthorizationException("runtime_action_conflict");
        }
        if (reserveToolInvocation && !await HasToolInvocationReservationAsync(
            connection, transaction, request, cancellationToken).ConfigureAwait(false))
        {
            if (binding.MaxToolCalls is { } maxToolCalls)
            {
                await using var count = new NpgsqlCommand($"""
                    SELECT count(*) FROM {_schema}.outbox_events
                    WHERE event_type = @type
                        AND payload->>'projectId' = @project AND payload->>'runId' = @run
                    """, connection, transaction);
                count.Parameters.AddWithValue("type", ToolInvocationEventType);
                count.Parameters.AddWithValue("project", binding.ProjectId);
                count.Parameters.AddWithValue("run", binding.RunId);
                var observedCount = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                if (observedCount >= maxToolCalls)
                    throw new RuntimeNumericLimitDeniedException(
                        "runtime_tool_call_limit_exhausted", RuntimeNumericLimitKind.ToolCalls,
                        maxToolCalls, observedCount, decisionVersion,
                        sendStateBefore?.Hash ?? throw new RuntimeAuthorizationException(
                            "runtime_numeric_limit_send_state_unavailable"),
                        sendStateBefore?.IsEmpty ?? false);
            }
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
                request.EventId, $"coordination/{binding.ProjectId}/{binding.RunId}/runtime-tool-invocations",
                $"runtime-tool-invocation/{request.EventId:N}", ToolInvocationEventType, 1,
                JsonSerializer.SerializeToElement(new
                {
                    projectId = binding.ProjectId, runId = binding.RunId, binding.SessionId,
                    binding.ExecutionFence, binding.MaxToolCalls, requestHash
                }), timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken);
        return reference;
    }

    internal async Task<RuntimeNumericLimitDenialReceipt> RecordNumericLimitDenialAsync(
        HttpContext context,
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        RuntimeNumericLimitDeniedException denial,
        CancellationToken cancellationToken)
    {
        RuntimeActionContract.Validate(request);
        var binding = registration.Binding;
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var authorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        var roleRevisions = authorized.Authorization.EffectiveAuthority
            .Where(item => item.ResourceType == "project" && item.ResourceId == binding.ProjectId)
            .SelectMany(item => item.Permissions)
            .Where(item => item.Permission == "acceptRunSelection")
            .Select(item => item.RoleRevision).Distinct().Take(2).ToArray();
        if (roleRevisions.Length != 1 || actor.Issuer != binding.ActorIssuer ||
            actor.Subject != binding.ActorId || authorized.Authorization.ActorId != binding.ActorId ||
            authorized.Authorization.Issuer != binding.ActorIssuer ||
            authorized.Authorization.TenantId != binding.TenantId ||
            authorized.Selection.ProjectId != binding.ProjectId ||
            authorized.Selection.RunId != binding.RunId ||
            authorized.Selection.ProjectRevision != binding.ProjectRevision ||
            authorized.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            authorized.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            authorized.Selection.ContextRevision != binding.ContextRevision ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
                authorized.Selection.Snapshot.GetRawText())) != binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (denial.Kind == RuntimeNumericLimitKind.ToolCalls)
        {
            await using var acquire = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('agentweaver.runtime.tools'), hashtext(@runScope))",
                connection, transaction);
            acquire.Parameters.AddWithValue("runScope", $"{binding.ProjectId}/{binding.RunId}");
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Cancellation locks accepted_runs before changing session rows. Take that lock explicitly
        // before the session/decision locks below to preserve the same order and avoid a deadlock.
        await using (var run = new NpgsqlCommand($"""
            SELECT tenant_id, accepted_selection_hash, execution_fence, execution_state
            FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            FOR SHARE
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            run.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetString(0) != binding.TenantId ||
                !string.Equals(reader.GetString(1).Trim(), binding.AcceptedSelectionHash,
                    StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt64(2) != binding.ExecutionFence ||
                reader.GetString(3) is "completed" or "failed" or "indeterminate" or "cancelled")
                throw new RuntimeAuthorizationException("runtime_action_owner_unavailable");
        }

        await using (var current = new NpgsqlCommand($"""
            SELECT h.current_revision, r.state, r.expires_at, r.binding_hash
            FROM {_schema}.runtime_registration_heads h
            JOIN {_schema}.runtime_registration_revisions r
              ON r.runtime_instance_id = h.runtime_instance_id AND r.revision = h.current_revision
            WHERE h.runtime_instance_id = @runtime
            FOR SHARE OF h
            """, connection, transaction))
        {
            current.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt64(0) != registration.Revision || reader.GetInt32(1) != 0 ||
                reader.GetFieldValue<DateTimeOffset>(2) != registration.ExpiresAt ||
                reader.GetFieldValue<DateTimeOffset>(2) <= timeProvider.GetUtcNow() ||
                reader.GetString(3).Trim() != RuntimeContractValidation.Hash(
                    JsonSerializer.SerializeToUtf8Bytes(binding)))
                throw new RuntimeAuthorizationException("runtime_registration_stale");
        }

        long ownerRevision;
        Guid decisionId;
        string rootSessionId;
        await using (var owner = new NpgsqlCommand($"""
            SELECT s.root_session_id, d.decision_id, d.state_version
            FROM {_schema}.coordination_sessions s
            JOIN {_schema}.accepted_runs r ON r.project_id = s.project_id AND r.run_id = s.run_id
            JOIN {_schema}.coordinator_decisions d
              ON d.project_id = s.project_id AND d.run_id = s.run_id AND d.session_id = s.root_session_id
            WHERE s.project_id = @project AND s.run_id = @run AND s.session_id = @session
              AND s.lifecycle_state = 'active' AND s.execution_fence = @fence
              AND r.execution_fence = @fence AND r.execution_state <> 'completed'
              AND r.tenant_id = @tenant AND r.accepted_selection_hash = @selectionHash
              AND d.decision_state = 'accepted'
            ORDER BY d.state_version DESC LIMIT 1
              FOR SHARE OF s, d
            """, connection, transaction))
        {
            BindRuntime(owner, binding);
            await using var reader = await owner.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RuntimeAuthorizationException("runtime_action_owner_unavailable");
            rootSessionId = reader.GetString(0);
            decisionId = reader.GetGuid(1);
            ownerRevision = reader.GetInt64(2);
        }
        if (ownerRevision != denial.OwnerRevision)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete");

        if (denial.Kind == RuntimeNumericLimitKind.ModelTurns &&
            binding.AgentId == RuntimeActionContract.CoordinatorRootStepId &&
            binding.WorkflowStepId is null)
        {
            // Match native preparation and root grant admission: lock the active owner before the MAF execution lock.
            await using var acquire = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('agentweaver.maf.execution'), hashtext(@scope))",
                connection, transaction);
            acquire.Parameters.AddWithValue("scope", JsonSerializer.Serialize(
                new[] { binding.ProjectId, binding.RunId, binding.SessionId }));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var currentAuthorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        var currentRoleRevisions = currentAuthorized.Authorization.EffectiveAuthority
            .Where(item => item.ResourceType == "project" && item.ResourceId == binding.ProjectId)
            .SelectMany(item => item.Permissions)
            .Where(item => item.Permission == "acceptRunSelection")
            .Select(item => item.RoleRevision).Distinct().Take(2).ToArray();
        var currentActor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        if (currentRoleRevisions.Length != 1 || currentActor.Issuer != binding.ActorIssuer ||
            currentActor.Subject != binding.ActorId ||
            currentAuthorized.Authorization.ActorId != binding.ActorId ||
            currentAuthorized.Authorization.Issuer != binding.ActorIssuer ||
            currentAuthorized.Authorization.TenantId != binding.TenantId ||
            currentAuthorized.Selection.ProjectId != binding.ProjectId ||
            currentAuthorized.Selection.RunId != binding.RunId ||
            currentAuthorized.Selection.ProjectRevision != binding.ProjectRevision ||
            currentAuthorized.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            currentAuthorized.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            currentAuthorized.Selection.ContextRevision != binding.ContextRevision ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
                currentAuthorized.Selection.Snapshot.GetRawText())) != binding.AcceptedSelectionHash ||
            registration.ExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete");

        if (denial.Kind == RuntimeNumericLimitKind.ModelTurns &&
            binding.AgentId == RuntimeActionContract.CoordinatorRootStepId &&
            binding.WorkflowStepId is null)
        {
            if (request.DispatchId != request.EventId ||
                !await HasCoordinatorRootModelTurnAdmissionAsync(
                    connection, transaction, binding, request, decisionId, ownerRevision, cancellationToken)
                    .ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete");
        }

        var source = await _runtimeSources.ReadCurrentSourceAsync(
            connection, transaction, registration, cancellationToken).ConfigureAwait(false);
        var count = denial.Kind switch
        {
            RuntimeNumericLimitKind.ModelTurns when binding.AgentId == RuntimeActionContract.CoordinatorRootStepId =>
                await RuntimeModelTurnReservationStore.CountRunWideAsync(
                    connection, transaction, _schema, binding.ProjectId, binding.RunId,
                    rootSessionId, request.EventId, cancellationToken).ConfigureAwait(false),
            RuntimeNumericLimitKind.ToolCalls => await ReadToolInvocationCountAsync(
                connection, transaction, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false),
            _ => throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete")
        };
        var acceptedLimit = denial.Kind switch
        {
            RuntimeNumericLimitKind.ModelTurns => binding.MaxModelTurns,
            RuntimeNumericLimitKind.ToolCalls => binding.MaxToolCalls,
            _ => null
        };
        if (acceptedLimit != denial.Limit || count != denial.ObservedCount || count < denial.Limit)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete");
        var sendStateAfter = await RuntimeNumericLimitSendState.ReadAsync(
            connection, transaction, _schema, binding.ProjectId, binding.RunId,
            request.EventId, cancellationToken).ConfigureAwait(false);
        if (!denial.SendStateEmpty || !sendStateAfter.IsEmpty ||
            sendStateAfter.Hash != denial.SendStateHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_send_state_changed");

        var dispatchId = request.DispatchId;
        string? checkpointId = null;
        long? checkpointRevision = null;
        string? checkpointHash = null;
        long? checkpointDecisionVersion = null;
        string? workPlanId = null;
        string? associationId = null;
        if (request.DispatchId is { } admittedDispatch)
        {
            if (!(denial.Kind == RuntimeNumericLimitKind.ModelTurns &&
                  binding.AgentId == RuntimeActionContract.CoordinatorRootStepId &&
                  binding.WorkflowStepId is null))
            {
                await using var checkpoint = new NpgsqlCommand($"""
                    SELECT d.root_session_id, d.checkpoint_id, d.checkpoint_revision,
                        d.decision_state_version, d.work_plan_id, d.association_id, c.payload::text
                    FROM {_schema}.maf_execution_dispatches d
                    JOIN {_schema}.maf_workflow_checkpoints c
                      ON c.project_id = d.project_id AND c.run_id = d.run_id
                        AND c.session_id = d.root_session_id AND c.store_name = d.checkpoint_store_name
                        AND c.checkpoint_id = d.checkpoint_id
                    WHERE d.project_id = @project AND d.run_id = @run AND d.dispatch_id = @dispatch
                      AND d.child_session_id = @session AND d.runtime_instance_id = @runtime
                      AND d.registration_revision = @registration AND d.execution_fence = @fence
                      AND d.accepted_selection_hash = @dispatchSelectionHash
                    FOR SHARE OF d, c
                    """, connection, transaction);
                BindRuntime(checkpoint, binding);
                checkpoint.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, admittedDispatch);
                checkpoint.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
                checkpoint.Parameters.AddWithValue("registration", NpgsqlDbType.Bigint, registration.Revision);
                checkpoint.Parameters.AddWithValue(
                    "dispatchSelectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
                await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete");
                if (reader.GetString(0) != rootSessionId)
                    throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
                checkpointId = reader.GetString(1);
                checkpointRevision = reader.GetInt64(2);
                checkpointDecisionVersion = reader.GetInt64(3);
                workPlanId = reader.GetString(4);
                associationId = reader.GetString(5);
                checkpointHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(reader.GetString(6)));
            }
        }

        var receipt = RuntimeNumericLimitDenialReceipt.Seal(new(
            1, RuntimeNumericLimitDenialIdentity.ReceiptId(binding.ProjectId, binding.RunId, request.EventId),
            binding.ProjectId, binding.RunId, rootSessionId, binding.SessionId,
            registration.RuntimeInstanceId, registration.Revision, source.RegistrationRevision,
            source.SourceGrantId, source.CanonicalPayloadHash, request.EventId,
            "1",
            binding.ExecutionFence, binding.AcceptedSelectionHash, ownerRevision, denial.Kind,
            denial.Limit, count, request.EventId, RuntimeActionContract.Hash(request), dispatchId,
            checkpointId, checkpointRevision, checkpointHash, checkpointDecisionVersion,
            workPlanId, associationId, denial.SendStateHash, sendStateAfter.Hash, timeProvider.GetUtcNow(),
            string.Empty));
        var stored = await _executionEvidence.AppendNumericLimitDenialAsync(
            connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
        if (registration.ExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_incomplete");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    internal async Task<RuntimeNumericLimitDenialReadResult> ReadNumericLimitDenialAsync(
        RuntimeRegistration registration,
        RuntimeNumericLimitDenialReadRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeNumericLimitDenialReadContract.ValidateRequest(request);
        RuntimeNumericLimitDenialReadContract.ValidateResult(
            new(null), registration, request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        long ownerRevision;
        string rootSessionId;
        await using (var owner = new NpgsqlCommand($"""
            SELECT s.root_session_id, d.state_version
            FROM {_schema}.coordination_sessions s
            JOIN {_schema}.accepted_runs r ON r.project_id = s.project_id AND r.run_id = s.run_id
            JOIN {_schema}.coordinator_decisions d
              ON d.project_id = s.project_id AND d.run_id = s.run_id AND d.session_id = s.root_session_id
            WHERE s.project_id = @project AND s.run_id = @run AND s.session_id = @session
              AND s.lifecycle_state = 'active' AND s.execution_fence = @fence
              AND r.execution_fence = @fence AND r.execution_state <> 'completed'
              AND r.tenant_id = @tenant AND r.accepted_selection_hash = @selectionHash
              AND d.decision_state = 'accepted'
            ORDER BY d.state_version DESC LIMIT 1
            FOR SHARE OF s, r, d
            """, connection, transaction))
        {
            BindRuntime(owner, binding);
            await using var reader = await owner.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_action_owner_unavailable");
            rootSessionId = reader.GetString(0);
            ownerRevision = reader.GetInt64(1);
        }
        var source = await _runtimeSources.ReadCurrentSourceAsync(
            connection, transaction, registration, cancellationToken).ConfigureAwait(false);
        var receipt = await _executionEvidence.ReadNumericLimitDenialAsync(
            connection, transaction, binding.ProjectId, binding.RunId, request.AttemptId,
            cancellationToken).ConfigureAwait(false);
        if (receipt is not null &&
            (receipt.RootSessionId != rootSessionId || receipt.OwnerRevision != ownerRevision ||
             receipt.SourceHash != source.CanonicalPayloadHash ||
             receipt.SourceGrantId != source.SourceGrantId))
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        var result = RuntimeNumericLimitDenialReadContract.ValidateResult(
            new(receipt), registration, request);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task<RuntimeReplacementComparisonBlocker> ReadReplacementComparisonBlockerAsync(
        RuntimeRegistration registration,
        RuntimeReplacementComparisonRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeReplacementComparisonContract.ValidateRequest(request);
        var binding = registration.Binding;
        if (request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.RegistrationRevision != registration.Revision ||
            request.ExecutionFence != binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_conflict");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string checkpointId;
        long checkpointRevision;
        long fence;
        string workPlanId;
        string associationId;
        await using (var dispatch = new NpgsqlCommand($"""
            SELECT d.checkpoint_id, d.checkpoint_revision, d.execution_fence,
                d.work_plan_id, d.association_id
            FROM {_schema}.maf_execution_dispatches d
            JOIN {_schema}.accepted_runs r
              ON r.project_id = d.project_id AND r.run_id = d.run_id
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = d.project_id AND s.run_id = d.run_id
                AND s.session_id = d.child_session_id
            WHERE d.project_id = @project AND d.run_id = @run AND d.dispatch_id = @dispatch
              AND d.child_session_id = @session AND d.runtime_instance_id = @runtime
              AND d.registration_revision = @registration AND d.execution_fence = @fence
              AND d.accepted_selection_hash = @dispatchSelectionHash
              AND r.execution_fence = @fence AND r.tenant_id = @tenant
              AND r.accepted_selection_hash = @selectionHash
              AND r.execution_state <> 'completed'
              AND s.lifecycle_state = 'active' AND s.execution_fence = @fence
            FOR SHARE OF d, r, s
            """, connection, transaction))
        {
            BindRuntime(dispatch, binding);
            dispatch.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, request.DispatchId);
            dispatch.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
            dispatch.Parameters.AddWithValue("registration", NpgsqlDbType.Bigint, registration.Revision);
            dispatch.Parameters.AddWithValue(
                "dispatchSelectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
            await using var reader = await dispatch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_replacement_comparison_conflict");
            checkpointId = reader.GetString(0);
            checkpointRevision = reader.GetInt64(1);
            fence = reader.GetInt64(2);
            workPlanId = reader.GetString(3);
            associationId = reader.GetString(4);
        }
        var blocker = new RuntimeReplacementComparisonBlocker(
            "blocked", request.InvocationId, checkpointId, checkpointRevision, workPlanId,
            associationId, request.DispatchId, fence,
            [
                "environment.replacement-lineage",
                "environment.pod-observation-identity",
                "host.events.cursor-interval",
                "host.events.effect-receipt",
                "host.effects.sequence-baseline",
                "host.events.durable-acknowledgment",
                "orchestrator.invocation-checkpoint-binding",
                "orchestrator.result-hash-binding",
                "orchestrator.pre-post-fence-observation",
                "orchestrator.no-duplicate-send-event-effect"
            ]);
        var result = RuntimeReplacementComparisonContract.ValidateResult(
            blocker, registration, request);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Requires that a runtime tool action already has a matching admission reservation.
    /// </summary>
    /// <param name="registration">The runtime registration that owns the request.</param>
    /// <param name="request">The tool invocation request to verify.</param>
    /// <param name="cancellationToken">Cancels the outbox read.</param>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the request is not bound to the registration or lacks its reservation.</exception>
    internal async Task RequireToolInvocationReservationAsync(
        RuntimeRegistration registration, RuntimeActionRequest request, CancellationToken cancellationToken)
    {
        if (!request.IsToolInvocation || request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.RegistrationRevision != registration.Revision ||
            request.ExecutionFence != registration.Binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_tool_invocation_invalid");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await HasToolInvocationReservationAsync(
            connection, null, request, cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_tool_invocation_admission_required");
    }

    /// <summary>
    /// Checks the outbox for a reservation matching this event ID and request hash.
    /// </summary>
    /// <param name="connection">The database connection.</param>
    /// <param name="transaction">The optional transaction that must contain the reservation.</param>
    /// <param name="request">The tool action whose reservation is checked.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <returns><see langword="true"/> for a matching reservation, or <see langword="false"/> when no event exists.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the event ID belongs to a different event or request hash.</exception>
    private async Task<bool> HasToolInvocationReservationAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand($"""
            SELECT event_type, payload->>'requestHash'
            FROM {_schema}.outbox_events WHERE id = @event
            """, connection, transaction);
        read.Parameters.AddWithValue("event", request.EventId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return false;
        if (reader.GetString(0) != ToolInvocationEventType ||
            reader.IsDBNull(1) || reader.GetString(1) != RuntimeActionContract.Hash(request))
            throw new RuntimeAuthorizationException("runtime_tool_invocation_conflict");
        return true;
    }

    private async Task<long> ReadToolInvocationCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var count = new NpgsqlCommand($"""
            SELECT count(*) FROM {_schema}.outbox_events
            WHERE event_type = @type
              AND payload->>'projectId' = @project AND payload->>'runId' = @run
            """, connection, transaction);
        count.Parameters.AddWithValue("type", ToolInvocationEventType);
        count.Parameters.AddWithValue("project", projectId);
        count.Parameters.AddWithValue("run", runId);
        return (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Adds the common project, run, session, actor, tenant, fence, and selection parameters to an owner query.
    /// </summary>
    private static void BindRuntime(NpgsqlCommand command, RuntimeBinding binding)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, binding.ActorIssuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, binding.ActorId);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash.ToUpperInvariant());
    }

    private async Task<(Guid DecisionId, long StateVersion, string DecisionJson)> ReadCoordinatorRootDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeBinding binding,
        CancellationToken cancellationToken)
    {
        // Cancellation locks the run before changing session rows; keep the same row-lock order here.
        await using (var run = new NpgsqlCommand($"""
            SELECT tenant_id, accepted_selection_hash, execution_fence, execution_state
            FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            FOR SHARE
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            run.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != binding.TenantId ||
                !string.Equals(reader.GetString(1).Trim(), binding.AcceptedSelectionHash,
                    StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt64(2) != binding.ExecutionFence ||
                reader.GetString(3) is "completed" or "failed" or "indeterminate" or "cancelled")
                throw new RuntimeAuthorizationException("runtime_action_owner_unavailable");
        }

        await using (var session = new NpgsqlCommand($"""
            SELECT writer_issuer, writer_subject, node_kind, parent_session_id,
                   root_session_id, lifecycle_state, execution_fence
            FROM {_schema}.coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            FOR SHARE
            """, connection, transaction))
        {
            session.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            session.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            session.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
            await using var reader = await session.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != binding.ActorIssuer ||
                reader.GetString(1) != binding.ActorId ||
                reader.GetString(2) != "coordinator" ||
                !reader.IsDBNull(3) ||
                reader.GetString(4) != binding.SessionId ||
                reader.GetString(5) != "active" ||
                reader.GetInt64(6) != binding.ExecutionFence)
                throw new RuntimeAuthorizationException("runtime_action_owner_unavailable");
        }

        await using var decision = new NpgsqlCommand($"""
            SELECT decision_id, state_version, decision::text
            FROM {_schema}.coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND decision_state = 'accepted'
            ORDER BY state_version DESC LIMIT 1
            FOR SHARE
            """, connection, transaction);
        decision.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        decision.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
        decision.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
        await using var decisionReader = await decision.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await decisionReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_action_owner_unavailable");
        return (decisionReader.GetGuid(0), decisionReader.GetInt64(1), decisionReader.GetString(2));
    }

    private async Task<bool> HasCoordinatorRootModelTurnAdmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeBinding binding,
        RuntimeActionRequest request,
        Guid decisionId,
        long decisionVersion,
        CancellationToken cancellationToken)
    {
        await using (var available = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection, transaction))
        {
            available.Parameters.AddWithValue(
                "table", NpgsqlDbType.Text, $"{options.Schema}.{RootModelTurnAdmissionTable}");
            if (!(bool)(await available.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
        }
        await using var admission = new NpgsqlCommand($"""
            SELECT 1 FROM {_schema}.{RootModelTurnAdmissionTable}
            WHERE project_id = @project AND run_id = @run AND root_session_id = @session
              AND request_id = @request AND actor_issuer = @issuer AND actor_id = @actor
              AND tenant_id = @tenant AND accepted_selection_hash = @selectionHash
              AND prompt_hash = @prompt AND execution_fence = @fence
              AND decision_id = @decision AND decision_state_version = @decisionVersion
            """, connection, transaction);
        BindRuntime(admission, binding);
        admission.Parameters["selectionHash"].Value = binding.AcceptedSelectionHash;
        admission.Parameters.AddWithValue("request", NpgsqlDbType.Uuid, request.EventId);
        admission.Parameters.AddWithValue("prompt", NpgsqlDbType.Char, request.InputHash);
        admission.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
        admission.Parameters.AddWithValue("decisionVersion", NpgsqlDbType.Bigint, decisionVersion);
        return await admission.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    internal static void RequireCoordinatorRootSpendBudgetsSupported(
        IEnumerable<SpendBudget> pinnedBudgets,
        IEnumerable<SpendBudget> currentProjectBudgets)
    {
        // Root turns have no atomic Events evidence and exposure reservation path, so hard budgets must fail closed.
        if (pinnedBudgets.Any(budget =>
                budget.Scope != SpendBudgetScopes.Project && budget.HardLimit is not null) ||
            currentProjectBudgets.Any(budget => budget.HardLimit is not null))
            throw new RuntimeAuthorizationException(SpendBudgetAdmissionMath.EvidenceUnavailable);
    }

    private async Task RequireCoordinatorRootDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string? decisionJson,
        RuntimeRegistration registration,
        long stateVersion,
        Guid operationId,
        string promptHash,
        string actionId,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        try
        {
            using var document = JsonDocument.Parse(decisionJson
                ?? throw new JsonException("Coordinator decision is missing."));
            if (!document.RootElement.TryGetProperty("envelope", out var envelopeElement))
                throw new JsonException("Coordinator decision envelope is missing.");
            var envelope = JsonSerializer.Deserialize<CoordinatorDecisionStateEnvelope>(
                envelopeElement.GetRawText(), DecisionJsonOptions);
            if (envelope is null ||
                envelope.Version != CoordinatorDecisionStateEnvelope.CurrentVersion ||
                envelope.Issuer != binding.ActorIssuer ||
                envelope.Subject != binding.ActorId ||
                envelope.TenantId != binding.TenantId ||
                envelope.ProjectId != binding.ProjectId ||
                envelope.RunId != binding.RunId ||
                envelope.RootSessionId != binding.SessionId ||
                envelope.AcceptedSelectionHash?.ToLowerInvariant() != binding.AcceptedSelectionHash ||
                envelope.ProjectRevision != binding.ProjectRevision ||
                envelope.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
                envelope.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
                envelope.ContextRevision != binding.ContextRevision ||
                envelope.Fence != binding.ExecutionFence ||
                stateVersion < 1)
                throw new JsonException("Coordinator decision does not match the root runtime binding.");

            // Ordinary root turns stay pre-plan; a confirmed plan permits only its exact persisted RubberDuck turn.
            if (envelope.ConfirmedWorkPlan is null &&
                envelope.CandidateWorkPlan is null &&
                envelope.PendingGate is null)
                return;

            var plan = envelope.ConfirmedWorkPlan;
            var workflow = envelope.SelectedWorkflow;
            if (registration.State != RuntimeRegistrationState.Active ||
                registration.ExpiresAt <= timeProvider.GetUtcNow() ||
                binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
                binding.WorkflowStepId is not null ||
                plan is null ||
                envelope.CandidateWorkPlan is not null ||
                envelope.PendingGate is not null ||
                !envelope.OutcomeConfirmed ||
                !envelope.WorkflowConfirmed ||
                workflow is null ||
                plan.WorkflowId != workflow.Id ||
                plan.DefinitionRevision != workflow.Revision ||
                operationId == Guid.Empty ||
                actionId != "model.turn")
                throw new JsonException("A confirmed plan permits only its persisted RubberDuck operation.");

            var checkpointBinding = new MafCheckpointBinding(
                new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId),
                new CoordinationActor(binding.ActorIssuer, binding.ActorId),
                binding.ExecutionFence,
                MafExecutionCheckpointStore.CurrentSdkVersion,
                binding.ModelSelectionReference ?? string.Empty,
                null,
                MafExecutionCheckpointContract.StoreName);
            var checkpointStore = new MafExecutionCheckpointStore(
                new PostgresMafCheckpointStore(dataSource, options.Schema, null, timeProvider),
                checkpointBinding);
            await checkpointStore.AcquireExecutionLockInTransactionAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var latest = await checkpointStore.ReadLatestInTransactionAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            if (latest is null)
                throw new JsonException("The RubberDuck checkpoint is unavailable.");

            var state = latest.State;
            MafExecutionCheckpointContract.ValidateRubberduckBinding(
                state, checkpointBinding.Identity, checkpointBinding.ExecutionFence,
                latest.Info.CheckpointId);
            var intents = state.RubberduckIntents.Values
                .Where(intent => intent.OperationId == operationId)
                .Take(2)
                .ToArray();
            if (intents.Length != 1)
                throw new JsonException("The operation is not a unique persisted RubberDuck intent.");
            var intent = intents[0];
            var reviewStep = workflow.Steps.SingleOrDefault(step =>
                step.Id == intent.StepId &&
                step.Mode == WorkflowStepMode.Platform &&
                step.PlatformGate == WorkflowPlatformGate.RubberDuck);
            if (!MafRubberduckReviewContract.IsValidIntent(intent) ||
                intent.OwnerRuntimeInstanceId != registration.RuntimeInstanceId ||
                intent.OwnerRegistrationRevision != registration.Revision ||
                intent.Identity != checkpointBinding.Identity ||
                intent.AuthorizedActorId != binding.ActorId ||
                intent.ExecutionFence != binding.ExecutionFence ||
                intent.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
                intent.WorkPlanId != plan.Id ||
                intent.WorkflowId != workflow.Id ||
                intent.DefinitionRevision != workflow.Revision ||
                intent.DecisionStateVersion != stateVersion ||
                state.DecisionStateVersion != stateVersion ||
                state.Progress.NonModelSteps.GetValueOrDefault(intent.StepId) != MafExecutionTaskStatus.Running ||
                state.RubberduckReceipts.ContainsKey(intent.StepId) ||
                reviewStep is null ||
                MafRubberduckReviewContract.CreateEvidenceSummary(
                    workflow, plan, reviewStep, state) != intent.EvidenceSummary ||
                RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
                    MafRubberduckReviewContract.CreatePrompt(intent))) != promptHash)
                throw new JsonException("The RubberDuck operation does not match the current plan and owner.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new RuntimeAuthorizationException(
                "runtime_action_owner_unavailable", null, exception);
        }
    }

    /// <summary>
    /// Looks up a grant and returns it only if it remains current for the caller and accepted run.
    /// </summary>
    /// <param name="reference">The grant ID and requested revision.</param>
    /// <param name="cancellationToken">Cancels authority and database reads.</param>
    /// <returns>A current validated grant, or a status describing unknown, stale, revoked, expired, or unavailable state.</returns>
    /// <remarks>Selection authority and the grant row are read together; changed selection, actor, tenant, role, or fences revoke use.</remarks>
    public async Task<ExecutableActionGrantLookupResult> GetCurrentAsync(
        ExecutableActionGrantReference reference,
        CancellationToken cancellationToken = default)
    {
        if (reference is null || !IsIdentifier(reference.GrantId) || !IsIdentifier(reference.Revision))
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Unknown);

        var context = httpContextAccessor.HttpContext;
        if (context is null)
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Error);

        CoordinationActor caller;
        CoordinationRunScope scope;
        try
        {
            caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
            CoordinationIdentity.RequireScopes(context.User);
            scope = CoordinationIdentity.RequireRunScope(context.User);
        }
        catch (CoordinationException)
        {
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Unknown);
        }

        var authorityTask = projects.ReadAcceptedSelectionWithAuthorityAsync(
            context,
            scope.ProjectId,
            scope.RunId,
            cancellationToken);
        var grantTask = ReadGrantSnapshotAsync(
            scope,
            reference.GrantId,
            cancellationToken);
        try
        {
            await Task.WhenAll(authorityTask, grantTask).ConfigureAwait(false);
        }
        catch (CoordinationException exception) when (
            exception.StatusCode is StatusCodes.Status401Unauthorized or
                StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound)
        {
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Unknown);
        }
        var authorized = authorityTask.Result;
        var snapshot = grantTask.Result;
        if (snapshot is null)
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Unknown);
        if (!snapshot.SourceIntentCurrent)
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Revoked);

        if (!snapshot.IsCurrent ||
            !string.Equals(snapshot.Revision, reference.Revision, StringComparison.Ordinal))
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Stale);
        if (snapshot.State != ExecutableActionGrantState.Active ||
            snapshot.SessionLifecycle != "active" ||
            snapshot.RunState == "completed")
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Revoked);
        if (snapshot.RunFence != snapshot.SessionFence ||
            snapshot.RunFence != snapshot.GrantFence)
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Stale);
        if (snapshot.ExpiresAt <= timeProvider.GetUtcNow())
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Expired);

        var selectionHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(authorized.Selection.Snapshot.GetRawText())));
        var hasCurrentProjectRole = authorized.Authorization.EffectiveAuthority.Any(authorityEntry =>
            authorityEntry.ResourceType == "project" &&
            authorityEntry.ResourceId == scope.ProjectId &&
            !authorityEntry.Permissions.IsDefault &&
            authorityEntry.Permissions.Any(permission =>
                permission.Permission == "acceptRunSelection" &&
                permission.RoleRevision == snapshot.RoleRevision));
        if (caller.Issuer != snapshot.Issuer ||
            caller.Subject != snapshot.ActorId ||
            snapshot.WriterIssuer != caller.Issuer ||
            snapshot.WriterSubject != caller.Subject ||
            authorized.Authorization.Issuer != snapshot.Issuer ||
            authorized.Authorization.ActorId != snapshot.ActorId ||
            authorized.Authorization.TenantId != snapshot.TenantId ||
            authorized.Authorization.BoundProjectId != scope.ProjectId ||
            authorized.Authorization.BoundRunId != scope.RunId ||
            authorized.Authorization.MembershipRevision != snapshot.MembershipRevision ||
            authorized.Selection.ProjectRevision != snapshot.ProjectRevision ||
            authorized.Selection.ProjectConfigurationRevision != snapshot.ProjectConfigurationRevision ||
            authorized.Selection.PlatformRuntimeRevision != snapshot.PlatformRuntimeRevision ||
            !string.Equals(
                authorized.Selection.ContextRevision, snapshot.ContextRevision, StringComparison.Ordinal) ||
            !hasCurrentProjectRole ||
            snapshot.TenantId != snapshot.RunTenantId ||
            !string.Equals(snapshot.GrantSelectionHash, selectionHash, StringComparison.Ordinal) ||
            !string.Equals(snapshot.AcceptedSelectionHash, selectionHash, StringComparison.Ordinal))
            return new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Revoked);

        return ExecutableActionGrantLookupResult.Current(snapshot.Grant);
    }

    /// <summary>
    /// Reads the grant and its linked run, session, current decision, and source-control intent state.
    /// </summary>
    /// <param name="scope">The project and run whose grant is being looked up.</param>
    /// <param name="grantId">The grant identifier.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <returns>The joined grant snapshot, or <see langword="null"/> when no eligible row is present.</returns>
    private async Task<GrantCurrentSnapshot?> ReadGrantSnapshotAsync(
        CoordinationRunScope scope,
        string grantId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT g.revision, g.is_current, g.grant_state, g.issuer, g.actor_id, g.tenant_id,
                   g.project_id, g.run_id, g.session_id, g.step_id, g.action_ids, g.purpose,
                   g.execution_fence, g.expires_at, g.membership_revision, g.role_revision,
                   g.project_revision, g.project_configuration_revision, g.platform_runtime_revision,
                   g.context_revision, g.accepted_selection_hash,
                   r.execution_fence, r.execution_state, r.accepted_selection_hash,
                   COALESCE(r.tenant_id, ''),
                   s.execution_fence, s.writer_issuer, s.writer_subject, s.lifecycle_state,
                   g.source_state_version,
                   CASE
                     WHEN g.purpose = 'runtime.execution' THEN
                       (s.node_kind = 'child_work' AND s.turn_state = 'active' OR
                        s.node_kind = 'coordinator' AND s.parent_session_id IS NULL
                          AND s.session_id = s.root_session_id AND g.step_id = @coordinatorStep
                         AND r.execution_state NOT IN ('completed', 'failed', 'indeterminate', 'cancelled')
                         AND jsonb_array_length(g.action_ids) = 1
                          AND (g.action_ids @> '["model.turn"]'::jsonb OR
                               g.action_ids @> '["tool.read"]'::jsonb OR
                               g.action_ids @> '["tool.write"]'::jsonb))
                       AND g.source_control_intent_id IS NULL
                     WHEN g.action_ids @> '["source_control.merge"]'::jsonb THEN
                       jsonb_array_length(g.action_ids) = 1
                       AND g.purpose = 'source-control.merge'
                       AND g.source_control_intent_id IS NOT NULL
                       AND EXISTS (
                           SELECT 1
                           FROM {_schema}.source_control_merge_intents AS source_intent
                           WHERE source_intent.project_id = g.project_id
                             AND source_intent.run_id = g.run_id
                             AND source_intent.intent_id = g.source_control_intent_id
                             AND source_intent.session_id = g.session_id
                             AND source_intent.intent_state IN ('approved', 'merge_started')
                             AND source_intent.issuer = g.issuer
                             AND source_intent.actor_id = g.actor_id
                             AND source_intent.tenant_id = g.tenant_id
                             AND source_intent.accepted_selection_hash = g.accepted_selection_hash
                             AND source_intent.execution_fence = g.execution_fence
                             AND source_intent.workflow_step_id = g.step_id
                             AND source_intent.approval_request_decision_id IS NOT NULL
                             AND source_intent.approval_request_state_version =
                                 source_intent.source_state_version + 1
                             AND source_intent.approval_state_version =
                                 source_intent.approval_request_state_version + 1
                             AND source_intent.approval_decision_id = g.source_decision_id
                             AND source_intent.approval_state_version = g.source_state_version)
                     ELSE g.source_control_intent_id IS NULL
                       AND g.purpose <> 'source-control.merge'
                   END
            FROM {_schema}.executable_action_grants AS g
            INNER JOIN {_schema}.accepted_runs AS r
              ON r.project_id = g.project_id AND r.run_id = g.run_id
            INNER JOIN {_schema}.coordination_sessions AS s
              ON s.project_id = g.project_id AND s.run_id = g.run_id
             AND s.session_id = g.session_id
            INNER JOIN LATERAL (
                SELECT d.decision_id, d.state_version, d.decision_state
                FROM {_schema}.coordinator_decisions AS d
                WHERE d.project_id = g.project_id AND d.run_id = g.run_id
                  AND d.session_id = s.root_session_id
                ORDER BY d.state_version DESC
                LIMIT 1
            ) AS current_decision
              ON current_decision.decision_id = g.source_decision_id
             AND current_decision.state_version = g.source_state_version
             AND current_decision.decision_state = 'accepted'
            WHERE g.project_id = @project AND g.run_id = @run AND g.grant_id = @grant
            ORDER BY g.is_current DESC, g.created_at DESC
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, scope.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, scope.RunId);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grantId);
        command.Parameters.AddWithValue("coordinatorStep", NpgsqlDbType.Varchar,
            RuntimeActionContract.CoordinatorRootStepId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var state = reader.GetString(2) switch
        {
            "active" => ExecutableActionGrantState.Active,
            "revoked" => ExecutableActionGrantState.Revoked,
            "superseded" => ExecutableActionGrantState.Superseded,
            _ => (ExecutableActionGrantState?)null
        };
        ImmutableHashSet<string> actionIds;
        try
        {
            actionIds = JsonSerializer.Deserialize<string[]>(reader.GetString(10)) is { Length: > 0 } values &&
                values.All(IsIdentifier)
                    ? values.ToImmutableHashSet(StringComparer.Ordinal)
                    : ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
        if (state is null || actionIds.IsEmpty)
            return null;

        var reference = new ExecutableActionGrantReference(grantId, reader.GetString(0));
        var grant = new ValidatedExecutableActionGrant(
            reference,
            state.Value,
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            actionIds,
            reader.GetString(11),
            reader.GetInt64(12),
            reader.GetFieldValue<DateTimeOffset>(13));
        return new GrantCurrentSnapshot(
            grant,
            reader.GetBoolean(1),
            reader.GetString(0),
            state.Value,
            reader.GetInt64(12),
            reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetInt64(16),
            reader.GetInt64(17),
            reader.GetInt64(18),
            reader.GetString(19),
            reader.GetString(20).TrimEnd(),
            reader.GetInt64(21),
            reader.GetString(22),
            reader.GetString(23).TrimEnd(),
            reader.GetString(24),
            reader.GetInt64(25),
            reader.GetString(26),
            reader.GetString(27),
            reader.GetString(28),
            reader.GetInt64(29),
            reader.GetBoolean(30),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    /// <summary>
    /// Persists an allow-policy source receipt emitted by a runtime action.
    /// </summary>
    /// <param name="receipt">The action receipt and the grant/provider binding that authorized it.</param>
    /// <param name="cancellationToken">Cancels database and authority checks.</param>
    /// <returns>Stored, duplicate, rejected, or unavailable status; caller cancellation is rethrown.</returns>
    /// <remarks>The grant must still be current. Invalid or stale evidence is rejected; unexpected storage failures are reported as unavailable.</remarks>
    public async Task<ExecutableActionSourceReceiptWriteResult> StoreAsync(
        ExecutableActionSourceReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var grant = receipt.Grant;
        var policyBinding = receipt.PolicyBinding;
        if (grant is null || policyBinding is null ||
            policyBinding.Seam != ProviderSeam.Policy ||
            !string.Equals(policyBinding.RunId, grant.RunId, StringComparison.Ordinal) ||
            !string.Equals(policyBinding.ProviderId, AgtPolicyProvider.ProviderId, StringComparison.Ordinal) ||
            receipt.Fence != grant.Fence ||
            !string.Equals(receipt.SessionId, grant.SessionId, StringComparison.Ordinal) ||
            !string.Equals(receipt.StepId, grant.StepId, StringComparison.Ordinal) ||
            !grant.ActionIds.Contains(receipt.ActionId) ||
            !string.Equals(receipt.Purpose, grant.Purpose, StringComparison.Ordinal))
            return new ExecutableActionSourceReceiptWriteResult(ExecutableActionSourceReceiptWriteStatus.Rejected);

        var identity = new SessionIdentity(grant.ProjectId, grant.RunId, receipt.SessionId);
        var evidence = new PolicyEvaluationSessionPayload(
            grant.ActorId,
            grant.TenantId,
            receipt.StepId,
            grant.Reference.GrantId,
            grant.Reference.Revision,
            receipt.Purpose,
            receipt.ActionId,
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            receipt.Fence,
            policyBinding.ProviderId,
            policyBinding.AdapterVersion.ToString(),
            policyBinding.OptionsSchemaVersion,
            policyBinding.OptionsRevision);

        try
        {
            return await PersistReceiptAsync(
                new CoordinationActor(grant.Issuer, grant.ActorId),
                identity,
                receipt.ReceiptId,
                evidence,
                requireCurrentGrant: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationException exception) when (
            exception.StatusCode is StatusCodes.Status400BadRequest or
                StatusCodes.Status403Forbidden or StatusCodes.Status409Conflict)
        {
            return new ExecutableActionSourceReceiptWriteResult(ExecutableActionSourceReceiptWriteStatus.Rejected);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ExecutableActionSourceReceiptWriteResult(ExecutableActionSourceReceiptWriteStatus.Unavailable);
        }
    }

    /// <summary>
    /// Persists a deny or error policy-evaluation receipt from the authenticated runtime caller.
    /// </summary>
    /// <param name="receipt">The denial or failure evidence to record.</param>
    /// <param name="cancellationToken">Cancels database and authority checks.</param>
    /// <returns>Stored, duplicate, rejected, or unavailable status; caller cancellation is rethrown.</returns>
    /// <remarks>Denied/error receipts may refer to an issued grant that is no longer effective, but still require current session and selection authority.</remarks>
    public async Task<ExecutableActionSourceReceiptWriteResult> StoreAsync(
        ExecutableActionPolicyEvaluationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var context = httpContextAccessor.HttpContext;
        if (context is null)
            return new ExecutableActionSourceReceiptWriteResult(
                ExecutableActionSourceReceiptWriteStatus.Unavailable);

        CoordinationActor actor;
        CoordinationRunScope scope;
        try
        {
            actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
            CoordinationIdentity.RequireScopes(context.User);
            scope = CoordinationIdentity.RequireRunScope(context.User);
        }
        catch (CoordinationException)
        {
            return new ExecutableActionSourceReceiptWriteResult(
                ExecutableActionSourceReceiptWriteStatus.Rejected);
        }

        if (receipt.Outcome is not (PolicyEvaluationOutcome.Deny or PolicyEvaluationOutcome.Error) ||
            receipt.GrantReference is null ||
            !IsIdentifier(receipt.SessionId) ||
            !IsIdentifier(receipt.StepId) ||
            !IsIdentifier(receipt.GrantReference.GrantId) ||
            !IsIdentifier(receipt.GrantReference.Revision) ||
            !IsIdentifier(receipt.Purpose) ||
            !IsIdentifier(receipt.ActionId) ||
            receipt.Fence < 1 ||
            receipt.ProviderId != AgtPolicyProvider.ProviderId ||
            receipt.AdapterVersion != AgtPolicyProvider.AdapterVersion.ToString() ||
            receipt.OptionsSchemaVersion < 1 ||
            !IsIdentifier(receipt.OptionsRevision) ||
            !IsValidOutcomeReason(receipt.Outcome, receipt.ReasonCode))
            return new ExecutableActionSourceReceiptWriteResult(
                ExecutableActionSourceReceiptWriteStatus.Rejected);

        try
        {
            var authorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, scope.ProjectId, scope.RunId, cancellationToken).ConfigureAwait(false);
            var evidence = new PolicyEvaluationSessionPayload(
                actor.Subject,
                authorized.Authorization.TenantId,
                receipt.StepId,
                receipt.GrantReference.GrantId,
                receipt.GrantReference.Revision,
                receipt.Purpose,
                receipt.ActionId,
                receipt.Outcome,
                receipt.ReasonCode,
                receipt.Fence,
                receipt.ProviderId,
                receipt.AdapterVersion,
                receipt.OptionsSchemaVersion,
                receipt.OptionsRevision);
            return await PersistReceiptAsync(
                actor,
                new SessionIdentity(scope.ProjectId, scope.RunId, receipt.SessionId),
                receipt.ReceiptId,
                evidence,
                requireCurrentGrant: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationException exception) when (
            exception.StatusCode is StatusCodes.Status400BadRequest or
                StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound or
                StatusCodes.Status409Conflict)
        {
            return new ExecutableActionSourceReceiptWriteResult(
                ExecutableActionSourceReceiptWriteStatus.Rejected);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ExecutableActionSourceReceiptWriteResult(
                ExecutableActionSourceReceiptWriteStatus.Unavailable);
        }
    }

    /// <summary>
    /// Validates the grant, inserts a receipt idempotently, verifies any existing row, and rechecks writer authority.
    /// </summary>
    /// <param name="actor">The issuer and subject allowed to write the receipt.</param>
    /// <param name="identity">The owning project, run, and session.</param>
    /// <param name="receiptId">The idempotency key for this receipt.</param>
    /// <param name="evidence">The policy provider's decision and pinned options.</param>
    /// <param name="requireCurrentGrant">Whether the grant must remain effective at write time.</param>
    /// <param name="cancellationToken">Cancels database and authority checks.</param>
    /// <returns>Stored for a new receipt or duplicate for an identical existing receipt.</returns>
    /// <exception cref="CoordinationException">Thrown when the writer, grant, or existing receipt conflicts with the supplied evidence.</exception>
    /// <remarks>Receipt insertion, conflict verification, and owner-session checks occur in one PostgreSQL transaction.</remarks>
    private async Task<ExecutableActionSourceReceiptWriteResult> PersistReceiptAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        Guid receiptId,
        PolicyEvaluationSessionPayload evidence,
        bool requireCurrentGrant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!string.Equals(evidence.ActorId, actor.Subject, StringComparison.Ordinal))
            throw new CoordinationException("policy_receipt_actor_mismatch", StatusCodes.Status403Forbidden);
        ValidateReceipt(identity, receiptId, evidence);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var grantValid = await IsCurrentGrantForReceiptAsync(
            connection, transaction, actor, identity, evidence, cancellationToken).ConfigureAwait(false);
        if (!grantValid && !requireCurrentGrant)
        {
            grantValid = await IsIssuedGrantForReceiptAsync(
                connection, transaction, actor, identity, evidence, cancellationToken).ConfigureAwait(false);
        }
        if (!grantValid)
            throw new CoordinationException("policy_receipt_grant_stale", StatusCodes.Status409Conflict);

        int inserted;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.policy_evaluation_receipts
                (project_id, run_id, receipt_id, issuer, actor_id, tenant_id, session_id, step_id,
                 grant_id, grant_revision, purpose, action_id, outcome, reason_code, execution_fence,
                 provider_id, adapter_version, options_schema_version, options_revision)
            VALUES
                (@project, @run, @receipt, @issuer, @actor, @tenant, @session, @step,
                 @grant, @revision, @purpose, @action, @outcome, @reason, @fence,
                 @provider, @adapter, @schemaVersion, @optionsRevision)
            ON CONFLICT (project_id, run_id, receipt_id) DO NOTHING
            """, connection, transaction))
        {
            BindReceipt(insert, actor, identity, receiptId, evidence);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = await ReadReceiptRowAsync(
            connection, transaction, identity, receiptId, cancellationToken).ConfigureAwait(false);
        if (saved is null || saved.ActorId != evidence.ActorId ||
            saved.TenantId != evidence.TenantId || saved.StepId != evidence.StepId ||
            saved.GrantId != evidence.GrantId || saved.GrantRevision != evidence.GrantRevision ||
            saved.Purpose != evidence.Purpose || saved.ActionId != evidence.ActionId ||
            saved.Outcome != evidence.Outcome || saved.ReasonCode != evidence.ReasonCode ||
            saved.Fence != evidence.Fence || saved.ProviderId != evidence.ProviderId ||
            saved.AdapterVersion != evidence.AdapterVersion ||
            saved.OptionsSchemaVersion != evidence.OptionsSchemaVersion ||
            saved.OptionsRevision != evidence.OptionsRevision ||
            saved.Issuer != actor.Issuer)
            throw new CoordinationException("policy_receipt_conflict", StatusCodes.Status409Conflict);

        await ValidateReceiptWriterAuthorityAsync(
            actor, identity, evidence, requireCurrentGrant, cancellationToken,
            connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExecutableActionSourceReceiptWriteResult(inserted == 1
            ? ExecutableActionSourceReceiptWriteStatus.Stored
            : ExecutableActionSourceReceiptWriteStatus.Duplicate);
    }

    /// <summary>
    /// Reads a receipt only when its issuer and actor match the requesting actor.
    /// </summary>
    /// <param name="actor">The caller identity required to read the receipt.</param>
    /// <param name="projectId">The project containing the receipt.</param>
    /// <param name="runId">The run containing the receipt.</param>
    /// <param name="receiptId">The receipt identifier.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <returns>The receipt view, or <see langword="null"/> when missing or owned by another actor.</returns>
    internal async Task<PolicyEvaluationReceiptView?> ReadReceiptAsync(
        CoordinationActor actor,
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!IsIdentifier(projectId) || !IsIdentifier(runId) || receiptId == Guid.Empty)
            throw new CoordinationException("policy_receipt_invalid", StatusCodes.Status400BadRequest);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT issuer, actor_id, tenant_id, session_id, step_id, grant_id, grant_revision,
                purpose, action_id, outcome, reason_code, execution_fence, provider_id,
                adapter_version, options_schema_version, options_revision, created_at
            FROM {_schema}.policy_evaluation_receipts
            WHERE project_id = @project AND run_id = @run AND receipt_id = @receipt
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receiptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(reader.GetString(0), actor.Issuer, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), actor.Subject, StringComparison.Ordinal))
            return null;

        var evidence = new PolicyEvaluationSessionPayload(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ParseOutcome(reader.GetString(9)),
            ParseReason(reader.GetString(10)),
            reader.GetInt64(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt32(14),
            reader.GetString(15));
        return new PolicyEvaluationReceiptView(
            receiptId,
            reader.GetString(0),
            new SessionIdentity(projectId, runId, reader.GetString(3)),
            evidence,
            reader.GetFieldValue<DateTimeOffset>(16));
    }

    /// <summary>
    /// Loads and validates a policy receipt before it is admitted to the session event journal.
    /// </summary>
    /// <param name="actor">The authenticated actor that must own the receipt.</param>
    /// <param name="projectId">The expected project.</param>
    /// <param name="runId">The expected run.</param>
    /// <param name="receiptId">The receipt to admit.</param>
    /// <param name="cancellationToken">Cancels owner and database checks.</param>
    /// <returns>The validated receipt view.</returns>
    /// <exception cref="CoordinationException">Thrown when the receipt is absent, not owned by this actor, or inconsistent with current run authority.</exception>
    internal async Task<PolicyEvaluationReceiptView> ValidateReceiptAdmissionAsync(
        CoordinationActor actor,
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!IsIdentifier(projectId) || !IsIdentifier(runId) || receiptId == Guid.Empty)
            throw new CoordinationException("policy_receipt_invalid", StatusCodes.Status400BadRequest);

        var receipt = await ReadReceiptAsync(
            actor, projectId, runId, receiptId, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
            throw new CoordinationException("policy_receipt_admission_denied", StatusCodes.Status403Forbidden);

        var evidence = receipt.Evidence;
        ValidateReceipt(receipt.Identity, receiptId, evidence);
        if (receipt.Identity.ProjectId != projectId ||
            receipt.Identity.RunId != runId ||
            !IsValidPolicyProviderEvidence(evidence))
            throw new CoordinationException("policy_receipt_admission_denied", StatusCodes.Status403Forbidden);

        await ValidateReceiptWriterAuthorityAsync(
            actor,
            receipt.Identity,
            evidence,
            requireCurrentGrant: evidence.Outcome == PolicyEvaluationOutcome.Allow,
            cancellationToken).ConfigureAwait(false);
        if (evidence.Outcome is PolicyEvaluationOutcome.Deny or PolicyEvaluationOutcome.Error)
            return receipt;
        if (evidence.Outcome != PolicyEvaluationOutcome.Allow)
            throw new CoordinationException("policy_receipt_admission_denied", StatusCodes.Status403Forbidden);

        return receipt;
    }

    /// <summary>
    /// Rechecks caller, selection, active owner session, and optionally the currently effective action grant.
    /// </summary>
    /// <param name="actor">The expected receipt writer.</param>
    /// <param name="identity">The receipt's session scope.</param>
    /// <param name="evidence">The decision evidence being admitted.</param>
    /// <param name="requireCurrentGrant">Whether the referenced grant must still be effective.</param>
    /// <param name="cancellationToken">Cancels authority and database reads.</param>
    /// <param name="ownerConnection">Optional connection for a caller-owned atomic write.</param>
    /// <param name="ownerTransaction">Optional transaction paired with <paramref name="ownerConnection"/>.</param>
    /// <exception cref="CoordinationException">Thrown when caller scope, selection, writer state, or grant authority has changed.</exception>
    /// <remarks>The selection hash is read before the row-locked session check and then reread to detect authority changes during validation.</remarks>
    private async Task ValidateReceiptWriterAuthorityAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        PolicyEvaluationSessionPayload evidence,
        bool requireCurrentGrant,
        CancellationToken cancellationToken,
        NpgsqlConnection? ownerConnection = null,
        NpgsqlTransaction? ownerTransaction = null)
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new CoordinationException(
                "policy_receipt_admission_denied", StatusCodes.Status403Forbidden);
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        if (caller.Issuer != actor.Issuer ||
            caller.Subject != actor.Subject ||
            evidence.ActorId != actor.Subject ||
            scope.ProjectId != identity.ProjectId ||
            scope.RunId != identity.RunId)
            throw new CoordinationException("policy_receipt_admission_denied", StatusCodes.Status403Forbidden);

        var selectionHash = await ReadCurrentReceiptWriterSelectionHashAsync(
            context, actor, identity, evidence, cancellationToken).ConfigureAwait(false);
        if (ownerConnection is null)
        {
            if (ownerTransaction is not null)
                throw new ArgumentException("An owner transaction requires its connection.", nameof(ownerTransaction));
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ValidateCurrentOwnerSessionAsync(
                connection, transaction, actor, identity, evidence, selectionHash, cancellationToken)
                .ConfigureAwait(false);
            await ValidateReceiptWriterSelectionUnchangedAsync(
                context, actor, identity, evidence, selectionHash, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (ownerTransaction is null)
                throw new ArgumentException("An owner connection requires its transaction.", nameof(ownerTransaction));
            await ValidateCurrentOwnerSessionAsync(
                ownerConnection, ownerTransaction, actor, identity, evidence, selectionHash, cancellationToken)
                .ConfigureAwait(false);
            await ValidateReceiptWriterSelectionUnchangedAsync(
                context, actor, identity, evidence, selectionHash, cancellationToken).ConfigureAwait(false);
        }

        if (requireCurrentGrant)
        {
            var current = await GetCurrentAsync(
                new ExecutableActionGrantReference(evidence.GrantId, evidence.GrantRevision),
                cancellationToken).ConfigureAwait(false);
            var grant = current.Grant;
            if (current.Status != ExecutableActionGrantLookupStatus.Current || grant is null ||
                grant.Issuer != actor.Issuer ||
                grant.ActorId != actor.Subject ||
                grant.TenantId != evidence.TenantId ||
                grant.ProjectId != identity.ProjectId ||
                grant.RunId != identity.RunId ||
                grant.SessionId != identity.SessionId ||
                grant.StepId != evidence.StepId ||
                !grant.ActionIds.Contains(evidence.ActionId) ||
                grant.Reference.GrantId != evidence.GrantId ||
                grant.Reference.Revision != evidence.GrantRevision ||
                grant.State != ExecutableActionGrantState.Active ||
                grant.Purpose != evidence.Purpose ||
                grant.Fence != evidence.Fence ||
                grant.ExpiresAt <= timeProvider.GetUtcNow())
                throw new CoordinationException(
                    "policy_receipt_admission_denied", StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>
    /// Rereads project authority and rejects a receipt when the selection hash changed during validation.
    /// </summary>
    private async Task ValidateReceiptWriterSelectionUnchangedAsync(
        HttpContext context,
        CoordinationActor actor,
        SessionIdentity identity,
        PolicyEvaluationSessionPayload evidence,
        string expectedSelectionHash,
        CancellationToken cancellationToken)
    {
        var currentSelectionHash = await ReadCurrentReceiptWriterSelectionHashAsync(
            context, actor, identity, evidence, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentSelectionHash, expectedSelectionHash, StringComparison.Ordinal))
            throw new CoordinationException(
                "policy_receipt_admission_denied", StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Reads the currently authorized run selection and verifies its actor, tenant, and project/run binding.
    /// </summary>
    /// <returns>The SHA-256 of the accepted selection snapshot.</returns>
    /// <exception cref="CoordinationException">Thrown when authority no longer matches the receipt writer.</exception>
    private async Task<string> ReadCurrentReceiptWriterSelectionHashAsync(
        HttpContext context,
        CoordinationActor actor,
        SessionIdentity identity,
        PolicyEvaluationSessionPayload evidence,
        CancellationToken cancellationToken)
    {
        var authorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (authorized.Authorization.Issuer != actor.Issuer ||
            authorized.Authorization.ActorId != actor.Subject ||
            authorized.Authorization.TenantId != evidence.TenantId ||
            authorized.Authorization.BoundProjectId != identity.ProjectId ||
            authorized.Authorization.BoundRunId != identity.RunId)
            throw new CoordinationException("policy_receipt_admission_denied", StatusCodes.Status403Forbidden);

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(authorized.Selection.Snapshot.GetRawText())));
    }

    /// <summary>
    /// Locks and validates the session and accepted-run rows against the receipt writer and selection hash.
    /// </summary>
    /// <remarks>The shared row locks keep the checked owner state stable until the surrounding transaction completes.</remarks>
    private async Task ValidateCurrentOwnerSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        PolicyEvaluationSessionPayload evidence,
        string selectionHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT s.writer_issuer, s.writer_subject, s.execution_fence, s.lifecycle_state,
                r.execution_fence, r.execution_state, r.tenant_id, r.accepted_selection_hash
            FROM {_schema}.coordination_sessions s
            JOIN {_schema}.accepted_runs r
              ON r.project_id = s.project_id AND r.run_id = s.run_id
            WHERE s.project_id = @project AND s.run_id = @run AND s.session_id = @session
            FOR SHARE OF s, r
            """, connection, transaction);
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != actor.Issuer ||
            reader.GetString(1) != actor.Subject ||
            reader.GetInt64(2) != evidence.Fence ||
            reader.GetString(3) != "active" ||
            reader.GetInt64(4) != evidence.Fence ||
            reader.GetString(5) == "completed" ||
            reader.GetString(6) != evidence.TenantId ||
            !string.Equals(reader.GetString(7), selectionHash, StringComparison.Ordinal))
            throw new CoordinationException(
                "policy_receipt_admission_denied", StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Checks that receipt evidence identifies the supported policy provider and a bounded options revision.
    /// </summary>
    private static bool IsValidPolicyProviderEvidence(PolicyEvaluationSessionPayload evidence) =>
        evidence.ProviderId == AgtPolicyProvider.ProviderId &&
        evidence.AdapterVersion == AgtPolicyProvider.AdapterVersion.ToString() &&
        evidence.OptionsSchemaVersion == AgtPolicyProvider.OptionsSchemaVersion &&
        IsIdentifier(evidence.OptionsRevision);

    /// <summary>
    /// Checks whether the evidence names the active grant with matching identity, action, purpose, and fences.
    /// </summary>
    private async Task<bool> IsCurrentGrantForReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        PolicyEvaluationSessionPayload evidence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT g.issuer, g.actor_id, g.tenant_id, g.project_id, g.run_id, g.session_id, g.step_id,
                g.action_ids, g.purpose, g.execution_fence, g.expires_at, g.grant_state,
                s.execution_fence, s.lifecycle_state, r.execution_fence
            FROM {_schema}.executable_action_grants g
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = g.project_id AND s.run_id = g.run_id AND s.session_id = g.session_id
            JOIN {_schema}.accepted_runs r
              ON r.project_id = g.project_id AND r.run_id = g.run_id
            WHERE g.project_id = @project AND g.run_id = @run AND g.grant_id = @grant
                AND g.revision = @revision AND g.is_current
            FOR SHARE OF g, s, r
            """, connection, transaction);
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, evidence.GrantId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, evidence.GrantRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return false;

        var actionIds = JsonSerializer.Deserialize<string[]>(reader.GetString(7));
        return reader.GetString(0) == actor.Issuer &&
            reader.GetString(1) == actor.Subject &&
            reader.GetString(2) == evidence.TenantId &&
            reader.GetString(3) == identity.ProjectId &&
            reader.GetString(4) == identity.RunId &&
            reader.GetString(5) == identity.SessionId &&
            reader.GetString(6) == evidence.StepId &&
            actionIds is not null && actionIds.Contains(evidence.ActionId, StringComparer.Ordinal) &&
            reader.GetString(8) == evidence.Purpose &&
            reader.GetInt64(9) == evidence.Fence &&
            reader.GetFieldValue<DateTimeOffset>(10) > timeProvider.GetUtcNow() &&
            reader.GetString(11) == "active" &&
            reader.GetInt64(12) == evidence.Fence &&
            reader.GetString(13) == "active" &&
            reader.GetInt64(14) == evidence.Fence;
    }

    /// <summary>
    /// Checks whether an issued grant matches the evidence even if it is no longer current or effective.
    /// </summary>
    /// <remarks>This relaxed check supports persisted deny/error receipts without allowing use of a stale grant as an allow.</remarks>
    private async Task<bool> IsIssuedGrantForReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        PolicyEvaluationSessionPayload evidence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT g.is_current, g.grant_state, g.issuer, g.actor_id, g.tenant_id, g.project_id,
                g.run_id, g.session_id, g.step_id, g.action_ids, g.purpose, g.execution_fence,
                g.expires_at, s.execution_fence, s.lifecycle_state, r.execution_fence, r.execution_state
            FROM {_schema}.executable_action_grants g
            JOIN {_schema}.coordination_sessions s
              ON s.project_id = g.project_id AND s.run_id = g.run_id AND s.session_id = g.session_id
            JOIN {_schema}.accepted_runs r
              ON r.project_id = g.project_id AND r.run_id = g.run_id
            WHERE g.project_id = @project AND g.run_id = @run AND g.grant_id = @grant
                AND g.revision = @revision
            FOR SHARE OF g, s, r
            """, connection, transaction);
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, evidence.GrantId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, evidence.GrantRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return false;

        var actionIds = JsonSerializer.Deserialize<string[]>(reader.GetString(9));
        var isNoLongerEffective = !reader.GetBoolean(0) ||
            reader.GetString(1) != "active" ||
            reader.GetFieldValue<DateTimeOffset>(12) <= timeProvider.GetUtcNow() ||
            reader.GetInt64(11) != evidence.Fence ||
            reader.GetInt64(13) != evidence.Fence ||
            reader.GetInt64(15) != evidence.Fence;
        return reader.GetString(2) == actor.Issuer &&
            reader.GetString(3) == actor.Subject &&
            reader.GetString(4) == evidence.TenantId &&
            reader.GetString(5) == identity.ProjectId &&
            reader.GetString(6) == identity.RunId &&
            reader.GetString(7) == identity.SessionId &&
            reader.GetString(8) == evidence.StepId &&
            actionIds is not null && actionIds.Contains(evidence.ActionId, StringComparer.Ordinal) &&
            reader.GetString(10) == evidence.Purpose &&
            isNoLongerEffective &&
            reader.GetString(14) == "active" &&
            reader.GetString(16) != "completed";
    }

    /// <summary>
    /// Reads the receipt row within the caller's transaction for idempotency and exact-content checks.
    /// </summary>
    private async Task<StoredReceipt?> ReadReceiptRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT issuer, actor_id, tenant_id, step_id, grant_id, grant_revision, purpose, action_id,
                outcome, reason_code, execution_fence, provider_id, adapter_version,
                options_schema_version, options_revision
            FROM {_schema}.policy_evaluation_receipts
            WHERE project_id = @project AND run_id = @run AND receipt_id = @receipt
            """, connection, transaction);
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receiptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredReceipt(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                ParseOutcome(reader.GetString(8)), ParseReason(reader.GetString(9)), reader.GetInt64(10),
                reader.GetString(11), reader.GetString(12), reader.GetInt32(13), reader.GetString(14))
            : null;
    }

    /// <summary>
    /// Binds receipt evidence to the typed database parameters used for insert.
    /// </summary>
    private static void BindReceipt(
        NpgsqlCommand command,
        CoordinationActor actor,
        SessionIdentity identity,
        Guid receiptId,
        PolicyEvaluationSessionPayload evidence)
    {
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receiptId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, evidence.ActorId);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, evidence.TenantId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, evidence.StepId);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, evidence.GrantId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, evidence.GrantRevision);
        command.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, evidence.Purpose);
        command.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, evidence.ActionId);
        command.Parameters.AddWithValue("outcome", NpgsqlDbType.Varchar, ToDatabaseValue(evidence.Outcome));
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, ToDatabaseValue(evidence.ReasonCode));
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, evidence.Fence);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, evidence.ProviderId);
        command.Parameters.AddWithValue("adapter", NpgsqlDbType.Varchar, evidence.AdapterVersion);
        command.Parameters.AddWithValue("schemaVersion", NpgsqlDbType.Integer, evidence.OptionsSchemaVersion);
        command.Parameters.AddWithValue("optionsRevision", NpgsqlDbType.Varchar, evidence.OptionsRevision);
    }

    /// <summary>
    /// Rejects receipt identifiers, metadata, fences, or outcome/reason pairs outside the persisted contract.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when any required field or outcome/reason combination is invalid.</exception>
    private static void ValidateReceipt(
        SessionIdentity identity,
        Guid receiptId,
        PolicyEvaluationSessionPayload evidence)
    {
        if (receiptId == Guid.Empty || !IsIdentifier(identity.ProjectId) ||
            !IsIdentifier(identity.RunId) || !IsIdentifier(identity.SessionId) ||
            !IsIdentifier(evidence.ActorId) || !IsIdentifier(evidence.TenantId) ||
            !IsIdentifier(evidence.StepId) || !IsIdentifier(evidence.GrantId) ||
            !IsIdentifier(evidence.GrantRevision) || !IsIdentifier(evidence.Purpose) ||
            !IsIdentifier(evidence.ActionId) || !IsIdentifier(evidence.ProviderId) ||
            !IsIdentifier(evidence.AdapterVersion) || !IsIdentifier(evidence.OptionsRevision) ||
            evidence.Fence < 1 || evidence.OptionsSchemaVersion < 1 ||
            !IsValidOutcomeReason(evidence.Outcome, evidence.ReasonCode))
            throw new CoordinationException("policy_receipt_invalid", StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Allows only outcome and reason combinations emitted by the policy evaluator.
    /// </summary>
    private static bool IsValidOutcomeReason(
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reason) =>
        (outcome, reason) switch
        {
            (PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed) => true,
            (PolicyEvaluationOutcome.Deny, PolicyEvaluationReasonCode.DefaultDeny or
                PolicyEvaluationReasonCode.NoEffectiveGrant or
                PolicyEvaluationReasonCode.PlatformRuleDenied or
                PolicyEvaluationReasonCode.ProjectRuleNarrowed or
                PolicyEvaluationReasonCode.StaleFence) => true,
            (PolicyEvaluationOutcome.Error, PolicyEvaluationReasonCode.ProviderUnavailable or
                PolicyEvaluationReasonCode.EvaluationFailed) => true,
            _ => false
        };

    /// <summary>
    /// Parses the database's stable lowercase outcome value.
    /// </summary>
    private static PolicyEvaluationOutcome ParseOutcome(string value) => value switch
    {
        "allow" => PolicyEvaluationOutcome.Allow,
        "deny" => PolicyEvaluationOutcome.Deny,
        "error" => PolicyEvaluationOutcome.Error,
        _ => throw new InvalidOperationException("Stored policy receipt outcome is invalid.")
    };

    /// <summary>
    /// Parses the database's stable lowercase reason value.
    /// </summary>
    private static PolicyEvaluationReasonCode ParseReason(string value) => value switch
    {
        "allowed" => PolicyEvaluationReasonCode.Allowed,
        "default_deny" => PolicyEvaluationReasonCode.DefaultDeny,
        "no_effective_grant" => PolicyEvaluationReasonCode.NoEffectiveGrant,
        "platform_rule_denied" => PolicyEvaluationReasonCode.PlatformRuleDenied,
        "project_rule_narrowed" => PolicyEvaluationReasonCode.ProjectRuleNarrowed,
        "stale_fence" => PolicyEvaluationReasonCode.StaleFence,
        "provider_unavailable" => PolicyEvaluationReasonCode.ProviderUnavailable,
        "evaluation_failed" => PolicyEvaluationReasonCode.EvaluationFailed,
        _ => throw new InvalidOperationException("Stored policy receipt reason is invalid.")
    };

    /// <summary>
    /// Converts a typed enum value to the snake-case representation stored in PostgreSQL.
    /// </summary>
    private static string ToDatabaseValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>
    /// Checks for a non-empty identifier of bounded length with only supported identifier characters.
    /// </summary>
    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    /// <summary>
    /// Adds project and run parameters to receipt queries.
    /// </summary>
    private static void AddIdentity(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
    }

    /// <summary>
    /// The exact policy receipt fields read from storage for idempotent write verification.
    /// </summary>
    private sealed record StoredReceipt(
        string Issuer,
        string ActorId,
        string TenantId,
        string StepId,
        string GrantId,
        string GrantRevision,
        string Purpose,
        string ActionId,
        PolicyEvaluationOutcome Outcome,
        PolicyEvaluationReasonCode ReasonCode,
        long Fence,
        string ProviderId,
        string AdapterVersion,
        int OptionsSchemaVersion,
        string OptionsRevision);

    /// <summary>
    /// The grant row together with the run, session, selection, and source-intent state used by grant lookup.
    /// </summary>
    private sealed record GrantCurrentSnapshot(
        ValidatedExecutableActionGrant Grant,
        bool IsCurrent,
        string Revision,
        ExecutableActionGrantState State,
        long GrantFence,
        DateTimeOffset ExpiresAt,
        long MembershipRevision,
        long RoleRevision,
        long ProjectRevision,
        long ProjectConfigurationRevision,
        long PlatformRuntimeRevision,
        string ContextRevision,
        string GrantSelectionHash,
        long RunFence,
        string RunState,
        string AcceptedSelectionHash,
        string RunTenantId,
        long SessionFence,
        string WriterIssuer,
        string WriterSubject,
        string SessionLifecycle,
        long SourceStateVersion,
        bool SourceIntentCurrent,
        string Issuer,
        string ActorId,
        string TenantId);
}
