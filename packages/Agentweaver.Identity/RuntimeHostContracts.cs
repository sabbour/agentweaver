using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Environment-owner evidence that the registered placement is ready for runtime execution.</summary>
public sealed record EnvironmentRuntimeReadinessContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    DateTimeOffset LeaseCreatedAt,
    SandboxObservation Observation,
    SandboxStartupTimeBudgets StartupBudgets,
    string WorkspaceMountPath);

/// <summary>Environment-owner workspace attachment and transition context for a runtime.</summary>
public sealed record EnvironmentRuntimeWorkspaceContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    WorkspaceVolumeAttachmentNegotiation Workspace,
    long TransitionRevision);

/// <summary>Versioned request to resolve an environment profile for runtime bootstrap.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapProfileResolveRequest(int ContractVersion)
{
    /// <summary>Requires the supported profile-resolution contract version.</summary>
    public RuntimeBootstrapProfileResolveRequest Validate()
    {
        if (ContractVersion != 1)
            throw new ArgumentException("The Runtime bootstrap profile request is invalid.");
        return this;
    }
}

/// <summary>Receipt linking the requested profile to the profile selected by the owner.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapProfileResolveReceipt(
    int ContractVersion,
    string SourceProfileId,
    string ProfileId)
{
    /// <summary>Checks the receipt against the profile requested by the caller.</summary>
    public RuntimeBootstrapProfileResolveReceipt ValidateFor(string sourceProfileId)
    {
        if (ContractVersion != 1 ||
            SourceProfileId != sourceProfileId)
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_receipt_invalid");
        RuntimeContractValidation.ValidateIdentifier(ProfileId);
        return this;
    }
}

/// <summary>Request to configure the runtime with a registration, operation IDs, and settings payload.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostConfigureRequest(
    int ContractVersion,
    RuntimeRegistration Registration,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId,
    JsonElement Configuration)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeMcpServerEndpoint? McpServer { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeResumeBootstrapRequest? ResumeBootstrap { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostReplacementRecovery? ReplacementRecovery { get; init; }
}

/// <summary>Owner-retained lineage that permits exact replay of one completed root request.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostReplacementRecovery(
    RuntimeRegistration PreviousRegistration,
    RuntimeRootTurnCompletionEvidence Completion,
    string NativeCacheBaselineCursor)
{
    public RuntimeHostReplacementRecovery ValidateFor(RuntimeRegistration current)
    {
        RuntimeContractValidation.Validate(PreviousRegistration);
        RuntimeContractValidation.Validate(current);
        var oldBinding = PreviousRegistration.Binding;
        var newBinding = current.Binding;
        var evidence = Completion;
        RuntimeContractValidation.ValidateHash(evidence.AcceptedSelectionHash);
        RuntimeContractValidation.ValidateHash(evidence.PromptSha256);
        RuntimeContractValidation.ValidateHash(evidence.OutputSha256);
        RuntimeContractValidation.ValidateHash(evidence.RegistrationSha256);
        RuntimeContractValidation.ValidateHash(evidence.SourceSha256);
        RuntimeContractValidation.ValidateHash(evidence.SdkSessionSha256);
        RuntimeContractValidation.ValidateHash(evidence.NativeCursorBeforeSha256);
        RuntimeContractValidation.ValidateHash(evidence.NativeCursorAfterSha256);
        RuntimeContractValidation.ValidateHash(evidence.NativeEventIntervalSha256);
        RuntimeContractValidation.ValidateHash(evidence.CacheBaselineCursorSha256);
        RuntimeContractValidation.ValidateHash(evidence.CacheBaselineAfterCursorSha256);
        if (string.IsNullOrWhiteSpace(NativeCacheBaselineCursor) ||
            NativeCacheBaselineCursor.Length > 4096 ||
            NativeCacheBaselineCursor.Any(char.IsControl) ||
            PreviousRegistration.State != RuntimeRegistrationState.Active ||
            current.State != RuntimeRegistrationState.Active ||
            PreviousRegistration.RuntimeInstanceId == current.RuntimeInstanceId ||
            PreviousRegistration.Revision != evidence.RegistrationRevision ||
            PreviousRegistration.RuntimeInstanceId != evidence.RuntimeInstanceId ||
            RuntimeContractValidation.RegistrationHash(PreviousRegistration) != evidence.RegistrationSha256 ||
            evidence.ContractVersion != 1 || evidence.RequestId == Guid.Empty ||
            evidence.ExecutionFence != newBinding.ExecutionFence ||
            evidence.ExecutionFence != oldBinding.ExecutionFence ||
            evidence.AcceptedSelectionHash != newBinding.AcceptedSelectionHash ||
            evidence.AcceptedSelectionHash != oldBinding.AcceptedSelectionHash ||
            evidence.ModelSelectionReference != newBinding.ModelSelectionReference ||
            evidence.ModelSelectionReference != oldBinding.ModelSelectionReference ||
            evidence.Image != newBinding.Image || evidence.Image != oldBinding.Image ||
            oldBinding.TenantId != newBinding.TenantId ||
            oldBinding.ProjectId != newBinding.ProjectId ||
            oldBinding.RunId != newBinding.RunId ||
            oldBinding.SessionId != newBinding.SessionId ||
            oldBinding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
            oldBinding.WorkflowStepId is not null || newBinding.AgentId != oldBinding.AgentId ||
            newBinding.WorkflowStepId is not null ||
            oldBinding.ModelSourceMode != newBinding.ModelSourceMode ||
            oldBinding.ReasoningEffort != newBinding.ReasoningEffort ||
            oldBinding.MaxPromptTokens != newBinding.MaxPromptTokens ||
            oldBinding.ModelBindingPin != newBinding.ModelBindingPin ||
            oldBinding.EnvironmentId != newBinding.EnvironmentId ||
            evidence.ModelSourceMode != RuntimeRootTurnCompletionEvidenceContract.ModelSourceModeName(
                newBinding.ModelSourceMode
                    ?? throw new RuntimeAuthorizationException("runtime_replacement_lineage_invalid")) ||
            evidence.ReasoningEffort != newBinding.ReasoningEffort ||
            evidence.MaxPromptTokens != newBinding.MaxPromptTokens ||
            evidence.Image != newBinding.Image ||
            evidence.SdkSessionSha256 != RuntimeContractValidation.Hash(
                System.Text.Encoding.UTF8.GetBytes(RuntimeContractValidation.NativeSessionId(newBinding))) ||
            evidence.SuccessfulToolCount != 0 || evidence.FailedToolCount != 0 ||
            evidence.UserMaterial.Kind != SessionMaterialKind.TurnContent ||
            evidence.AssistantMaterial.Kind != SessionMaterialKind.TurnContent ||
            evidence.CacheMaterial.Kind != SessionMaterialKind.SdkCache ||
            evidence.UserMaterial.EventId != evidence.RequestId ||
            evidence.AssistantMaterial.EventId != RuntimeRootTurnCompletionReceipt.AssistantEventId(evidence.RequestId) ||
            evidence.UserMaterial.Position < 1 ||
            evidence.AssistantMaterial.Position <= evidence.UserMaterial.Position ||
            evidence.CacheMaterial.Position <= evidence.AssistantMaterial.Position ||
            evidence.UserMaterial.EventId == evidence.AssistantMaterial.EventId ||
            evidence.CacheMaterial.EventId == evidence.UserMaterial.EventId ||
            evidence.CacheMaterial.EventId == evidence.AssistantMaterial.EventId ||
            evidence.CacheBaselineEventCount != 0 || !evidence.CacheBaselineIsComplete ||
            evidence.CacheBaselineCursorSha256 != evidence.CacheBaselineAfterCursorSha256)
            throw new RuntimeAuthorizationException("runtime_replacement_lineage_invalid");
        foreach (var material in new[] { evidence.UserMaterial, evidence.AssistantMaterial, evidence.CacheMaterial })
        {
            RuntimeContractValidation.ValidateHash(material.ContentSha256);
            RuntimeContractValidation.ValidateHash(material.BindingSha256);
            if (material.EventId == Guid.Empty || material.Position < 1)
                throw new RuntimeAuthorizationException("runtime_replacement_lineage_invalid");
        }
        return this;
    }
}

