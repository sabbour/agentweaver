using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerateProjectWorkflowRequest(
    string RunId,
    string SessionId,
    long ExpectedDecisionStateVersion,
    string Prompt)
{
    // Leave room for the fixed purpose prefix within the addressed-message text limit.
    internal const int MaximumPromptLength = AddressedMessageValidation.MaximumTextLength - 1024;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RunId) || RunId.Length > 256 ||
            string.IsNullOrWhiteSpace(SessionId) || SessionId.Length > 256 ||
            ExpectedDecisionStateVersion < 1 ||
            string.IsNullOrWhiteSpace(Prompt) ||
            Prompt.Length > MaximumPromptLength ||
            Prompt.Contains('\0') ||
            Prompt.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new CoordinationException(
                "workflow_generation_request_invalid", StatusCodes.Status400BadRequest);
    }
}

public sealed record WorkflowGenerationArtifactSummary(
    Guid ArtifactId,
    string WorkflowId,
    string DefinitionRevision);

public sealed record WorkflowGenerationFailure(string Code, string Message);

public sealed record WorkflowGenerationZeroCreditNoSendStatus(
    Guid NativeRequestId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    string Stage,
    string Code,
    string ReceiptSha256,
    int ObservedEventCount,
    bool RangeComplete,
    string BeforeCursorSha256,
    string AfterCursorSha256,
    DateTimeOffset RecordedAt,
    DateTimeOffset VerifiedAt);

