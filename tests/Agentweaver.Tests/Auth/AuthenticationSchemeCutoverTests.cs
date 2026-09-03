using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Agentweaver.Api;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Assistant;
using Agentweaver.AgentRuntime;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentweaver.Tests.Auth;

public sealed class AuthenticationSchemeCutoverTests : IClassFixture<EntraWebApplicationFactory>
{
    private readonly EntraWebApplicationFactory _factory;

    public AuthenticationSchemeCutoverTests(EntraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task WebRole_AllEndpointBoundSchemes_AreRegistered()
    {
        var provider = _factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var names = (await provider.GetAllSchemesAsync()).Select(scheme => scheme.Name);

        names.Should().Contain(
        [
            AgentweaverAuthenticationSchemes.Composite,
            AgentweaverAuthenticationSchemes.Entra,
            AgentweaverAuthenticationSchemes.BrowserSession,
            AgentweaverAuthenticationSchemes.BrokerBearer,
            AgentweaverAuthenticationSchemes.InternalServiceKey,
            AgentweaverAuthenticationSchemes.RunCapability,
            AgentweaverAuthenticationSchemes.TestBypass,
        ]);
    }

    [Fact]
    public void DevelopmentWorkerRole_BuildsWithoutApiAuthenticationOrBrokerConfiguration()
    {
        using var factory = new WorkerWebApplicationFactory();

        factory.Services.GetService<IAuthenticationSchemeProvider>().Should().BeNull();
        factory.Services.GetService<OAuthServerConfiguration>().Should().BeNull();
        factory.Services.GetService<IOperatorAssistantBrokerTokenIssuer>().Should().BeNull();
        factory.Services.GetService<IAssistantRunService>().Should().BeNull();
        factory.Services.GetService<IOperatorAssistantAgent>().Should().BeNull();
    }

    [Fact]
    public void WebRole_BuildsCompleteAssistantIssuanceGraph()
    {
        _factory.Services.GetRequiredService<OAuthServerConfiguration>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IOperatorAssistantBrokerTokenIssuer>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IAssistantRunService>().Should().NotBeNull();
        _factory.Services.GetRequiredService<IOperatorAssistantAgent>().Should().NotBeNull();
    }

    [Fact]
    public async Task FallbackPolicy_IsDenyByDefault()
    {
        var provider = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await provider.GetFallbackPolicyAsync();
        policy.Should().NotBeNull();

        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        var identity = CallerContextClaimsAdapter.ToPrincipal(
            new Agentweaver.Api.Security.CallerContext { User = "classified-nowhere" },
            AgentweaverAuthenticationSchemes.Entra);
        (await authorization.AuthorizeAsync(identity, resource: null, policy!))
            .Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/projects/.")]
    [InlineData("/api/projects/%2e")]
    [InlineData("/api/version%2f..%2fprojects")]
    [InlineData("/api//projects")]
    public async Task AnonymousLookalikePaths_NeverReachProtectedEndpoint(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WrongIssuerAndAudience_AreRejected()
    {
        using var client = _factory.CreateClient();
        foreach (var token in new[]
        {
            _factory.CreateBearerTokenWithIssuer(
                "wrong-issuer",
                "https://sts.windows.net/foreign/",
                PlatformRoles.Contributor),
            _factory.CreateBearerTokenWithOverrides(
                "wrong-audience",
                "different-audience",
                PlatformRoles.Contributor),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        }
    }

    [Fact]
    public async Task ForgedPrivateClaims_AreOverwrittenByTheAuthenticatingScheme()
    {
        var token = _factory.CreateBearerTokenWithAdditionalClaims(
            "forged-private-claims",
            [
                new Claim(
                    AgentweaverClaimTypes.AuthenticationScheme,
                    AgentweaverAuthenticationSchemes.InternalServiceKey),
                new Claim(AgentweaverClaimTypes.InternalService, "true"),
            ],
            PlatformRoles.Contributor);
        using var scope = _factory.Services.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new EndpointAuthorizationMetadata(EndpointAuthorizationKind.AuthenticatedPlatform)),
            "forged-private-claims"));
        context.Request.Headers.Authorization = $"Bearer {token}";

        var result = await context.AuthenticateAsync(AgentweaverAuthenticationSchemes.Composite);

        result.Succeeded.Should().BeTrue();
        result.Principal!.Claims
            .Where(claim => claim.Type == AgentweaverClaimTypes.AuthenticationScheme)
            .Select(claim => claim.Value)
            .Should().Equal(AgentweaverAuthenticationSchemes.Entra);
        result.Principal.HasClaim(AgentweaverClaimTypes.InternalService, "true").Should().BeFalse();
    }

    [Fact]
    public async Task RunCapabilityCredential_IsRejectedOnUnrelatedEndpoint()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "run-capability-token");
        request.Headers.Add(RunAuthorshipHeaders.RunId, "run-id");
        request.Headers.Add(RunAuthorshipHeaders.RunToken, "run-capability-token");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ForgedHostHeaders_DoNotSteerEntraValidation()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.Host = "attacker.example";
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "attacker.example");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.CreateBearerTokenWithIssuer(
                "foreign-host",
                "https://attacker.example",
                PlatformRoles.Contributor));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer not-a-jwt")]
    public async Task PresentedMalformedCredential_ReturnsStableChallenge(string authorization)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"unauthorized\"}");
    }

    [Fact]
    public async Task AuthenticatedCallerWithoutPlatformRole_ReturnsStableForbidShape()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.CreateBearerToken("no-platform-role", "UnrecognizedRole"));

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain("recognized Agentweaver platform role");
        body.Should().Contain("roles_found_on_token");
    }

    private sealed class WorkerWebApplicationFactory : EntraWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("App:Role", AppRole.Worker);
        }
    }
}
