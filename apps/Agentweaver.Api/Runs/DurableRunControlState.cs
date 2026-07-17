using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class DurableRunControlState(IServiceScopeFactory scopeFactory, IRunEventStream eventStream)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IRunEventStream _eventStream = eventStream;

    public void Append(string runId, string eventType, object payload)
    {
        _eventStream.AppendAsync(
            runId,
            new RunEvent(0, eventType, payload),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
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
