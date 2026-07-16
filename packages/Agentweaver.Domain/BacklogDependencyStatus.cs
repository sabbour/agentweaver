namespace Agentweaver.Domain;

public sealed record BacklogDependencyStatus(
    BacklogTaskId TaskId,
    BacklogTaskId DependsOnTaskId,
    string DependsOnTitle,
    RunId? DependsOnRunId,
    RunStatus? DependsOnRunStatus,
    bool IsSatisfied);

