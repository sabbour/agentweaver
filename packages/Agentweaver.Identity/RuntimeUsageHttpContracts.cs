using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeSdkSourceRequest(
    RuntimeCredentialHttpRequest Authorization, SdkSessionFacts Source)
{
    public override string ToString() => nameof(RuntimeSdkSourceRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageObservationRequest(
    RuntimeCredentialHttpRequest Authorization, SdkUsageObservation Observation)
{
    public override string ToString() => nameof(RuntimeUsageObservationRequest) + " [REDACTED]";
}

public sealed record RuntimeSdkSourceReceipt(
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    Guid SourceGrantId,
    SdkSessionFacts Source,
    string CanonicalPayloadHash,
    DateTimeOffset RecordedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageCostPreflightRequest(int ContractVersion = 1);

public sealed record RuntimeUsageCostPreflightReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    string ModelSelectionReference,
    string ModelId,
    string MeterSource,
    string AcceptedSelectionHash,
    string SourceReceiptHash,
    CostBinding? Binding,
    bool IsPriced,
    string? UnpricedReason);

public static class RuntimeUsageCostPreflightContract
{
    public static void Validate(RuntimeUsageCostPreflightReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ContractVersion != 1 || receipt.RuntimeInstanceId == Guid.Empty ||
            receipt.RegistrationRevision < 1 || receipt.ExecutionFence < 1 ||
            receipt.SourceReceiptHash is null ||
            receipt.SourceReceiptHash.Length != 64 ||
            !receipt.SourceReceiptHash.All(Uri.IsHexDigit) ||
            receipt.AcceptedSelectionHash is null ||
            receipt.AcceptedSelectionHash.Length != 64 ||
            !receipt.AcceptedSelectionHash.All(Uri.IsHexDigit))
            throw new RuntimeAuthorizationException("runtime_cost_preflight_invalid");
        RuntimeContractValidation.ValidateIdentifier(receipt.ModelSelectionReference);
        RuntimeContractValidation.ValidateIdentifier(receipt.MeterSource);
        if (string.IsNullOrWhiteSpace(receipt.ModelId) || receipt.ModelId.Length > 512 ||
            receipt.ModelId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            receipt.IsPriced != (receipt.Binding is not null && receipt.UnpricedReason is null) ||
            !receipt.IsPriced && string.IsNullOrWhiteSpace(receipt.UnpricedReason) ||
            receipt.Binding is { } binding &&
                (binding.MeterSource != receipt.MeterSource || binding.RateCard is null ||
                 binding.RateCard.MeterSource != receipt.MeterSource))
            throw new RuntimeAuthorizationException("runtime_cost_preflight_invalid");
    }
}
