using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

public enum RuntimeResumeHostState
{
    Unspecified,
    BootstrapPresent,
    Activated,
    Missing
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeHostStateReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    RuntimeRegistration Registration,
    Guid ResumeAttemptId,
    RuntimeResumeHostState State)
{
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeBootstrapStateLossReceipt(
    RuntimeBootstrapDeliveryReceipt Delivery,
    RuntimeResumeHostStateReceipt HostState)
{
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeBootstrapOutcome(
    RuntimeResumeBootstrapReceipt? Receipt,
    RuntimeResumeBootstrapStateLossReceipt? StateLoss)
{
    public RuntimeResumeBootstrapOutcome ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        if ((Receipt is null) == (StateLoss is null))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_outcome_invalid");
        Receipt?.ValidateFor(request);
        StateLoss?.ValidateFor(request);
        return this;
    }
}

public sealed class RuntimeResumeBootstrapStateLostException(
    RuntimeResumeBootstrapStateLossReceipt receipt) : Exception("runtime_resume_bootstrap_state_lost")
{
    public RuntimeResumeBootstrapStateLossReceipt Receipt { get; } = receipt;
}
