using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Entra web factory pre-wired for the interactive browser sign-in redirect flow
/// (<c>/auth/entra/authorize</c> + <c>/auth/entra/callback</c>). Adds the confidential-client
/// <c>ClientSecret</c>, a registered <c>RedirectUri</c>, and a frontend URL on top of
/// <see cref="EntraWebApplicationFactory"/>, and replaces the <c>entra-oidc</c> HttpClient with a
/// stub that answers Microsoft's token endpoint with an access token this factory itself signs — so
/// the token the callback validates is accepted by the same JWKS the API is configured with.
/// </summary>
public sealed class EntraSignInWebApplicationFactory : EntraWebApplicationFactory
{
    public const string ClientSecretValue = "test-entra-client-secret";
    public const string RedirectUriValue = "http://localhost:5000/auth/entra/callback";
    public const string FrontendUrlValue = "http://localhost:5173";

    /// <summary>The Entra object id the stubbed token endpoint mints an access token for.</summary>
    public string SignedInObjectId { get; } = Guid.NewGuid().ToString();

    /// <summary>The App Role embedded in the minted access token.</summary>
    public string SignedInRole { get; } = PlatformRoles.Contributor;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Entra:ClientSecret"] = ClientSecretValue,
                ["Auth:Entra:RedirectUri"] = RedirectUriValue,
                ["Auth:Entra:FrontendUrl"] = FrontendUrlValue,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Registered last, so this primary handler wins for the "entra-oidc" named client and the
            // token-redemption POST never leaves the process.
            services.AddHttpClient("entra-oidc")
                .ConfigurePrimaryHttpMessageHandler(() => new TokenEndpointHandler(this));
        });
    }

    /// <summary>Stub Microsoft token endpoint: returns an access token signed by this factory's key.</summary>
    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        private readonly EntraSignInWebApplicationFactory _factory;

        public TokenEndpointHandler(EntraSignInWebApplicationFactory factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                var jwt = _factory.CreateBearerToken(_factory.SignedInObjectId, _factory.SignedInRole);
                var json =
                    $$"""{"token_type":"Bearer","expires_in":3600,"access_token":"{{jwt}}","id_token":"{{jwt}}"}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            // Any other entra-oidc call (not expected — JWKS is provided inline) returns empty OK.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
