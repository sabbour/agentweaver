using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

internal sealed record CheckpointRecoverySupersessionReceipt(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string ActorIssuer,
    string ActorId,
    string SelectionHash,
    string WorkPlanId,
    string WorkPlanHash,
    Guid SupersedesOperationId,
    string SupersededRequestHash,
    string SupersededManifestSha256,
    string SupersededAdmissionSha256,
    string TerminalStatusSha256,
    long TerminalPhaseVersion,
    string SupersededCheckpointId,
    long SupersededFence,
    string SupersededCheckpointHash,
    Guid SuccessorOperationId,
    string SuccessorRequestHash,
    string SuccessorManifestSha256,
    string SuccessorSourceCheckpointId,
    long SuccessorSourceFence,
    string SuccessorSourceCheckpointHash,
    int ReplacementRegistrationCount,
    string ReplacementRegistrationSetSha256,
    int NoSendCount,
    string NoSendSha256,
    string ChildIdentitySha256,
    Guid ReceiptId,
    Guid AttemptId,
    string CanonicalPayloadHash)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static CheckpointRecoverySupersessionReceipt Create(
        string tenantId,
        CoordinationActor actor,
        string selectionHash,
        ChildCheckpointRecoveryManifest successorManifest,
        CheckpointRecoverySupersessionSource superseded,
        Guid successorOperationId,
        string successorRequestHash,
        MafExecutionCheckpointSnapshot sourceCheckpoint)
    {
        var binding = successorManifest.Root;
        var receiptId = ReceiptIdFor(superseded.OperationId, successorOperationId);
        var receipt = new CheckpointRecoverySupersessionReceipt(
            1,
            tenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            actor.Issuer,
            actor.Subject,
            selectionHash.ToLowerInvariant(),
            successorManifest.WorkPlanId,
            successorManifest.WorkPlanHash,
            superseded.OperationId,
            superseded.RequestHash,
            superseded.ManifestSha256,
            superseded.AdmissionSha256,
            superseded.TerminalStatusSha256,
            superseded.PhaseVersion,
            superseded.Status.RecoveryCheckpointId!,
            superseded.Status.ExecutionFence,
            superseded.Status.RecoveryCheckpointHash!,
            successorOperationId,
            successorRequestHash,
            ChildCheckpointRecoveryManifestCodec.Hash(successorManifest),
            sourceCheckpoint.Info.CheckpointId,
            successorManifest.SourceExecutionFence,
            sourceCheckpoint.PayloadHash,
            superseded.ReplacementRegistrationCount,
            superseded.ReplacementRegistrationSetSha256,
            superseded.NoSendCount,
            superseded.NoSendSha256,
            CheckpointRecoverySupersessionSource.ChildIdentityHash(successorManifest),
            receiptId,
            superseded.OperationId,
            "");
        // Hash the receipt with an empty hash field, then store the hash. Validate computes it the same way.
        return (receipt with { CanonicalPayloadHash = ComputePayloadHash(receipt) }).Validate();
    }

    internal CheckpointRecoverySupersessionReceipt Validate()
    {
        // The successor must restart from the same checkpoint and fence where the superseded
        // operation stopped, and the stored hash must match the payload.
        if (ContractVersion != 1 ||
            string.IsNullOrWhiteSpace(TenantId) ||
            string.IsNullOrWhiteSpace(ProjectId) ||
            string.IsNullOrWhiteSpace(RunId) ||
            string.IsNullOrWhiteSpace(RootSessionId) ||
            string.IsNullOrWhiteSpace(ActorIssuer) ||
            string.IsNullOrWhiteSpace(ActorId) ||
            string.IsNullOrWhiteSpace(WorkPlanId) ||
            SupersedesOperationId == Guid.Empty ||
            SuccessorOperationId == Guid.Empty ||
            SupersedesOperationId == SuccessorOperationId ||
            AttemptId != SupersedesOperationId ||
            ReceiptId != ReceiptIdFor(SupersedesOperationId, SuccessorOperationId) ||
            TerminalPhaseVersion < 1 ||
            SuccessorSourceFence != SupersededFence ||
            SuccessorSourceCheckpointId != SupersededCheckpointId ||
            SuccessorSourceCheckpointHash != SupersededCheckpointHash ||
            ReplacementRegistrationCount < 1 ||
            NoSendCount < 1 ||
            !IsHash(SelectionHash) || !IsHash(WorkPlanHash) ||
            !IsHash(SupersededRequestHash) || !IsHash(SupersededManifestSha256) ||
            !IsHash(SupersededAdmissionSha256) || !IsHash(TerminalStatusSha256) ||
            !IsHash(SupersededCheckpointHash) || !IsHash(SuccessorRequestHash) ||
            !IsHash(SuccessorManifestSha256) || !IsHash(SuccessorSourceCheckpointHash) ||
            !IsHash(ReplacementRegistrationSetSha256) || !IsHash(NoSendSha256) ||
            !IsHash(ChildIdentitySha256) ||
            CanonicalPayloadHash != ComputePayloadHash(this))
            throw new RuntimeAuthorizationException("checkpoint_recovery_supersession_invalid");
        return this;
    }

    // The ID comes from both operation IDs, so a retry of the same supersession gets the same ID.
    internal static Guid ReceiptIdFor(Guid supersededOperationId, Guid successorOperationId)
    {
        var value = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
            $"checkpoint-recovery-supersession\0{supersededOperationId:D}\0{successorOperationId:D}"));
        return Guid.ParseExact(value[..32], "N");
    }

    private static string ComputePayloadHash(CheckpointRecoverySupersessionReceipt receipt) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            receipt with { CanonicalPayloadHash = "" }, JsonOptions));

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal sealed record CheckpointRecoverySupersessionSource(
    Guid OperationId,
    string RequestHash,
    string ManifestSha256,
    string AdmissionSha256,
    string TerminalStatusSha256,
    long PhaseVersion,
    CheckpointRecoveryStatus Status,
    int ReplacementRegistrationCount,
    string ReplacementRegistrationSetSha256,
    int NoSendCount,
    string NoSendSha256)
{
    internal static string ChildIdentityHash(ChildCheckpointRecoveryManifest manifest)
    {
        // Sort members by association ID so the hash does not depend on manifest order.
        var members = manifest.Members
            .OrderBy(member => member.AssociationId, StringComparer.Ordinal)
            .Select(member => new
            {
                member.AssociationId,
                member.Session.SessionId,
                member.Session.WorkPlanItemId,
                member.WorkflowStepId,
                member.AgentId,
                member.ModelSelectionReference,
                member.AssociationHash,
                member.WorkItemBindingSha256,
                member.MessageId,
                member.PromptHash,
                member.EgressReceiptHash,
                member.MessageStatus,
                member.MessageRequestHash,
                member.Disposition,
                member.ResultHash,
                member.TurnReceiptHash
            });
        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest.Root.ProjectId,
            manifest.Root.RunId,
            manifest.Root.SessionId,
            SelectionHash = manifest.SelectionHash.ToLowerInvariant(),
            manifest.WorkPlanId,
            manifest.WorkPlanHash,
            manifest.WorkflowHash,
            Members = members
        }));
    }
}
