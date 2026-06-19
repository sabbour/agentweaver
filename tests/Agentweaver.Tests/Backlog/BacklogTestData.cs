using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Shared builders for the backlog backend tests. Everything is constructed against a REAL
/// <see cref="SqliteBacklogTaskStore"/> / <see cref="SqliteProjectStore"/> over a temp SQLite file
/// (Principle VII: no mocks of the store or its transaction logic).
/// </summary>
internal static class BacklogTestData
{
    public static Project MakeProject(ProjectId? id = null, ProjectState state = ProjectState.Active) => new()
    {
        Id               = id ?? ProjectId.New(),
        Name             = "Test Project",
        Origin           = ProjectOrigin.Blank(),
        WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        DefaultBranch    = "main",
        Owner            = "alice",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State            = state,
        CreatedAt        = DateTimeOffset.UtcNow,
        UpdatedAt        = DateTimeOffset.UtcNow,
    };

    public static BacklogTask MakeReadyTask(
        ProjectId projectId, string orderKey, BacklogTaskId? id = null, string capturedBy = "alice") => new()
    {
        Id          = id ?? BacklogTaskId.New(),
        ProjectId   = projectId,
        Title       = "A task",
        Description = "desc",
        State       = BacklogTaskState.Ready,
        OrderKey    = orderKey,
        CapturedBy  = capturedBy,
        CreatedAt   = DateTimeOffset.UtcNow,
        CommittedAt = DateTimeOffset.UtcNow,
        ClaimedAt   = null,
        RunId       = null,
    };

    public static BacklogTask MakeBacklogTask(
        ProjectId projectId, string orderKey, BacklogTaskId? id = null) => new()
    {
        Id          = id ?? BacklogTaskId.New(),
        ProjectId   = projectId,
        Title       = "A backlog task",
        Description = null,
        State       = BacklogTaskState.Backlog,
        OrderKey    = orderKey,
        CapturedBy  = "alice",
        CreatedAt   = DateTimeOffset.UtcNow,
        CommittedAt = null,
        ClaimedAt   = null,
        RunId       = null,
    };

    public static Run MakeCoordinatorRun(
        ProjectId projectId, RunId runId, string capturedBy = "alice") => new()
    {
        Id                = runId,
        RepositoryPath    = Path.Combine(Path.GetTempPath(), "repo"),
        OriginatingBranch = "main",
        ModelSource       = ModelSource.GitHubCopilot,
        Task              = "A task",
        SubmittingUser    = capturedBy,
        Status            = RunStatus.InProgress,
        StartedAt         = DateTimeOffset.UtcNow,
        ProjectId         = projectId,
        AgentName         = "Coordinator",
        WorkflowRunId     = null,   // coordinator runs (interactive AND pickup) carry no workflow_run_id
        ParentRunId       = null,
    };
}
