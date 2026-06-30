using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// EF Core-backed token usage store. Used when Database:Provider = postgres (or any non-SQLite provider).
/// Replaces SqliteTokenUsageStore for multi-replica Postgres deployments.
/// </summary>
public sealed class EfTokenUsageStore : ITokenUsageStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;

    public EfTokenUsageStore(IDbContextFactory<MemoryDbContext> factory)
    {
        _factory = factory;
    }

    public async Task RecordAsync(TokenUsageRecord record, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = new TokenUsageRecordRow
        {
            Id = record.Id,
            RunId = record.RunId,
            WorkflowRunId = record.WorkflowRunId,
            ProjectId = record.ProjectId,
            ModelId = record.ModelId,
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,
            TotalNanoAiu = record.TotalNanoAiu,
            RecordedAt = record.RecordedAt,
        };
        // INSERT OR IGNORE semantics: ignore duplicate ids.
        var existing = await db.TokenUsageRecords
            .AsNoTracking()
            .AnyAsync(r => r.Id == record.Id, ct);
        if (!existing)
        {
            db.TokenUsageRecords.Add(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<TokenUsageSummary> GetRunUsageAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.TokenUsageRecords
            .AsNoTracking()
            .Where(r => r.RunId == runId)
            .GroupBy(r => r.ModelId)
            .Select(g => new { ModelId = g.Key, Input = g.Sum(r => r.InputTokens), Output = g.Sum(r => r.OutputTokens), NanoAiu = g.Sum(r => r.TotalNanoAiu) })
            .ToListAsync(ct);

        return BuildSummary(rows.Select(r => new TokenUsageByModel
        {
            ModelId = r.ModelId,
            InputTokens = r.Input,
            OutputTokens = r.Output,
            TotalNanoAiu = r.NanoAiu,
        }).ToList());
    }

    public async Task<TokenUsageSummary> GetWorkflowRunUsageAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.TokenUsageRecords
            .AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId)
            .GroupBy(r => r.ModelId)
            .Select(g => new { ModelId = g.Key, Input = g.Sum(r => r.InputTokens), Output = g.Sum(r => r.OutputTokens), NanoAiu = g.Sum(r => r.TotalNanoAiu) })
            .ToListAsync(ct);

        return BuildSummary(rows.Select(r => new TokenUsageByModel
        {
            ModelId = r.ModelId,
            InputTokens = r.Input,
            OutputTokens = r.Output,
            TotalNanoAiu = r.NanoAiu,
        }).ToList());
    }

    public async Task<TokenUsageSummary> GetProjectUsageAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.TokenUsageRecords
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.RecordedAt >= from && r.RecordedAt <= to)
            .GroupBy(r => r.ModelId)
            .Select(g => new { ModelId = g.Key, Input = g.Sum(r => r.InputTokens), Output = g.Sum(r => r.OutputTokens), NanoAiu = g.Sum(r => r.TotalNanoAiu) })
            .ToListAsync(ct);

        return BuildSummary(rows.Select(r => new TokenUsageByModel
        {
            ModelId = r.ModelId,
            InputTokens = r.Input,
            OutputTokens = r.Output,
            TotalNanoAiu = r.NanoAiu,
        }).ToList());
    }

    public async Task<IReadOnlyList<TokenUsageByProject>> GetAppUsageAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.TokenUsageRecords
            .AsNoTracking()
            .Where(r => r.ProjectId != null && r.RecordedAt >= from && r.RecordedAt <= to)
            .Join(db.Projects, t => t.ProjectId, p => p.ProjectId, (t, p) => new
            {
                t.ProjectId,
                ProjectName = p.Name,
                t.ModelId,
                t.InputTokens,
                t.OutputTokens,
                t.TotalNanoAiu,
            })
            .GroupBy(r => new { r.ProjectId, r.ProjectName, r.ModelId })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.ProjectName,
                g.Key.ModelId,
                Input = g.Sum(r => r.InputTokens),
                Output = g.Sum(r => r.OutputTokens),
                NanoAiu = g.Sum(r => r.TotalNanoAiu),
            })
            .ToListAsync(ct);

        var byProject = new Dictionary<string, (string projectName, List<TokenUsageByModel> models)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!byProject.TryGetValue(row.ProjectId!, out var entry))
            {
                entry = (row.ProjectName, new List<TokenUsageByModel>());
                byProject[row.ProjectId!] = entry;
            }
            entry.models.Add(new TokenUsageByModel
            {
                ModelId = row.ModelId,
                InputTokens = row.Input,
                OutputTokens = row.Output,
                TotalNanoAiu = row.NanoAiu,
            });
        }

        return byProject
            .Select(kv =>
            {
                var totalTokens = kv.Value.models.Sum(m => m.InputTokens + m.OutputTokens);
                var totalNano = kv.Value.models.Sum(m => m.TotalNanoAiu);
                return new TokenUsageByProject
                {
                    ProjectId = kv.Key,
                    ProjectName = kv.Value.projectName,
                    TotalTokens = totalTokens,
                    TotalNanoAiu = totalNano,
                    ByModel = kv.Value.models,
                };
            })
            .OrderByDescending(p => p.TotalTokens)
            .ToList();
    }

    private static TokenUsageSummary BuildSummary(IReadOnlyList<TokenUsageByModel> byModel)
    {
        var totalInput = byModel.Sum(m => m.InputTokens);
        var totalOutput = byModel.Sum(m => m.OutputTokens);
        var totalNano = byModel.Sum(m => m.TotalNanoAiu);
        return new TokenUsageSummary
        {
            InputTokens = totalInput,
            OutputTokens = totalOutput,
            TotalTokens = totalInput + totalOutput,
            TotalNanoAiu = totalNano,
            ByModel = byModel,
        };
    }
}