/// <summary>Proof that identifies the runtime registration and source grant for an owner operation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostSessionProof(
    int ContractVersion,
    RuntimeRegistration Registration,
    Guid SourceGrantId,
    long SourceGrantRevision,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuntimeCredentialPurpose>))]
    RuntimeCredentialPurpose Purpose);

/// <summary>Request to refresh a source grant using a session proof and idempotent operation ID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostRefreshRequest(RuntimeHostSessionProof Proof, Guid OperationId);

/// <summary>Request to suspend a runtime and persist its manifest and optional workspace flush evidence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostSuspendRequest(
    RuntimeHostSessionProof Proof, Guid OperationId, Guid ManifestId, long PhaseVersion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceVolumeFlushRequest? WorkspaceFlush { get; init; }
}

/// <summary>Owner receipt for a suspended runtime, durable turn, cache, and optional flushed workspace.</summary>
public sealed record RuntimeHostSuspendReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid ManifestId,
    RuntimeRegistration Registration,
    RuntimeGrantReceipt SourceGrant,
    RuntimeNativeTurnRecordedReceipt NativeTurn,
    SessionMaterialAcknowledgment CacheAcknowledgment)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceVolumeFlushReceipt? WorkspaceFlush { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionMaterialAcknowledgment? ConfigurationAcknowledgment { get; init; }
}

/// <summary>Receipt confirming image and startup observations for one registered runtime.</summary>
public sealed record RuntimeHostReadinessReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    SandboxImageIdentity Image,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    RuntimeGrantReceipt SourceGrant)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostResumeCacheReceipt? ResumeCache { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostRecoveryReceipt? Recovery { get; init; }
}

/// <summary>Host-owned current readiness, Pod identity, and actual workspace mount observation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostReadinessEvidenceReceipt(
    int ContractVersion,
    DateTimeOffset ObservedAt,
    RuntimeHostReadinessReceipt Readiness,
    SandboxPodObservation EnvironmentPodObservation,
    string EnvironmentPodObservationHash,
    RuntimeHostWorkspaceMountEvidence WorkspaceMount,
    string RegistrationHash,
    string SourceHash,
    string NativeSessionHash);

/// <summary>Read-only Linux mount identity observed from the AgentHost process.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostWorkspaceMountEvidence(
    int ContractVersion,
    DateTimeOffset ObservedAt,
    string PodUid,
    string MountPath,
    ulong MountId,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong DirectoryInode,
    string FileSystemType,
    bool ReadOnly,
    string MountInfoSha256);

/// <summary>Strict request to read one persisted Orchestrator numeric-limit denial.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostNumericLimitDenialReadRequest(
    RuntimeHostSessionProof? Proof,
    Guid AttemptId)
{
    public override string ToString() => nameof(RuntimeHostNumericLimitDenialReadRequest) + " [REDACTED]";
}

/// <summary>Recovery source used to recreate a native SDK conversation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeHostRecoveryMode>))]
public enum RuntimeHostRecoveryMode
{
    Fresh,
    NativeCache,
    JournalRebuild
}

/// <summary>Hash and journal position evidence used when rebuilding native context from owner history.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostJournalRecoveryEvidence(long ThroughPosition, string JournalSha256);

