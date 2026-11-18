using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
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
    UsageRunTotals CopilotTotals)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RateCardContentHash { get; init; }

    public ImmutableArray<RuntimeRateCardAdmission> RateCardAdmissions { get; init; } = [];
}

/// <summary>Validates run admission requests, selection scope, and exact hosted or BYOK pricing pins.</summary>
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
            !RuntimeAcceptedModelSelection.SelectionsEqual(receipt.ModelSelection, model) ||
            receipt.ModelBindingPin is not { } pin ||
            pin.ModelSelectionReference != model.Reference || pin.SourceMode != model.SourceMode ||
            pin.ReasoningEffort != model.ReasoningEffort ||
            model.ModelBindingPin is { } acceptedPin && pin != acceptedPin)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        if (receipt.RateCardContentHash is { } contentHash)
            RuntimeContractValidation.ValidateHash(contentHash);
        if (receipt.RateCardContentHash !=
            (receipt.CostBinding?.RuntimeRateCardPin?.ContentHash ?? receipt.CostBinding?.RateCard?.ContentHash))
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        RuntimeModelBindingsResolver.ValidatePin(pin);
        var rateCardAdmissions = ValidateRateCardAdmissions(receipt.RateCardAdmissions, selection);
        if (model.SourceMode == ModelSourceMode.Byok &&
            model.ProviderBindingRevision is { } providerBinding &&
            model.CredentialReference is { } credential &&
            model.CredentialIdentity is { } credentialIdentity)
        {
            RuntimeModelBindingsResolver.ValidateProviderBinding(providerBinding);
            var expectedPin = RuntimeModelBindingsResolver.Pin(providerBinding, model.ReasoningEffort);
            var rateCardPin = providerBinding.RateCardPin;
            if (credential.Version != providerBinding.CredentialVersion ||
                providerBinding.CredentialIdentity != credentialIdentity ||
                pin != expectedPin ||
                rateCardPin is null ||
                receipt.CostBinding is not { } costBinding ||
                costBinding.MeterSource != SdkMeterSources.ByokTokens ||
                costBinding.RateCard.MeterSource != SdkMeterSources.ByokTokens ||
                !RuntimeRateCardReadContract.MatchesPin(
                    new(RuntimeRateCardReadContract.CurrentSchemaVersion,
                        costBinding.RateCard, rateCardPin.Provenance),
                    rateCardPin) ||
                costBinding.RuntimeRateCardPin is not { } pinnedCard ||
                costBinding.RuntimeByokIdentity != ByokIdentity(model, providerBinding) ||
                pinnedCard.RateCardId != rateCardPin.RateCardId ||
                pinnedCard.RateCardVersion != rateCardPin.RateCardVersion ||
                pinnedCard.ContentHash != rateCardPin.ContentHash ||
                !ProvenanceEqual(pinnedCard.Provenance, rateCardPin.Provenance) ||
                !rateCardAdmissions.TryGetValue((model.Reference, model.ReasoningEffort), out var admission) ||
                admission.CostBinding.RuntimeByokIdentity != ByokIdentity(model, providerBinding) ||
                receipt.Quote.Disposition != CostDisposition.Unpriced ||
                receipt.Quote.UnpricedReason != "byok-token-quotes-unsupported")
                throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
            return 0m;
        }
        if (model.SourceMode == ModelSourceMode.Byok &&
            model.ProviderBindingRevision is null &&
            model.CredentialIdentity is null &&
            pin.ProviderId is null)
        {
            if (receipt.Quote is not
                    {
                        Amount: null,
                        Disposition: CostDisposition.Unpriced,
                        UnpricedReason: "byok-token-quotes-unsupported"
                    } ||
                receipt.CostBinding is { } legacyBinding &&
                    (legacyBinding.MeterSource != SdkMeterSources.ByokTokens ||
                     legacyBinding.RateCard.MeterSource != SdkMeterSources.ByokTokens ||
                     legacyBinding.RuntimeRateCardPin is not null ||
                     legacyBinding.RuntimeByokIdentity is not null))
                throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
            return 0m;
        }
        if (model.SourceMode != ModelSourceMode.HostedCopilot || model.ConnectionId is null ||
            model.CredentialReference is not null || model.ProviderBindingRevision is not null)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        return RuntimeUsageCostSnapshotContract.ValidatePricedCopilotSnapshot(
            receipt.CostBinding, receipt.Quote, receipt.CopilotTotals, pin.ModelId, tenantId, projectId, runId);
    }

    private static Dictionary<(string Reference, string Effort), RuntimeRateCardAdmission>
        ValidateRateCardAdmissions(
            ImmutableArray<RuntimeRateCardAdmission> admissions, JsonElement selection)
    {
        if (admissions.IsDefault)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        var required = RuntimeAcceptedModelSelection.ReadAll(selection)
            .Where(item => item.SourceMode == ModelSourceMode.Byok && item.ProviderBindingRevision is not null)
            .ToDictionary(item => (item.Reference, item.ReasoningEffort));
        if (admissions.Length != required.Count)
            throw new RuntimeAuthorizationException("runtime_run_admission_invalid");

        var result = new Dictionary<(string Reference, string Effort), RuntimeRateCardAdmission>();
        foreach (var admission in admissions)
        {
            if (admission is null ||
                admission.ProviderBindingRevision is not { } providerBinding ||
                admission.ModelBindingPin is not { } pin ||
                admission.CostBinding is not { } costBinding)
                throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
            RuntimeModelBindingsResolver.ValidateProviderBinding(providerBinding);
            RuntimeModelBindingsResolver.ValidatePin(pin);
            var key = (pin.ModelSelectionReference, pin.ReasoningEffort);
            if (!required.TryGetValue(key, out var model) ||
                !result.TryAdd(key, admission) ||
                model.ModelBindingPin != pin ||
                !RuntimeModelBindingsResolver.ProviderBindingsEqual(model.ProviderBindingRevision, providerBinding) ||
                pin != RuntimeModelBindingsResolver.Pin(providerBinding, model.ReasoningEffort) ||
                model.CredentialReference != providerBinding.CredentialReference ||
                model.CredentialIdentity != providerBinding.CredentialIdentity ||
                providerBinding.RateCardPin is not { } rateCardPin ||
                costBinding.MeterSource != SdkMeterSources.ByokTokens ||
                costBinding.RuntimeRateCardPin is not { } costRateCardPin ||
                costRateCardPin.RateCardId != rateCardPin.RateCardId ||
                costRateCardPin.RateCardVersion != rateCardPin.RateCardVersion ||
                costRateCardPin.ContentHash != rateCardPin.ContentHash ||
                !ProvenanceEqual(costRateCardPin.Provenance, rateCardPin.Provenance) ||
                costBinding.RuntimeByokIdentity != ByokIdentity(model, providerBinding) ||
                !RuntimeRateCardReadContract.MatchesPin(
                    new(RuntimeRateCardReadContract.CurrentSchemaVersion,
                        costBinding.RateCard, costRateCardPin.Provenance),
                    rateCardPin))
                throw new RuntimeAuthorizationException("runtime_run_admission_invalid");
        }
        return result;
    }

    private static RuntimeByokCostBindingIdentity ByokIdentity(
        RuntimeAcceptedModelSelection model, ModelProviderBindingRevision providerBinding) =>
        RuntimeByokCostBindingIdentity.From(
            model.ModelBindingPin
                ?? throw new RuntimeAuthorizationException("runtime_model_binding_pin_required"),
            providerBinding);

    private static bool ProvenanceEqual(
        RuntimeRateCardReadProvenance left, RuntimeRateCardReadProvenance right) =>
        (left, right) switch
        {
            (ManualRuntimeRateCardReadProvenance a, ManualRuntimeRateCardReadProvenance b) =>
                a.SourceUrl == b.SourceUrl && a.RetrievedOn == b.RetrievedOn,
            (AzureRetailRuntimeRateCardReadProvenance a, AzureRetailRuntimeRateCardReadProvenance b) =>
                a.Region == b.Region && a.MeterIds.SequenceEqual(b.MeterIds, StringComparer.Ordinal),
            _ => false
        };
}
