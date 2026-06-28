using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Replica-safe broker for the web sign-in one-time code exchange (security finding F5).
///
/// The GitHub web sign-in callback used to place the GitHub access token directly in the browser
/// redirect URL (<c>?session_token=...</c>), which leaks the credential to browser history, server
/// access logs, and Referer headers. Instead, the callback now issues a short-lived, single-use,
/// cryptographically-random one-time code and redirects with only that opaque code. The frontend
/// then exchanges the code for the actual session token via a server-side POST, so the token never
/// appears in a URL.
///
/// State is persisted in <c>MemoryDbContext</c> (Postgres in prod, SQLite in dev) rather than a
/// per-process <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>. With
/// <c>replicas:2</c> and no session affinity the POST <c>/api/auth/session/exchange</c> can land on
/// a DIFFERENT pod than the one that issued the code; a purely in-memory store would cause a ~50%
/// miss rate → 401 → bounce to login loop. At-most-once redemption is enforced atomically across
/// replicas via a conditional <c>ExecuteDeleteAsync</c> on <see cref="WebSessionExchangeCode.Code"/>:
/// exactly one caller's delete succeeds; a zero-rows result means unknown, already consumed, or
/// expired. Short-lived (60s TTL).
/// </summary>
public sealed class WebSessionExchangeService
{
    // One-time codes are single-use and must be redeemed quickly.
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebSessionExchangeService> _logger;

    public WebSessionExchangeService(
        IServiceScopeFactory scopeFactory,
        ILogger<WebSessionExchangeService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Issues a single-use one-time code bound to the GitHub access token + login and persists it
    /// to the shared database (visible to all replicas). Returns the opaque code the user agent
    /// carries back in the redirect URL.
    /// </summary>
    public async Task<string> IssueAsync(string accessToken, string login, CancellationToken ct = default)
    {
        var code = GenerateOpaqueCode();
        var expiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        // Best-effort purge of expired codes before inserting the new one. Only translatable on
        // Postgres (prod, where row growth matters); SQLite/dev is skipped — expiry is still
        // enforced at redeem time.
        if (db.Database.IsNpgsql())
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                await db.WebSessionExchangeCodes
                    .Where(c => c.ExpiresAt < now)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Opportunistic purge of expired web session exchange codes failed; continuing.");
            }
        }

        db.WebSessionExchangeCodes.Add(new WebSessionExchangeCode
        {
            Code = code,
            AccessToken = accessToken,
            Login = login,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Issued web session exchange code for GitHub login {Login}.", login);
        return code;
    }

    /// <summary>
    /// Atomically redeems a one-time code across replicas: removes it (single-use, via conditional
    /// <c>ExecuteDeleteAsync</c>) and validates expiry. Returns true with the bound GitHub access
    /// token + login on success; false otherwise.
    /// </summary>
    public async Task<(bool Success, string AccessToken, string Login)> TryRedeemAsync(
        string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (false, string.Empty, string.Empty);

        var now = DateTimeOffset.UtcNow;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        // Atomic single-use claim across replicas. Read the row for its payload + expiry snapshot,
        // then conditionally delete by Code only: exactly one caller's delete affects the row so a
        // replay (or a redeem on another pod that consumed the code first) sees zero rows affected →
        // reject. Expiry is enforced on the snapshot rather than in the DELETE predicate because the
        // DateTimeOffset comparison is not translatable on SQLite (it is on Postgres); this mirrors
        // the OAuthState and McpOAuthBrokerState pattern. Guarantees at-most-once redemption across
        // replicas.
        var existing = await db.WebSessionExchangeCodes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == code, ct)
            .ConfigureAwait(false);

        if (existing is null)
            return (false, string.Empty, string.Empty);

        var deleted = await db.WebSessionExchangeCodes
            .Where(c => c.Code == code)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false) > 0;

        if (!deleted || now > existing.ExpiresAt)
            return (false, string.Empty, string.Empty);

        return (true, existing.AccessToken, existing.Login);
    }

    /// <summary>Generates a 256-bit random, URL-safe opaque one-time code.</summary>
    public static string GenerateOpaqueCode() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
}