/// <summary>Authenticated evidence describing how the runtime recovered its native SDK session.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostRecoveryReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    RuntimeHostRecoveryMode RecoveryMode,
    SdkSessionFacts Source)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionMaterialAcknowledgment? NativeCacheAcknowledgment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostJournalRecoveryEvidence? Journal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostNativeEmptyIntervalEvidence? ReplacementReconstructionInterval { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostNativeCallSequenceEvidence? ReplacementCallSequence { get; init; }

    /// <summary>Validates recovery evidence against the registration and matching SDK source.</summary>
    public RuntimeHostRecoveryReceipt ValidateFor(RuntimeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, Source);
        var binding = registration.Binding;
        if (ContractVersion != 1 ||
            RuntimeInstanceId != registration.RuntimeInstanceId ||
            RegistrationRevision != registration.Revision ||
            ExecutionFence != binding.ExecutionFence ||
            Source.SdkSessionId != RuntimeContractValidation.NativeSessionId(binding) ||
            !Enum.IsDefined(RecoveryMode))
            throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");

        if (RecoveryMode == RuntimeHostRecoveryMode.NativeCache)
        {
            var cache = NativeCacheAcknowledgment
                ?? throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
            SessionMaterialValidation.Validate(cache.Reference);
            var material = cache.Reference.Material!;
            if (Journal is not null ||
                cache.ContractVersion != 1 ||
                cache.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
                cache.EventId == Guid.Empty || cache.Position < 1 ||
                material.Kind != SessionMaterialKind.SdkCache ||
                material.TenantId != binding.TenantId ||
                material.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
                material.ModelSelectionReference != Source.ModelSelectionReference ||
                material.SdkVersion != Source.SdkVersion ||
                material.RuntimeVersion != Source.RuntimeVersion ||
                material.ModelId != Source.ModelId ||
                material.MaxPromptTokens != Source.MaxPromptTokens ||
                material.ReasoningEffort != Source.ReasoningEffort ||
                material.ModelBindingPin != Source.ModelBindingPin ||
                material.ResolvedModelBindingPin != Source.ResolvedModelBindingPin)
                throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
        }
        else if (RecoveryMode == RuntimeHostRecoveryMode.JournalRebuild)
        {
            if (NativeCacheAcknowledgment is not null ||
                Journal is not { ThroughPosition: > 0, JournalSha256: { Length: 64 } hash } ||
                !hash.All(Uri.IsHexDigit))
                throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
        }
        else if (NativeCacheAcknowledgment is not null || Journal is not null)
            throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
        if ((ReplacementReconstructionInterval is null) != (ReplacementCallSequence is null))
            throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
        if (ReplacementReconstructionInterval is { } interval)
        {
            RuntimeContractValidation.ValidateHash(interval.StartCursorSha256);
            RuntimeContractValidation.ValidateHash(interval.EndCursorSha256);
            if (RecoveryMode != RuntimeHostRecoveryMode.NativeCache ||
                interval.EventCount != 0 || !interval.IsComplete ||
                interval.StartCursorSha256 != interval.EndCursorSha256)
                throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
            ReplacementCallSequence!.ValidateFor(registration, requireZero: true);
        }

        return this;
    }
}

/// <summary>Complete empty native event-log range observed across replacement recovery.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostNativeEmptyIntervalEvidence(
    string StartCursorSha256,
    string EndCursorSha256,
    int EventCount,
    bool IsComplete);

/// <summary>Actual native send and tool-call counters captured on one Host process.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostNativeCallSequenceEvidence(
    int HostProcessId,
    Guid HostProcessInstanceId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long SendSequenceBefore,
    long SendSequenceAfter,
    long ToolEffectSequenceBefore,
    long ToolEffectSequenceAfter)
{
    public RuntimeHostNativeCallSequenceEvidence ValidateFor(
        RuntimeRegistration registration, bool requireZero = false) =>
        ValidateFor(registration.RuntimeInstanceId, registration.Revision, requireZero);

    public RuntimeHostNativeCallSequenceEvidence ValidateFor(
        Guid runtimeInstanceId, long registrationRevision, bool requireZero = false)
    {
        if (HostProcessId <= 0 || HostProcessInstanceId == Guid.Empty ||
            RuntimeInstanceId != runtimeInstanceId ||
            RegistrationRevision != registrationRevision ||
            SendSequenceBefore < 0 || SendSequenceAfter < SendSequenceBefore ||
            ToolEffectSequenceBefore < 0 || ToolEffectSequenceAfter < ToolEffectSequenceBefore ||
            SendSequenceBefore != SendSequenceAfter ||
            ToolEffectSequenceBefore != ToolEffectSequenceAfter ||
            requireZero && (SendSequenceBefore != 0 || ToolEffectSequenceBefore != 0))
            throw new RuntimeAuthorizationException("runtime_native_call_sequence_invalid");
        return this;
    }
}

