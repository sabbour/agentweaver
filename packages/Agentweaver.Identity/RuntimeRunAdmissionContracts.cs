using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRunAdmissionRequest(int ContractVersion, string AcceptedSelectionHash);

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

public static class RuntimeRunAdmissionContract
{
    public static void ValidateRequest(RuntimeRunAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        RuntimeContractValidation.ValidateHash(request.AcceptedSelectionHash);
    }

    public static string SelectionHash(JsonElement selection) =>
        RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(selection.GetRawText()));

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
            pin.ModelSelectionReference != model.Reference || pin.SourceMode != model.SourceMode)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        RuntimeModelBindingsResolver.ValidatePin(pin);
        return RuntimeUsageCostSnapshotContract.ValidatePricedCopilotSnapshot(
            receipt.CostBinding, receipt.Quote, receipt.CopilotTotals, pin.ModelId, tenantId, projectId, runId);
    }
}
