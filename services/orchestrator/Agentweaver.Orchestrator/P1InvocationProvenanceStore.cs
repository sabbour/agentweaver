using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    private const int P1ReceiptHashVersion = 1;
    private const int P1ReceiptSnapshotMaximumBytes = 16 * 1024;
    private const string P1ReceiptHashPrefix = "agentweaver.p1.setup-receipt.v1\n";

    internal static string HashP1SetupReceipt(P1ProjectSetupReceiptSnapshot receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(P1ReceiptHashPrefix + json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void ValidateP1SetupReceiptForRequest(
        CoordinationActor actor,
        string tenantId,
        string projectId,
        CreateProjectRunRequest request,
        P1ProjectSetupReceiptSnapshot? receipt)
    {
        if (request.P1SetupInvocationId is null && receipt is null)
            return;
        if (request.P1SetupInvocationId is not { } invocationId || receipt is null ||
            invocationId == Guid.Empty || receipt.InvocationId != invocationId ||
            receipt.Issuer != actor.Issuer || receipt.Subject != actor.Subject ||
            receipt.CreatedByActorId != actor.Subject || receipt.TenantId != tenantId ||
            receipt.ProjectId != projectId ||
            receipt.ProjectConfigurationRevision != request.ExpectedProjectConfigRevision ||
            receipt.MembershipId == Guid.Empty || receipt.MembershipRevision < 1 ||
            receipt.ProjectRevision < 1 || receipt.AssignmentId == Guid.Empty ||
            receipt.AssignmentRevision < 1 || receipt.AuthorityAuditEventId == Guid.Empty ||
            receipt.OwnerAssignmentId == Guid.Empty ||
            receipt.OwnerAuthorityAuditEventId == Guid.Empty ||
            (receipt.OwnerAssignmentId is null) != (receipt.OwnerAssignmentRevision is null) ||
            receipt.OwnerAssignmentRevision is <= 0 ||
            receipt.CreatedAt == default ||
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(receipt, JsonOptions)) >
                P1ReceiptSnapshotMaximumBytes)
            throw new CoordinationException("p1_setup_receipt_stale", StatusCodes.Status409Conflict);
    }

    private async Task PersistP1RunRequestProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        string tenantId,
        string projectId,
        ProjectRunRequestRow row,
        P1ProjectSetupReceiptSnapshot receipt,
        CancellationToken cancellationToken)
    {
        ValidateP1SetupReceiptForRequest(
            actor, tenantId, projectId,
            new CreateProjectRunRequest(row.FirstMessage, row.ExpectedProjectConfigRevision,
                WorkflowId: row.WorkflowId, P1SetupInvocationId: row.P1SetupInvocationId),
            receipt);
        var hash = HashP1SetupReceipt(receipt);
        var existing = await ReadP1RunRequestProvenanceAsync(
            connection, transaction, tenantId, projectId, row.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.SetupInvocationId != receipt.InvocationId ||
                existing.SetupReceiptHash != hash || existing.ReceiptSnapshot != receipt ||
                existing.TenantId != tenantId || existing.ProjectId != projectId ||
                existing.RunId != row.RunId || existing.RootSessionId != row.RootSessionId ||
                existing.ActorIssuer != actor.Issuer || existing.ActorSubject != actor.Subject ||
                existing.MembershipId != receipt.MembershipId ||
                existing.MembershipRevision != receipt.MembershipRevision ||
                existing.ProjectRevision != receipt.ProjectRevision ||
                existing.ProjectConfigurationRevision != receipt.ProjectConfigurationRevision)
                throw new CoordinationException("p1_setup_receipt_changed", StatusCodes.Status409Conflict);
            return;
        }

        var snapshot = JsonSerializer.Serialize(receipt, JsonOptions);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.p1_run_request_provenance
                (provenance_id, contract_version, hash_version, setup_invocation_id, setup_receipt_hash,
                 receipt_snapshot, tenant_id, project_id, run_id, root_session_id, actor_issuer,
                 actor_subject, membership_id, membership_revision, project_revision,
                 project_configuration_revision)
            VALUES (@provenance, 1, @hash_version, @invocation, @receipt_hash, @snapshot,
                    @tenant, @project, @run, @root, @issuer, @subject, @membership,
                    @membership_revision, @project_revision, @configuration_revision)
            """, connection, transaction);
        insert.Parameters.AddWithValue("provenance", NpgsqlDbType.Uuid, Guid.NewGuid());
        insert.Parameters.AddWithValue("hash_version", NpgsqlDbType.Integer, P1ReceiptHashVersion);
        insert.Parameters.AddWithValue("invocation", NpgsqlDbType.Uuid, receipt.InvocationId);
        insert.Parameters.AddWithValue("receipt_hash", NpgsqlDbType.Char, hash);
        insert.Parameters.AddWithValue("snapshot", NpgsqlDbType.Jsonb, snapshot);
        insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, row.RunId);
        insert.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, row.RootSessionId);
        insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        insert.Parameters.AddWithValue("membership", NpgsqlDbType.Uuid, receipt.MembershipId);
        insert.Parameters.AddWithValue("membership_revision", NpgsqlDbType.Bigint, receipt.MembershipRevision);
        insert.Parameters.AddWithValue("project_revision", NpgsqlDbType.Bigint, receipt.ProjectRevision);
        insert.Parameters.AddWithValue(
            "configuration_revision", NpgsqlDbType.Bigint, receipt.ProjectConfigurationRevision);
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == "23505")
        {
            throw new CoordinationException("p1_setup_invocation_conflict", StatusCodes.Status409Conflict, exception);
        }
    }

    private async Task<P1RunRequestProvenance?> ReadP1RunRequestProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string runId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand($"""
            SELECT provenance_id, contract_version, hash_version, setup_invocation_id, setup_receipt_hash,
                   receipt_snapshot::text, tenant_id, project_id, run_id, root_session_id, actor_issuer,
                   actor_subject, membership_id, membership_revision, project_revision,
                   project_configuration_revision, verified_at
            FROM {_schema}.p1_run_request_provenance
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        query.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        query.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        query.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        P1ProjectSetupReceiptSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<P1ProjectSetupReceiptSnapshot>(
                reader.GetString(5), JsonOptions)
                ?? throw new JsonException("The saved P1 setup receipt snapshot is empty.");
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "p1_setup_provenance_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
        var provenance = new P1RunRequestProvenance(
            reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetGuid(3),
            reader.GetString(4).TrimEnd(), snapshot, reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.GetGuid(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
            reader.GetFieldValue<DateTimeOffset>(16));
        if (provenance.ContractVersion != 1 || provenance.HashVersion != P1ReceiptHashVersion ||
            provenance.SetupReceiptHash != HashP1SetupReceipt(snapshot))
            throw new CoordinationException("p1_setup_provenance_invalid", StatusCodes.Status503ServiceUnavailable);
        return provenance;
    }
}

internal sealed record P1RunRequestProvenance(
    Guid ProvenanceId,
    int ContractVersion,
    int HashVersion,
    Guid SetupInvocationId,
    string SetupReceiptHash,
    P1ProjectSetupReceiptSnapshot ReceiptSnapshot,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string ActorIssuer,
    string ActorSubject,
    Guid MembershipId,
    long MembershipRevision,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    DateTimeOffset VerifiedAt);