/// <summary>Genuine native and stored-material facts for one completed coordinator-root turn.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRootTurnCompletionReceipt(
    int ContractVersion,
    Guid RequestId,
    string PromptHash,
    RuntimeRegistration Registration,
    SdkSessionFacts Source,
    RuntimeGrantReceipt ObserveGrant,
    RuntimeNativeTurnObservation NativeTurn,
    string NativeCursorBeforeHash,
    string NativeCursorAfterHash,
    string NativeEventIntervalHash,
    int NativeEventCount,
    SessionMaterialAcknowledgment UserMaterial,
    SessionMaterialAcknowledgment AssistantMaterial,
    SessionMaterialAcknowledgment CacheMaterial,
    int SuccessfulToolCount,
    int FailedToolCount,
    string CacheBaselineCursor,
    string CacheBaselineCursorHash,
    string CacheBaselineAfterCursorHash,
    int CacheBaselineEventCount,
    bool CacheBaselineIsComplete)
{
    /// <summary>Checks the receipt against the completed native turn and its exact owner bindings.</summary>
    public RuntimeRootTurnCompletionReceipt Validate()
    {
        RuntimeContractValidation.Validate(Registration);
        RuntimeUsageSourceReceiptContract.ValidateSource(Registration, Source);
        var binding = Registration.Binding;
        if (ContractVersion != 1 || RequestId == Guid.Empty ||
            Registration.State != RuntimeRegistrationState.Active ||
            Source.RuntimeInstanceId != Registration.RuntimeInstanceId ||
            Source.RegistrationRevision != Registration.Revision ||
            Source.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            binding.ModelSourceMode is null || binding.Image is null ||
            binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
            binding.WorkflowStepId is not null ||
            ObserveGrant is null ||
            ObserveGrant.GrantId == Guid.Empty ||
            ObserveGrant.RuntimeInstanceId != Registration.RuntimeInstanceId ||
            ObserveGrant.RegistrationRevision != Registration.Revision ||
            ObserveGrant.Revision < 1 ||
            ObserveGrant.Purpose != RuntimeCredentialPurpose.Observe ||
            ObserveGrant.State != RuntimeCredentialState.Active ||
            NativeTurn is null || NativeTurn.Source != Source ||
            !Guid.TryParse(NativeTurn.NativeMessageId, out var nativeMessageId) ||
            nativeMessageId == Guid.Empty ||
            NativeTurn.NativeTurnStartEventId == Guid.Empty ||
            NativeTurn.NativeTurnEndEventId == Guid.Empty ||
            NativeTurn.NativeCompletionReceiptEventId == Guid.Empty ||
            NativeTurn.UsageEventIds.IsDefault ||
            NativeTurn.UsageEventIds.Any(id => id == Guid.Empty) ||
            NativeTurn.UsageEventIds.Distinct().Count() != NativeTurn.UsageEventIds.Length ||
            NativeTurn.CompletedAt == default ||
            NativeTurn.DurableCursor is null || NativeTurn.DurableCursor.Length is < 1 or > 4096 ||
            NativeTurn.DurableCursor.Any(char.IsControl) ||
            NativeEventCount is < 1 or > 512 ||
            NativeCursorBeforeHash is null ||
            NativeCursorAfterHash is null ||
            NativeEventIntervalHash is null ||
            SuccessfulToolCount < 0 || FailedToolCount < 0 ||
            CacheBaselineCursor is null || CacheBaselineCursor.Length is < 1 or > 4096 ||
            CacheBaselineCursor.Any(char.IsControl) ||
            CacheBaselineEventCount != 0 || !CacheBaselineIsComplete)
            throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
        RuntimeContractValidation.ValidateHash(PromptHash);
        RuntimeContractValidation.ValidateHash(NativeTurn.OutputHash);
        RuntimeContractValidation.ValidateHash(NativeCursorBeforeHash);
        RuntimeContractValidation.ValidateHash(NativeCursorAfterHash);
        RuntimeContractValidation.ValidateHash(NativeEventIntervalHash);
        if (NativeCursorAfterHash != RuntimeContractValidation.Hash(
                System.Text.Encoding.UTF8.GetBytes(NativeTurn.DurableCursor)))
            throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
        RuntimeContractValidation.ValidateHash(CacheBaselineCursorHash);
        RuntimeContractValidation.ValidateHash(CacheBaselineAfterCursorHash);
        if (CacheBaselineCursorHash != CacheBaselineAfterCursorHash ||
            CacheBaselineCursorHash != RuntimeContractValidation.Hash(
                System.Text.Encoding.UTF8.GetBytes(CacheBaselineCursor)))
            throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
        RequireMaterial(UserMaterial, RequestId, SessionMaterialKind.TurnContent, Registration, Source);
        RequireMaterial(AssistantMaterial, AssistantEventId(RequestId), SessionMaterialKind.TurnContent, Registration, Source);
        RequireMaterial(CacheMaterial, CacheMaterial.EventId, SessionMaterialKind.SdkCache, Registration, Source);
        if (PromptHash != UserMaterial.Reference.Material!.Sha256 ||
            NativeTurn.OutputHash != AssistantMaterial.Reference.Material!.Sha256 ||
            UserMaterial.Position >= AssistantMaterial.Position ||
            AssistantMaterial.Position >= CacheMaterial.Position ||
            UserMaterial.EventId == AssistantMaterial.EventId ||
            UserMaterial.EventId == CacheMaterial.EventId ||
            AssistantMaterial.EventId == CacheMaterial.EventId)
            throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
        return this;
    }

    /// <summary>Returns only identifiers, positions, and hashes for external acceptance evidence.</summary>
    public RuntimeRootTurnCompletionEvidence ToEvidence()
    {
        Validate();
        var binding = Registration.Binding;
        var sourceMode = binding.ModelSourceMode
            ?? throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
        var image = binding.Image
            ?? throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
        _ = Guid.TryParse(NativeTurn.NativeMessageId, out var nativeMessageId);
        return new(
            ContractVersion,
            RequestId,
            PromptHash,
            NativeTurn.OutputHash,
            Registration.RuntimeInstanceId,
            Registration.Revision,
            RuntimeContractValidation.RegistrationHash(Registration),
            binding.ExecutionFence,
            binding.AcceptedSelectionHash,
            RuntimeUsageSourceReceiptContract.HashSource(Registration, Source),
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(Source.SdkSessionId)),
            ObserveGrant.GrantId,
            ObserveGrant.Revision,
            Source.ModelSelectionReference,
            Source.ModelId,
            RuntimeRootTurnCompletionEvidenceContract.ModelSourceModeName(sourceMode),
            Source.ReasoningEffort,
            Source.MaxPromptTokens,
            image,
            nativeMessageId,
            NativeTurn.NativeTurnStartEventId,
            NativeTurn.NativeTurnEndEventId,
            NativeTurn.NativeCompletionReceiptEventId,
            NativeTurn.CompletedAt,
            NativeCursorBeforeHash,
            NativeCursorAfterHash,
            NativeEventIntervalHash,
            NativeEventCount,
            NativeTurn.UsageEventIds,
            SuccessfulToolCount,
            FailedToolCount,
            ProjectMaterial(UserMaterial),
            ProjectMaterial(AssistantMaterial),
            ProjectMaterial(CacheMaterial),
            CacheBaselineCursorHash,
            CacheBaselineAfterCursorHash,
            CacheBaselineEventCount,
            CacheBaselineIsComplete);
    }

    public static Guid AssistantEventId(Guid requestId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"agentweaver:a2a:assistant:{requestId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static RuntimeMaterialAcknowledgmentEvidence ProjectMaterial(
        SessionMaterialAcknowledgment acknowledgment)
    {
        var material = acknowledgment.Reference.Material!;
        return new(
            acknowledgment.EventId,
            acknowledgment.Position,
            material.Kind,
            material.Sha256,
            RuntimeContractValidation.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                acknowledgment.Reference,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
    }

    private static void RequireMaterial(
        SessionMaterialAcknowledgment acknowledgment,
        Guid eventId,
        SessionMaterialKind kind,
        RuntimeRegistration registration,
        SdkSessionFacts source)
    {
        var binding = registration.Binding;
        if (acknowledgment is null || acknowledgment.ContractVersion != 1 ||
            acknowledgment.EventId != eventId ||
            acknowledgment.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
            acknowledgment.Position < 1)
            throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
        SessionMaterialValidation.Validate(acknowledgment.Reference);
        var material = acknowledgment.Reference.Material!;
        if (material.Kind != kind || material.TenantId != binding.TenantId ||
            material.RuntimeInstanceId != registration.RuntimeInstanceId ||
            material.RegistrationRevision != registration.Revision ||
            material.ExecutionFence != binding.ExecutionFence ||
            material.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            material.SdkVersion != source.SdkVersion ||
            material.RuntimeVersion != source.RuntimeVersion ||
            material.ModelSelectionReference != source.ModelSelectionReference ||
            material.ModelId != source.ModelId ||
            material.ReasoningEffort != source.ReasoningEffort ||
            material.ModelBindingPin != source.ModelBindingPin ||
            material.ResolvedModelBindingPin != source.ResolvedModelBindingPin)
            throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid");
    }
}

