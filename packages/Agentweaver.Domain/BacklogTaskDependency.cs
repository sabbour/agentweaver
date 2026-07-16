namespace Agentweaver.Domain;

public sealed record BacklogTaskDependency
{
    public required ProjectId ProjectId { get; init; }
    public required BacklogTaskId TaskId { get; init; }
    public required BacklogTaskId DependsOnTaskId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

