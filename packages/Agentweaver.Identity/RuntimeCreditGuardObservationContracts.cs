using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeCreditGuardIntent(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string AcceptedSelectionHash,
    long ExecutionFence,
    decimal? SoftCreditLimit,
    decimal? HardCreditLimit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeCreditGuardReadRequest(
    RuntimeCredentialHttpRequest Authorization,
    RuntimeCreditGuardIntent Intent)
{
    public override string ToString() => nameof(RuntimeCreditGuardReadRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeCreditGuardSnapshot(
    int ContractVersion,
    RuntimeCreditGuardIntent Intent,
    string RegistrationHash,
    string CostIdentity,
    string CostUnit,
    string CostScope,
    decimal CopilotAicFloor,
    long AccountedThroughLedgerEventCount,
    string LedgerDigest,
    long GuardRevision,
    bool SoftWarningEmitted,
    bool HardCapExhausted,
    DateTimeOffset UpdatedAt,
    string CanonicalPayloadHash);

public static class RuntimeCreditGuardObservationContract
{
    public static RuntimeCreditGuardIntent CreateIntent(
        RuntimeRegistration registration, string rootSessionId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        RuntimeContractValidation.ValidateIdentifier(rootSessionId);
        var binding = registration.Binding;
        return new(1, registration.RuntimeInstanceId, registration.Revision,
            binding.TenantId, binding.ProjectId, binding.RunId, rootSessionId,
            binding.AcceptedSelectionHash, binding.ExecutionFence,
            binding.CopilotSoftCreditLimit, binding.CopilotHardCreditLimit);
    }

    public static void ValidateReadRequest(RuntimeCreditGuardReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ValidateIntentShape(request.Intent);
    }

    public static RuntimeCreditGuardSnapshot Seal(RuntimeCreditGuardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with { CanonicalPayloadHash = SnapshotHash(snapshot) };
    }

    public static void ValidateSnapshot(
        RuntimeCreditGuardSnapshot snapshot, RuntimeRegistration registration, RuntimeCreditGuardIntent intent)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(intent);
        RequireIntent(intent, registration);
        ValidateIntentShape(intent);
        if (snapshot.ContractVersion != 1 || snapshot.Intent != intent ||
            snapshot.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration) ||
            snapshot.CostUnit != "AIC" || snapshot.CostScope != "root-run" ||
            snapshot.CopilotAicFloor < 0 || snapshot.AccountedThroughLedgerEventCount < 0 ||
            snapshot.GuardRevision < 1 || snapshot.UpdatedAt == default ||
            snapshot.SoftWarningEmitted && intent.SoftCreditLimit is null ||
            snapshot.HardCapExhausted && intent.HardCreditLimit is null ||
            snapshot.SoftWarningEmitted && snapshot.CopilotAicFloor < intent.SoftCreditLimit ||
            snapshot.HardCapExhausted && snapshot.CopilotAicFloor < intent.HardCreditLimit)
            throw new RuntimeAuthorizationException("runtime_credit_guard_observation_invalid");
        RuntimeContractValidation.ValidateHash(snapshot.CostIdentity);
        RuntimeContractValidation.ValidateHash(snapshot.LedgerDigest);
        RuntimeContractValidation.ValidateHash(snapshot.CanonicalPayloadHash);
        if (snapshot.CanonicalPayloadHash != SnapshotHash(snapshot))
            throw new RuntimeAuthorizationException("runtime_credit_guard_observation_invalid");
    }

    public static void RequireIntent(RuntimeCreditGuardIntent intent, RuntimeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(registration);
        var binding = registration.Binding;
        if (intent.ContractVersion != 1 ||
            intent.RuntimeInstanceId != registration.RuntimeInstanceId ||
            intent.RegistrationRevision != registration.Revision ||
            intent.TenantId != binding.TenantId ||
            intent.ProjectId != binding.ProjectId ||
            intent.RunId != binding.RunId ||
            intent.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            intent.ExecutionFence != binding.ExecutionFence ||
            intent.SoftCreditLimit != binding.CopilotSoftCreditLimit ||
            intent.HardCreditLimit != binding.CopilotHardCreditLimit)
            throw new RuntimeAuthorizationException("runtime_credit_guard_intent_conflict");
        ValidateIntentShape(intent);
    }

    private static void ValidateIntentShape(RuntimeCreditGuardIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.ContractVersion != 1 || intent.RuntimeInstanceId == Guid.Empty ||
            intent.RegistrationRevision < 1 || intent.ExecutionFence < 1 ||
            intent.SoftCreditLimit is < 0 || intent.HardCreditLimit is < 0 ||
            intent.SoftCreditLimit is { } soft && intent.HardCreditLimit is { } hard && soft > hard)
            throw new RuntimeAuthorizationException("runtime_credit_guard_intent_invalid");
        RuntimeContractValidation.ValidateIdentifier(intent.TenantId);
        RuntimeContractValidation.ValidateIdentifier(intent.ProjectId);
        RuntimeContractValidation.ValidateIdentifier(intent.RunId);
        RuntimeContractValidation.ValidateIdentifier(intent.RootSessionId);
        RuntimeContractValidation.ValidateHash(intent.AcceptedSelectionHash);
    }

    private static string SnapshotHash(RuntimeCreditGuardSnapshot snapshot) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            snapshot with { CanonicalPayloadHash = string.Empty }));
}