/// <summary>Non-secret evidence for one durable runtime material acknowledgment.</summary>
public sealed record RuntimeMaterialAcknowledgmentEvidence(
    Guid EventId,
    long Position,
    SessionMaterialKind Kind,
    string ContentSha256,
    string BindingSha256);

/// <summary>Safe public projection of a genuine coordinator-root turn receipt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRootTurnCompletionEvidence(
    int ContractVersion,
    Guid RequestId,
    string PromptSha256,
    string OutputSha256,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string RegistrationSha256,
    long ExecutionFence,
    string AcceptedSelectionHash,
    string SourceSha256,
    string SdkSessionSha256,
    Guid ObserveGrantId,
    long ObserveGrantRevision,
    string ModelSelectionReference,
    string ModelId,
    string ModelSourceMode,
    string? ReasoningEffort,
    int? MaxPromptTokens,
    SandboxImageIdentity Image,
    Guid NativeMessageId,
    Guid NativeTurnStartEventId,
    Guid NativeTurnEndEventId,
    Guid NativeCompletionReceiptEventId,
    DateTimeOffset NativeCompletedAt,
    string NativeCursorBeforeSha256,
    string NativeCursorAfterSha256,
    string NativeEventIntervalSha256,
    int NativeEventCount,
    ImmutableArray<Guid> UsageEventIds,
    int SuccessfulToolCount,
    int FailedToolCount,
    RuntimeMaterialAcknowledgmentEvidence UserMaterial,
    RuntimeMaterialAcknowledgmentEvidence AssistantMaterial,
    RuntimeMaterialAcknowledgmentEvidence CacheMaterial,
    string CacheBaselineCursorSha256,
    string CacheBaselineAfterCursorSha256,
    int CacheBaselineEventCount,
    bool CacheBaselineIsComplete);

internal static class RuntimeRootTurnCompletionEvidenceContract
{
    internal static string ModelSourceModeName(ModelSourceMode mode) => mode switch
    {
        ModelSourceMode.HostedCopilot => "hosted-copilot",
        ModelSourceMode.Byok => "byok",
        _ => throw new RuntimeAuthorizationException("runtime_root_turn_receipt_invalid")
    };
}

