using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// Signs and validates the short-lived, audience-bound JWT access tokens minted by the
/// Agentweaver-hosted OAuth 2.1 Authorization Server (Option C / Seraph design).
///
/// The MCP server is a pure OAuth Resource Server: it validates these tokens offline using the
/// public key published at <c>/oauth/jwks</c>, so no per-request database or GitHub call is needed.
///
/// Key loading:
///  - Production: an RSA private key (PEM) is loaded from <c>Auth:OAuth:SigningKey</c>, which Link
///    binds to the Key Vault secret named <c>mcp-oauth-signing-key</c> via the CSI secret store.
///  - Local dev fallback: when no key is configured, an EPHEMERAL key is generated at startup so the
///    flow works end-to-end on a developer box. Ephemeral keys do NOT survive a restart and MUST NOT
///    be used in production (a warning is logged).
/// </summary>
public sealed class McpTokenService : IDisposable
{
    public const string AccessTokenScope = "mcp:invoke";
    public const string SigningAlgorithm = SecurityAlgorithms.RsaSha256; // RS256

    /// <summary>Default access-token lifetime. Short by design — refresh (T4) keeps UX seamless.</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly RSA _rsa;
    private readonly RsaSecurityKey _signingKey;
    private readonly SigningCredentials _signingCredentials;
    private readonly bool _ephemeral;

    public McpTokenService(IConfiguration configuration, ILogger<McpTokenService> logger)
    {
        var pem = configuration["Auth:OAuth:SigningKey"];
        _rsa = RSA.Create();

        if (!string.IsNullOrWhiteSpace(pem))
        {
            // Accept either a full PEM document (-----BEGIN ... KEY-----) or a bare base64 PKCS#8 body.
            try
            {
                _rsa.ImportFromPem(pem);
            }
            catch (ArgumentException)
            {
                _rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pem.Trim()), out _);
            }

