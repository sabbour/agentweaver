extern alias GatewayHost;

using System.Text;
using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using GatewayProgram = GatewayHost::Program;
using Microsoft.AspNetCore.Builder;
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

    public HttpMessageHandler CreateHandler() => _factory.Server.CreateHandler();

    public static GatewayProductionResourceServer Start(
        SecurityKey signingKey,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> projectsOwner,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? eventsOwner = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? knowledgeOwner = null,
        int ownerRequestTimeoutSeconds = 10,
        Action<string>? observeSseData = null)
    {
        var factory = new ProductionGatewayFactory(
            signingKey, projectsOwner, eventsOwner, knowledgeOwner, ownerRequestTimeoutSeconds, observeSseData);
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
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _eventsOwner;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _knowledgeOwner;
        private readonly Action<string>? _observeSseData;
        private bool _restored;

        public ProductionGatewayFactory(
            SecurityKey signingKey,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> projectsOwner,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? eventsOwner,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? knowledgeOwner,
            int ownerRequestTimeoutSeconds,
            Action<string>? observeSseData)
        {
            _signingKey = signingKey;
            _projectsOwner = projectsOwner;
            _eventsOwner = eventsOwner;
            _knowledgeOwner = knowledgeOwner;
            _observeSseData = observeSseData;
            SetEnvironment("Identity__Issuer", IdentityBrokerWebApplicationFactory.Issuer);
            SetEnvironment("Identity__Audience", "https://api.test/");
            SetEnvironment("Gateway__Owners__Projects", "https://projects.test");
            SetEnvironment("Gateway__Owners__Orchestrator", "https://orchestrator.test");
            SetEnvironment("Gateway__Owners__Knowledge", "https://knowledge.test");
            SetEnvironment("Gateway__Owners__Events", "https://events.test");
            SetEnvironment("Gateway__OwnerRequestTimeoutSeconds", ownerRequestTimeoutSeconds.ToString());
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
                        new OwnerHandler(_projectsOwner)));
                if (_eventsOwner is not null)
                    services.Configure<HttpClientFactoryOptions>(
                        nameof(GatewayOwner.Events),
                        options => options.HttpMessageHandlerBuilderActions.Add(
                            handlerBuilder => handlerBuilder.PrimaryHandler =
                            new OwnerHandler(_eventsOwner)));
                if (_knowledgeOwner is not null)
                    services.Configure<HttpClientFactoryOptions>(
                        nameof(GatewayOwner.Knowledge),
                        options => options.HttpMessageHandlerBuilderActions.Add(
                        handlerBuilder => handlerBuilder.PrimaryHandler =
                            new OwnerHandler(_knowledgeOwner)));
                if (_observeSseData is not null)
                    services.AddSingleton<IStartupFilter>(
                        new SseObservationStartupFilter(_observeSseData));
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

    private sealed class OwnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class SseObservationStartupFilter(Action<string> observe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextRequest) =>
                {
                    if (context.Request.Path.Value?.EndsWith("/events/live", StringComparison.Ordinal) != true)
                    {
                        await nextRequest();
                        return;
                    }

                    var originalBody = context.Response.Body;
                    context.Response.Body = new SseObservationStream(originalBody, observe);
                    try
                    {
                        await nextRequest();
                    }
                    finally
                    {
                        context.Response.Body = originalBody;
                    }
                });
                next(app);
            };
    }

    private sealed class SseObservationStream(Stream inner, Action<string> observe) : MemoryStream
    {
        private string _pending = string.Empty;

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Observe(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Observe(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Observe(buffer.Span);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken);
            Observe(buffer.AsSpan(offset, count));
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        private void Observe(ReadOnlySpan<byte> bytes)
        {
            _pending += Encoding.UTF8.GetString(bytes);
            var separator = _pending.IndexOf("\n\n", StringComparison.Ordinal);
            while (separator >= 0)
            {
                var block = _pending[..separator];
                _pending = _pending[(separator + 2)..];
                foreach (var line in block.Split('\n'))
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                        observe(line["data: ".Length..]);
                separator = _pending.IndexOf("\n\n", StringComparison.Ordinal);
            }
        }
    }
}
