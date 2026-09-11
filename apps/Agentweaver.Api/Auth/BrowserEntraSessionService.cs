using System.Security.Cryptography;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Issues and validates the HttpOnly browser session required for GitHub App handoffs.
/// The cookie contains only an opaque identifier; its Entra subject remains server-side.
/// </summary>
public sealed class BrowserEntraSessionService
{
    public const string CookieName = "__Host-agentweaver-entra-browser-session";
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    private readonly MemoryDbContext _db;
    private readonly TimeProvider _timeProvider;

    public BrowserEntraSessionService(MemoryDbContext db)
        : this(db, TimeProvider.System)
    {
    }

    internal BrowserEntraSessionService(MemoryDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<BrowserEntraSession> IssueAsync(
        HttpContext context,
        EntraAccessTokenClaims claims,
        CancellationToken ct = default)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var currentSessionId) &&
            !string.IsNullOrWhiteSpace(currentSessionId))
        {
            var currentSession = await _db.BrowserEntraSessions.FindAsync([currentSessionId], ct)
                .ConfigureAwait(false);
            if (currentSession is not null)
                _db.BrowserEntraSessions.Remove(currentSession);
        }

        var session = new BrowserEntraSession
        {
            Id = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            EntraObjectId = claims.ObjectId,
            DisplayName = claims.DisplayName,
            Email = claims.Email,
            PlatformRoles = string.Join(',', claims.RecognizedRoles),
            ExpiresAt = _timeProvider.GetUtcNow().Add(SessionLifetime),
        };
        _db.BrowserEntraSessions.Add(session);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        context.Response.Cookies.Append(CookieName, session.Id, CookieOptions(session.ExpiresAt));
        return session;
    }

    public async Task<BrowserEntraSession?> GetCurrentAsync(HttpContext context, CancellationToken ct = default)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId))
            return null;

        var session = await _db.BrowserEntraSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || session.ExpiresAt <= _timeProvider.GetUtcNow() ||
            string.IsNullOrWhiteSpace(session.DisplayName))
            return null;

        return session;
    }

    public async Task RevokeCurrentAsync(HttpContext context, CancellationToken ct = default)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var sessionId) &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await _db.BrowserEntraSessions.FindAsync([sessionId], ct).ConfigureAwait(false);
            if (session is not null)
            {
                _db.BrowserEntraSessions.Remove(session);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        Clear(context);
    }

    public static void Clear(HttpContext context) =>
        context.Response.Cookies.Append(CookieName, string.Empty, CookieOptions(DateTimeOffset.UnixEpoch));

    private static CookieOptions CookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expiresAt,
    };
}
