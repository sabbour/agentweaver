namespace Agentweaver.Mcp;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Validates Agentweaver-minted OAuth access tokens (signed RS256 JWTs) offline in the MCP
/// Resource Server, per the RFC 9728 design. The signing keys are fetched from the Authorization
/// Server's JWKS endpoint (<c>{issuer}/oauth/jwks</c>) and cached. A token is accepted only when its
/// signature, <c>iss</c>, <c>aud</c> (= the MCP resource <c>https://{HOST}/mcp</c>) and <c>exp</c>
/// all validate — no per-request call to the AS or GitHub is made.
/// </summary>
public sealed class McpAccessTokenValidator
{
    private const string JwksCacheKey = "mcp.oauth.jwks";

    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpAccessTokenValidator> _logger;

    public McpAccessTokenValidator(
        IConfiguration configuration,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        ILogger<McpAccessTokenValidator> logger)
    {
        _configuration = configuration;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the validated identity when <paramref name="token"/> is a well-formed, signed,
    /// non-expired Agentweaver access token bound to this MCP resource; otherwise <c>null</c>.
    /// Never throws — any validation failure is treated as "not an AS token" so the caller can fall
    /// through to the remaining authentication paths.
    /// </summary>
    public async Task<McpOAuthIdentity?> ValidateAsync(string token, HttpContext context, CancellationToken ct)
    {
        // Cheap structural reject: a JWT has exactly two dots. GitHub PATs / API keys never do, so we
        // avoid hitting the JWKS endpoint for them.
        if (string.IsNullOrEmpty(token) || token.AsSpan().Count('.') != 2)
            return null;

        var issuer = ResolveIssuer(context);
        var audience = ResolveAudience(issuer);

        var signingKeys = await GetSigningKeysAsync(issuer, ct).ConfigureAwait(false);
        if (signingKeys is null || signingKeys.Count == 0)
            return null;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        try
        {
            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
                return null;

            var sub = jwt.GetClaim("sub")?.Value;
            if (string.IsNullOrWhiteSpace(sub))
                return null;

            var ghLogin = jwt.TryGetClaim("gh_login", out var ghClaim) ? ghClaim.Value : sub;
            var jti = jwt.TryGetClaim("jti", out var jtiClaim) ? jtiClaim.Value : null;
            var org = jwt.TryGetClaim("org", out var orgClaim) ? orgClaim.Value : null;

            return new McpOAuthIdentity(sub, ghLogin, jti, org);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Access-token validation did not succeed; falling through to other auth paths.");
            return null;
        }
    }

    private async Task<ICollection<SecurityKey>?> GetSigningKeysAsync(string issuer, CancellationToken ct)
    {
        if (_cache.TryGetValue(JwksCacheKey, out ICollection<SecurityKey>? cached) && cached is not null)
            return cached;

        var jwksUri = _configuration["Auth:Mcp:JwksUri"];
        if (string.IsNullOrWhiteSpace(jwksUri))
            jwksUri = $"{issuer}/oauth/jwks";

        try
        {
            using var client = _httpClientFactory.CreateClient("oauth-jwks");
            var json = await client.GetStringAsync(jwksUri, ct).ConfigureAwait(false);
            var keySet = new JsonWebKeySet(json);
            var keys = keySet.GetSigningKeys();

            // Cache for 10 minutes; kid rotation is rare and the AS publishes new keys ahead of use.
            _cache.Set(JwksCacheKey, keys, TimeSpan.FromMinutes(10));
            return keys;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch OAuth JWKS from {JwksUri}.", jwksUri);
            return null;
        }
    }

    /// <summary>Issuer = configured <c>Auth:Mcp:Issuer</c>, else derived from the incoming request host.</summary>
    private string ResolveIssuer(HttpContext context)
    {
        var configured = _configuration["Auth:Mcp:Issuer"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        var request = context.Request;
        return $"{request.Scheme}://{request.Host.Value}";
    }

    /// <summary>Audience = configured <c>Auth:Mcp:Audience</c>, else <c>{issuer}/mcp</c> (RFC 8707).</summary>
    private string ResolveAudience(string issuer)
    {
        var configured = _configuration["Auth:Mcp:Audience"];
        return !string.IsNullOrWhiteSpace(configured) ? configured : $"{issuer}/mcp";
    }
}

/// <summary>Identity resolved from a validated Agentweaver OAuth access token.</summary>
public sealed record McpOAuthIdentity(string Subject, string GitHubLogin, string? Jti, string? Org);
