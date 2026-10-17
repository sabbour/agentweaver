using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>
/// Validated request for restoring a suspended run into a newly placed runtime, with
/// per-attempt operation IDs and an exact next execution fence.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeResumeBootstrapRequest(
    int ContractVersion,
    RuntimeWorkspaceResumeRequest Resume,
    RuntimeRegistration Registration,
    Guid BootstrapOperationId,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId)
{
    public Guid ResumeAttemptId { get; init; }

    /// <summary>Checks source suspension evidence, placement, operation IDs, and the new registration binding.</summary>
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
            ResumeAttemptId == Guid.Empty || binding.ResumeOperationId != Resume.OperationId ||
            binding.ResumeAttemptId != ResumeAttemptId ||
            BootstrapOperationId != RuntimeResumeAttemptIdentity.Bootstrap(Resume.OperationId, ResumeAttemptId) ||
            ConsumeOperationId != RuntimeResumeAttemptIdentity.Consume(Resume.OperationId, ResumeAttemptId) ||
            ExchangeOperationId != RuntimeResumeAttemptIdentity.Exchange(Resume.OperationId, ResumeAttemptId) ||
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

    /// <summary>Requires the durable configuration material recorded before the suspended cache.</summary>
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

    /// <summary>Checks the restored configuration bytes against the original session material acknowledgment.</summary>
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

    /// <summary>Checks the delivered grant and placement against this exact resume request.</summary>
    public RuntimeBootstrapDeliveryReceipt ValidateDelivery(RuntimeBootstrapDeliveryReceipt delivery)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(delivery);
        var binding = Registration.Binding;
        var hash = RequireConfigurationAcknowledgment(Resume.SourceRelease.HostReceipt).Reference.Material!.Sha256;
        if (delivery.OperationId != BootstrapOperationId || delivery.GrantId == Guid.Empty ||
            delivery.RuntimeInstanceId != Registration.RuntimeInstanceId ||
            delivery.RegistrationRevision != Registration.Revision || delivery.ConfigurationHash != hash ||
            delivery.PlacementUid != binding.PlacementUid || delivery.PlacementGeneration != binding.PlacementGeneration ||
            delivery.ExecutionFence != binding.ExecutionFence ||
            delivery.EnvironmentCurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            delivery.EnvironmentProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            delivery.DeliveredAt >= Registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_delivery_invalid");
        return delivery;
    }
}

/// <summary>Whether recovered native cache remains unavailable until an authorized turn is accepted.</summary>
public enum RuntimeResumeNativeCacheState
{
    Unspecified,
    PendingUntilAuthorizedTurn
}

/// <summary>Evidence binding rebuilt context to the exact flushed journal prefix and resume attempt.</summary>
public sealed record RuntimeResumeJournalRebuildReceipt(
    SessionSuspendResumeCheckpointReference Checkpoint,
    long ThroughPosition,
    string JournalSha256,
    Guid ResumeAttemptId,
    RuntimeResumeNativeCacheState NativeCacheState)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Checks the checkpoint, prefix position, attempt ID, and required pending-cache state.</summary>
    public RuntimeResumeJournalRebuildReceipt ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        request.Validate();
        var source = request.Resume.SourceManifest;
        if (!RuntimeWorkspaceResumeHttpClient.SameJson(Checkpoint, source.Checkpoint) ||
            ThroughPosition != source.FlushedJournalPosition ||
            ResumeAttemptId != request.ResumeAttemptId ||
            NativeCacheState != RuntimeResumeNativeCacheState.PendingUntilAuthorizedTurn)
            throw new RuntimeAuthorizationException("runtime_resume_journal_receipt_invalid");
        RuntimeContractValidation.ValidateHash(JournalSha256);
        return this;
    }

    /// <summary>
    /// Validates an ordered, unique journal prefix containing both cache and configuration
    /// acknowledgments, then returns its canonical serialized digest.
    /// </summary>
    public static string ComputeJournalSha256(
        RuntimeResumeBootstrapRequest request, IReadOnlyList<SessionEventEnvelope> prefix)
    {
        request.Validate();
        ArgumentNullException.ThrowIfNull(prefix);
        var source = request.Resume.SourceManifest;
        var cache = source.CacheAcknowledgment!;
        var configuration = RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(
            request.Resume.SourceRelease.HostReceipt);
        var eventIds = new HashSet<Guid>();
        long lastPosition = 0;
        var foundCache = false;
        var foundConfiguration = false;
        foreach (var recorded in prefix)
        {
            if (recorded is null || recorded.Identity.ProjectId != source.Identity.ProjectId ||
                recorded.Identity.RunId != source.Identity.RunId ||
                recorded.Position <= lastPosition || recorded.Position > source.FlushedJournalPosition ||
                recorded.EventId == Guid.Empty || !eventIds.Add(recorded.EventId) ||
                recorded.SchemaVersion != SessionsContractVersions.CurrentSchemaVersion ||
                recorded.Payload is null || recorded.ObjectReferences.IsDefault ||
                !SessionEventPayloadValidation.SupportsEventVersion(recorded.EventVersion, recorded.Payload))
                throw new RuntimeAuthorizationException("runtime_resume_journal_prefix_invalid");
            SessionEventPayloadValidation.ValidateAndGetReferences(recorded.Payload);
            lastPosition = recorded.Position;
            if (recorded.EventId == cache.EventId)
            {
                foundCache = recorded.Identity == source.Identity && recorded.Position == cache.Position &&
                    recorded.Payload is CacheReferenceSessionPayload saved &&
                    RuntimeWorkspaceResumeHttpClient.SameJson(saved.Cache, cache.Reference);
                if (!foundCache)
                    throw new RuntimeAuthorizationException("runtime_resume_journal_prefix_invalid");
            }
            if (recorded.EventId == configuration.EventId)
            {
                foundConfiguration = recorded.Identity == source.Identity &&
                    recorded.Position == configuration.Position &&
                    recorded.Payload is RuntimeConfigurationSessionPayload saved &&
                    RuntimeWorkspaceResumeHttpClient.SameJson(saved.Configuration, configuration.Reference);
                if (!foundConfiguration)
                    throw new RuntimeAuthorizationException("runtime_resume_journal_prefix_invalid");
            }
        }
        if (!foundCache || !foundConfiguration)
            throw new RuntimeAuthorizationException("runtime_resume_journal_source_missing");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(prefix, JsonOptions);
        if (bytes.Length > SessionMaterialValidation.MaximumBytes)
            throw new RuntimeAuthorizationException("runtime_resume_journal_prefix_too_large");
        return RuntimeContractValidation.Hash(bytes);
    }
}

