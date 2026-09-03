using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Tests.Assistant;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Tests.Mcp;

[CollectionDefinition("McpRealProcess", DisableParallelization = true)]
public sealed class McpRealProcessCollection;

[Trait("Category", "ProcessEnvironment")]
[Collection("McpRealProcess")]
public sealed class McpBrokerRealProcessTests : IAsyncLifetime
{
    private readonly RSA _trustedRsa = RSA.Create(2048);
    private readonly RsaSecurityKey _trustedKey;
    private readonly WebApplication _authority;
    private readonly string _origin;
    private readonly int _mcpPort = GetFreeTcpPort();
    private Process? _mcpProcess;
    private string? _lastApiAuthorization;
    private string? _jwksOverride;

    public McpBrokerRealProcessTests()
    {
        _trustedKey = new RsaSecurityKey(_trustedRsa) { KeyId = "broker-test-key" };
        _origin = $"http://127.0.0.1:{GetFreeTcpPort()}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(_origin);
        _authority = builder.Build();

        var publicParameters = _trustedRsa.ExportParameters(false);
        var jwks = new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = _trustedKey.KeyId,
                    n = Base64UrlEncoder.Encode(publicParameters.Modulus),
                    e = Base64UrlEncoder.Encode(publicParameters.Exponent),
                },
            },
        };
        var metadata = new
        {
            issuer = _origin + "/",
            authorization_endpoint = _origin + "/oauth/authorize",
            token_endpoint = _origin + "/oauth/token",
            jwks_uri = _origin + "/oauth/jwks",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { SecurityAlgorithms.RsaSha256 },
        };

        _authority.MapGet("/.well-known/openid-configuration", () => Results.Json(metadata));
        _authority.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(metadata));
        _authority.MapGet("/oauth/jwks", () =>
            _jwksOverride is null
                ? Results.Json(jwks)
                : Results.Text(_jwksOverride, "application/json"));
        _authority.MapGet("/api/projects", (HttpContext context) =>
        {
            _lastApiAuthorization = context.Request.Headers.Authorization.ToString();
            return Results.Json(Array.Empty<object>());
        });
    }

    public async Task InitializeAsync()
    {
        await _authority.StartAsync();
        await StartMcpProcessAsync();
    }

    private async Task StartMcpProcessAsync()
    {
        var mcpDll = FindMcpAssemblyPath();
        var startInfo = new ProcessStartInfo("dotnet", $"\"{mcpDll}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{_mcpPort}";
        startInfo.Environment["AGENTWEAVER_API_URL"] = _origin;
        startInfo.Environment["Auth__OAuth__PublicOrigin"] = _origin;

        _mcpProcess = Process.Start(startInfo);
        _mcpProcess.Should().NotBeNull();
        _ = _mcpProcess!.StandardOutput.ReadToEndAsync();
        _ = _mcpProcess.StandardError.ReadToEndAsync();

        using var client = new HttpClient();
        var health = new Uri($"http://127.0.0.1:{_mcpPort}/healthz");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (_mcpProcess.HasExited)
                throw new InvalidOperationException($"MCP process exited with code {_mcpProcess.ExitCode}.");
            try
            {
                using var response = await client.GetAsync(health);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("MCP process did not become healthy.");
    }

    public async Task DisposeAsync()
    {
        await StopMcpProcessAsync();
        await _authority.StopAsync();
        await _authority.DisposeAsync();
        _trustedRsa.Dispose();
    }

    private async Task StopMcpProcessAsync()
    {
        if (_mcpProcess is { HasExited: false })
        {
            _mcpProcess.Kill(entireProcessTree: true);
            await _mcpProcess.WaitForExitAsync();
        }
        _mcpProcess?.Dispose();
        _mcpProcess = null;
    }

    [Fact]
    public async Task PublicMetadata_AndHealth_AdvertiseCanonicalBrokerContract()
    {
        using var client = CreateClient();
        foreach (var path in new[]
                 {
                     "/.well-known/oauth-protected-resource",
                     "/.well-known/oauth-protected-resource/mcp",
                 })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("resource").GetString().Should().Be(_origin + "/mcp");
            document.RootElement.GetProperty("authorization_servers")[0].GetString()
                .Should().Be(_origin + "/");
            document.RootElement.GetProperty("scopes_supported")[0].GetString()
                .Should().Be("mcp:invoke");
        }

        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/.well-known/oauth-protected-resource/mcp-lookalike"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MissingAndInvalidTokens_ReturnExactStandardsChallenges()
    {
        using var client = CreateClient();
        using (var missing = await PostInitializeAsync(client, token: null))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            missing.Headers.WwwAuthenticate.ToString().Should().Be(ExpectedChallenge());
        }

        using var unknownRsa = RSA.Create(2048);
        var invalidTokens = new Dictionary<string, (string Token, string Error)>
        {
            ["raw Entra"] = (CreateToken(issuer: "https://login.microsoftonline.com/tenant/v2.0"), "invalid_token"),
            ["raw GitHub"] = ("ghp_not-a-broker-token", "invalid_token"),
            ["API key"] = ("internal-api-key", "invalid_token"),
            ["wrong issuer"] = (CreateToken(issuer: "https://other.example/"), "invalid_token"),
            ["wrong audience"] = (CreateToken(audience: _origin + "/other"), "invalid_token"),
            ["expired"] = (CreateToken(expires: DateTime.UtcNow.AddMinutes(-5)), "invalid_token"),
            ["unknown key"] = (CreateToken(
                credentials: new SigningCredentials(
                    new RsaSecurityKey(unknownRsa) { KeyId = "unknown-key" },
                    SecurityAlgorithms.RsaSha256)), "invalid_token"),
            ["malformed"] = ("definitely.not.a.jwt", "invalid_token"),
            ["missing subject"] = (CreateToken(subject: null), "invalid_token"),
            ["missing kid"] = (CreateToken(
                credentials: new SigningCredentials(
                    new RsaSecurityKey(_trustedRsa),
                    SecurityAlgorithms.RsaSha256)), "invalid_token"),
            ["wrong algorithm"] = (CreateToken(
                credentials: new SigningCredentials(_trustedKey, SecurityAlgorithms.RsaSha512)), "invalid_token"),
        };

        foreach (var (tokenClass, invalid) in invalidTokens)
        {
            using var response = await PostInitializeAsync(client, invalid.Token);
            response.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                because: $"{tokenClass} credentials are outside the MCP broker trust boundary");
            response.Headers.WwwAuthenticate.ToString().Should().Be(
                ExpectedChallenge(invalid.Error),
                because: tokenClass);
        }

        using var insufficientScope = await PostInitializeAsync(
            client,
            CreateToken(scope: "project:read"));
        insufficientScope.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        insufficientScope.Headers.WwwAuthenticate.ToString().Should().Be(
            ExpectedChallenge("insufficient_scope"));
    }

    [Fact]
    public async Task ValidBrokerToken_InitializesListsToolsAndForwardsToReadOnlyApiCall()
    {
        var token = CreateToken();
        var provider = new AgentweaverMcpToolProvider(new AgentweaverMcpConnectionOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_mcpPort}/mcp"),
        });

        await using var session = await provider.ConnectAsync(token, CancellationToken.None);
        var tool = session.Tools.Single(candidate => candidate.Name == "project_list");
        var result = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        JsonSerializer.Serialize(result).Should().Contain("content");
        AuthenticationHeaderValue.TryParse(_lastApiAuthorization, out var forwarded).Should().BeTrue();
        forwarded!.Scheme.Should().Be("Bearer");
        forwarded.Parameter.Should().Be(token,
            "MCP must forward the exact validated broker token and no service credential");
    }

    [Fact]
    public async Task AssistantEndpoint_IssuesToken_ThatRealMcpAcceptsAndForwards()
    {
        var probe = new McpProbeOperatorAssistantAgent(
            new Uri($"http://127.0.0.1:{_mcpPort}/mcp"));
        await using var factory = new AssistantWebApplicationFactory
        {
            AgentOverride = probe,
            OAuthPublicOrigin = _origin,
        };
        using var apiClient = factory.CreateClient();
        apiClient.BaseAddress = new Uri(_origin);
        apiClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AssistantWebApplicationFactory.TestApiKey);

        var signingKey = factory.Services.GetRequiredService<OAuthCertificateSet>()
            .SigningKeys.OfType<X509SecurityKey>().Single();
        using (var rsa = signingKey.Certificate.GetRSAPublicKey())
        {
            var parameters = rsa!.ExportParameters(false);
            _jwksOverride = JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = SecurityAlgorithms.RsaSha256,
                        kid = signingKey.KeyId,
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent),
                    },
                },
            });
        }
        await StopMcpProcessAsync();
        await StartMcpProcessAsync();
        using var response = await apiClient.PostAsJsonAsync(
            "/api/assistant/runs",
            new { message = "list projects through MCP" });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        probe.BrokerToken.Should().NotBeNullOrWhiteSpace();
        probe.ListedProjectTool.Should().BeTrue();
        _lastApiAuthorization.Should().Be("Bearer " + probe.BrokerToken,
            "the exact server-issued credential must survive the assistant provider, MCP authentication, and backend forwarding");

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(probe.BrokerToken);
        jwt.Issuer.Should().Be(_origin + "/");
        jwt.Audiences.Should().Equal(_origin + "/mcp");
        jwt.Claims.Single(claim => claim.Type == "scope").Value.Should().Be("mcp:invoke");
        jwt.Subject.Should().Be(AssistantWebApplicationFactory.TestUser);
    }

    private HttpClient CreateClient() => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{_mcpPort}"),
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static async Task<HttpResponseMessage> PostInitializeAsync(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"auth-test","version":"1.0"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private string ExpectedChallenge(string? error = null)
    {
        var value =
            $"Bearer resource_metadata=\"{_origin}/.well-known/oauth-protected-resource/mcp\", " +
            "scope=\"mcp:invoke\"";
        return error is null ? value : value + $", error=\"{error}\"";
    }

    private string CreateToken(
        string? issuer = null,
        string? audience = null,
        string? scope = "mcp:invoke",
        string? subject = "broker-user",
        DateTime? expires = null,
        SigningCredentials? credentials = null)
    {
        var claims = new List<Claim>();
        if (subject is not null)
            claims.Add(new Claim("sub", subject));
        if (scope is not null)
            claims.Add(new Claim("scope", scope));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? _origin + "/",
            Audience = audience ?? _origin + "/mcp",
            Subject = new ClaimsIdentity(claims),
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = credentials ?? new SigningCredentials(
                _trustedKey,
                SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
        });
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindMcpAssemblyPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = directory.Name;
        var configuration = directory.Parent?.Name ?? "Debug";
        DirectoryInfo? root = directory;
        while (root is not null
               && !File.Exists(Path.Combine(
                   root.FullName, "apps", "Agentweaver.Mcp", "Agentweaver.Mcp.csproj")))
            root = root.Parent;

        root.Should().NotBeNull();
        var bin = Path.Combine(root!.FullName, "apps", "Agentweaver.Mcp", "bin");
        var exact = Path.Combine(bin, configuration, framework, "Agentweaver.Mcp.dll");
        if (File.Exists(exact))
            return exact;
        return Directory.GetFiles(bin, "Agentweaver.Mcp.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private sealed class McpProbeOperatorAssistantAgent(Uri endpoint) : IOperatorAssistantAgent
    {
        public string? BrokerToken { get; private set; }
        public bool ListedProjectTool { get; private set; }

        public async Task<OperatorAssistantResponse> RunTurnAsync(
            OperatorAssistantRequest request,
            IOperatorAssistantTurnSink? sink,
            CancellationToken ct)
        {
            BrokerToken = request.McpBrokerToken;
            var provider = new AgentweaverMcpToolProvider(
                new AgentweaverMcpConnectionOptions { Endpoint = endpoint });
            AgentweaverMcpToolSession session;
            try
            {
                session = await provider.ConnectAsync(request.McpBrokerToken, ct);
            }
            catch (HttpRequestException exception)
            {
                using var client = new HttpClient { BaseAddress = new Uri(endpoint.GetLeftPart(UriPartial.Authority)) };
                using var diagnostic = await PostInitializeAsync(client, request.McpBrokerToken);
                var jwt = new JsonWebTokenHandler().ReadJsonWebToken(request.McpBrokerToken);
                throw new InvalidOperationException(
                    $"MCP rejected the issued token with {(int)diagnostic.StatusCode}: " +
                    $"{diagnostic.Headers.WwwAuthenticate}; {await diagnostic.Content.ReadAsStringAsync(ct)}; " +
                    $"iss={jwt.Issuer}; aud={string.Join(',', jwt.Audiences)}; kid={jwt.Kid}; alg={jwt.Alg}; " +
                    $"typ={jwt.Typ}; sub={jwt.Subject}; scope={jwt.Claims.FirstOrDefault(c => c.Type == "scope")?.Value}",
                    exception);
            }
            await using var ownedSession = session;
            var tool = ownedSession.Tools.Single(candidate => candidate.Name == "project_list");
            await tool.InvokeAsync(new AIFunctionArguments(), ct);
            ListedProjectTool = true;
            return new OperatorAssistantResponse("MCP broker token accepted.", ["project_list"]);
        }
    }
}
