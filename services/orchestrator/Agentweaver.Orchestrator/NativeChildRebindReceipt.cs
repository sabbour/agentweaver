using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

internal sealed record NativeChildRebindCheckpointReference(
    Guid OperationId,
    string SourceCheckpointId,
    long SourceCheckpointRevision,
    string SourceCheckpointHash,
    long SourceFence,
    string DestinationCheckpointId,
    long DestinationCheckpointRevision,
    string DestinationCheckpointHash,
    long DestinationFence,
    Guid? SupersedesOperationId,
    Guid? SupersessionReceiptId,
    string? SupersessionPayloadHash);

internal sealed record NativeChildRebindReceipt(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string ChildSessionId,
    string ActorIssuer,
    string ActorId,
    string SelectionHash,
    Guid RecoveryOperationId,
    string ManifestSha256,
    string WorkPlanId,
    string WorkPlanHash,
    string WorkflowHash,
    string AssociationId,
    string AssociationHash,
    string? WorkItemBindingSha256,
    Guid DispatchId,
    Guid MessageId,
    string PromptHash,
    string MessageRequestHash,
    string EgressReceiptHash,
    string OriginFingerprint,
    string OriginState,
    long OriginOwnerRevision,
    string PriorAuthorityKey,
    Guid? PriorReceiptId,
    string? PriorReceiptHash,
    string SourceCheckpointId,
    long SourceCheckpointRevision,
    string SourceCheckpointHash,
    long SourceFence,
    long SourceDecisionStateVersion,
    string AnchorCheckpointId,
    long AnchorCheckpointRevision,
    string AnchorCheckpointHash,
    long DestinationFence,
    long AnchorDecisionStateVersion,
    ImmutableArray<NativeChildRebindCheckpointReference> Lineage,
    Guid? SupersessionReceiptId,
    string? SupersessionReceiptHash,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string RegistrationHash,
    string SdkSourceReceiptHash,
    Guid SourceGrantId,
    long SourceGrantRevision,
    string OriginalRequestHash,
    string EffectiveRequestHash,
    Guid ReceiptId,
    Guid AttemptId,
    string CanonicalPayloadHash)
{
    internal const int MaximumLineageEdges = 512;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static NativeChildRebindReceipt Create(
        string tenantId,
        CoordinationActor actor,
        string selectionHash,
        ChildCheckpointRecoveryManifest manifest,
        ChildRecoveryMember member,
        NativeChildRebindOriginFingerprint origin,
        MafExecutionCheckpointSnapshot source,
        Guid? supersessionReceiptId,
        string? supersessionReceiptHash,
        RuntimeRegistration registration,
        string sourceHash,
        Guid sourceGrantId,
        long sourceGrantRevision,
        string effectiveRequestHash,
        string messageRequestHash,
        string egressReceiptHash,
        ImmutableArray<NativeChildRebindCheckpointReference> lineage)
    {
        var priorReceiptId = member.PriorNativeRebindReceiptId;
        var priorReceiptHash = member.PriorNativeRebindPayloadHash;
        var priorAuthorityKey = priorReceiptId is null
            ? "origin"
            : $"receipt:{priorReceiptId.Value:D}";
        if (member.NativeOrigin?.DispatchId != member.MessageId ||
            origin.State != "prepared" || origin.OwnerRevision != 1)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        var receipt = new NativeChildRebindReceipt(
            1,
            tenantId,
            manifest.Root.ProjectId,
            manifest.Root.RunId,
            manifest.Root.SessionId,
            member.Session.SessionId,
            actor.Issuer,
            actor.Subject,
            manifest.SelectionHash.ToLowerInvariant(),
            manifest.OperationId,
            ChildCheckpointRecoveryManifestCodec.Hash(manifest),
            manifest.WorkPlanId,
            manifest.WorkPlanHash,
            manifest.WorkflowHash,
            member.AssociationId,
            member.AssociationHash,
            member.WorkItemBindingSha256,
            member.MessageId,
            member.MessageId,
            member.PromptHash,
            messageRequestHash,
            egressReceiptHash,
            origin.Hash,
            origin.State,
            origin.OwnerRevision,
            priorAuthorityKey,
            priorReceiptId,
            priorReceiptHash,
            manifest.SourceCheckpointId,
            manifest.SourceCheckpointRevision,
            manifest.SourceCheckpointHash,
            manifest.SourceExecutionFence,
            manifest.SourceDecisionStateVersion,
            source.Info.CheckpointId,
            source.State.Revision,
            source.PayloadHash,
            manifest.HoldFence,
            source.State.DecisionStateVersion,
            lineage,
            supersessionReceiptId,
            supersessionReceiptHash,
            registration.RuntimeInstanceId,
            registration.Revision,
            RuntimeContractValidation.RegistrationHash(registration),
            sourceHash,
            sourceGrantId,
            sourceGrantRevision,
            origin.RequestHash,
            effectiveRequestHash,
            ReceiptIdFor(manifest.OperationId, member.MessageId),
            AttemptIdFor(manifest.OperationId, member.MessageId),
            "");
        return (receipt with { CanonicalPayloadHash = ComputePayloadHash(receipt) }).Validate();
    }

    internal NativeChildRebindReceipt Validate()
    {
        if (ContractVersion != 1 ||
            string.IsNullOrWhiteSpace(TenantId) ||
            string.IsNullOrWhiteSpace(ProjectId) ||
            string.IsNullOrWhiteSpace(RunId) ||
            string.IsNullOrWhiteSpace(RootSessionId) ||
            string.IsNullOrWhiteSpace(ChildSessionId) ||
            string.IsNullOrWhiteSpace(ActorIssuer) ||
            string.IsNullOrWhiteSpace(ActorId) ||
            string.IsNullOrWhiteSpace(WorkPlanId) ||
            string.IsNullOrWhiteSpace(AssociationId) ||
            RecoveryOperationId == Guid.Empty ||
            DispatchId == Guid.Empty ||
            MessageId != DispatchId ||
            RuntimeInstanceId == Guid.Empty ||
            SourceGrantId == Guid.Empty ||
            RegistrationRevision < 1 ||
            SourceGrantRevision < 1 ||
            OriginState != "prepared" ||
            OriginOwnerRevision != 1 ||
            SourceCheckpointRevision < 1 ||
            SourceFence < 1 ||
            SourceDecisionStateVersion < 1 ||
            AnchorCheckpointRevision < 1 ||
            DestinationFence < 1 ||
            AnchorDecisionStateVersion < 1 ||
            string.IsNullOrWhiteSpace(SourceCheckpointId) ||
            string.IsNullOrWhiteSpace(AnchorCheckpointId) ||
            SourceFence == long.MaxValue || DestinationFence != SourceFence + 1 ||
            Lineage.IsDefault || Lineage.Length == 0 || Lineage.Length > MaximumLineageEdges ||
            (PriorReceiptId is null) != (PriorReceiptHash is null) ||
            (SupersessionReceiptId is null) != (SupersessionReceiptHash is null) ||
            PriorReceiptId is null && PriorAuthorityKey != "origin" ||
            PriorReceiptId is { } priorId &&
                (priorId == Guid.Empty || PriorAuthorityKey != $"receipt:{priorId:D}") ||
            ReceiptId != ReceiptIdFor(RecoveryOperationId, DispatchId) ||
            AttemptId != AttemptIdFor(RecoveryOperationId, DispatchId) ||
            CanonicalPayloadHash != ComputePayloadHash(this))
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

        foreach (var hash in new[]
                 {
                     SelectionHash, ManifestSha256, WorkPlanHash, WorkflowHash, AssociationHash, PromptHash,
                     MessageRequestHash, EgressReceiptHash, OriginFingerprint, SourceCheckpointHash,
                     AnchorCheckpointHash, RegistrationHash, SdkSourceReceiptHash, OriginalRequestHash,
                     EffectiveRequestHash
                 })
            RequireHash(hash);
        if (WorkItemBindingSha256 is not null)
            RequireHash(WorkItemBindingSha256);
        if (PriorReceiptHash is not null)
            RequireHash(PriorReceiptHash);
        if (SupersessionReceiptHash is not null)
            RequireHash(SupersessionReceiptHash);
        if (Lineage.Any(edge => edge.OperationId == Guid.Empty ||
                edge.SourceCheckpointRevision < 1 || edge.SourceFence < 1 ||
                edge.DestinationCheckpointRevision < 1 || edge.DestinationFence < 1 ||
                edge.SourceCheckpointId.Length == 0 || edge.DestinationCheckpointId.Length == 0 ||
                (edge.SupersedesOperationId is null) != (edge.SupersessionReceiptId is null) ||
                (edge.SupersedesOperationId is null) != (edge.SupersessionPayloadHash is null) ||
                edge.SupersedesOperationId == Guid.Empty ||
                edge.SupersessionReceiptId == Guid.Empty))
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        if (Lineage.Select(edge => edge.OperationId).Distinct().Count() != Lineage.Length ||
            Lineage.Select(edge => (edge.SourceCheckpointId, edge.SourceCheckpointRevision, edge.SourceFence))
                .Distinct().Count() != Lineage.Length ||
            Lineage.Zip(Lineage.Skip(1)).Any(pair =>
                pair.First.DestinationCheckpointId != pair.Second.SourceCheckpointId ||
                pair.First.DestinationCheckpointRevision != pair.Second.SourceCheckpointRevision ||
                pair.First.DestinationFence != pair.Second.SourceFence) ||
            Lineage.Any(edge => edge.SourceFence == long.MaxValue ||
                edge.DestinationFence != edge.SourceFence + 1) ||
            Lineage[^1].OperationId != RecoveryOperationId ||
            Lineage[^1].SourceCheckpointId != SourceCheckpointId ||
            Lineage[^1].SourceCheckpointRevision != SourceCheckpointRevision ||
            Lineage[^1].SourceCheckpointHash != SourceCheckpointHash ||
            Lineage[^1].SourceFence != SourceFence ||
            Lineage[^1].DestinationCheckpointId != AnchorCheckpointId ||
            Lineage[^1].DestinationCheckpointRevision != AnchorCheckpointRevision ||
            Lineage[^1].DestinationCheckpointHash != AnchorCheckpointHash ||
            Lineage[^1].DestinationFence != DestinationFence ||
            Lineage[^1].SupersessionReceiptId != SupersessionReceiptId ||
            Lineage[^1].SupersessionPayloadHash != SupersessionReceiptHash)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        foreach (var hash in Lineage.SelectMany(edge => new[]
                 {
                     edge.SourceCheckpointHash,
                     edge.DestinationCheckpointHash
                 }).Concat(Lineage.Where(edge => edge.SupersessionPayloadHash is not null)
                     .Select(edge => edge.SupersessionPayloadHash!)))
            RequireHash(hash);
        return this;
    }

    internal static ImmutableArray<NativeChildRebindReceipt> ValidateChain(
        ImmutableArray<NativeChildRebindReceipt> receipts,
        Guid dispatchId)
    {
        if (receipts.IsDefault || receipts.Length > MaximumLineageEdges)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

        var receiptIds = new HashSet<Guid>();
        var attemptIds = new HashSet<Guid>();
        var operationIds = new HashSet<Guid>();
        var priorKeys = new HashSet<string>(StringComparer.Ordinal);
        var destinationFences = new HashSet<long>();
        NativeChildRebindReceipt? previous = null;
        var edgeCount = 0;
        foreach (var receipt in receipts)
        {
            receipt.Validate();
            edgeCount = checked(edgeCount + receipt.Lineage.Length +
                (receipt.SupersessionReceiptId is null ? 0 : 1));
            if (previous is not null)
            {
                if (receipt.SourceFence < previous.DestinationFence)
                    throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
                var recoveryGap = receipt.SourceFence - previous.DestinationFence;
                if (recoveryGap > 0)
                {
                    if (recoveryGap > MaximumLineageEdges)
                        throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
                    edgeCount = checked(edgeCount + checked((int)((2 * recoveryGap) - 1)));
                }
            }
            if (edgeCount > MaximumLineageEdges ||
                receipt.DispatchId != dispatchId ||
                !receiptIds.Add(receipt.ReceiptId) ||
                !attemptIds.Add(receipt.AttemptId) ||
                !operationIds.Add(receipt.RecoveryOperationId) ||
                !priorKeys.Add(receipt.PriorAuthorityKey) ||
                !destinationFences.Add(receipt.DestinationFence))
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            if (previous is null)
            {
                if (receipt.PriorAuthorityKey != "origin" ||
                    receipt.PriorReceiptId is not null || receipt.PriorReceiptHash is not null)
                    throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            }
            else if (receipt.PriorAuthorityKey != $"receipt:{previous.ReceiptId:D}" ||
                     receipt.PriorReceiptId != previous.ReceiptId ||
                     receipt.PriorReceiptHash != previous.CanonicalPayloadHash ||
                     receipt.OriginFingerprint != previous.OriginFingerprint ||
                     receipt.OriginalRequestHash != previous.OriginalRequestHash ||
                     receipt.SourceFence < previous.DestinationFence)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            previous = receipt;
        }
        return receipts;
    }

    internal static Guid ReceiptIdFor(Guid recoveryOperationId, Guid dispatchId) =>
        GuidFor($"native-child-rebind-receipt\0{recoveryOperationId:D}\0{dispatchId:D}");

    internal static Guid AttemptIdFor(Guid recoveryOperationId, Guid dispatchId) =>
        GuidFor($"native-child-rebind\0{recoveryOperationId:D}\0{dispatchId:D}");

    private static Guid GuidFor(string value)
    {
        var hash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(value));
        return Guid.ParseExact(hash[..32], "N");
    }

    private static string ComputePayloadHash(NativeChildRebindReceipt receipt) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            receipt with { CanonicalPayloadHash = "" }, JsonOptions));

    private static void RequireHash(string? value)
    {
        if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
    }
}

