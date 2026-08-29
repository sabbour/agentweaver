using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Mcp;

/// <summary>
/// Validates the Microsoft Entra access token used by the browser/API session so the hosted MCP
/// endpoint can preserve that same platform identity when it forwards tool calls to the API.
/// </summary>
public sealed class McpEntraAccessTokenValidator
{
    private static readonly TimeSpan SigningKeyCacheTtl = TimeSpan.FromMinutes(15);

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpEntraAccessTokenValidator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyList<SecurityKey>? _cachedSigningKeys;
    private DateTimeOffset _cachedSigningKeysExpiresAt;

    public McpEntraAccessTokenValidator(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<McpEntraAccessTokenValidator> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string? ClientId => _configuration["Auth:Entra:ClientId"];
    private string? TenantId => _configuration["Auth:Entra:TenantId"];

    private string? Authority =>
        FirstNonWhiteSpace(
            _configuration["Auth:Entra:Authority"],
            !string.IsNullOrWhiteSpace(TenantId)
                ? $"https://login.microsoftonline.com/{TenantId}/v2.0"
                : null);

    private string? Issuer =>
        FirstNonWhiteSpace(
            _configuration["Auth:Entra:Issuer"],
            Authority);

    private bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(Issuer);

    public async Task<McpEntraIdentity?> ValidateAsync(string token, CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(token) || token.Count(c => c == '.') != 2)
            return null;

        try
        {
            var signingKeys = await GetSigningKeysAsync(forceRefresh: false, ct).ConfigureAwait(false);
            var identity = await ValidateCoreAsync(token, signingKeys).ConfigureAwait(false);
            if (identity is not null)
                return identity;

            signingKeys = await GetSigningKeysAsync(forceRefresh: true, ct).ConfigureAwait(false);
            return await ValidateCoreAsync(token, signingKeys).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Microsoft Entra token validation failed at the MCP resource server.");
            return null;
        }
    }

    private async Task<McpEntraIdentity?> ValidateCoreAsync(
        string token,
        IReadOnlyList<SecurityKey> signingKeys)
    {
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

        var objectId = result.ClaimsIdentity.FindFirst("oid")?.Value;
        var tenantId = result.ClaimsIdentity.FindFirst("tid")?.Value;
        if (string.IsNullOrWhiteSpace(objectId)
            || string.IsNullOrWhiteSpace(tenantId)
            || !string.Equals(tenantId, TenantId, StringComparison.OrdinalIgnoreCase))
            return null;

        return new McpEntraIdentity(objectId, tenantId);
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
            using var metadataClient = _httpClientFactory.CreateClient("entra-oidc");
            var metadataJson = await metadataClient
                .GetStringAsync($"{authority.TrimEnd('/')}/.well-known/openid-configuration", ct)
                .ConfigureAwait(false);
            using var metadata = System.Text.Json.JsonDocument.Parse(metadataJson);
            jwksUri = metadata.RootElement.GetProperty("jwks_uri").GetString();
        }

        if (string.IsNullOrWhiteSpace(jwksUri))
            throw new InvalidOperationException("Could not resolve Entra JWKS URI.");

        using var http = _httpClientFactory.CreateClient("entra-oidc");
        var jwksJson = await http.GetStringAsync(jwksUri, ct).ConfigureAwait(false);
        return new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray();
    }

    private static string? FirstNonWhiteSpace(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record McpEntraIdentity(string ObjectId, string TenantId);