/// <summary>Evidence that an exact root retry read prior material without a new native event.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRootTurnReplayEvidence(
    int ContractVersion,
    Guid RequestId,
    Guid PreviousRuntimeInstanceId,
    long PreviousRegistrationRevision,
    string PreviousRegistrationSha256,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string RegistrationSha256,
    long ExecutionFence,
    string AcceptedSelectionHash,
    string PromptSha256,
    string OutputSha256,
    RuntimeMaterialAcknowledgmentEvidence UserMaterial,
    RuntimeMaterialAcknowledgmentEvidence AssistantMaterial,
    RuntimeHostNativeEmptyIntervalEvidence NativeInterval)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostNativeCallSequenceEvidence? NativeCallSequence { get; init; }

    public RuntimeRootTurnReplayEvidence ValidateFor(
        RuntimeRootTurnCompletionEvidence original,
        RuntimeRegistration previous,
        RuntimeRegistration current)
    {
        RuntimeContractValidation.ValidateHash(PreviousRegistrationSha256);
        RuntimeContractValidation.ValidateHash(RegistrationSha256);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        RuntimeContractValidation.ValidateHash(PromptSha256);
        RuntimeContractValidation.ValidateHash(OutputSha256);
        RuntimeContractValidation.ValidateHash(NativeInterval.StartCursorSha256);
        RuntimeContractValidation.ValidateHash(NativeInterval.EndCursorSha256);
        (NativeCallSequence
            ?? throw new RuntimeAuthorizationException("runtime_root_turn_replay_invalid"))
            .ValidateFor(current);
        if (ContractVersion != 1 || RequestId != original.RequestId ||
            PreviousRuntimeInstanceId != previous.RuntimeInstanceId ||
            PreviousRegistrationRevision != previous.Revision ||
            PreviousRegistrationSha256 != RuntimeContractValidation.RegistrationHash(previous) ||
            RuntimeInstanceId != current.RuntimeInstanceId ||
            RegistrationRevision != current.Revision ||
            RegistrationSha256 != RuntimeContractValidation.RegistrationHash(current) ||
            ExecutionFence != current.Binding.ExecutionFence ||
            ExecutionFence != previous.Binding.ExecutionFence ||
            AcceptedSelectionHash != current.Binding.AcceptedSelectionHash ||
            AcceptedSelectionHash != previous.Binding.AcceptedSelectionHash ||
            PromptSha256 != original.PromptSha256 ||
            OutputSha256 != original.OutputSha256 ||
            UserMaterial != original.UserMaterial ||
            AssistantMaterial != original.AssistantMaterial ||
            NativeInterval.EventCount != 0 || !NativeInterval.IsComplete ||
            NativeInterval.StartCursorSha256 != NativeInterval.EndCursorSha256)
            throw new RuntimeAuthorizationException("runtime_root_turn_replay_invalid");
        return this;
    }
}

/// <summary>Small health response describing the runtime host status.</summary>
public sealed record RuntimeHostLivenessReceipt(string Status);

/// <summary>Text content part in the runtime's A2A message representation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ATextPart(string Kind, string Text);

/// <summary>Runtime proof and delivery mode attached to an A2A message.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMetadata(
    RuntimeHostSessionProof Runtime,
    AddressedMessageDeliveryMode DeliveryMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool ContinueAfterCallerDisconnect = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool ReadOnlyToolsOnly = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeA2AChildDispatchBinding? ChildDispatch { get; init; }
}

/// <summary>Owner-selected work-plan binding for a child runtime dispatch.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AChildDispatchBinding(
    int ContractVersion,
    string WorkPlanId,
    string AssociationId);

/// <summary>Validated A2A text message shape sent to the runtime.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMessage(
    string Kind,
    Guid MessageId,
    string ContextId,
    string Role,
    ImmutableArray<RuntimeA2ATextPart> Parts,
    RuntimeA2AMetadata Metadata);

/// <summary>Envelope for submitting one A2A message to a runtime host.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ASendRequest(RuntimeA2AMessage Message);

/// <summary>Runtime response containing the resulting A2A message content.</summary>
public sealed record RuntimeA2AResponse(
    string Kind, Guid MessageId, string ContextId, string Role, ImmutableArray<RuntimeA2ATextPart> Parts)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeRootTurnCompletionReceipt? RootCompletion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeRootTurnReplayEvidence? RootReplay { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeChildTurnCompletionReceipt? ChildCompletion { get; init; }
}

