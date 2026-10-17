using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Identity;

/// <summary>Request for owner authorization of a run's immutable accepted-selection hash.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRunAdmissionRequest(int ContractVersion, string AcceptedSelectionHash);

/// <summary>Owner-issued run admission snapshot containing the selected model and initial cost quote.</summary>
public sealed record RuntimeRunAdmissionReceipt(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string AcceptedSelectionHash,
    RuntimeAcceptedModelSelection ModelSelection,
    RuntimeModelBindingPin ModelBindingPin,
    CostBinding? CostBinding,
    CostPrice Quote,
    UsageRunTotals CopilotTotals);

/// <summary>Validates run admission requests, selection scope, and priced Copilot receipts.</summary>
public static class RuntimeRunAdmissionContract
{
    /// <summary>Requires the supported contract version and a canonical accepted-selection hash.</summary>
    public static void ValidateRequest(RuntimeRunAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        RuntimeContractValidation.ValidateHash(request.AcceptedSelectionHash);
    }

    /// <summary>Hashes the exact JSON bytes representing the accepted selection.</summary>
    public static string SelectionHash(JsonElement selection) =>
        RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(selection.GetRawText()));

    /// <summary>Requires the selection to contain the expected project/run and positive revision fields.</summary>
    public static void ValidateSelectionScope(JsonElement selection, string projectId, string runId)
    {
        if (selection.ValueKind != JsonValueKind.Object ||
            !selection.TryGetProperty("projectId", out var project) || project.ValueKind != JsonValueKind.String ||
            project.GetString() != projectId ||
            !selection.TryGetProperty("runId", out var run) || run.ValueKind != JsonValueKind.String ||
            run.GetString() != runId ||
            !selection.TryGetProperty("projectRevision", out var projectRevision) ||
            projectRevision.ValueKind != JsonValueKind.Number ||
            !projectRevision.TryGetInt64(out var projectVersion) || projectVersion < 1 ||
            !selection.TryGetProperty("projectConfigurationRevision", out var configurationRevision) ||
            configurationRevision.ValueKind != JsonValueKind.Number ||
            !configurationRevision.TryGetInt64(out var configurationVersion) || configurationVersion < 1 ||
            !selection.TryGetProperty("platformRuntimeRevision", out var platformRevision) ||
            platformRevision.ValueKind != JsonValueKind.Number ||
            !platformRevision.TryGetInt64(out var platformVersion) || platformVersion < 1 ||
            !selection.TryGetProperty("contextRevision", out var context) || context.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(context.GetString()))
            throw new RuntimeAuthorizationException("runtime_run_admission_selection_invalid");
    }

    /// <summary>
    /// Checks tenant, project, run, selection and hosted-model bindings, then validates the
    /// initial Copilot snapshot and returns its known priced subtotal.
    /// </summary>
    public static decimal ValidateReceipt(
        RuntimeRunAdmissionReceipt receipt, RuntimeRunAdmissionRequest request,
        string tenantId, string projectId, string runId, JsonElement selection)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateRequest(request);
        ValidateSelectionScope(selection, projectId, runId);
        var model = RuntimeAcceptedModelSelection.Read(selection);
        if (receipt.ContractVersion != 1 || receipt.TenantId != tenantId ||
            receipt.ProjectId != projectId || receipt.RunId != runId ||
            receipt.AcceptedSelectionHash != request.AcceptedSelectionHash ||
            SelectionHash(selection) != request.AcceptedSelectionHash ||
            model.SourceMode != ModelSourceMode.HostedCopilot || model.ConnectionId is null ||
            model.CredentialReference is not null || receipt.ModelSelection != model ||
            receipt.ModelBindingPin is not { } pin ||
            pin.ModelSelectionReference != model.Reference || pin.SourceMode != model.SourceMode ||
            pin.ReasoningEffort != model.ReasoningEffort ||
            model.ModelBindingPin is { } acceptedPin && pin != acceptedPin)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        RuntimeModelBindingsResolver.ValidatePin(pin);
        return RuntimeUsageCostSnapshotContract.ValidatePricedCopilotSnapshot(
            receipt.CostBinding, receipt.Quote, receipt.CopilotTotals, pin.ModelId, tenantId, projectId, runId);
    }
}
