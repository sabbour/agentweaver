using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class BrokerRunBindingTokenValidationTests : IAsyncLifetime
{
    private const string Scheme = "BrokerRunBinding";
    private const string Policy = "BrokerRunBinding";
    private const string Issuer = "https://identity.test/";
    private const string Audience = "https://projects-private.test/";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey signingKey;
    private IHost host = null!;
    private HttpClient client = null!;
    private string? authenticationFailure;

    public BrokerRunBindingTokenValidationTests()
    {
        signingKey = new RsaSecurityKey(rsa) { KeyId = "projects-broker-test-key" };
    }

    public async Task InitializeAsync()
    {
        host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(Scheme)
                        .AddJwtBearer(Scheme, options =>
                        {
                            options.MapInboundClaims = false;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuer = Issuer,
                                ValidateAudience = true,
                                ValidAudience = Audience,
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKey = signingKey,
                                ValidateLifetime = true,
                                RequireExpirationTime = true,
                                RequireSignedTokens = true,
                                ValidTypes = [ProjectAuthorizationOwner.BrokerRunBindingTokenType],
                                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                                ClockSkew = TimeSpan.FromSeconds(5),
                            };
                            var events = BrokerRunBindingTokenValidation.CreateEvents(
                                new FixedTimeProvider(Now));
                            events.OnAuthenticationFailed = context =>
                            {
                                Volatile.Write(ref authenticationFailure, context.Exception.ToString());
                                return Task.CompletedTask;
                            };
                            options.Events = events;
                        });
                    services.AddAuthorization(options => options.AddPolicy(
                        Policy,
                        policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/proof", () => Results.Ok()).RequireAuthorization(Policy));
                });
            })
            .StartAsync();
        client = host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await host.StopAsync();
        host.Dispose();
        rsa.Dispose();
    }

    [Fact]
    public async Task AcceptsPrivateQueryTokenWithBoundedTimeProfile()
    {
        var token = CreateToken();
        var parsedToken = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.True(
            BrokerRunBindingTokenValidation.HasValidTimeProfile(parsedToken.Payload, Now),
            string.Join(
                ", ",
                parsedToken.Payload.Select(claim => $"{claim.Key}={claim.Value} ({claim.Value?.GetType().Name})")));
        using var response = await SendAsync(token);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            Volatile.Read(ref authenticationFailure) ?? response.StatusCode.ToString());
    }

    [Theory]
    [InlineData("missing-iat")]
    [InlineData("malformed-iat")]
    [InlineData("stale-iat")]
    [InlineData("future-iat")]
    [InlineData("missing-nbf")]
    [InlineData("malformed-nbf")]
    [InlineData("future-nbf")]
    [InlineData("missing-exp")]
    [InlineData("malformed-exp")]
    [InlineData("long-lifetime")]
    public async Task RejectsPrivateQueryTokensOutsideTimeProfile(string profile)
    {
        using var response = await SendAsync(CreateToken(profile));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/proof");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private string CreateToken(string profile = "valid")
    {
        var now = Now.ToUnixTimeSeconds();
        object? issuedAt = now;
        object? notBefore = now - 5;
        object? expires = now + 30;
        var includeIat = true;
        var includeNbf = true;
        var includeExp = true;
        switch (profile)
        {
            case "missing-iat":
                includeIat = false;
                break;
            case "malformed-iat":
                issuedAt = "now";
                break;
            case "stale-iat":
                issuedAt = now - 60;
                notBefore = now - 65;
                expires = now + 30;
                break;
            case "future-iat":
                issuedAt = now + 6;
                notBefore = now + 1;
                expires = now + 36;
                break;
            case "missing-nbf":
                includeNbf = false;
                break;
            case "malformed-nbf":
                notBefore = "later";
                break;
            case "future-nbf":
                notBefore = now + 6;
                break;
            case "missing-exp":
                includeExp = false;
                break;
            case "malformed-exp":
                expires = "later";
                break;
            case "long-lifetime":
                expires = now + 31;
                break;
        }

        var payload = new JwtPayload
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = ProjectAuthorizationOwner.BrokerRunBindingServiceSubject,
            ["actor_iss"] = Issuer,
            ["actor_sub"] = "7fc0a64d-a91e-4f71-88c2-32f7f8dc8531",
            ["project_id"] = "7fc0a64da91e4f7188c232f7f8dc8531",
            ["run_id"] = "run-1",
            ["purpose"] = ProjectAuthorizationOwner.BrokerRunBindingPurpose,
            ["jti"] = "0e16d39101a34613a0032f8621f843e3",
        };
        if (includeIat) payload["iat"] = issuedAt;
        if (includeNbf) payload["nbf"] = notBefore;
        if (includeExp) payload["exp"] = expires;

        var header = new JwtHeader(
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256))
        {
            ["typ"] = ProjectAuthorizationOwner.BrokerRunBindingTokenType,
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
