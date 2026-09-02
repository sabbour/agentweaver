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
public sealed class BrowserEntraSessionService(MemoryDbContext db)
{
    public const string CookieName = "__Host-agentweaver-entra-browser-session";

    public async Task<BrowserEntraSession> IssueAsync(
        HttpContext context,
        EntraAccessTokenClaims claims,
        CancellationToken ct = default)
    {
        var session = new BrowserEntraSession
        {
            Id = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            EntraObjectId = claims.ObjectId,
            PlatformRoles = string.Join(',', claims.RecognizedRoles),
            ExpiresAt = claims.ExpiresAt,
        };
        db.BrowserEntraSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        context.Response.Cookies.Append(CookieName, session.Id, CookieOptions(session.ExpiresAt));
        return session;
    }

    public async Task<BrowserEntraSession?> GetCurrentAsync(HttpContext context, CancellationToken ct = default)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId))
            return null;

        var session = await db.BrowserEntraSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        return session;
    }

    public async Task RevokeCurrentAsync(HttpContext context, CancellationToken ct = default)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var sessionId) &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await db.BrowserEntraSessions.FindAsync([sessionId], ct).ConfigureAwait(false);
            if (session is not null)
            {
                db.BrowserEntraSessions.Remove(session);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
