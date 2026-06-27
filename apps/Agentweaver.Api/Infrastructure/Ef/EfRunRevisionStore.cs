using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

public sealed class EfRunRevisionStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;
    public EfRunRevisionStore(IDbContextFactory<MemoryDbContext> factory) => _factory = factory;

    public async Task InsertRevisionAsync(
        RunId runId, int revisionNumber, string reviewerUser,
        string rawComment, string sanitizedComment, string previousTreeHash,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.RunRevisions.Add(new RunRevisionRecord
        {
            RunId = runId.ToString(),
            RevisionNumber = revisionNumber,
            ReviewerUser = reviewerUser,
            CreatedAt = DateTimeOffset.UtcNow,
            RawComment = rawComment,
            SanitizedComment = sanitizedComment,
            PreviousTreeHash = previousTreeHash,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> GetMaxRevisionNumberAsync(RunId runId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var id = runId.ToString();
        return await db.RunRevisions.AsNoTracking()
            .Where(r => r.RunId == id)
            .Select(r => (int?)r.RevisionNumber)
            .MaxAsync(ct) ?? 0;
    }
}
