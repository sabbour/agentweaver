using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

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
    private const int MaximumIdLength = 256;
    private readonly string _schema = $"\"{options.Schema}\"";

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
                   g.source_state_version
            FROM {_schema}.executable_action_grants AS g
            INNER JOIN {_schema}.accepted_runs AS r
              ON r.project_id = g.project_id AND r.run_id = g.run_id
            INNER JOIN {_schema}.coordination_sessions AS s
              ON s.project_id = g.project_id AND s.run_id = g.run_id
             AND s.session_id = g.session_id
             AND s.parent_session_id IS NULL
            INNER JOIN LATERAL (
                SELECT d.decision_id, d.state_version, d.decision_state
                FROM {_schema}.coordinator_decisions AS d
                WHERE d.project_id = g.project_id AND d.run_id = g.run_id
                  AND d.session_id = g.session_id
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
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

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
            actor, identity, evidence, requireCurrentGrant, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExecutableActionSourceReceiptWriteResult(inserted == 1
            ? ExecutableActionSourceReceiptWriteStatus.Stored
            : ExecutableActionSourceReceiptWriteStatus.Duplicate);
    }

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

    private async Task ValidateReceiptWriterAuthorityAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        PolicyEvaluationSessionPayload evidence,
        bool requireCurrentGrant,
        CancellationToken cancellationToken)
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
            return;
        }

        var authorized = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (authorized.Authorization.Issuer != actor.Issuer ||
            authorized.Authorization.ActorId != actor.Subject ||
            authorized.Authorization.TenantId != evidence.TenantId ||
            authorized.Authorization.BoundProjectId != identity.ProjectId ||
            authorized.Authorization.BoundRunId != identity.RunId)
            throw new CoordinationException("policy_receipt_admission_denied", StatusCodes.Status403Forbidden);
    }

    private static bool IsValidPolicyProviderEvidence(PolicyEvaluationSessionPayload evidence) =>
        evidence.ProviderId == AgtPolicyProvider.ProviderId &&
        evidence.AdapterVersion == AgtPolicyProvider.AdapterVersion.ToString() &&
        evidence.OptionsSchemaVersion == AgtPolicyProvider.OptionsSchemaVersion &&
        IsIdentifier(evidence.OptionsRevision);

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

    private static PolicyEvaluationOutcome ParseOutcome(string value) => value switch
    {
        "allow" => PolicyEvaluationOutcome.Allow,
        "deny" => PolicyEvaluationOutcome.Deny,
        "error" => PolicyEvaluationOutcome.Error,
        _ => throw new InvalidOperationException("Stored policy receipt outcome is invalid.")
    };

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

    private static string ToDatabaseValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static void AddIdentity(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
    }

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
        string Issuer,
        string ActorId,
        string TenantId);
}