public sealed record WorkflowGenerationJobEnvelope(
    Guid JobId,
    string Status,
    string ProjectId,
    string RunId,
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string StatusUrl,
    string ResultUrl,
    WorkflowGenerationArtifactSummary? Artifact,
    WorkflowGenerationFailure? Failure,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkflowGenerationZeroCreditNoSendStatus? ZeroCreditNoSend = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowGenerationZeroCreditNoSendProof(
    int ContractVersion,
    Guid JobId,
    Guid NativeRequestId,
    string ProjectId,
    string RunId,
    string SessionId,
    string TenantId,
    string ActorIssuer,
    string ActorId,
    long ExpectedDecisionStateVersion,
    long ConfigurationRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string ModelSelectionReference,
    string PromptSha256,
    string RequestHash,
    string GuardReceiptHash,
    string NoSendReceiptHash,
    string Stage,
    string Code,
    int ObservedEventCount,
    bool RangeComplete,
    string BeforeCursorHash,
    string AfterCursorHash,
    DateTimeOffset RecordedAt,
    DateTimeOffset VerifiedAt,
    string CanonicalPayloadHash)
{
    private static readonly System.Text.Json.JsonSerializerOptions DigestJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    internal static Guid EventId(Guid jobId, Guid nativeRequestId) =>
        Guid.ParseExact(RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(
            $"workflow-generation-fixed-credit-no-send\0{jobId:D}\0{nativeRequestId:D}"))[..32], "N");

    internal static string StreamId(string projectId, string runId) =>
        $"coordination/{projectId}/{runId}/fixed-credit-denials";

    internal static WorkflowGenerationZeroCreditNoSendProof Seal(WorkflowGenerationZeroCreditNoSendProof proof) =>
        proof with { CanonicalPayloadHash = ComputePayloadHash(proof) };

    internal WorkflowGenerationZeroCreditNoSendProof Validate()
    {
        if (ContractVersion != 1 ||
            JobId == Guid.Empty ||
            NativeRequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(ProjectId) ||
            string.IsNullOrWhiteSpace(RunId) ||
            string.IsNullOrWhiteSpace(SessionId) ||
            string.IsNullOrWhiteSpace(TenantId) ||
            string.IsNullOrWhiteSpace(ActorIssuer) ||
            string.IsNullOrWhiteSpace(ActorId) ||
            ExpectedDecisionStateVersion < 1 ||
            ConfigurationRevision < 1 ||
            ExecutionFence < 1 ||
            RuntimeInstanceId == Guid.Empty ||
            RegistrationRevision < 1 ||
            string.IsNullOrWhiteSpace(ModelSelectionReference) ||
            Stage != "hostPreQueue" ||
            Code != "hardLimitZero" ||
            ObservedEventCount != 0 ||
            !RangeComplete ||
            BeforeCursorHash != AfterCursorHash ||
            RecordedAt == default ||
            VerifiedAt == default)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_no_send_invalid");
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        RuntimeContractValidation.ValidateHash(PromptSha256);
        RuntimeContractValidation.ValidateHash(RequestHash);
        RuntimeContractValidation.ValidateHash(GuardReceiptHash);
        RuntimeContractValidation.ValidateHash(NoSendReceiptHash);
        RuntimeContractValidation.ValidateHash(BeforeCursorHash);
        RuntimeContractValidation.ValidateHash(AfterCursorHash);
        RuntimeContractValidation.ValidateHash(CanonicalPayloadHash);
        if (CanonicalPayloadHash != ComputePayloadHash(this))
            throw new RuntimeAuthorizationException("runtime_fixed_credit_no_send_invalid");
        return this;
    }

    internal bool MatchesJob(WorkflowGenerationJob job) =>
        JobId == job.JobId &&
        NativeRequestId == job.NativeRequestId &&
        ProjectId == job.ProjectId &&
        RunId == job.RunId &&
        SessionId == job.SessionId &&
        TenantId == job.TenantId &&
        ActorIssuer == job.ActorIssuer &&
        ActorId == job.ActorId &&
        ExpectedDecisionStateVersion == job.ExpectedDecisionStateVersion &&
        ConfigurationRevision == job.ConfigurationRevision &&
        AcceptedSelectionHash == job.AcceptedSelectionHash &&
        ExecutionFence == job.ExecutionFence &&
        RuntimeInstanceId == job.RuntimeInstanceId &&
        RegistrationRevision == job.RegistrationRevision &&
        ModelSelectionReference == job.ModelSelectionReference &&
        PromptSha256 == job.PromptSha256;

    internal WorkflowGenerationZeroCreditNoSendStatus ToStatus() =>
        new(
            NativeRequestId,
            RuntimeInstanceId,
            RegistrationRevision,
            ExecutionFence,
            Stage,
            Code,
            NoSendReceiptHash,
            ObservedEventCount,
            RangeComplete,
            BeforeCursorHash,
            AfterCursorHash,
            RecordedAt,
            VerifiedAt);

    private static string ComputePayloadHash(WorkflowGenerationZeroCreditNoSendProof proof) =>
        RuntimeContractValidation.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            proof with { CanonicalPayloadHash = string.Empty }, DigestJsonOptions));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowGenerationJobResult(
    Guid JobId,
    Guid ArtifactId,
    WorkflowDefinition WorkflowDefinition,
    string DefinitionSha256,
    RuntimeRootTurnCompletionEvidence NativeCompletion,
    long ConfigurationRevision,
    string AcceptedSelectionHash);

internal sealed record WorkflowGenerationJobCreate(
    Guid JobId,
    Guid NativeRequestId,
    string ProjectId,
    string RunId,
    string SessionId,
    string TenantId,
    string ActorIssuer,
    string ActorId,
    string IdempotencyKey,
    string RequestFingerprint,
    string Prompt,
    string PromptSha256,
    long ExpectedDecisionStateVersion,
    long ConfigurationRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string ModelSelectionReference);

internal sealed record WorkflowGenerationJob(
    Guid JobId,
    Guid NativeRequestId,
    string ProjectId,
    string RunId,
    string SessionId,
    string TenantId,
    string ActorIssuer,
    string ActorId,
    string IdempotencyKey,
    string RequestFingerprint,
    string Prompt,
    string PromptSha256,
    long ExpectedDecisionStateVersion,
    long ConfigurationRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string ModelSelectionReference,
    string Status,
    string ExecutionPhase,
    string? FailureCode,
    string? FailureMessage,
    WorkflowGenerationJobResult? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal enum WorkflowGenerationCreateDisposition
{
    Created,
    Existing,
    Conflict,
    BindingStale
}

internal sealed record WorkflowGenerationCreateResult(
    WorkflowGenerationJob Job,
    WorkflowGenerationCreateDisposition Disposition);
