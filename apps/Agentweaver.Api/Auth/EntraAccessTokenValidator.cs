using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Validates Microsoft Entra access tokens for single-tenant Agentweaver deployments.
/// The validator loads signing keys from the configured JWKS (or OpenID discovery) and
/// enforces issuer, audience, signature, tenant, and lifetime on every request.
/// </summary>
public sealed class EntraAccessTokenValidator
{
    private static readonly TimeSpan SigningKeyCacheTtl = TimeSpan.FromMinutes(15);

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EntraAccessTokenValidator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyList<SecurityKey>? _cachedSigningKeys;
    private DateTimeOffset _cachedSigningKeysExpiresAt;

    public EntraAccessTokenValidator(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<EntraAccessTokenValidator> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string? ClientId => _configuration["Auth:Entra:ClientId"];
    public string? TenantId =>
        EntraConfiguration.TryGetTenantId(_configuration["Auth:Entra:TenantId"], out var tenantId)
            ? tenantId
            : null;

    public string? Authority =>
        EntraConfiguration.TryGetAuthority(_configuration["Auth:Entra:Authority"], TenantId, out var authority)
            ? authority
            : null;

    public string? Issuer =>
        EntraConfiguration.TryGetIssuer(_configuration["Auth:Entra:Issuer"], Authority, out var issuer)
            ? issuer
            : null;

    public bool IsConfigured =>
        Guid.TryParseExact(ClientId, "D", out _)
        && !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(Issuer);

    public async Task<EntraAccessTokenClaims?> ValidateAsync(string token, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(token) || token.Count(c => c == '.') != 2)
            return null;

        try
        {
            var signingKeys = await GetSigningKeysAsync(forceRefresh: false, ct).ConfigureAwait(false);
            var claims = await ValidateCoreAsync(token, signingKeys, ct).ConfigureAwait(false);
            if (claims is not null)
                return claims;

            signingKeys = await GetSigningKeysAsync(forceRefresh: true, ct).ConfigureAwait(false);
            return await ValidateCoreAsync(token, signingKeys, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Microsoft Entra token validation failed.");
            return null;
        }
    }

    private async Task<EntraAccessTokenClaims?> ValidateCoreAsync(
        string token,
        IReadOnlyList<SecurityKey> signingKeys,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(ClientId))
            return null;

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = ClientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ClockSkew = TimeSpan.FromMinutes(1),
        }).ConfigureAwait(false);

        if (!result.IsValid || result.ClaimsIdentity is null)
            return null;

        var oid = result.ClaimsIdentity.FindFirst("oid")?.Value;
        var tid = result.ClaimsIdentity.FindFirst("tid")?.Value;
        if (string.IsNullOrWhiteSpace(oid)
            || string.IsNullOrWhiteSpace(tid)
            || !string.Equals(tid, TenantId, StringComparison.OrdinalIgnoreCase))
            return null;

        var allRoles = result.ClaimsIdentity.FindAll("roles").Select(x => x.Value).ToArray();
        var recognizedRoles = PlatformRoles.FilterRecognized(allRoles);
        var email = FirstNonWhiteSpace(
            result.ClaimsIdentity.FindFirst("preferred_username")?.Value,
            result.ClaimsIdentity.FindFirst(ClaimTypes.Upn)?.Value,
            result.ClaimsIdentity.FindFirst(ClaimTypes.Email)?.Value);
        var displayName = FirstNonWhiteSpace(
            result.ClaimsIdentity.FindFirst("name")?.Value,
            email,
            oid);
        if (!long.TryParse(result.ClaimsIdentity.FindFirst("exp")?.Value, out var expirationUnixSeconds))
            return null;

        return new EntraAccessTokenClaims(
            oid,
            tid,
            displayName!,
            email,
            recognizedRoles,
            allRoles,
            PlatformRoles.SelectPrimaryRole(recognizedRoles),
            DateTimeOffset.FromUnixTimeSeconds(expirationUnixSeconds));
    }

    private async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh
            && _cachedSigningKeys is not null
            && _cachedSigningKeysExpiresAt > DateTimeOffset.UtcNow)
            return _cachedSigningKeys;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh
                && _cachedSigningKeys is not null
                && _cachedSigningKeysExpiresAt > DateTimeOffset.UtcNow)
                return _cachedSigningKeys;

            var keys = await LoadSigningKeysAsync(ct).ConfigureAwait(false);
            _cachedSigningKeys = keys;
            _cachedSigningKeysExpiresAt = DateTimeOffset.UtcNow.Add(SigningKeyCacheTtl);
            return keys;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<SecurityKey>> LoadSigningKeysAsync(CancellationToken ct)
    {
        var configuredJwks = _configuration["Auth:Entra:JwksJson"];
        if (!string.IsNullOrWhiteSpace(configuredJwks))
            return new JsonWebKeySet(configuredJwks).GetSigningKeys().ToArray();

        var jwksUri = _configuration["Auth:Entra:JwksUri"];
        if (string.IsNullOrWhiteSpace(jwksUri))
        {
            var authority = Authority ?? throw new InvalidOperationException("Auth:Entra:Authority is not configured.");
            var metadataUrl = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";
            using var metadataClient = _httpClientFactory.CreateClient("entra-oidc");
            var metadataJson = await metadataClient.GetStringAsync(metadataUrl, ct).ConfigureAwait(false);
            using var metadata = System.Text.Json.JsonDocument.Parse(metadataJson);
            jwksUri = metadata.RootElement.GetProperty("jwks_uri").GetString();
        }

        if (string.IsNullOrWhiteSpace(jwksUri))
            throw new InvalidOperationException("Could not resolve Entra JWKS URI.");

        using var http = _httpClientFactory.CreateClient("entra-oidc");
        var jwksJson = await http.GetStringAsync(jwksUri, ct).ConfigureAwait(false);
        return new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray();
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}

