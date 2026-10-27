using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    internal async Task<P1InvocationBinding> AcceptP1InteractiveRootInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        ProjectRunRequestRow request,
        P1ProjectSetupReceiptSnapshot verifiedReceipt,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<RuntimeRunAdmissionReceipt>>? readRunAdmission = null,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        if (request.P1SetupInvocationId is not { } setupInvocationId ||
            setupInvocationId != verifiedReceipt.InvocationId ||
            request.RunId != selection.Selection.RunId ||
            request.RootSessionId is not { Length: > 0 } ||
            selection.Selection.ProjectId is not { Length: > 0 })
            throw new CoordinationException("p1_invocation_binding_invalid", StatusCodes.Status409Conflict);

        var provenance = await ReadP1RunRequestProvenanceAsync(
            connection, transaction, selection.Authorization.TenantId,
            selection.Selection.ProjectId, selection.Selection.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException("p1_setup_provenance_unavailable", StatusCodes.Status409Conflict);
        var selectionHash = HashSelection(selection.Selection).ToLowerInvariant();
        if (provenance.ContractVersion != 1 ||
            provenance.SetupInvocationId != setupInvocationId ||
            provenance.ReceiptSnapshot != verifiedReceipt ||
            provenance.SetupReceiptHash != HashP1SetupReceipt(verifiedReceipt) ||
            provenance.TenantId != selection.Authorization.TenantId ||
            provenance.ProjectId != selection.Selection.ProjectId ||
            provenance.RunId != selection.Selection.RunId ||
            provenance.RootSessionId != request.RootSessionId ||
            provenance.ActorIssuer != actor.Issuer || provenance.ActorSubject != actor.Subject ||
            provenance.MembershipId != verifiedReceipt.MembershipId ||
            provenance.MembershipRevision != selection.Authorization.MembershipRevision ||
            provenance.ProjectRevision != selection.Selection.ProjectRevision ||
            provenance.ProjectConfigurationRevision != request.ExpectedProjectConfigRevision ||
            selection.Selection.ProjectConfigurationRevision != request.ExpectedProjectConfigRevision ||
            verifiedReceipt.Issuer != actor.Issuer || verifiedReceipt.Subject != actor.Subject ||
            verifiedReceipt.CreatedByActorId != actor.Subject ||
            verifiedReceipt.TenantId != selection.Authorization.TenantId ||
            verifiedReceipt.ProjectId != selection.Selection.ProjectId ||
            verifiedReceipt.MembershipRevision != selection.Authorization.MembershipRevision ||
            verifiedReceipt.ProjectRevision != selection.Selection.ProjectRevision ||
            verifiedReceipt.ProjectConfigurationRevision != selection.Selection.ProjectConfigurationRevision ||
            selection.Authorization.ContractVersion != 1 ||
            selection.Authorization.Issuer != actor.Issuer ||
            selection.Authorization.ActorId != actor.Subject ||
            selection.Authorization.BoundProjectId != selection.Selection.ProjectId ||
            selection.Authorization.BoundRunId != selection.Selection.RunId)
            throw new CoordinationException("p1_setup_receipt_stale", StatusCodes.Status409Conflict);

        var existingBinding = await ReadP1InvocationBindingInTransactionAsync(
            connection, transaction, setupInvocationId, cancellationToken).ConfigureAwait(false);
        if (existingBinding is not null)
        {
            if (existingBinding.ProvenanceId != provenance.ProvenanceId ||
                existingBinding.Binding.SetupReceiptHash != provenance.SetupReceiptHash ||
                existingBinding.Binding.TenantId != provenance.TenantId ||
                existingBinding.Binding.ProjectId != provenance.ProjectId ||
                existingBinding.Binding.RunId != provenance.RunId ||
                existingBinding.Binding.RootSessionId != provenance.RootSessionId ||
                existingBinding.Binding.ActorIssuer != actor.Issuer ||
                existingBinding.Binding.ActorSubject != actor.Subject ||
                existingBinding.Binding.MembershipId != provenance.MembershipId ||
                existingBinding.Binding.MembershipRevision != provenance.MembershipRevision ||
                existingBinding.Binding.AcceptedSelectionHash != selectionHash)
                throw new CoordinationException("p1_invocation_binding_conflict", StatusCodes.Status409Conflict);
            await RequireCurrentP1RootInTransactionAsync(
                connection, transaction, actor, selection, existingBinding.Binding, cancellationToken)
                .ConfigureAwait(false);
            return existingBinding.Binding;
        }

        await using (var exists = new NpgsqlCommand($"""
            SELECT EXISTS (SELECT 1 FROM {_runs} WHERE project_id = @project AND run_id = @run)
            """, connection, transaction))
        {
            AddRunScope(exists, selection.Selection.ProjectId, selection.Selection.RunId);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                throw new CoordinationException(
                    "p1_invocation_binding_missing", StatusCodes.Status409Conflict);
        }

        var accepted = await AcceptRootInTransactionAsync(
            connection, transaction, actor, selection, request.RootSessionId, cancellationToken,
            readRunAdmission, revalidateCurrentAuthority, requireNewRoot: true).ConfigureAwait(false);
        var decisions = _decisionStore
            ?? throw new InvalidOperationException("The coordinator decision owner is not configured.");
        await decisions.InitializeRootInTransactionAsync(
            connection, transaction, actor,
            new SessionIdentity(selection.Selection.ProjectId, selection.Selection.RunId, accepted.RootSessionId),
            selection,
            CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Selection.Snapshot),
            cancellationToken).ConfigureAwait(false);

        await using var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction);
        var acceptedAt = new DateTimeOffset(
            (DateTime)(await clock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        var coreInvocationId = Guid.NewGuid();
        var binding = new P1InvocationBinding(
            1, coreInvocationId, setupInvocationId, provenance.SetupReceiptHash,
            provenance.TenantId, provenance.ProjectId, provenance.RunId, provenance.RootSessionId,
            actor.Issuer, actor.Subject, provenance.MembershipId, provenance.MembershipRevision,
            selectionHash, accepted.ExecutionFence, accepted.StateVersion, acceptedAt);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.p1_invocation_bindings
                (contract_version, core_invocation_id, provenance_id, setup_invocation_id,
                 setup_receipt_hash, tenant_id, project_id, run_id, root_session_id,
                 actor_issuer, actor_subject, membership_id, membership_revision,
                 accepted_selection_hash, execution_fence, root_state_version, accepted_at)
            VALUES (@contract, @core_invocation, @provenance, @setup_invocation,
                    @receipt_hash, @tenant, @project, @run, @root,
                    @issuer, @subject, @membership, @membership_revision,
                    @selection_hash, @fence, @state_version, @accepted_at)
            """, connection, transaction);
        insert.Parameters.AddWithValue("contract", NpgsqlDbType.Integer, binding.ContractVersion);
        insert.Parameters.AddWithValue("core_invocation", NpgsqlDbType.Uuid, binding.CoreInvocationId);
        insert.Parameters.AddWithValue("provenance", NpgsqlDbType.Uuid, provenance.ProvenanceId);
        insert.Parameters.AddWithValue("setup_invocation", NpgsqlDbType.Uuid, binding.SetupInvocationId);
        insert.Parameters.AddWithValue("receipt_hash", NpgsqlDbType.Char, binding.SetupReceiptHash);
        insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
        insert.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, binding.RootSessionId);
        insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, binding.ActorIssuer);
        insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, binding.ActorSubject);
        insert.Parameters.AddWithValue("membership", NpgsqlDbType.Uuid, binding.MembershipId);
        insert.Parameters.AddWithValue("membership_revision", NpgsqlDbType.Bigint, binding.MembershipRevision);
        insert.Parameters.AddWithValue("selection_hash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        insert.Parameters.AddWithValue("state_version", NpgsqlDbType.Bigint, binding.RootStateVersion);
        insert.Parameters.AddWithValue("accepted_at", NpgsqlDbType.TimestampTz, binding.AcceptedAt);
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == "23505")
        {
            throw new CoordinationException(
                "p1_invocation_binding_conflict", StatusCodes.Status409Conflict, exception);
        }
        return binding;
    }

    internal async Task<P1InvocationBinding> ReadCurrentP1InvocationBindingAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT b.contract_version, b.core_invocation_id, b.setup_invocation_id, b.setup_receipt_hash,
                   b.tenant_id, b.project_id, b.run_id, b.root_session_id, b.actor_issuer, b.actor_subject,
                   b.membership_id, b.membership_revision, b.accepted_selection_hash, b.execution_fence,
                   b.root_state_version, b.accepted_at, p.receipt_snapshot::text,
                   r.accepted_selection_hash, r.accepted_by_issuer, r.accepted_by_subject, r.tenant_id,
                   r.execution_fence, r.state_version, s.session_id, s.parent_session_id, s.writer_issuer,
                   s.writer_subject, s.execution_fence, s.lifecycle_state
            FROM {_schema}.p1_invocation_bindings b
            INNER JOIN {_schema}.p1_run_request_provenance p ON p.provenance_id = b.provenance_id
            INNER JOIN {_runs} r ON r.project_id = b.project_id AND r.run_id = b.run_id
            INNER JOIN {_sessions} s ON s.project_id = b.project_id AND s.run_id = b.run_id
                AND s.session_id = b.root_session_id
            WHERE b.project_id = @project AND b.run_id = @run
            """, connection);
        AddRunScope(command, selection.Selection.ProjectId, selection.Selection.RunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("p1_invocation_binding_not_found", StatusCodes.Status404NotFound);
        var binding = ReadP1InvocationBinding(reader);
        P1ProjectSetupReceiptSnapshot receiptSnapshot;
        try
        {
            receiptSnapshot = JsonSerializer.Deserialize<P1ProjectSetupReceiptSnapshot>(
                reader.GetString(16), JsonOptions)
                ?? throw new JsonException("The saved P1 setup receipt snapshot is empty.");
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "p1_setup_provenance_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
        var currentAcceptedSelectionHash = reader.GetString(17).TrimEnd().ToLowerInvariant();
        var rootSessionId = reader.GetString(23);
        if (binding.ContractVersion != 1 ||
            binding.SetupReceiptHash != HashP1SetupReceipt(receiptSnapshot) ||
            binding.TenantId != selection.Authorization.TenantId ||
            binding.ProjectId != selection.Selection.ProjectId ||
            binding.RunId != selection.Selection.RunId ||
            binding.ActorIssuer != actor.Issuer || binding.ActorSubject != actor.Subject ||
            binding.MembershipRevision != selection.Authorization.MembershipRevision ||
            selection.Authorization.ContractVersion != 1 ||
            selection.Authorization.Issuer != actor.Issuer ||
            selection.Authorization.ActorId != actor.Subject ||
            selection.Authorization.BoundProjectId != selection.Selection.ProjectId ||
            selection.Authorization.BoundRunId != selection.Selection.RunId ||
            binding.AcceptedSelectionHash != HashSelection(selection.Selection).ToLowerInvariant() ||
            binding.AcceptedSelectionHash != currentAcceptedSelectionHash ||
            binding.ActorIssuer != reader.GetString(18) || binding.ActorSubject != reader.GetString(19) ||
            binding.TenantId != reader.GetString(20) ||
            binding.ExecutionFence != reader.GetInt64(21) ||
            reader.GetInt64(22) < binding.RootStateVersion ||
            rootSessionId != binding.RootSessionId ||
            !reader.IsDBNull(24) ||
            reader.GetString(25) != actor.Issuer || reader.GetString(26) != actor.Subject ||
            reader.GetInt64(27) != binding.ExecutionFence ||
            reader.GetString(28) != "active" ||
            receiptSnapshot.InvocationId != binding.SetupInvocationId ||
            receiptSnapshot.Issuer != binding.ActorIssuer ||
            receiptSnapshot.Subject != binding.ActorSubject ||
            receiptSnapshot.TenantId != binding.TenantId ||
            receiptSnapshot.MembershipId != binding.MembershipId ||
            receiptSnapshot.MembershipRevision != binding.MembershipRevision ||
            receiptSnapshot.ProjectId != selection.Selection.ProjectId ||
            receiptSnapshot.ProjectRevision != selection.Selection.ProjectRevision ||
            receiptSnapshot.ProjectConfigurationRevision != selection.Selection.ProjectConfigurationRevision)
            throw new CoordinationException(
                "p1_invocation_binding_stale", StatusCodes.Status409Conflict);
        return binding;
    }

    private async Task RequireCurrentP1RootInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        P1InvocationBinding binding,
        CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand($"""
            SELECT r.accepted_selection_hash, r.tenant_id, r.accepted_by_issuer, r.accepted_by_subject,
                   r.execution_fence, r.state_version, s.session_id, s.parent_session_id,
                   s.writer_issuer, s.writer_subject, s.execution_fence, s.lifecycle_state
            FROM {_runs} r
            INNER JOIN {_sessions} s ON s.project_id = r.project_id AND s.run_id = r.run_id
                AND s.session_id = @root
            WHERE r.project_id = @project AND r.run_id = @run
            FOR UPDATE OF r, s
            """, connection, transaction);
        AddRunScope(query, binding.ProjectId, binding.RunId);
        query.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, binding.RootSessionId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0).TrimEnd().ToLowerInvariant() != binding.AcceptedSelectionHash ||
            reader.GetString(1) != selection.Authorization.TenantId ||
            reader.GetString(2) != actor.Issuer || reader.GetString(3) != actor.Subject ||
            reader.GetInt64(4) != binding.ExecutionFence ||
            reader.GetInt64(5) < binding.RootStateVersion ||
            reader.GetString(6) != binding.RootSessionId ||
            !reader.IsDBNull(7) ||
            reader.GetString(8) != actor.Issuer || reader.GetString(9) != actor.Subject ||
            reader.GetInt64(10) != binding.ExecutionFence ||
            reader.GetString(11) != "active")
            throw new CoordinationException("p1_invocation_binding_stale", StatusCodes.Status409Conflict);
    }

    private async Task<P1InvocationBindingRow?> ReadP1InvocationBindingInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid setupInvocationId,
        CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand($"""
            SELECT b.contract_version, b.core_invocation_id, b.provenance_id, b.setup_invocation_id,
                   b.setup_receipt_hash, b.tenant_id, b.project_id, b.run_id, b.root_session_id,
                   b.actor_issuer, b.actor_subject, b.membership_id, b.membership_revision,
                   b.accepted_selection_hash, b.execution_fence, b.root_state_version, b.accepted_at
            FROM {_schema}.p1_invocation_bindings b
            WHERE b.setup_invocation_id = @setup_invocation
            FOR UPDATE
            """, connection, transaction);
        query.Parameters.AddWithValue("setup_invocation", NpgsqlDbType.Uuid, setupInvocationId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new P1InvocationBindingRow(
            new P1InvocationBinding(
                reader.GetInt32(0), reader.GetGuid(1), reader.GetGuid(3), reader.GetString(4).TrimEnd(),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), reader.GetString(10), reader.GetGuid(11), reader.GetInt64(12),
                reader.GetString(13).TrimEnd(), reader.GetInt64(14), reader.GetInt64(15),
                reader.GetFieldValue<DateTimeOffset>(16)),
            reader.GetGuid(2));
    }

    private static P1InvocationBinding ReadP1InvocationBinding(NpgsqlDataReader reader) => new(
        reader.GetInt32(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3).TrimEnd(),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.GetString(8), reader.GetString(9), reader.GetGuid(10), reader.GetInt64(11),
        reader.GetString(12).TrimEnd(), reader.GetInt64(13), reader.GetInt64(14),
        reader.GetFieldValue<DateTimeOffset>(15));

    private sealed record P1InvocationBindingRow(P1InvocationBinding Binding, Guid ProvenanceId);
}
