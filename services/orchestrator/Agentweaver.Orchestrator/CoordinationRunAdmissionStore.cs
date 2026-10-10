using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    private const string RunAdmissionEventType = "orchestrator.run.copilot_admission";

    internal static bool RequiresRunAdmission(JsonElement selection)
    {
        if (selection.ValueKind != JsonValueKind.Object)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        if (!selection.TryGetProperty("runLimits", out _))
            return false;
        var limits = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(selection);
        if (limits.CopilotSoftCreditLimit is null && limits.CopilotHardCreditLimit is null)
            return false;
        try
        {
            var model = RuntimeAcceptedModelSelection.Read(selection);
            if (model.SourceMode != ModelSourceMode.HostedCopilot ||
                model.ConnectionId is null || model.CredentialReference is not null)
                throw new CoordinationException(
                    "runtime_cost_budget_model_unavailable", StatusCodes.Status409Conflict);
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "runtime_cost_budget_model_unavailable", StatusCodes.Status409Conflict, exception);
        }
        return true;
    }

    private async Task PrepareRunAdmissionInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AuthorizedRunSelection selection,
        Func<CancellationToken, Task<RuntimeRunAdmissionReceipt>>? readAdmission,
        Func<CancellationToken, Task>? revalidateCurrentAuthority, CancellationToken cancellationToken)
    {
        if (!RequiresRunAdmission(selection.Selection.Snapshot))
            return;
        if (readAdmission is null || revalidateCurrentAuthority is null)
            throw new CoordinationException(
                "runtime_run_admission_unavailable", StatusCodes.Status503ServiceUnavailable);
        var scope = selection.Selection;
        var tenant = selection.Authorization.TenantId;
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.run.admission'), hashtext(@run))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("run", NpgsqlDbType.Text,
                RunAdmissionId(tenant, scope.ProjectId, scope.RunId).ToString("D"));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var stored = await ReadRunAdmissionInTransactionAsync(
            connection, transaction, selection, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            var admission = await readAdmission(cancellationToken).ConfigureAwait(false);
            var observed = ValidateRunAdmission(admission, selection);
            var hard = CoordinatorWorkflowCatalog.ReadRuntimeBudgetLimits(scope.Snapshot).CopilotHardCreditLimit;
            if (hard is { } limit && observed >= limit)
                throw new CoordinationException(
                    "runtime_cost_budget_exhausted", StatusCodes.Status409Conflict);
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
                RunAdmissionId(tenant, scope.ProjectId, scope.RunId),
                $"coordination/{scope.ProjectId}/{scope.RunId}/admission",
                "copilot-run-admission",
                RunAdmissionEventType, 1, JsonSerializer.SerializeToElement(admission, JsonOptions),
                _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateRunAdmission(stored, selection);
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<RuntimeRunAdmissionReceipt?> ReadRunAdmissionAsync(
        CoordinationActor actor, AuthorizedRunSelection selection, CancellationToken cancellationToken)
    {
        ValidateAcceptRootInput(actor, selection, "run-admission");
        if (!RequiresRunAdmission(selection.Selection.Snapshot))
            return null;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await ReadRunAdmissionInTransactionAsync(
            connection, null, selection, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "runtime_run_admission_missing", StatusCodes.Status409Conflict);
        ValidateRunAdmission(receipt, selection);
        return receipt;
    }

    private async Task<RuntimeRunAdmissionReceipt?> ReadRunAdmissionInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        AuthorizedRunSelection selection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_schema}.outbox_events
            WHERE id = @receipt AND event_type = @type AND event_version = 1
            """, connection, transaction);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid,
            RunAdmissionId(selection.Authorization.TenantId, selection.Selection.ProjectId, selection.Selection.RunId));
        command.Parameters.AddWithValue("type", NpgsqlDbType.Text, RunAdmissionEventType);
        var payload = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<RuntimeRunAdmissionReceipt>(payload, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "runtime_run_admission_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }

    private static Guid RunAdmissionId(string tenantId, string projectId, string runId) =>
        Guid.ParseExact(RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
            $"copilot-run-admission\0{tenantId}\0{projectId}\0{runId}"))[..32], "N");

    private static decimal ValidateRunAdmission(
        RuntimeRunAdmissionReceipt admission, AuthorizedRunSelection selection)
    {
        try
        {
            var scope = selection.Selection;
            return RuntimeRunAdmissionContract.ValidateReceipt(admission,
                new(1, RuntimeRunAdmissionContract.SelectionHash(scope.Snapshot)),
                selection.Authorization.TenantId, scope.ProjectId, scope.RunId, scope.Snapshot);
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(exception.Code, StatusCodes.Status409Conflict, exception);
        }
    }
}
