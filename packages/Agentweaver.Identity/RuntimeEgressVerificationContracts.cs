using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Stable fail-closed results for a current Environment egress verification.</summary>
public static class RuntimeEgressVerificationFailureCodes
{
    public const string Missing = "egress_verification_missing";
    public const string StaleFence = "egress_verification_stale_fence";
    public const string GenerationUnverified = "egress_verification_generation_unverified";
    public const string AuthorizationChanged = "egress_verification_authorization_changed";
    public const string SelectionChanged = "egress_verification_selection_changed";
    public const string Unavailable = "egress_verification_unavailable";
}

/// <summary>Requests a read-only verification for one Environment fence and admitted selection revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeEgressVerificationRequest(
    int ContractVersion,
    EnvironmentGenerationFence Fence,
    long PolicyGeneration,
    string ContextRevision)
{
    /// <summary>Rejects incomplete owner fences and selection revisions before they cross the service boundary.</summary>
    public RuntimeEgressVerificationRequest Validate()
    {
        // The lifecycle fence and selection revision are inseparable request identity, not optional hints.
        if (ContractVersion != 1 || Fence?.Owner is null ||
            string.IsNullOrWhiteSpace(Fence.Owner.TenantId) ||
            string.IsNullOrWhiteSpace(Fence.Owner.ProjectId) ||
            string.IsNullOrWhiteSpace(Fence.Owner.RunId) ||
            string.IsNullOrWhiteSpace(Fence.Owner.EnvironmentId) ||
            Fence.LifecycleGeneration < 1 || PolicyGeneration < 1 ||
            string.IsNullOrWhiteSpace(ContextRevision) || ContextRevision.Length > 256)
            throw new ArgumentException("The Environment egress verification request is incomplete.");
        return this;
    }
}

/// <summary>Evidence that Environment verified one exact owner fence, policy generation, and selection.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeEgressVerificationReceipt(
    int ContractVersion,
    EnvironmentGenerationFence Fence,
    long PolicyGeneration,
    string ContextRevision,
    string IntentHash)
{
    /// <summary>Checks every returned binding against the request rather than accepting a partial owner match.</summary>
    public RuntimeEgressVerificationReceipt ValidateFor(RuntimeEgressVerificationRequest request)
    {
        request.Validate();
        // Record equality covers all owner IDs and the lifecycle generation, preventing partial-fence receipts.
        if (ContractVersion != 1 || Fence is null || Fence.Owner is null ||
            Fence != request.Fence || PolicyGeneration != request.PolicyGeneration ||
            !string.Equals(ContextRevision, request.ContextRevision, StringComparison.Ordinal) ||
            IntentHash is not { Length: 64 } || !IntentHash.All(Uri.IsHexDigit))
            throw new RuntimeAuthorizationException("egress_verification_receipt_invalid");
        return this;
    }
}

/// <summary>Success receipt or a coded, non-ready result returned by the Environment owner.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeEgressVerificationResponse(
    int ContractVersion,
    RuntimeEgressVerificationReceipt? Receipt,
    string? FailureCode,
    string? FailureMessage)
{
    /// <summary>Validates the response shape and fails with the Environment's stable outcome code.</summary>
    public RuntimeEgressVerificationReceipt ValidateFor(RuntimeEgressVerificationRequest request)
    {
        request.Validate();
        // Require one unambiguous success or known failure; malformed responses cannot become verification.
        if (ContractVersion != 1 || (Receipt is null) == (FailureCode is null))
            throw new RuntimeAuthorizationException("egress_verification_contract_invalid");
        if (Receipt is not null)
            return Receipt.ValidateFor(request);
        if (!RuntimeEgressVerificationHttpClient.IsKnownFailureCode(FailureCode!))
            throw new RuntimeAuthorizationException("egress_verification_contract_invalid");
        throw new RuntimeAuthorizationException(FailureCode!);
    }

    /// <summary>Creates a typed success response after the owner has completed all readback checks.</summary>
    public static RuntimeEgressVerificationResponse Success(RuntimeEgressVerificationReceipt receipt) =>
        new(1, receipt ?? throw new ArgumentNullException(nameof(receipt)), null, null);

    /// <summary>Creates a typed non-ready response without exposing provider or credential details.</summary>
    public static RuntimeEgressVerificationResponse Failure(string code, string message) =>
        new(1, null, code, message);
}
