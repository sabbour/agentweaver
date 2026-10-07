extern alias GatewayHost;

using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using GatewayProgram = GatewayHost::Program;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation;

namespace Agentweaver.Identity.Broker.Tests;

internal sealed class GatewayProductionResourceServer : IAsyncDisposable
{
    private readonly ProductionGatewayFactory _factory;

    private GatewayProductionResourceServer(ProductionGatewayFactory factory, HttpClient client)
    {
        _factory = factory;
        Client = client;
    }

    public HttpClient Client { get; }

    public static GatewayProductionResourceServer Start(
        SecurityKey signingKey,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> projectsOwner)
    {
        var factory = new ProductionGatewayFactory(signingKey, projectsOwner);
        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://gateway.test"),
            });
            return new GatewayProductionResourceServer(factory, client);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        _factory.RestoreEnvironment();
    }

    private sealed class ProductionGatewayFactory : WebApplicationFactory<GatewayProgram>
    {
        private readonly Dictionary<string, string?> _previousSettings = new(StringComparer.Ordinal);
        private readonly SecurityKey _signingKey;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _projectsOwner;
        private bool _restored;

        public ProductionGatewayFactory(
            SecurityKey signingKey,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> projectsOwner)
        {
            _signingKey = signingKey;
            _projectsOwner = projectsOwner;
            SetEnvironment("Identity__Issuer", IdentityBrokerWebApplicationFactory.Issuer);
            SetEnvironment("Identity__Audience", "https://api.test/");
            SetEnvironment("Gateway__Owners__Projects", "https://projects.test");
            SetEnvironment("Gateway__Owners__Orchestrator", "https://orchestrator.test");
            SetEnvironment("Gateway__Owners__Knowledge", "https://knowledge.test");
            SetEnvironment("Gateway__Owners__Events", "https://events.test");
            SetEnvironment("Gateway__OwnerRequestTimeoutSeconds", "10");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigureAll<OpenIddictValidationOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIddictConfiguration>(
                        new OpenIddictConfiguration
                        {
                            Issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer),
                            SigningKeys = { _signingKey },
                        }));
                services.Configure<HttpClientFactoryOptions>(
                    nameof(GatewayOwner.Projects),
                    options => options.HttpMessageHandlerBuilderActions.Add(
                        handlerBuilder => handlerBuilder.PrimaryHandler =
                        new ProjectsOwnerHandler(_projectsOwner)));
            });
        }

        public void RestoreEnvironment()
        {
            if (_restored)
                return;
            foreach (var (key, value) in _previousSettings)
                Environment.SetEnvironmentVariable(key, value);
            _restored = true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                RestoreEnvironment();
        }

        private void SetEnvironment(string key, string value)
        {
            _previousSettings[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class ProjectsOwnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