internal sealed record NativeChildRebindOriginFingerprint(
    string Hash,
    string RequestHash,
    string State,
    long OwnerRevision)
{
    internal static bool HasSameModelAndCostBinding(
        RuntimeRegistration origin,
        RuntimeRegistration replacement,
        ChildRecoveryNativeOrigin native,
        SdkSessionFacts originSource,
        SdkSessionFacts replacementSource)
    {
        var oldBinding = origin.Binding;
        var newBinding = replacement.Binding;
        return origin.RuntimeInstanceId == native.RuntimeInstanceId &&
            origin.Revision == native.RegistrationRevision &&
            RuntimeContractValidation.RegistrationHash(origin) == native.RuntimeBindingHash &&
            originSource.RuntimeInstanceId == origin.RuntimeInstanceId &&
            originSource.RegistrationRevision == origin.Revision &&
            originSource.ModelSelectionReference == native.ModelSelectionReference &&
            originSource.ModelId == native.SelectedModelId &&
            replacementSource.RuntimeInstanceId == replacement.RuntimeInstanceId &&
            replacementSource.RegistrationRevision == replacement.Revision &&
            replacementSource.ModelSelectionReference == originSource.ModelSelectionReference &&
            replacementSource.ModelId == originSource.ModelId &&
            replacementSource.MeterSource == originSource.MeterSource &&
            newBinding.ModelSelectionReference == oldBinding.ModelSelectionReference &&
            newBinding.ModelCredentialReference == oldBinding.ModelCredentialReference &&
            newBinding.ModelSourceMode == oldBinding.ModelSourceMode &&
            newBinding.ReasoningEffort == oldBinding.ReasoningEffort &&
            newBinding.ModelBindingPin == oldBinding.ModelBindingPin &&
            newBinding.ModelConnectionId == oldBinding.ModelConnectionId &&
            newBinding.ModelConnectionScope == oldBinding.ModelConnectionScope &&
            newBinding.PlacementProviderId == oldBinding.PlacementProviderId &&
            newBinding.MaxModelTurns == oldBinding.MaxModelTurns &&
            newBinding.MaxToolCalls == oldBinding.MaxToolCalls &&
            newBinding.MaxPromptTokens == oldBinding.MaxPromptTokens &&
            newBinding.MaxRevisionAttempts == oldBinding.MaxRevisionAttempts &&
            newBinding.CopilotSoftCreditLimit == oldBinding.CopilotSoftCreditLimit &&
            newBinding.CopilotHardCreditLimit == oldBinding.CopilotHardCreditLimit &&
            (newBinding.ModelBindingPin is null ||
             newBinding.ModelBindingPin.ModelId == native.SelectedModelId);
    }

    internal static NativeChildRebindOriginFingerprint Create(
        ChildCheckpointRecoveryManifest manifest,
        ChildRecoveryMember member) =>
        Create(manifest.TenantId, manifest.Root, member);

    internal static NativeChildRebindOriginFingerprint Create(
        string tenantId,
        SessionIdentity root,
        ChildRecoveryMember member)
    {
        var native = member.NativeOrigin
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        var hash = ChildCheckpointRecoveryManifestCodec.Hash(new
        {
            TenantId = tenantId,
            root.ProjectId,
            root.RunId,
            RootSessionId = root.SessionId,
            ChildSessionId = member.Session.SessionId,
            member.AssociationId,
            member.AssociationHash,
            member.WorkItemBindingSha256,
            native.DispatchId,
            native.CheckpointId,
            native.CheckpointRevision,
            native.DecisionStateVersion,
            native.ExecutionFence,
            native.AcceptedSelectionHash,
            native.PromptHash,
            native.RequestHash,
            native.RuntimeInstanceId,
            native.RegistrationRevision,
            native.RuntimeBindingHash,
            native.SdkSourceReceiptHash,
            native.ModelSelectionReference,
            native.SelectedModelId,
            native.CostBindingHash,
            native.IsCostProducing,
            native.HardCapSerialized,
            native.CreatedAt
        });
        return new(hash, native.RequestHash, native.State, native.OwnerRevision);
    }
}
