namespace Agentweaver.Orchestrator;

public sealed record RuntimeStartupSummary(
    string State,
    string Step,
    string? FailureCode = null);

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
