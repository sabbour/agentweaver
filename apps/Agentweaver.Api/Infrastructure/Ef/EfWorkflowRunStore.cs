using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

public sealed class EfWorkflowRunStore : IWorkflowRunStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;
    public EfWorkflowRunStore(IDbContextFactory<MemoryDbContext> factory) => _factory = factory;

    public async Task InsertAsync(WorkflowRun run, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.WorkflowRuns.Add(new WorkflowRunRecord
        {
            WorkflowRunId = run.Id,
            ProjectId = run.ProjectId.ToString(),
            Task = run.Task,
            SubmittingUser = run.SubmittingUser,
            StartedAt = run.StartedAt,
            OrchestrationWorktreePath = run.OrchestrationWorktreePath,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetOrchestrationWorktreePathAsync(string workflowRunId, string worktreePath, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.WorkflowRuns
            .Where(w => w.WorkflowRunId == workflowRunId && w.OrchestrationWorktreePath == null)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.OrchestrationWorktreePath, worktreePath), ct);
    }

    public async Task<string?> GetOrchestrationWorktreePathAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns.AsNoTracking()
            .Where(w => w.WorkflowRunId == workflowRunId)
            .Select(w => w.OrchestrationWorktreePath)
            .FirstOrDefaultAsync(ct);
    }
}
