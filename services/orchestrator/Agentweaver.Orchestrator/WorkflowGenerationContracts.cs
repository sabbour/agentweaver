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
    WorkflowGenerationFailure? Failure);

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
