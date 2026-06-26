using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Api.Auth;

/// <summary>
/// In-memory broker for the web sign-in one-time code exchange (security finding F5).
///
/// The GitHub web sign-in callback used to place the GitHub access token directly in the browser
/// redirect URL (<c>?session_token=...</c>), which leaks the credential to browser history, server
/// access logs, and Referer headers. Instead, the callback now issues a short-lived, single-use,
/// cryptographically-random one-time code and redirects with only that opaque code. The frontend
/// then exchanges the code for the actual session token via a server-side POST, so the token never
/// appears in a URL.
///
/// State held in memory (single-instance only): issued one-time codes mapped to the GitHub access
/// token + login, single-use and short-lived (60s TTL).
/// </summary>
public sealed class WebSessionExchangeService
{
    // One-time codes are single-use and must be redeemed quickly.
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, IssuedSession> _codes = new();
    private readonly ILogger<WebSessionExchangeService> _logger;

    public WebSessionExchangeService(ILogger<WebSessionExchangeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Issues a single-use one-time code bound to the GitHub access token + login. Returns the
    /// opaque code the user agent carries back in the redirect URL.
    /// </summary>
    public string Issue(string accessToken, string login)
    {
        PurgeExpired();

        var code = GenerateOpaqueCode();
        _codes[code] = new IssuedSession(
            accessToken, login, DateTimeOffset.UtcNow.Add(CodeLifetime));

        _logger.LogInformation("Issued web session exchange code for GitHub login {Login}.", login);
        return code;
    }

    /// <summary>
    /// Atomically redeems a one-time code: removes it (single-use) and validates expiry. Returns
    /// true with the bound GitHub access token + login on success; false otherwise.
    /// </summary>
    public bool TryRedeem(string? code, out string accessToken, out string login)
    {
        accessToken = string.Empty;
        login = string.Empty;

        if (string.IsNullOrWhiteSpace(code) || !_codes.TryRemove(code, out var issued))
            return false;

        if (issued.IsExpired)
            return false;

        accessToken = issued.AccessToken;
        login = issued.Login;
        return true;
    }

    /// <summary>Generates a 256-bit random, URL-safe opaque one-time code.</summary>
    public static string GenerateOpaqueCode() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _codes)
            if (now > kvp.Value.ExpiresAt)
                _codes.TryRemove(kvp.Key, out _);
    }

    private sealed record IssuedSession(
        string AccessToken, string Login, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}
