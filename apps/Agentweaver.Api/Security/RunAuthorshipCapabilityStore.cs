using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Security;

public interface IRunAuthorshipCapabilityStore
{
    Task RegisterAsync(string runId, string token, DateTimeOffset expiresAt, CancellationToken ct);
    Task<bool> ValidateAsync(string runId, string token, CancellationToken ct);
    Task RemoveAsync(string runId, CancellationToken ct);
}

public sealed class EfRunAuthorshipCapabilityStore(
    IServiceScopeFactory scopeFactory) : IRunAuthorshipCapabilityStore
{
    public async Task RegisterAsync(
        string runId,
        string token,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var capability = await db.RunAuthorshipCapabilities
            .SingleOrDefaultAsync(candidate => candidate.RunId == runId, ct);
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        if (capability is null)
        {
            db.RunAuthorshipCapabilities.Add(new RunAuthorshipCapability
            {
                RunId = runId,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt,
            });
        }
        else
        {
            capability.TokenHash = tokenHash;
            capability.ExpiresAt = expiresAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ValidateAsync(string runId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(token))
            return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var capability = await db.RunAuthorshipCapabilities
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.RunId == runId, ct);
        if (capability is null || capability.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(capability.TokenHash, submittedHash);
    }

    public async Task RemoveAsync(string runId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.RunAuthorshipCapabilities
            .Where(capability => capability.RunId == runId)
            .ExecuteDeleteAsync(ct);
    }
}
