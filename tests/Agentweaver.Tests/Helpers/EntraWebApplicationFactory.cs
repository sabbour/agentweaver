using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

public class EntraWebApplicationFactory : ApiWebApplicationFactory
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;
    private readonly SigningCredentials _signingCredentials;

    public EntraWebApplicationFactory() : base("agentweaver-entra", createWorkspaceRoot: true)
    {
        var unique = Guid.NewGuid().ToString("N");
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = $"kid-{unique}" };
        _signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
    }

    public const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string ClientId = "11111111-2222-3333-4444-555555555555";
    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public string CreateBearerToken(
        string objectId,
        params string[] roles)
        => CreateBearerTokenWithOverrides(objectId, audience: null, roles);

    public string CreateBearerTokenWithOverrides(
        string objectId,
        string? audience = null,
        params string[] roles)
        => CreateBearerToken(objectId, audience ?? ClientId, Issuer, [], roles);

    public string CreateBearerTokenWithIssuer(
        string objectId,
        string issuer,
        params string[] roles)
        => CreateBearerToken(objectId, ClientId, issuer, [], roles);

    public string CreateBearerTokenWithAdditionalClaims(
        string objectId,
        IReadOnlyList<Claim> additionalClaims,
        params string[] roles)
        => CreateBearerToken(objectId, ClientId, Issuer, additionalClaims, roles);

    private string CreateBearerToken(
        string objectId,
        string audience,
        string issuer,
        IReadOnlyList<Claim> additionalClaims,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("oid", objectId),
            new("tid", TenantId),
            new("preferred_username", "entra.user@contoso.com"),
        };
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        claims.AddRange(additionalClaims);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(30),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = _signingCredentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public HttpClient CreateAuthenticatedClient(params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                CreateBearerToken(Guid.NewGuid().ToString(), roles));
        return client;
    }

    public HttpClient CreateAuthenticatedClientForObjectId(string objectId, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                CreateBearerToken(objectId, roles));
        return client;
    }

    public string NewWorkingDirectory() => CreateWorkspaceDirectory();

    public async Task<int> CountEntraOAuthStatesAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.EntraOAuthStates.CountAsync();
    }

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Auth:Entra:TenantId"] = TenantId;
        configuration["Auth:Entra:ClientId"] = ClientId;
        configuration["Auth:Entra:Issuer"] = Issuer;
        configuration["Auth:Entra:JwksJson"] = BuildJwksJson();
        configuration["Auth:GitHub:ClientId"] = "test-github-client-id";
        configuration["Auth:GitHub:BaseUrl"] = "https://github.com";
        configuration["Auth:ApiKey"] = "internal-test-api-key";
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        RemoveService<ProjectGitInitializer>(services);
        services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();
    }

    protected override void DisposeFixture() => _rsa.Dispose();

    protected string BuildJwksJson()
    {
        var parameters = _rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = _signingKey.KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                }
            }
        });
    }

}