            _ephemeral = false;
            logger.LogInformation("MCP OAuth signing key loaded from Auth:OAuth:SigningKey configuration.");
        }
        else
        {
            // LOCAL DEV ONLY. Production loads the key from the 'mcp-oauth-signing-key' Key Vault secret.
            _rsa.KeySize = 2048;
            _ephemeral = true;
            logger.LogWarning(
                "Auth:OAuth:SigningKey is not configured. Generating an EPHEMERAL RSA signing key for " +
                "LOCAL DEVELOPMENT ONLY. Tokens will not survive a restart and this MUST NOT be used in " +
                "production. Configure the 'mcp-oauth-signing-key' Key Vault secret (bound to " +
                "Auth:OAuth:SigningKey) for any shared/hosted deployment.");
        }

        var keyId = ComputeKeyId(_rsa.ExportParameters(false));
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = keyId };
        _signingCredentials = new SigningCredentials(_signingKey, SigningAlgorithm);
    }

    /// <summary>The <c>kid</c> identifying the current signing key in JWT headers and JWKS.</summary>
    public string KeyId => _signingKey.KeyId!;

    /// <summary>True when running on an ephemeral (dev-only) key. Surfaced for diagnostics.</summary>
    public bool IsEphemeralKey => _ephemeral;

    /// <summary>
    /// Mints a signed access token bound to the MCP resource (<paramref name="audience"/>).
    /// Claims follow the Seraph design: iss, aud (RFC 8707 resource binding), sub, scope,
    /// gh_login, org, plus iat/exp/jti supplied by the handler.
    /// </summary>
    public string CreateAccessToken(string issuer, string audience, string subject, string githubLogin, string? org)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["scope"] = AccessTokenScope,
            ["gh_login"] = githubLogin,
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (!string.IsNullOrWhiteSpace(org))
            claims["org"] = org;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(AccessTokenLifetime),
            Claims = claims,
            SigningCredentials = _signingCredentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// Validation parameters the MCP Resource Server (T6) uses to verify tokens offline:
    /// signature against the published JWKS, plus strict issuer/audience/lifetime checks.
    /// Exposed here so signing and validation stay in lock-step.
    /// </summary>
    public TokenValidationParameters CreateValidationParameters(string issuer, string audience) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _signingKey,
        ValidAlgorithms = [SigningAlgorithm],
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>The public signing key as an RFC 7517 JWK, for publication at <c>/oauth/jwks</c>.</summary>
    public JsonWebKey GetPublicJsonWebKey()
    {
        var parameters = _rsa.ExportParameters(false);
        return new JsonWebKey
        {
            Kty = "RSA",
            Use = "sig",
            Alg = SigningAlgorithm,
            Kid = KeyId,
            N = Base64UrlEncoder.Encode(parameters.Modulus),
            E = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    /// <summary>Claims extracted from a validated Agentweaver access token.</summary>
    public sealed record AccessTokenClaims(string Subject, string GitHubLogin, string? Org, string? Jti, string Scope);

    /// <summary>
    /// Validates an Agentweaver-minted access token (signature via the in-process signing key, plus
    /// strict iss/aud/exp checks) and extracts its identity claims. Returns false for any token that
    /// is not a valid Agentweaver JWT for this issuer/audience (e.g. a raw GitHub token or API key),
    /// so callers can fall through to the legacy validation paths. Never throws.
    /// </summary>
    public bool TryValidateAccessToken(string token, string issuer, string audience, out AccessTokenClaims? claims)
    {
        claims = null;
        if (string.IsNullOrWhiteSpace(token) || token.Count(c => c == '.') != 2)
            return false;

        try
        {
            var handler = new JsonWebTokenHandler();
            var result = handler.ValidateTokenAsync(token, CreateValidationParameters(issuer, audience)).GetAwaiter().GetResult();
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
                return false;

            var subject = jwt.TryGetClaim("sub", out var sub) ? sub.Value : null;
            if (string.IsNullOrEmpty(subject))
                return false;

            var ghLogin = jwt.TryGetClaim("gh_login", out var gh) ? gh.Value : subject;
            var org = jwt.TryGetClaim("org", out var o) ? o.Value : null;
            var jti = jwt.TryGetClaim("jti", out var j) ? j.Value : null;
            var scope = jwt.TryGetClaim("scope", out var s) ? s.Value : AccessTokenScope;

            claims = new AccessTokenClaims(subject, ghLogin, org, jti, scope);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates an Agentweaver-minted access token and extracts its <c>jti</c> and expiry. Used by
    /// <c>/oauth/revoke</c> to denylist the token's id until natural expiry. Returns false when the
    /// token is not a valid token for this issuer/audience (revoke then no-ops on the access-token path).
    /// </summary>
    public bool TryReadJtiAndExpiry(string token, string issuer, string audience, out string? jti, out DateTimeOffset expiresAt)
    {
        jti = null;
        expiresAt = default;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var handler = new JsonWebTokenHandler();
        var result = handler.ValidateTokenAsync(token, CreateValidationParameters(issuer, audience)).GetAwaiter().GetResult();
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return false;

        jti = jwt.TryGetClaim("jti", out var jtiClaim) ? jtiClaim.Value : null;
        expiresAt = jwt.ValidTo == default ? DateTimeOffset.UtcNow.Add(AccessTokenLifetime) : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        return !string.IsNullOrEmpty(jti);
    }

    private static string ComputeKeyId(RSAParameters publicParameters)
    {
        // Deterministic kid derived from the public key material (RFC 7638-style), so the same key
        // always advertises the same kid across restarts and supports future kid-based rotation.
        var modulus = publicParameters.Modulus ?? [];
        var exponent = publicParameters.Exponent ?? [];
        var material = new byte[modulus.Length + exponent.Length];
        Buffer.BlockCopy(modulus, 0, material, 0, modulus.Length);
        Buffer.BlockCopy(exponent, 0, material, modulus.Length, exponent.Length);
        return Base64UrlEncoder.Encode(SHA256.HashData(material));
    }

    public void Dispose() => _rsa.Dispose();
}