/// <summary>Verified evidence that one associated child completed a native model turn and usage accounting.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeChildTurnCompletionReceipt(
    int ContractVersion,
    Guid RequestId,
    string RequestSha256,
    string PromptSha256,
    string OutputSha256,
    string WorkPlanId,
    string AssociationId,
    string ChildSessionId,
    string SdkSessionId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string RegistrationSha256,
    string AcceptedSelectionHash,
    long ExecutionFence,
    string SourceSha256,
    SandboxImageIdentity Image,
    string ModelSelectionReference,
    string ModelId,
    string ModelSourceMode,
    string? ReasoningEffort,
    Guid NativeMessageId,
    Guid NativeTurnStartEventId,
    Guid NativeTurnEndEventId,
    Guid NativeCompletionReceiptEventId,
    DateTimeOffset NativeCompletedAt,
    string NativeCursorSha256,
    string NativePayloadSha256,
    long NativeOwnerRevision,
    long AccountingOwnerRevision,
    ImmutableArray<Guid> UsageEventIds,
    RuntimeMaterialAcknowledgmentEvidence UserMaterial,
    RuntimeMaterialAcknowledgmentEvidence AssistantMaterial)
{
    /// <summary>Checks the receipt against the exact child request and completed response.</summary>
    public RuntimeChildTurnCompletionReceipt ValidateFor(
        RuntimeA2ASendRequest request,
        RuntimeA2AResponse response,
        RuntimeRegistration registration,
        string workPlanId,
        string associationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(registration);
        _ = ValidateEvidenceFor(workPlanId, associationId);
        var message = request.Message;
        if (message is null ||
            message.Parts is not [{ Kind: "text", Text: { Length: > 0 } prompt }] ||
            response.Parts is not [{ Kind: "text", Text: { Length: > 0 } answer }])
            throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        var binding = registration.Binding;
        var sourceMode = binding.ModelSourceMode;
        var image = binding.Image;
        var childDispatch = message.Metadata.ChildDispatch;
        RuntimeContractValidation.ValidateHash(RequestSha256);
        RuntimeContractValidation.ValidateHash(PromptSha256);
        RuntimeContractValidation.ValidateHash(OutputSha256);
        RuntimeContractValidation.ValidateHash(RegistrationSha256);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        RuntimeContractValidation.ValidateHash(SourceSha256);
        RuntimeContractValidation.ValidateHash(NativeCursorSha256);
        RuntimeContractValidation.ValidateHash(NativePayloadSha256);
        if (ContractVersion != 1 ||
            RequestId == Guid.Empty ||
            RequestId != message.MessageId ||
            RequestSha256 != RuntimeFixedCreditGuardContract.HashHostRequest(request) ||
            childDispatch is null || childDispatch.ContractVersion != 1 ||
            childDispatch.WorkPlanId != workPlanId ||
            childDispatch.AssociationId != associationId ||
            message.Metadata.Runtime.Registration != registration ||
            binding.WorkflowStepId is null ||
            PromptSha256 != RuntimeContractValidation.Hash(
                System.Text.Encoding.UTF8.GetBytes(prompt)) ||
            OutputSha256 != RuntimeContractValidation.Hash(
                System.Text.Encoding.UTF8.GetBytes(answer)) ||
            WorkPlanId != workPlanId || AssociationId != associationId ||
            ChildSessionId != binding.SessionId ||
            SdkSessionId != message.ContextId ||
            SdkSessionId != RuntimeContractValidation.NativeSessionId(binding) ||
            RuntimeInstanceId != registration.RuntimeInstanceId ||
            RegistrationRevision != registration.Revision ||
            RegistrationSha256 != RuntimeContractValidation.RegistrationHash(registration) ||
            AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            ExecutionFence != binding.ExecutionFence ||
            sourceMode is null ||
            ModelSourceMode != RuntimeRootTurnCompletionEvidenceContract.ModelSourceModeName(sourceMode.Value) ||
            image is null || Image != image ||
            ModelSelectionReference != registration.Binding.ModelSelectionReference ||
            ReasoningEffort != registration.Binding.ReasoningEffort ||
            string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 256 ||
            response.Kind != "message" || response.MessageId == Guid.Empty ||
            response.ContextId != SdkSessionId || response.Role != "agent")
            throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        return this;
    }

    /// <summary>Checks the persisted receipt shape and its plan-association binding.</summary>
    public RuntimeChildTurnCompletionReceipt ValidateEvidenceFor(
        string workPlanId, string associationId)
    {
        RuntimeContractValidation.ValidateIdentifier(workPlanId);
        RuntimeContractValidation.ValidateIdentifier(associationId);
        RuntimeContractValidation.ValidateHash(RequestSha256);
        RuntimeContractValidation.ValidateHash(PromptSha256);
        RuntimeContractValidation.ValidateHash(OutputSha256);
        RuntimeContractValidation.ValidateHash(RegistrationSha256);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        RuntimeContractValidation.ValidateHash(SourceSha256);
        RuntimeContractValidation.ValidateHash(NativeCursorSha256);
        RuntimeContractValidation.ValidateHash(NativePayloadSha256);
        if (ContractVersion != 1 || RequestId == Guid.Empty ||
            WorkPlanId != workPlanId || AssociationId != associationId ||
            string.IsNullOrWhiteSpace(ChildSessionId) || ChildSessionId.Length > 256 ||
            ChildSessionId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(SdkSessionId) || SdkSessionId.Length > 256 ||
            SdkSessionId.Any(char.IsControl) ||
            RuntimeInstanceId == Guid.Empty || RegistrationRevision < 1 || ExecutionFence < 1 ||
            Image is null || string.IsNullOrWhiteSpace(ModelSelectionReference) ||
            ModelSelectionReference.Length > 256 || ModelSelectionReference.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 256 || ModelId.Any(char.IsControl) ||
            ModelSourceMode is not ("hosted-copilot" or "byok") ||
            ReasoningEffort?.Length > 128 ||
            NativeMessageId == Guid.Empty ||
            NativeTurnStartEventId == Guid.Empty ||
            NativeTurnEndEventId == Guid.Empty ||
            NativeCompletionReceiptEventId == Guid.Empty ||
            NativeCompletedAt == default ||
            NativeOwnerRevision < 1 || AccountingOwnerRevision < 1 ||
            UsageEventIds.IsDefault || UsageEventIds.Length > 512 ||
            UsageEventIds.Any(id => id == Guid.Empty) ||
            UsageEventIds.Distinct().Count() != UsageEventIds.Length ||
            UserMaterial is null || AssistantMaterial is null ||
            UserMaterial.EventId != RequestId ||
            AssistantMaterial.EventId != RuntimeRootTurnCompletionReceipt.AssistantEventId(RequestId) ||
            UserMaterial.Kind != SessionMaterialKind.TurnContent ||
            AssistantMaterial.Kind != SessionMaterialKind.TurnContent ||
            UserMaterial.ContentSha256 != PromptSha256 ||
            AssistantMaterial.ContentSha256 != OutputSha256 ||
            UserMaterial.Position <= 0 ||
            AssistantMaterial.Position <= UserMaterial.Position)
            throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        RuntimeContractValidation.ValidateHash(UserMaterial.BindingSha256);
        RuntimeContractValidation.ValidateHash(AssistantMaterial.BindingSha256);
        return this;
    }

    /// <summary>Checks the child receipt against the native source, completion events, and priced accounting retained by the owner.</summary>
    public RuntimeChildTurnCompletionReceipt ValidateAgainstAccountingEvidence(
        RuntimeA2ASendRequest request,
        RuntimeRegistration registration,
        RuntimeChildTurnAccountingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(evidence);
        var recorded = evidence.Recorded;
        RuntimeNativeTurnContract.ValidateRecorded(recorded);
        var admission = recorded.Admission;
        var source = admission.Source;
        var observation = recorded.Observation;
        RuntimeNativeTurnContract.ValidateAdmission(admission, registration, source, request);
        RuntimeContractValidation.ValidateHash(evidence.SourceSha256);
        var binding = registration.Binding;
        var sourceMode = registration.Binding.ModelSourceMode;
        if (!Guid.TryParse(observation.NativeMessageId, out var nativeMessageId) ||
            evidence.AccountingStatus != "priced" ||
            evidence.SourceSha256 != RuntimeUsageSourceReceiptContract.HashSource(registration, source) ||
            evidence.AccountingOwnerRevision <= recorded.OwnerRevision ||
            admission.Registration != registration ||
            ChildSessionId != binding.SessionId ||
            SdkSessionId != request.Message.ContextId ||
            SdkSessionId != source.SdkSessionId ||
            RuntimeInstanceId != registration.RuntimeInstanceId ||
            RegistrationRevision != registration.Revision ||
            RegistrationSha256 != RuntimeContractValidation.RegistrationHash(registration) ||
            AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            ExecutionFence != binding.ExecutionFence ||
            SourceSha256 != evidence.SourceSha256 ||
            RequestId != admission.MessageId ||
            RequestSha256 != RuntimeFixedCreditGuardContract.HashHostRequest(request) ||
            PromptSha256 != admission.PromptHash ||
            OutputSha256 != observation.OutputHash ||
            ModelSelectionReference != source.ModelSelectionReference ||
            ModelId != source.ModelId ||
            ReasoningEffort != source.ReasoningEffort ||
            sourceMode is null ||
            ModelSourceMode != RuntimeRootTurnCompletionEvidenceContract.ModelSourceModeName(sourceMode.Value) ||
            NativeMessageId != nativeMessageId ||
            NativeTurnStartEventId != observation.NativeTurnStartEventId ||
            NativeTurnEndEventId != observation.NativeTurnEndEventId ||
            NativeCompletionReceiptEventId != observation.NativeCompletionReceiptEventId ||
            NativeCompletedAt != observation.CompletedAt ||
            NativeCursorSha256 != RuntimeContractValidation.Hash(
                System.Text.Encoding.UTF8.GetBytes(observation.DurableCursor)) ||
            NativePayloadSha256 != recorded.CanonicalPayloadHash ||
            NativeOwnerRevision != recorded.OwnerRevision ||
            AccountingOwnerRevision != evidence.AccountingOwnerRevision ||
            !UsageEventIds.SequenceEqual(observation.UsageEventIds))
            throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        return this;
    }

    /// <summary>Builds a safe receipt from the admitted native turn and its completed accounting snapshot.</summary>
    public static RuntimeChildTurnCompletionReceipt Create(
        RuntimeA2ASendRequest request,
        RuntimeNativeTurnRecordedReceipt recorded,
        RuntimeNativeTurnAccountingReceipt accounting,
        SessionMaterialAcknowledgment userMaterial,
        SessionMaterialAcknowledgment assistantMaterial)
    {
        ArgumentNullException.ThrowIfNull(accounting);
        return Create(request, recorded, accounting.OwnerRevision, userMaterial, assistantMaterial);
    }

    /// <summary>Rebuilds a receipt from owner-verified accounting evidence after a lost host reply.</summary>
    public static RuntimeChildTurnCompletionReceipt Create(
        RuntimeA2ASendRequest request,
        RuntimeChildTurnAccountingEvidence evidence,
        SessionMaterialAcknowledgment userMaterial,
        SessionMaterialAcknowledgment assistantMaterial)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Create(
            request, evidence.Recorded, evidence.AccountingOwnerRevision, userMaterial, assistantMaterial);
    }

    private static RuntimeChildTurnCompletionReceipt Create(
        RuntimeA2ASendRequest request,
        RuntimeNativeTurnRecordedReceipt recorded,
        long accountingOwnerRevision,
        SessionMaterialAcknowledgment userMaterial,
        SessionMaterialAcknowledgment assistantMaterial)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recorded);
        var registration = recorded.Admission.Registration;
        var source = recorded.Admission.Source;
        var binding = registration.Binding;
        var dispatch = request.Message.Metadata.ChildDispatch
            ?? throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        var native = recorded.Observation;
        var image = binding.Image
            ?? throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        var sourceMode = binding.ModelSourceMode
            ?? throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        var nativeMessageId = Guid.TryParse(native.NativeMessageId, out var parsed)
            ? parsed
            : throw new RuntimeAuthorizationException("runtime_child_turn_receipt_invalid");
        return new RuntimeChildTurnCompletionReceipt(
            1,
            request.Message.MessageId,
            RuntimeFixedCreditGuardContract.HashHostRequest(request),
            recorded.Admission.PromptHash,
            native.OutputHash,
            dispatch.WorkPlanId,
            dispatch.AssociationId,
            binding.SessionId,
            request.Message.ContextId,
            registration.RuntimeInstanceId,
            registration.Revision,
            RuntimeContractValidation.RegistrationHash(registration),
            binding.AcceptedSelectionHash,
            binding.ExecutionFence,
            RuntimeUsageSourceReceiptContract.HashSource(registration, source),
            image,
            source.ModelSelectionReference,
            source.ModelId,
            RuntimeRootTurnCompletionEvidenceContract.ModelSourceModeName(sourceMode),
            source.ReasoningEffort,
            nativeMessageId,
            native.NativeTurnStartEventId,
            native.NativeTurnEndEventId,
            native.NativeCompletionReceiptEventId,
            native.CompletedAt,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(native.DurableCursor)),
            recorded.CanonicalPayloadHash,
            recorded.OwnerRevision,
            accountingOwnerRevision,
            native.UsageEventIds,
            RuntimeRootTurnCompletionReceipt.ProjectMaterial(userMaterial),
            RuntimeRootTurnCompletionReceipt.ProjectMaterial(assistantMaterial));
    }
}

/// <summary>Owner-read native completion and pricing evidence for one child dispatch.</summary>
public sealed record RuntimeChildTurnAccountingEvidence(
    RuntimeNativeTurnRecordedReceipt Recorded,
    string SourceSha256,
    long AccountingOwnerRevision,
    string AccountingStatus);
