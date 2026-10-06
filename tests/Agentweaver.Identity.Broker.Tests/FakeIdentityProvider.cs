using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>
/// A real, in-process OpenID Connect authorization server used as the broker's external
/// federation provider in tests, backed by <see cref="TestServer"/> — not a hand-waved stub.
/// It auto-approves every authorization request (there is no human in a test) but otherwise
/// performs a real code exchange and signs real JWT id_tokens with a real RSA key, so the
/// broker's own OIDC middleware (not test code) is what validates issuer, signature, audience,
/// and expiry. Each test controls exactly one axis of malice via <see cref="IdTokenTampering"/>
/// to prove the broker's real-middleware validation fails closed.
/// </summary>
public sealed class FakeIdentityProvider : IAsyncDisposable
{
    public const string Authority = "https://fake-idp.test";

    private IHost _host = null!;
    private readonly RsaSecurityKey _signingKey;
    private readonly RsaSecurityKey _attackerKey;
    private readonly Dictionary<string, PendingCode> _codes = new();
    public List<string> SensitiveValues { get; } = [];
    public List<(bool HasClientSecret, string? ClientSecret, bool HasCodeVerifier)> TokenRequests { get; } = [];

    public IdTokenTampering Tampering { get; set; } = IdTokenTampering.None;

    /// <summary>Overrides the subject claim minted into the next id_token(s).</summary>
    public string Subject { get; set; } = "external-subject-1";

    public IReadOnlyList<string> TenantIds { get; set; } = [];

    public IReadOnlyList<string> Roles { get; set; } = [];

    public string? Email { get; set; } = "user@example.test";

    public string? Name { get; set; } = "Test User";

    private FakeIdentityProvider(RsaSecurityKey signingKey, RsaSecurityKey attackerKey)
    {
        _signingKey = signingKey;
        _attackerKey = attackerKey;
    }

    public TestServer Server => _host.GetTestServer();

