using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal enum RuntimeNativeApprovalDisposition
{
    Pending,
    Approved
}

internal sealed record RuntimeNativeApprovalDecision(
    RuntimeNativeApprovalDisposition Disposition,
    string RequestId,
    string SubjectId);

internal sealed class RuntimeNativeApprovalOwnerStore(
    NpgsqlDataSource dataSource,
    string schema,
    CoordinatorDecisionOwnerStore decisions,
    CoordinationOwnerStore coordination)
{
    public const string PendingCode = "runtime_native_policy_approval_pending";
    public const string CollisionCode = "runtime_native_policy_approval_pending_collision";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _table = CreateTableName(schema, dataSource, decisions, coordination);

    private static string CreateTableName(
        string schema,
        NpgsqlDataSource dataSource,
        CoordinatorDecisionOwnerStore decisions,
        CoordinationOwnerStore coordination)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(coordination);
        if (schema is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                schema, "^[a-z][a-z0-9_]{0,62}\\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
        return $"\"{schema}\".runtime_native_approval_suspensions";
    }

    public async Task<RuntimeNativeApprovalDecision> RequireOrSuspendAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        RuntimeNativePolicyGuardrailsResult retained,
        CancellationToken cancellationToken)
    {
        Validate(registration, request, retained);
        var childIdentity = new SessionIdentity(
            registration.Binding.ProjectId,
            registration.Binding.RunId,
            registration.Binding.SessionId);
        var subjectId = SubjectId(registration.Binding.TurnId, request);
        var requestId = "approval-" + subjectId;
        var policyInputHash = Hash(retained.Escalations.Select(item => item.PolicyInput));
        var retainedJson = JsonSerializer.Serialize(new
        {
            retained.BindingHash,
            retained.Escalations
        }, JsonOptions);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_table}
                (owner_turn_id, operation_id, project_id, run_id, session_id, request_hash,
                 policy_input_hash, retained_evaluation, execution_fence)
            VALUES
                (@turn, @operation, @project, @run, @session, @request_hash,
                 @policy_input_hash, @retained, @fence)
            ON CONFLICT (owner_turn_id, operation_id) DO NOTHING
            RETURNING true
            """, connection);
        command.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, registration.Binding.TurnId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.EventId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, childIdentity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, childIdentity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, childIdentity.SessionId);
        command.Parameters.AddWithValue("request_hash", NpgsqlDbType.Char, request.InputHash);
        command.Parameters.AddWithValue("policy_input_hash", NpgsqlDbType.Char, policyInputHash);
        command.Parameters.AddWithValue("retained", NpgsqlDbType.Jsonb, retainedJson);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, registration.Binding.ExecutionFence);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        await RequireStoredAsync(childIdentity, registration.Binding.TurnId,
            registration.Binding.ExecutionFence, request, policyInputHash, retainedJson, cancellationToken)
            .ConfigureAwait(false);

        var identity = await ReadRootIdentityAsync(
            actor, selection, registration, cancellationToken).ConfigureAwait(false);
        var current = await decisions.ReadCurrentAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        var receipt = current.State.DecisionReceipts.LastOrDefault(item =>
            item.RequestId == requestId && item.SubjectId == subjectId);
        if (receipt is not null)
        {
            if (receipt.ActorId != selection.Authorization.ActorId ||
                receipt.Fence != current.State.Fence ||
                receipt.Kind != CoordinatorGateKind.Approval)
                throw new CoordinationException(
                    "runtime_native_policy_approval_stale", StatusCodes.Status409Conflict);
            if (receipt.ChoiceId != CoordinatorGateChoices.Approve ||
                receipt.FreeformAnswer is not null)
                throw new RuntimeAuthorizationException(RuntimeNativePolicyGate.DeniedCode);
            await RequireApprovalStateVersionAsync(
                registration.Binding.TurnId,
                request.EventId,
                current.StateVersion,
                cancellationToken).ConfigureAwait(false);
            return new(RuntimeNativeApprovalDisposition.Approved, requestId, subjectId);
        }
        if (await HasApprovalStateVersionAsync(
                registration.Binding.TurnId,
                request.EventId,
                cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");

        if (current.State.PendingGate is { } pending)
        {
            if (pending.RequestId == requestId &&
                pending.SubjectId == subjectId &&
                pending.Kind == CoordinatorGateKind.Approval &&
                pending.AuthorizedActorId == selection.Authorization.ActorId &&
                pending.Fence == current.State.Fence)
                return new(RuntimeNativeApprovalDisposition.Pending, requestId, subjectId);
            throw new RuntimeAuthorizationException(CollisionCode);
        }

        var transition = CoordinatorDecisionFlow.RequestApproval(
            current.State, requestId, subjectId, selection.Authorization.ActorId,
            "Approve this exact Guardrails-escalated model turn.");
        if (!transition.IsSuccess || transition.Value is null)
            throw new RuntimeAuthorizationException(CollisionCode);
        _ = await decisions.PersistTransitionAsync(
            actor,
            identity,
            selection,
            current.StateVersion,
            "native-approval-" + subjectId,
            requestId,
            "approval.request",
            CoordinatorDecisionOwnerStore.ComputeCommandHash(new
            {
                current.StateVersion,
                requestId,
                subjectId,
                turnId = registration.Binding.TurnId,
                request.InputHash
            }),
            transition.State!,
            true,
            transition.Issues,
            null,
            null,
            transition.Value,
            cancellationToken).ConfigureAwait(false);
        return new(RuntimeNativeApprovalDisposition.Pending, requestId, subjectId);
    }

    public async Task ClaimAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var exists = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_table}
                WHERE owner_turn_id = @turn AND operation_id = @operation
                    AND project_id = @project AND run_id = @run AND session_id = @session
                    AND request_hash = @request_hash)
            """, connection, transaction))
        {
            exists.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, registration.Binding.TurnId);
            exists.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.EventId);
            exists.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
            exists.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
            exists.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, registration.Binding.SessionId);
            exists.Parameters.AddWithValue("request_hash", NpgsqlDbType.Char, request.InputHash);
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        var identity = await ReadRootIdentityAsync(
            actor, selection, registration, cancellationToken).ConfigureAwait(false);
        var current = await decisions.ReadCurrentInTransactionAsync(
            connection, transaction, actor, identity, selection, cancellationToken, forUpdate: true)
            .ConfigureAwait(false);
        var subjectId = SubjectId(registration.Binding.TurnId, request);
        var requestId = "approval-" + subjectId;
        var receipt = current.State.DecisionReceipts.LastOrDefault(item =>
            item.RequestId == requestId && item.SubjectId == subjectId);
        if (receipt is null ||
            receipt.Kind != CoordinatorGateKind.Approval ||
            receipt.ActorId != selection.Authorization.ActorId ||
            receipt.Fence != current.State.Fence ||
            receipt.ChoiceId != CoordinatorGateChoices.Approve ||
            receipt.FreeformAnswer is not null)
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");

        await using (var row = new NpgsqlCommand($"""
            SELECT approval_state_version, consumed_at, execution_fence
            FROM {_table}
            WHERE owner_turn_id = @turn AND operation_id = @operation
                AND project_id = @project AND run_id = @run AND session_id = @session
                AND request_hash = @request_hash
            FOR UPDATE
            """, connection, transaction))
        {
            row.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, registration.Binding.TurnId);
            row.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.EventId);
            row.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
            row.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
            row.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, registration.Binding.SessionId);
            row.Parameters.AddWithValue("request_hash", NpgsqlDbType.Char, request.InputHash);
            await using var reader = await row.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.IsDBNull(0) ||
                reader.GetInt64(0) != current.StateVersion ||
                reader.GetInt64(2) != registration.Binding.ExecutionFence)
                throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
            if (!reader.IsDBNull(1))
                throw new RuntimeAuthorizationException("runtime_native_policy_approval_already_consumed");
            if (await reader.ReadAsync(cancellationToken))
                throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
        }

        await using var command = new NpgsqlCommand($"""
            UPDATE {_table}
            SET consumed_at = clock_timestamp()
            WHERE owner_turn_id = @turn AND operation_id = @operation
                AND project_id = @project AND run_id = @run AND session_id = @session
                AND request_hash = @request_hash
                AND execution_fence = @fence
                AND approval_state_version = @state_version
                AND consumed_at IS NULL
            RETURNING true
            """, connection, transaction);
        command.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, registration.Binding.TurnId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.EventId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, registration.Binding.SessionId);
        command.Parameters.AddWithValue("request_hash", NpgsqlDbType.Char, request.InputHash);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, registration.Binding.ExecutionFence);
        command.Parameters.AddWithValue("state_version", NpgsqlDbType.Bigint, current.StateVersion);
        var consumed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (consumed is null)
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionIdentity> ReadRootIdentityAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        if (actor.Issuer != registration.Binding.ActorIssuer ||
            actor.Subject != registration.Binding.ActorId ||
            selection.Authorization.ActorId != actor.Subject ||
            selection.Authorization.Issuer != actor.Issuer)
            throw new RuntimeAuthorizationException("runtime_action_actor_invalid");
        var root = await coordination.ReadProjectRunRootSessionIdAsync(
            registration.Binding.ProjectId, registration.Binding.RunId, cancellationToken).ConfigureAwait(false);
        return new(registration.Binding.ProjectId, registration.Binding.RunId, root);
    }

    public async Task<bool> RequireApprovedHandoffAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var exists = new NpgsqlCommand($"""
            SELECT approval_state_version, consumed_at, execution_fence
            FROM {_table}
            WHERE owner_turn_id = @turn AND operation_id = @operation
                AND project_id = @project AND run_id = @run AND session_id = @session
                AND request_hash = @request_hash
            """, connection);
        exists.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, registration.Binding.TurnId);
        exists.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.EventId);
        exists.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
        exists.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
        exists.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, registration.Binding.SessionId);
        exists.Parameters.AddWithValue("request_hash", NpgsqlDbType.Char, request.InputHash);
        await using var reader = await exists.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken))
            return false;
        if (reader.IsDBNull(0))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            var identity = await ReadRootIdentityAsync(
                actor, selection, registration, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            var subjectId = SubjectId(registration.Binding.TurnId, request);
            var requestId = "approval-" + subjectId;
            var receipt = current.State.DecisionReceipts.LastOrDefault(item =>
                item.RequestId == requestId && item.SubjectId == subjectId);
            if (receipt is null)
                throw new RuntimeAuthorizationException(PendingCode);
            if (receipt.Kind != CoordinatorGateKind.Approval ||
                receipt.ActorId != selection.Authorization.ActorId ||
                receipt.Fence != current.State.Fence)
                throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
            if (receipt.ChoiceId != CoordinatorGateChoices.Approve ||
                receipt.FreeformAnswer is not null)
                throw new RuntimeAuthorizationException(RuntimeNativePolicyGate.DeniedCode);
            await RequireApprovalStateVersionAsync(
                registration.Binding.TurnId, request.EventId, current.StateVersion, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        if (!reader.IsDBNull(1))
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_already_consumed");
        if (reader.GetInt64(2) != registration.Binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
        if (await reader.ReadAsync(cancellationToken))
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
        await reader.DisposeAsync().ConfigureAwait(false);
        await RequireCurrentApprovalAsync(actor, selection, registration, request, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task RequireCurrentApprovalAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var exists = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_table}
                WHERE owner_turn_id = @turn AND operation_id = @operation
                    AND project_id = @project AND run_id = @run AND session_id = @session
                    AND request_hash = @request_hash)
            """, connection);
        exists.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, registration.Binding.TurnId);
        exists.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.EventId);
        exists.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
        exists.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
        exists.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, registration.Binding.SessionId);
        exists.Parameters.AddWithValue("request_hash", NpgsqlDbType.Char, request.InputHash);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            return;

        var identity = await ReadRootIdentityAsync(
            actor, selection, registration, cancellationToken).ConfigureAwait(false);
        var subjectId = SubjectId(registration.Binding.TurnId, request);
        var requestId = "approval-" + subjectId;
        var current = await decisions.ReadCurrentAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        var receipt = current.State.DecisionReceipts.LastOrDefault(item =>
            item.RequestId == requestId && item.SubjectId == subjectId);
        if (receipt is null ||
            receipt.Kind != CoordinatorGateKind.Approval ||
            receipt.ActorId != selection.Authorization.ActorId ||
            receipt.Fence != current.State.Fence ||
            receipt.ChoiceId != CoordinatorGateChoices.Approve ||
            receipt.FreeformAnswer is not null ||
            !await SameApprovalStateVersionAsync(
                registration.Binding.TurnId,
                request.EventId,
                current.StateVersion,
                cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
    }

    private async Task RequireApprovalStateVersionAsync(
        string turnId,
        Guid operationId,
        long stateVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_table}
            SET approval_state_version = @state_version
            WHERE owner_turn_id = @turn AND operation_id = @operation
                AND (approval_state_version IS NULL OR approval_state_version = @state_version)
            RETURNING approval_state_version
            """, connection);
        command.Parameters.AddWithValue("state_version", NpgsqlDbType.Bigint, stateVersion);
        command.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, turnId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long saved ||
            saved != stateVersion)
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_stale");
    }

    private async Task<bool> SameApprovalStateVersionAsync(
        string turnId,
        Guid operationId,
        long stateVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT approval_state_version = @state_version
            FROM {_table}
            WHERE owner_turn_id = @turn AND operation_id = @operation
            """, connection);
        command.Parameters.AddWithValue("state_version", NpgsqlDbType.Bigint, stateVersion);
        command.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, turnId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private async Task<bool> HasApprovalStateVersionAsync(
        string turnId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT approval_state_version IS NOT NULL
            FROM {_table}
            WHERE owner_turn_id = @turn AND operation_id = @operation
            """, connection);
        command.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, turnId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private async Task RequireStoredAsync(
        SessionIdentity identity,
        string turnId,
        long executionFence,
        RuntimeActionRequest request,
        string policyInputHash,
        string retainedJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT project_id, run_id, session_id, request_hash, policy_input_hash,
                retained_evaluation::text, execution_fence
            FROM {_table}
            WHERE owner_turn_id = @turn AND operation_id = @operation
            """, connection);
        command.Parameters.AddWithValue("turn", NpgsqlDbType.Varchar, turnId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.EventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetString(0) != identity.ProjectId ||
            reader.GetString(1) != identity.RunId ||
            reader.GetString(2) != identity.SessionId ||
            reader.GetString(3).TrimEnd() != request.InputHash ||
            reader.GetString(4).TrimEnd() != policyInputHash ||
            !JsonElement.DeepEquals(
                JsonDocument.Parse(reader.GetString(5)).RootElement,
                JsonDocument.Parse(retainedJson).RootElement) ||
            reader.GetInt64(6) != executionFence)
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_binding_changed");
        if (await reader.ReadAsync(cancellationToken))
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_binding_changed");
    }

    private static void Validate(
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        RuntimeNativePolicyGuardrailsResult retained)
    {
        RuntimeContractValidation.Validate(registration);
        RuntimeActionContract.Validate(request);
        if (!retained.RequiresApproval ||
            retained.Escalations.IsDefault ||
            retained.Escalations.Any(item => !MatchesApprovalBinding(item, registration, request)))
            throw new RuntimeAuthorizationException("runtime_native_policy_approval_invalid");
    }

    private static bool MatchesApprovalBinding(
        RuntimeNativePolicyRetainedEvaluation item,
        RuntimeRegistration registration,
        RuntimeActionRequest request) =>
        item is not null &&
        item.PolicyInput.ValueKind == JsonValueKind.Object &&
        item.PolicyInput.TryGetProperty("policy_target", out var target) &&
        target.TryGetProperty("value", out var value) &&
        value.TryGetProperty("approval", out var approval) &&
        approval.ValueKind == JsonValueKind.Object &&
        approval.TryGetProperty("turn_id", out var turnId) &&
        approval.TryGetProperty("arguments_digest", out var digest) &&
        turnId.ValueKind == JsonValueKind.String &&
        digest.ValueKind == JsonValueKind.String &&
        turnId.GetString() == registration.Binding.TurnId &&
        digest.GetString() == request.InputHash;

    internal static string SubjectId(string turnId, RuntimeActionRequest request)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{turnId}\n{request.EventId:N}\n{request.InputHash}"));
        return $"turn-{Convert.ToHexStringLower(hash)}";
    }

    private static string Hash(IEnumerable<JsonElement> inputs) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", inputs.Select(item => item.GetRawText())))));
}
