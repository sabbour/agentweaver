using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Agentweaver.Identity;

/// <summary>Loads the configured Kestrel endpoints once and composes service-owned endpoint settings.</summary>
public static class KestrelEndpointComposition
{
    private static readonly ConditionalWeakTable<IWebHostBuilder, EndpointComposition> Compositions = new();

    /// <summary>Adds an endpoint callback to the host's shared Kestrel configuration.</summary>
    public static void AddEndpoint(
        WebApplicationBuilder builder,
        Action<KestrelConfigurationLoader, KestrelServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        Compositions.GetValue(builder.WebHost, _ => new EndpointComposition(builder)).Add(configure);
    }

    private sealed class EndpointComposition
    {
        private readonly List<Action<KestrelConfigurationLoader, KestrelServerOptions>> _callbacks = [];

        public EndpointComposition(WebApplicationBuilder builder)
        {
            builder.WebHost.ConfigureKestrel(server =>
            {
                // Load the endpoint section once so later service callbacks cannot replace earlier listeners.
                var loader = server.Configure(builder.Configuration.GetSection("Kestrel"));
                foreach (var callback in _callbacks)
                    callback(loader, server);
            });
        }

        public void Add(Action<KestrelConfigurationLoader, KestrelServerOptions> configure) =>
            _callbacks.Add(configure);
    }
}
