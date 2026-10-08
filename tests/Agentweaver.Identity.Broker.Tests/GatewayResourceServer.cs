extern alias GatewayHost;

using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using GatewayOptions = GatewayHost::Agentweaver.Gateway.GatewayOptions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Identity.Broker.Tests;

internal sealed class GatewayResourceServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private GatewayResourceServer(WebApplication app)
    {
        _app = app;
        _client = app.GetTestClient();
        _client.BaseAddress = new Uri("https://gateway.test");
    }

    public HttpClient Client => _client;

    public IRequestSizeLimitMetadata? GetRequestSizeLimit(string routePattern) =>
        ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == routePattern)
            .Metadata.GetMetadata<IRequestSizeLimitMetadata>();

    public static async Task<GatewayResourceServer> StartAsync(
        SecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandlerFactory,
        Func<GatewayOwner, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ownerHandler,
        Action<HttpRequestMessage>? observeProjectsRequest = null,
        TimeSpan? ownerRequestTimeout = null)
    {
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer);
        var audience = "https://api.test";
        var options = new GatewayOptions(
            issuer,
            audience,
            new Uri("https://projects.test/"),
            new Uri("https://orchestrator.test/"),
            new Uri("https://knowledge.test/"),
            new Uri("https://events.test/"),
            new Uri("https://identity-broker.test/"),
            ownerRequestTimeout ?? TimeSpan.FromSeconds(10));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(options);
        builder.Services.AddHttpClient(nameof(GatewayOwner.Projects), client =>
                client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = projectsHandlerFactory();
                return observeProjectsRequest is null
                    ? handler
                    : new ObservingHandler(handler, observeProjectsRequest);
            });
        foreach (var owner in Enum.GetValues<GatewayOwner>().Where(owner => owner != GatewayOwner.Projects))
        {
            builder.Services.AddHttpClient(owner.ToString(), client =>
                    client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new GatewayOwnerHttpHandler(owner, ownerHandler));
        }

        builder.Services.AddSingleton<GatewayHost::Agentweaver.Gateway.GatewayOwnerClient>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(authentication =>
            {
                authentication.MapInboundClaims = false;
                authentication.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer.AbsoluteUri,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        GatewayHost::Agentweaver.Gateway.GatewayEndpoints.MapGatewayEndpoints(app);
        await app.StartAsync();
        return new GatewayResourceServer(app);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class GatewayOwnerHttpHandler(
        GatewayOwner owner,
        Func<GatewayOwner, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(owner, request, cancellationToken);
    }

    private sealed class ObservingHandler(
        HttpMessageHandler innerHandler,
        Action<HttpRequestMessage> observe) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            observe(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
