using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Identity;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task ProductionGatewayAcceptsOnlyGenuineBrokerTokensForItsAudience()
    {
        var validBrokerToken = await IssueTokenForAudienceBrokerAsync(
            "https://api.test/", "gateway-production-openiddict");
        var wrongAudienceBrokerToken = await IssueTokenForAudienceBrokerAsync(
            "https://other-api.test/", "gateway-wrong-audience");
        Assert.Equal(
            "https://api.test/",
            Assert.Single(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(validBrokerToken).Audiences));
        Assert.Equal(
            "https://other-api.test/",
            Assert.Single(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(wrongAudienceBrokerToken).Audiences));

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var forwardedBearerTokens = new List<string?>();
        await using var gateway = GatewayProductionResourceServer.Start(
            new X509SecurityKey(certificate),
            (request, _) =>
            {
                forwardedBearerTokens.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            });
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects");
        validRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validBrokerToken);
        using var validResponse = await gateway.Client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Equal("[]", await validResponse.Content.ReadAsStringAsync());
        Assert.Equal(["Bearer " + validBrokerToken], forwardedBearerTokens);

        using var wrongAudienceRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects");
        wrongAudienceRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", wrongAudienceBrokerToken);
        using var wrongAudienceResponse = await gateway.Client.SendAsync(wrongAudienceRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudienceResponse.StatusCode);
        Assert.Equal("application/problem+json", wrongAudienceResponse.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await wrongAudienceResponse.Content.ReadAsStringAsync());
        Assert.Equal("unauthenticated", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(["Bearer " + validBrokerToken], forwardedBearerTokens);
    }

    private async Task<string> IssueTokenForAudienceBrokerAsync(string audience, string subject)
    {
        var database = await postgres.CreateMigratedDatabaseAsync();
        await using var brokerFactory = new IdentityBrokerWebApplicationFactory(
            database,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                settings["IdentityBroker__Clients__0__Resources__0"] = audience;
                settings["IdentityBroker__SecretRedemption__Audience"] = audience;
            });

        _fakeIdp.Subject = subject;
        _fakeIdp.TenantIds = [TenantId];
        _fakeIdp.Roles = ["orchestrator"];
        using var broker = brokerFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
        var (verifier, challenge) = Pkce.Create();
        var code = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            broker,
            _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid profile email api.read offline_access",
            challenge);
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            broker,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code,
            verifier);
        return tokens.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The Identity broker did not return an access token.");
    }
}
