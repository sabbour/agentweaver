using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Runs;

public sealed class DurableRunControlState(IServiceScopeFactory scopeFactory)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly object _appendLock = new();

    public void Append(string runId, string eventType, object payload)
    {
        lock (_appendLock)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                    var nextSeq = db.RunEvents
                        .Where(e => e.RunId == runId)
                        .Select(e => (int?)e.Sequence)
                        .Max() ?? 0;
                    db.RunEvents.Add(new RunEventRecord
                    {
                        RunId = runId,
                        Sequence = nextSeq + 1,
                        EventType = eventType,
                        PayloadJson = JsonSerializer.Serialize(payload, JsonDefaults.Options),
                        CreatedAt = DateTime.UtcNow,
                    });
                    db.SaveChanges();
                    return;
                }
                catch (DbUpdateException) when (attempt < 4)
                {
                    Thread.Sleep(10);
                }
            }
        }
    }

    public IReadOnlyList<RunEventRecord> Load(string runId, params string[] eventTypes)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && eventTypes.Contains(e.EventType))
            .OrderBy(e => e.Sequence)
            .ToList();
    }
}
