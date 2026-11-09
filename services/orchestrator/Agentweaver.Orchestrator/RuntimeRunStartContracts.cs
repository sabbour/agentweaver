namespace Agentweaver.Orchestrator;

public sealed record RuntimeStartupSummary(
    string State,
    string Step,
    string? FailureCode = null,
    Guid? P1CoreInvocationId = null,
    Guid? P1SetupInvocationId = null,
    Guid? RuntimeInstanceId = null,
    long? RegistrationRevision = null,
    string? AcceptedSelectionHash = null,
    long? ExecutionFence = null,
    string? ModelSelectionReference = null,
    string? ModelSourceMode = null,
    string? ReasoningEffort = null,
    int? MaxPromptTokens = null,
    string? ImageDigest = null,
    string? ImagePlatform = null,
    string? PodUid = null);

internal sealed record RuntimeRunStartIntent(
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string AcceptedSelectionHash,
    long ExecutionFence,
    Guid IntentId,
    Guid BootstrapOperationId,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId,
    string State,
    string CurrentStep,
    string? FailureCode,
    string? EnvironmentId,
    Guid? RuntimeInstanceId,
    string ReceiptLinks,
    bool CleanupObligation,
    long RowVersion)
{
    public RuntimeStartupSummary Summary => new(State, CurrentStep, FailureCode);
}

internal sealed record RuntimeReplacementOperation(
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    Guid OperationId,
    string RequestHash,
    Guid ExpectedRuntimeInstanceId,
    long ExpectedRegistrationRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    string State,
    string CurrentStep,
    string? FailureCode,
    string ReceiptLinks,
    long RowVersion);
