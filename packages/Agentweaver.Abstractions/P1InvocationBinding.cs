namespace Agentweaver.Abstractions;

/// <summary>Current Orchestrator-owned provenance for a P1 setup receipt accepted as a run root.</summary>
public sealed record P1InvocationBinding(
    int ContractVersion,
    Guid CoreInvocationId,
    Guid SetupInvocationId,
    string SetupReceiptHash,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string ActorIssuer,
    string ActorSubject,
    Guid MembershipId,
    long MembershipRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    long RootStateVersion,
    DateTimeOffset AcceptedAt);