/// <summary>Host receipt confirming resumed cache source and the selected recovery method.</summary>
public sealed record RuntimeHostResumeCacheReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    RuntimeRegistration Registration,
    SessionMaterialAcknowledgment CacheAcknowledgment,
    SdkSessionFacts Source,
    string RecoveryMode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeResumeJournalRebuildReceipt? JournalRebuild { get; init; }

    /// <summary>Checks the saved cache acknowledgment and native-cache or journal-rebuild evidence.</summary>
    public RuntimeHostResumeCacheReceipt ValidateFor(RuntimeResumeBootstrapRequest request)
    {
        request.Validate();
        var saved = request.Resume.SourceManifest.CacheAcknowledgment!;
        var material = saved.Reference.Material!;
        RuntimeUsageSourceReceiptContract.ValidateSource(request.Registration, Source);
        if (ContractVersion != 1 || OperationId != request.Resume.OperationId ||
            SourceManifestId != request.Resume.SourceManifest.ManifestId ||
            Registration != request.Registration ||
            !RuntimeWorkspaceResumeHttpClient.SameJson(CacheAcknowledgment, saved))
            throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
        if (RecoveryMode == "native-cache")
        {
            if (JournalRebuild is not null ||
                Source.SdkVersion != material.SdkVersion || Source.RuntimeVersion != material.RuntimeVersion ||
                Source.ModelSelectionReference != material.ModelSelectionReference ||
                Source.ModelId != material.ModelId || Source.AcceptedSelectionHash != material.AcceptedSelectionHash ||
                Source.MaxPromptTokens != material.MaxPromptTokens ||
                Source.ReasoningEffort != material.ReasoningEffort ||
                Source.ModelBindingPin != material.ModelBindingPin ||
                Source.ResolvedModelBindingPin != material.ResolvedModelBindingPin)
                throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
        }
        else if (RecoveryMode == "journal-rebuild" && JournalRebuild is not null)
            JournalRebuild.ValidateFor(request);
        else
            throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
        return this;
    }
}

/// <summary>Combined environment delivery and host readiness receipt for a resumed runtime.</summary>
public sealed record RuntimeResumeBootstrapReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    RuntimeRegistration Registration,
    RuntimeBootstrapDeliveryReceipt Delivery,
    RuntimeHostReadinessReceipt HostReceipt)
{
    /// <summary>Matches delivery, registration, image, lease, and observe grant against the request.</summary>
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

/// <summary>Calls the Environment owner to create or confirm the resumed runtime bootstrap.</summary>
public sealed class RuntimeResumeBootstrapHttpClient(HttpClient client, Uri environmentOwnerAddress)
{
    private readonly Uri _owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentOwnerAddress);

    /// <summary>Requests a bounded resume bootstrap and distinguishes state loss from successful activation.</summary>
    public async Task<RuntimeResumeBootstrapReceipt> BootstrapAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        request.Validate();
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeResumeBootstrapOutcome>(
            client, _owner, "/internal/runtime/resume/bootstrap", actor, request,
            SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_bootstrap_unavailable");
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_resume_actor_expired");
        result.ValidateFor(request);
        if (result.StateLoss is { } loss)
            throw new RuntimeResumeBootstrapStateLostException(loss);
        return result.Receipt!;
    }

}

/// <summary>Reads the saved runtime configuration from the Events owner for a validated resume.</summary>
public sealed class RuntimeResumeConfigurationHttpClient(HttpClient client, Uri eventsOwnerAddress)
{
    private readonly Uri _owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(eventsOwnerAddress);

    /// <summary>Reads and verifies the exact acknowledged configuration bytes for the resume request.</summary>
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
