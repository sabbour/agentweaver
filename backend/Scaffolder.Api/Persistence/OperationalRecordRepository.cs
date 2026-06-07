using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

internal sealed class OperationalRecordRepository : IOperationalRecordRepository
{
    private readonly ScaffolderDbContext _db;

    public OperationalRecordRepository(ScaffolderDbContext db)
    {
        _db = db;
    }

    public async Task<OperationalRecordEntity> UpsertAsync(
        OperationalRecordEntity record,
        CancellationToken ct = default)
    {
        var existing = await _db.OperationalRecords
            .FirstOrDefaultAsync(o => o.RunId == record.RunId, ct);

        if (existing is null)
        {
            record.Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id;
            _db.OperationalRecords.Add(record);
        }
        else
        {
            existing.SubmittedBy = record.SubmittedBy;
            existing.ModelSource = record.ModelSource;
            existing.StartedAt = record.StartedAt;
            existing.EndedAt = record.EndedAt;
            existing.StepCount = record.StepCount;
            existing.Outcome = record.Outcome;
            // PolicyTrace is managed separately via AppendPolicyTraceEntryAsync
        }

        await _db.SaveChangesAsync(ct);
        return existing ?? record;
    }

    public async Task<OperationalRecordEntity?> GetByRunIdAsync(
        Guid runId,
        CancellationToken ct = default)
    {
        return await _db.OperationalRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.RunId == runId, ct);
    }

    public async Task AppendPolicyTraceEntryAsync(
        Guid runId,
        string entryJson,
        CancellationToken ct = default)
    {
        var record = await _db.OperationalRecords
            .FirstOrDefaultAsync(o => o.RunId == runId, ct);

        if (record is null)
        {
            throw new InvalidOperationException(
                $"OperationalRecord for run {runId} not found. " +
                "Create it via UpsertAsync before appending policy trace entries.");
        }

        var array = JsonNode.Parse(record.PolicyTrace)?.AsArray() ?? new JsonArray();
        var entry = JsonNode.Parse(entryJson);
        array.Add(entry);
        record.PolicyTrace = array.ToJsonString();

        await _db.SaveChangesAsync(ct);
    }
}