public sealed record EntraAccessTokenClaims(
    string ObjectId,
    string TenantId,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> RecognizedRoles,
    IReadOnlyList<string> RawRoles,
    string? PrimaryRole,
    DateTimeOffset ExpiresAt);

internal static class EntraConfiguration
{
    private const string EntraAuthorityHost = "login.microsoftonline.com";

    public static bool TryGetTenantId(string? configuredTenantId, out string tenantId)
    {
        if (Guid.TryParseExact(configuredTenantId, "D", out var parsedTenantId))
        {
            tenantId = parsedTenantId.ToString("D");
            return true;
        }

        tenantId = string.Empty;
        return false;
    }

    public static bool TryGetAuthority(string? configuredAuthority, string? tenantId, out string authority)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            authority = string.Empty;
            return false;
        }

        var candidate = string.IsNullOrWhiteSpace(configuredAuthority)
            ? $"https://{EntraAuthorityHost}/{tenantId}/v2.0"
            : configuredAuthority;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString()
            || !IsPermittedAuthorityOrigin(uri)
            || !HasMatchingTenantPath(uri, tenantId))
        {
            authority = string.Empty;
            return false;
        }

        authority = $"{uri.GetLeftPart(UriPartial.Authority)}/{tenantId}/v2.0";
        return true;
    }

    public static bool TryGetIssuer(string? configuredIssuer, string? authority, out string issuer)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            issuer = string.Empty;
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuredIssuer))
        {
            issuer = authority;
            return true;
        }

        if (Uri.TryCreate(configuredIssuer, UriKind.Absolute, out var uri)
            && uri.IsWellFormedOriginalString()
            && string.Equals(uri.AbsoluteUri.TrimEnd('/'), authority, StringComparison.OrdinalIgnoreCase))
        {
            issuer = authority;
            return true;
        }

        issuer = string.Empty;
        return false;
    }

    private static bool IsPermittedAuthorityOrigin(Uri authorityUri) =>
        string.IsNullOrEmpty(authorityUri.UserInfo)
        && string.IsNullOrEmpty(authorityUri.Query)
        && string.IsNullOrEmpty(authorityUri.Fragment)
        && ((authorityUri.Scheme == Uri.UriSchemeHttps
                && authorityUri.IsDefaultPort
                && string.Equals(authorityUri.Host, EntraAuthorityHost, StringComparison.OrdinalIgnoreCase))
            || (authorityUri.Scheme == Uri.UriSchemeHttp && authorityUri.IsLoopback));

    private static bool HasMatchingTenantPath(Uri authorityUri, string tenantId)
    {
        var segments = authorityUri.Segments;
        if (segments.Length is not (2 or 3))
            return false;

        if (segments.Length == 3
            && !string.Equals(segments[2].TrimEnd('/'), "v2.0", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(
            Uri.UnescapeDataString(segments[1].TrimEnd('/')),
            tenantId,
            StringComparison.OrdinalIgnoreCase);
    }
}
