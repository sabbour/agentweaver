using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

public class EntraWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    private readonly string _workspaceRoot;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;
    private readonly SigningCredentials _signingCredentials;

    public EntraWebApplicationFactory()
    {
        var unique = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"agentweaver-entra-{unique}.db");
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-entra-ws-{unique}");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"agentweaver-entra-wt-{unique}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-entra-cp-{unique}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-entra-ccp-{unique}");
        Directory.CreateDirectory(_workspaceRoot);

        _signingKey = new RsaSecurityKey(_rsa) { KeyId = $"kid-{unique}" };
        _signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
    }

    public const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string ClientId = "11111111-2222-3333-4444-555555555555";
    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public string CreateBearerToken(
        string objectId,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("oid", objectId),
            new("tid", TenantId),
            new("preferred_username", "entra.user@contoso.com"),
        };
        claims.AddRange(roles.Select(role => new Claim("roles", role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
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

    public string NewWorkingDirectory()
    {
        var dir = Path.Combine(_workspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Worktrees:BasePath"] = _worktreesPath,
                ["Checkpoints:Path"] = _checkpointsPath,
                ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                ["Auth:Entra:TenantId"] = TenantId,
                ["Auth:Entra:ClientId"] = ClientId,
                ["Auth:Entra:Issuer"] = Issuer,
                ["Auth:Entra:JwksJson"] = BuildJwksJson(),
                ["Auth:GitHub:ClientId"] = "test-github-client-id",
                ["Auth:GitHub:BaseUrl"] = "https://github.com",
                ["Auth:ApiKey"] = "internal-test-api-key",
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"] = "50",
                ["RunBounds:MaxMinutes"] = "10",
            });
        });

        builder.ConfigureServices(services =>
        {
            RemoveService<ProjectGitInitializer>(services);
            services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        _rsa.Dispose();
        var memoryDbPath = SqliteMemoryDbPathResolver.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _dbPath })
            .Build());
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", memoryDbPath, memoryDbPath + "-wal", memoryDbPath + "-shm" })
            try { File.Delete(p); } catch { }

        foreach (var dir in new[] { _workspaceRoot, _worktreesPath, _checkpointsPath, _coordinatorCheckpointsPath })
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

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

    protected static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null) services.Remove(descriptor);
    }
}
