using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

/// <summary>Host state that determines whether a resume bootstrap is new or already activated.</summary>
public enum RuntimeResumeHostState
{
    Unspecified,
    BootstrapPresent,
    Activated,
    Missing
}

/// <summary>Receipt for the observed host state of one resume bootstrap attempt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeHostStateReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    RuntimeRegistration Registration,
    Guid ResumeAttemptId,
    RuntimeResumeHostState State)
{
    /// <summary>Matches the receipt to the exact resume operation, source manifest, registration, and attempt.</summary>
    public RuntimeResumeHostStateReceipt ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        request.Validate();
        if (ContractVersion != 1 || OperationId != request.Resume.OperationId ||
            SourceManifestId != request.Resume.SourceManifest.ManifestId ||
            Registration != request.Registration || ResumeAttemptId != request.ResumeAttemptId ||
            State == RuntimeResumeHostState.Unspecified || !Enum.IsDefined(State))
            throw new RuntimeAuthorizationException("runtime_resume_host_state_invalid");
        return this;
    }
}

/// <summary>Evidence that previously delivered bootstrap state is missing and cannot be resumed.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeBootstrapStateLossReceipt(
    RuntimeBootstrapDeliveryReceipt Delivery,
    RuntimeResumeHostStateReceipt HostState)
{
    /// <summary>Requires a matching host state explicitly reporting missing bootstrap state.</summary>
    public RuntimeResumeBootstrapStateLossReceipt ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        request.ValidateDelivery(Delivery);
        ArgumentNullException.ThrowIfNull(HostState);
        HostState.ValidateFor(request);
        if (HostState.State != RuntimeResumeHostState.Missing)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_state_loss_invalid");
        return this;
    }
}

/// <summary>Exactly one of a successful bootstrap receipt or a state-loss receipt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeBootstrapOutcome(
    RuntimeResumeBootstrapReceipt? Receipt,
    RuntimeResumeBootstrapStateLossReceipt? StateLoss)
{
    /// <summary>Rejects contradictory or empty outcomes and validates the supplied branch.</summary>
    public RuntimeResumeBootstrapOutcome ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        if ((Receipt is null) == (StateLoss is null))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_outcome_invalid");
        Receipt?.ValidateFor(request);
        StateLoss?.ValidateFor(request);
        return this;
    }
}

/// <summary>Signals that the owner cannot find required bootstrap state for this resume attempt.</summary>
public sealed class RuntimeResumeBootstrapStateLostException(
    RuntimeResumeBootstrapStateLossReceipt receipt) : Exception("runtime_resume_bootstrap_state_lost")
{
    public RuntimeResumeBootstrapStateLossReceipt Receipt { get; } = receipt;
}