    public static async Task<FakeIdentityProvider> StartAsync()
    {
        using var signingRsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(signingRsa.ExportParameters(includePrivateParameters: true))
        {
            KeyId = "fake-idp-signing-key",
        };
        using var attackerRsa = RSA.Create(2048);
        var attackerKey = new RsaSecurityKey(attackerRsa.ExportParameters(includePrivateParameters: true))
        {
            KeyId = "attacker-key",
        };

        var provider = new FakeIdentityProvider(signingKey, attackerKey);
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app => app.Run(provider.HandleRequestAsync));
            });

        provider._host = await hostBuilder.StartAsync();
        return provider;
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        {
            var path = context.Request.Path.Value ?? string.Empty;
            switch (path)
            {
                case "/.well-known/openid-configuration":
                    await WriteJsonAsync(context, new
                    {
                        issuer = Authority,
                        authorization_endpoint = $"{Authority}/connect/authorize",
                        token_endpoint = $"{Authority}/connect/token",
                        jwks_uri = $"{Authority}/connect/jwks",
                        response_types_supported = new[] { "code" },
                        subject_types_supported = new[] { "public" },
                        id_token_signing_alg_values_supported = new[] { "RS256" },
                        scopes_supported = new[] { "openid", "profile", "email" },
                        claims_supported = new[] { "sub", "name", "email", "tid", "roles" },
                        code_challenge_methods_supported = new[] { "S256" },
                        grant_types_supported = new[] { "authorization_code" },
                    });
                    return;

                case "/connect/jwks":
                    var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_signingKey);
                    jwk.Use = "sig";
                    jwk.Alg = "RS256";
                    await WriteJsonAsync(context, new { keys = new[] { PublicJwk(jwk) } });
                    return;

                case "/connect/authorize":
                    HandleAuthorize(context);
                    return;

                case "/connect/token":
                    await HandleTokenAsync(context);
                    return;

                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
            }
        }
    }

    private void HandleAuthorize(HttpContext context)
    {
        var query = context.Request.Query;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();
        var state = query["state"].ToString();
        var nonce = query["nonce"].ToString();
        var codeChallenge = query["code_challenge"].ToString();
        var codeChallengeMethod = query["code_challenge_method"].ToString();

        var code = Guid.NewGuid().ToString("N");
        SensitiveValues.Add(code);
        _codes[code] = new PendingCode(clientId, redirectUri, nonce, codeChallenge, codeChallengeMethod,
            Tampering, Subject, Email, Name, [.. TenantIds], [.. Roles]);

        var location = $"{redirectUri}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";
        context.Response.Redirect(location);
    }

    private async Task HandleTokenAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        var clientSecret = form["client_secret"].ToString();
        TokenRequests.Add((
            form.ContainsKey("client_secret"),
            string.IsNullOrEmpty(clientSecret) ? null : clientSecret,
            form.ContainsKey("code_verifier")));
        var code = form["code"].ToString();
        var codeVerifier = form["code_verifier"].ToString();

        if (!_codes.Remove(code, out var pending))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonAsync(context, new { error = "invalid_grant" });
            return;
        }

        if (!string.IsNullOrEmpty(pending.CodeChallenge))
        {
            var computed = Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier)));
            if (!string.Equals(computed, pending.CodeChallenge, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(context, new { error = "invalid_grant", error_description = "PKCE verification failed." });
                return;
            }
        }

        var idToken = MintIdToken(pending);
        var accessToken = "fake-idp-access-token-" + Guid.NewGuid().ToString("N");
        SensitiveValues.Add(idToken);
        SensitiveValues.Add(accessToken);
        await WriteJsonAsync(context, new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = 3600,
            id_token = idToken,
        });
    }

    private string MintIdToken(PendingCode pending)
    {
        var now = DateTime.UtcNow;
        var expires = pending.Tampering == IdTokenTampering.Expired
            ? now.AddMinutes(-10)
            : now.AddMinutes(10);
        var issuer = pending.Tampering == IdTokenTampering.WrongIssuer
            ? "https://attacker-idp.test"
            : Authority;
        var signingKey = pending.Tampering == IdTokenTampering.WrongSignature ? _attackerKey : _signingKey;

        var claims = new List<Claim>
        {
            new("sub", pending.Subject),
            new("aud", pending.Tampering == IdTokenTampering.WrongAudience ? "wrong-client" : pending.ClientId),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };
        if (pending.Name is not null) claims.Add(new Claim("name", pending.Name));
        if (pending.Email is not null) claims.Add(new Claim("email", pending.Email));
        claims.AddRange(pending.TenantIds.Select(tenantId => new Claim("tid", tenantId)));
        claims.AddRange(pending.Roles.Select(role => new Claim("roles", role)));
        if (!string.IsNullOrEmpty(pending.Nonce) && pending.Tampering != IdTokenTampering.MissingNonce)
            claims.Add(new Claim("nonce", pending.Tampering == IdTokenTampering.WrongNonce ? "wrong-nonce" : pending.Nonce));

        var handler = new JwtSecurityTokenHandler();
        var token = new JwtSecurityToken(
            issuer: issuer,
            claims: claims,
            notBefore: now.AddHours(-1),
            expires: expires,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return handler.WriteToken(token);
    }

    private static object PublicJwk(Microsoft.IdentityModel.Tokens.JsonWebKey jwk) => new
    {
        kty = jwk.Kty,
        use = jwk.Use,
        alg = jwk.Alg,
        kid = jwk.Kid,
        n = jwk.N,
        e = jwk.E,
    };

    private static async Task WriteJsonAsync(HttpContext context, object value)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(value));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed record PendingCode(
        string ClientId, string RedirectUri, string Nonce, string CodeChallenge, string CodeChallengeMethod,
        IdTokenTampering Tampering, string Subject, string? Email, string? Name,
        string[] TenantIds, string[] Roles);
}

public enum IdTokenTampering
{
    None,
    WrongIssuer,
    WrongSignature,
    Expired,
    WrongAudience,
    WrongNonce,
    MissingNonce,
}
