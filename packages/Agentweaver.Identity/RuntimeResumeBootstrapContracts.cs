using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeBootstrapRequest(
    int ContractVersion,
    RuntimeWorkspaceResumeRequest Resume,
    RuntimeRegistration Registration,
    Guid BootstrapOperationId,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId)
{
    public RuntimeResumeBootstrapRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Resume);
        ArgumentNullException.ThrowIfNull(Registration);
        Resume.Validate();
        RuntimeContractValidation.Validate(Registration);
        var workspace = Resume.RestoredWorkspace
            ?? throw new RuntimeAuthorizationException("runtime_resume_bootstrap_workspace_required");
        var placement = workspace.Placement;
        var binding = Registration.Binding;
        var source = Resume.SourceRelease.HostReceipt.Registration;
        var operations = new[] { BootstrapOperationId, ConsumeOperationId, ExchangeOperationId };
        if (ContractVersion != 1 || Resume.PhaseVersion != 4 ||
            operations.Any(operation => operation == Guid.Empty || operation == Resume.OperationId) ||
            operations.Distinct().Count() != operations.Length ||
            Registration.RuntimeInstanceId == source.RuntimeInstanceId ||
            Registration.State != RuntimeRegistrationState.Active ||
            binding.ActorIssuer != source.Binding.ActorIssuer || binding.ActorId != source.Binding.ActorId ||
            binding.AcceptedSelectionHash != source.Binding.AcceptedSelectionHash ||
            binding.ModelSelectionReference != source.Binding.ModelSelectionReference ||
            binding.TenantId != placement.TenantId || binding.ProjectId != placement.ProjectId ||
            binding.RunId != placement.RunId || binding.SessionId != Resume.SourceManifest.Identity.SessionId ||
            source.Binding.ExecutionFence == long.MaxValue ||
            binding.ExecutionFence != source.Binding.ExecutionFence + 1 ||
            binding.EnvironmentId != placement.EnvironmentId ||
            binding.EnvironmentLifecycleGeneration != placement.LifecycleGeneration ||
            binding.EnvironmentCurrentFencingGeneration != placement.CurrentFencingGeneration ||
            binding.EnvironmentProviderFencingGeneration != placement.ProviderFencingGeneration ||
            binding.EnvironmentLeaseRevision != placement.LeaseRevision ||
            binding.PlacementProviderId != placement.Resource.ProviderId ||
            binding.PlacementUid != placement.Resource.ResourceId ||
            binding.PlacementGeneration != placement.Resource.Generation ||
            binding.ProfileId != placement.ProfileId || binding.Image != placement.Image ||
            binding.ConfigureEndpoint != placement.ConfigureEndpoint ||
            binding.ObservationEndpoint != placement.ObservationEndpoint ||
            Registration.ExpiresAt != placement.LeaseExpiresAt)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_request_invalid");
        RequireConfigurationAcknowledgment(Resume.SourceRelease.HostReceipt);
        return this;
    }

    public static SessionMaterialAcknowledgment RequireConfigurationAcknowledgment(RuntimeHostSuspendReceipt host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var acknowledgment = host.ConfigurationAcknowledgment
            ?? throw new RuntimeAuthorizationException("runtime_resume_configuration_missing");
        var binding = host.Registration.Binding;
        if (acknowledgment.ContractVersion != 1 || acknowledgment.EventId == Guid.Empty ||
            acknowledgment.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
            acknowledgment.Position <= 0 || acknowledgment.Position >= host.CacheAcknowledgment.Position ||
            acknowledgment.Reference?.Material is not { Kind: SessionMaterialKind.RuntimeConfiguration } material ||
            material.RuntimeInstanceId != host.Registration.RuntimeInstanceId ||
            material.RegistrationRevision != host.Registration.Revision ||
            material.ExecutionFence != binding.ExecutionFence || material.TenantId != binding.TenantId ||
            material.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            material.Sha256 != host.SourceGrant.ConfigurationHash)
            throw new RuntimeAuthorizationException("runtime_resume_configuration_changed");
        SessionMaterialValidation.Validate(acknowledgment.Reference);
        return acknowledgment;
    }

    public SessionMaterialReadResult ValidateConfiguration(SessionMaterialReadResult result)
    {
        Validate();
        var expected = RequireConfigurationAcknowledgment(Resume.SourceRelease.HostReceipt);
        if (!RuntimeWorkspaceResumeHttpClient.SameJson(result.Material, expected) ||
            result.Bytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes } ||
            result.Bytes.Length != expected.Reference.ByteLength ||
            RuntimeContractValidation.Hash(result.Bytes) != expected.Reference.Material!.Sha256)
            throw new RuntimeAuthorizationException("runtime_resume_configuration_changed");
        using var document = JsonDocument.Parse(result.Bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.GetRawText().Length > AddressedMessageValidation.MaximumPayloadLength)
            throw new RuntimeAuthorizationException("runtime_resume_configuration_invalid");
        return result;
    }
}

public sealed record RuntimeHostResumeCacheReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    RuntimeRegistration Registration,
    SessionMaterialAcknowledgment CacheAcknowledgment,
    SdkSessionFacts Source,
    string RecoveryMode)
{
    public RuntimeHostResumeCacheReceipt ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        request.Validate();
        var saved = request.Resume.SourceManifest.CacheAcknowledgment!;
        var material = saved.Reference.Material!;
        RuntimeUsageSourceReceiptContract.ValidateSource(request.Registration, Source);
        if (ContractVersion != 1 || OperationId != request.Resume.OperationId ||
            SourceManifestId != request.Resume.SourceManifest.ManifestId ||
            Registration != request.Registration || RecoveryMode != "native-cache" ||
            !RuntimeWorkspaceResumeHttpClient.SameJson(CacheAcknowledgment, saved) ||
            Source.SdkVersion != material.SdkVersion || Source.RuntimeVersion != material.RuntimeVersion ||
            Source.ModelSelectionReference != material.ModelSelectionReference ||
            Source.ModelId != material.ModelId || Source.AcceptedSelectionHash != material.AcceptedSelectionHash ||
            Source.MaxPromptTokens != material.MaxPromptTokens)
            throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
        return this;
    }
}

public sealed record RuntimeResumeBootstrapReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    RuntimeRegistration Registration,
    RuntimeBootstrapDeliveryReceipt Delivery,
    RuntimeHostReadinessReceipt HostReceipt)
{
    public RuntimeResumeBootstrapReceipt ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        request.Validate();
        var registration = request.Registration;
        var binding = registration.Binding;
        var hash = RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(
            request.Resume.SourceRelease.HostReceipt).Reference.Material!.Sha256;
        if (ContractVersion != 1 || OperationId != request.Resume.OperationId ||
            SourceManifestId != request.Resume.SourceManifest.ManifestId || Registration != registration ||
            Delivery is null || HostReceipt?.SourceGrant is not { } grant ||
            HostReceipt.ResumeCache is null ||
            Delivery.OperationId != request.BootstrapOperationId || Delivery.GrantId == Guid.Empty ||
            Delivery.RuntimeInstanceId != registration.RuntimeInstanceId ||
            Delivery.RegistrationRevision != registration.Revision ||
            Delivery.PlacementUid != binding.PlacementUid ||
            Delivery.PlacementGeneration != binding.PlacementGeneration ||
            Delivery.ExecutionFence != binding.ExecutionFence ||
            Delivery.EnvironmentCurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            Delivery.EnvironmentProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            Delivery.ConfigurationHash != hash ||
            HostReceipt.ContractVersion != 1 || HostReceipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            HostReceipt.RegistrationRevision != registration.Revision ||
            HostReceipt.ExecutionFence != binding.ExecutionFence || HostReceipt.Image != binding.Image ||
            grant.GrantId == Guid.Empty || grant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            grant.RegistrationRevision != registration.Revision ||
            grant.Purpose != RuntimeCredentialPurpose.Observe || grant.State != RuntimeCredentialState.Active ||
            grant.Audience != binding.ObservationEndpoint || grant.ConfigurationHash != hash ||
            grant.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_receipt_invalid");
        HostReceipt.ResumeCache.ValidateFor(request);
        return this;
    }
}

public sealed class RuntimeResumeBootstrapHttpClient(HttpClient client, Uri environmentOwnerAddress)
{
    private readonly Uri _owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentOwnerAddress);

    public async Task<RuntimeResumeBootstrapReceipt> BootstrapAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        request.Validate();
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeResumeBootstrapReceipt>(
            client, _owner, "/internal/runtime/resume/bootstrap", actor, request,
            SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_bootstrap_unavailable");
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_resume_actor_expired");
        return result.ValidateFor(request);
    }

}

public sealed class RuntimeResumeConfigurationHttpClient(HttpClient client, Uri eventsOwnerAddress)
{
    private readonly Uri _owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(eventsOwnerAddress);

    public async Task<SessionMaterialReadResult> ReadAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        request.Validate();
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<SessionMaterialReadResult>(
            client, _owner, "/internal/runtime/resume/configuration", actor, request,
            2 * SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_configuration_unavailable");
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_resume_actor_expired");
        return request.ValidateConfiguration(result);
    }
}
