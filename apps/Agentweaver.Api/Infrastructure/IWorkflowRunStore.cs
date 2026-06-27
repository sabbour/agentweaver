using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Provider-neutral workflow run store interface. Both SqliteWorkflowRunStore and
/// EfWorkflowRunStore implement this. Register the correct implementation via
/// Database:Provider in DI so consumers never bind to a concrete SQLite type.
/// </summary>
public interface IWorkflowRunStore
{
    Task InsertAsync(WorkflowRun run, CancellationToken ct = default);
    Task SetOrchestrationWorktreePathAsync(string workflowRunId, string worktreePath, CancellationToken ct = default);
    Task<string?> GetOrchestrationWorktreePathAsync(string workflowRunId, CancellationToken ct = default);
}
