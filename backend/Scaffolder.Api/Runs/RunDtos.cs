using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Request body for POST /runs.
/// </summary>
public sealed class CreateRunRequest
{
    /// <summary>
    /// The git branch to create the worktree from (must exist in the repo).
    /// </summary>
    public required string OriginatingBranch { get; init; }

    /// <summary>
    /// The model source provider to use. Must be one of the two supported providers.
    /// </summary>
    public required ModelSource ModelSource { get; init; }

    /// <summary>
    /// The natural-language task for the agent to execute.
    /// </summary>
    public required string TaskPrompt { get; init; }

    /// <summary>
    /// Maximum number of agent loop steps. Defaults to the configured value.
    /// </summary>
    public int? MaxSteps { get; init; }

    /// <summary>
    /// Maximum wall-clock duration in seconds. Defaults to the configured value.
    /// </summary>
    public int? MaxDurationSeconds { get; init; }
}

/// <summary>
/// Response body for run endpoints.
/// </summary>
public sealed class RunResponse
{
    public Guid Id { get; init; }
    public string OriginatingBranch { get; init; } = string.Empty;
    public string ModelSource { get; init; } = string.Empty;
    public string TaskPrompt { get; init; } = string.Empty;
    public string SubmittedBy { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int MaxSteps { get; init; }
    public int MaxDurationSeconds { get; init; }
    public Guid? SessionId { get; init; }
    public string? DiffSummary { get; init; }
    public string? FailureReason { get; init; }

    public static RunResponse FromEntity(RunEntity entity) => new()
    {
        Id = entity.Id,
        OriginatingBranch = entity.OriginatingBranch,
        ModelSource = entity.ModelSource.ToString(),
        TaskPrompt = entity.TaskPrompt,
        SubmittedBy = entity.SubmittedBy,
        Status = entity.Status.ToString(),
        CreatedAt = entity.CreatedAt,
        StartedAt = entity.StartedAt,
        CompletedAt = entity.CompletedAt,
        MaxSteps = entity.MaxSteps,
        MaxDurationSeconds = entity.MaxDurationSeconds,
        SessionId = entity.SessionId,
        DiffSummary = entity.DiffSummary,
        FailureReason = entity.FailureReason
    };
}
