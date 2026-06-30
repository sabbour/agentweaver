using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>Immutable grant details carried by a refresh token, returned on a successful rotation.</summary>
public sealed record McpRefreshGrant(string Subject, string GithubLogin, string ClientId, string Scope, string? Org);

/// <summary>Outcome of a refresh-token rotation.</summary>
public sealed record RefreshRotationResult(McpRefreshGrant? Grant, string? NewRefreshToken, string? Error, string? ErrorDescription);

/// <summary>
/// Persistent store for rotating OAuth refresh tokens and the access-token <c>jti</c> denylist (T4).
///
/// Tokens are stored only as SHA-256 hashes. Rotation is single-use: each refresh consumes the
/// presented token and issues a new one in the same chain. Presenting an already-consumed token is
/// treated as theft and revokes the whole chain (reuse detection, RFC 6819 §5.2.2.3).
///
/// Scoped (per-request) because it depends on the scoped <see cref="MemoryDbContext"/>; resolved
/// directly by the minimal-API OAuth handlers.
/// </summary>
public sealed class McpRefreshTokenStore
{
    /// <summary>Sliding lifetime of a refresh token, reset on each rotation.</summary>
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(30);

    /// <summary>Absolute cap from the original grant; the chain cannot refresh past this.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(90);

    private readonly MemoryDbContext _db;

    public McpRefreshTokenStore(MemoryDbContext db) => _db = db;

    /// <summary>
    /// Issues the first refresh token for a freshly-granted authorization code. Returns the opaque
    /// plaintext token (only its hash is persisted).
    /// </summary>
    public async Task<string> IssueAsync(McpRefreshGrant grant, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var plaintext = GenerateOpaqueToken();
        _db.McpRefreshTokens.Add(new McpRefreshToken
        {
            TokenHash = Hash(plaintext),
            ChainId = Guid.NewGuid().ToString("N"),
            Subject = grant.Subject,
            GithubLogin = grant.GithubLogin,
            ClientId = grant.ClientId,
            Scope = grant.Scope,
            Org = grant.Org,
            CreatedAt = now,
            ExpiresAt = now.Add(SlidingLifetime),
            AbsoluteExpiresAt = now.Add(AbsoluteLifetime),
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return plaintext;
    }

    /// <summary>
    /// Rotates a presented refresh token: validates it, consumes it, and issues a new token in the
    /// same chain. On reuse of an already-consumed token the entire chain is revoked.
    /// </summary>
    public async Task<RefreshRotationResult> RotateAsync(string? presentedToken, string? clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
            return Invalid("Refresh token is required.");

        var hash = Hash(presentedToken);
        var existing = await _db.McpRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct).ConfigureAwait(false);
        if (existing is null)
            return Invalid("Refresh token is invalid.");

        // client_id binding: a public client must present the same client_id it was issued to.
        if (!string.IsNullOrWhiteSpace(clientId) && !string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
            return Invalid("client_id does not match the refresh token.");

        var now = DateTimeOffset.UtcNow;

        // Reuse detection: a consumed or revoked token presented again invalidates the whole chain.
        if (existing.ConsumedAt is not null || existing.RevokedAt is not null)
        {
            await RevokeChainAsync(existing.ChainId, now, ct).ConfigureAwait(false);
            return Invalid("Refresh token has already been used or revoked; the token chain has been revoked.");
        }

        if (now > existing.ExpiresAt || now > existing.AbsoluteExpiresAt)
        {
            existing.RevokedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Invalid("Refresh token has expired.");
        }

        // Consume the presented token and mint its successor in the same chain.
        existing.ConsumedAt = now;
        var newPlaintext = GenerateOpaqueToken();
        var slidingExpiry = now.Add(SlidingLifetime);
        if (slidingExpiry > existing.AbsoluteExpiresAt)
            slidingExpiry = existing.AbsoluteExpiresAt;

        _db.McpRefreshTokens.Add(new McpRefreshToken
        {
            TokenHash = Hash(newPlaintext),
            ChainId = existing.ChainId,
            Subject = existing.Subject,
            GithubLogin = existing.GithubLogin,
            ClientId = existing.ClientId,
            Scope = existing.Scope,
            Org = existing.Org,
            CreatedAt = now,
            ExpiresAt = slidingExpiry,
            AbsoluteExpiresAt = existing.AbsoluteExpiresAt,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var grant = new McpRefreshGrant(existing.Subject, existing.GithubLogin, existing.ClientId, existing.Scope, existing.Org);
        return new RefreshRotationResult(grant, newPlaintext, null, null);
    }

    /// <summary>
    /// Revokes a presented refresh token and its chain (RFC 7009). Idempotent and non-throwing —
    /// the revocation endpoint always returns success regardless of whether the token was found.
    /// </summary>
    public async Task RevokeAsync(string? presentedToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
            return;

        var hash = Hash(presentedToken);
        var existing = await _db.McpRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct).ConfigureAwait(false);
        if (existing is null)
            return;

        await RevokeChainAsync(existing.ChainId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>Adds an access-token <c>jti</c> to the denylist until its natural expiry.</summary>
    public async Task DenyJtiAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
            return;

        var already = await _db.McpRevokedJtis.AnyAsync(j => j.Jti == jti, ct).ConfigureAwait(false);
        if (already)
            return;

        _db.McpRevokedJtis.Add(new McpRevokedJti { Jti = jti, ExpiresAt = expiresAt, RevokedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when the <c>jti</c> is on the denylist and not yet expired (purges expired entries lazily).</summary>
    public async Task<bool> IsJtiDeniedAsync(string? jti, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
            return false;

        var now = DateTimeOffset.UtcNow;
        // Jti is unique+indexed; the EF Core SQLite provider cannot translate
        // DateTimeOffset ordering comparisons (>, <), so fetch by Jti and compare expiry in memory.
        var entry = await _db.McpRevokedJtis
            .FirstOrDefaultAsync(j => j.Jti == jti, ct).ConfigureAwait(false);
        return entry is not null && entry.ExpiresAt > now;
    }

    private async Task RevokeChainAsync(string chainId, DateTimeOffset at, CancellationToken ct)
    {
        var chain = await _db.McpRefreshTokens
            .Where(t => t.ChainId == chainId && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var token in chain)
            token.RevokedAt = at;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static RefreshRotationResult Invalid(string description) =>
        new(null, null, "invalid_grant", description);

    /// <summary>256-bit URL-safe random opaque token.</summary>
    public static string GenerateOpaqueToken() =>
        McpOAuthBrokerService.GenerateOpaqueToken();

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
