using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agentweaver.Telemetry;

/// <summary>Registers the shared Agentweaver OpenTelemetry sources with a service container.</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Adds tracing, metrics, and structured logging for one service. Custom callbacks add
    /// exporters or other configuration; formatted messages and scopes are disabled by default.
    /// </summary>
    /// <param name="services">The service collection receiving the telemetry providers.</param>
    /// <param name="serviceName">The service identity attached to emitted telemetry.</param>
    /// <param name="configureTracing">Optional additional tracing configuration.</param>
    /// <param name="configureMetrics">Optional additional metrics configuration.</param>
    /// <param name="configureLogging">Optional additional logging configuration.</param>
    /// <returns>The same service collection for further registration.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    /// <exception cref="ArgumentException">The service name is blank.</exception>
    public static IServiceCollection AddAgentweaverTelemetry(
        this IServiceCollection services,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<OpenTelemetryLoggerOptions>? configureLogging = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("A nonempty service name is required.", nameof(serviceName));

        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);
        services.AddOpenTelemetry()
            .ConfigureResource(builder => builder.AddService(serviceName))
            .WithTracing(builder =>
            {
                builder.AddSource(TelemetrySignals.Name);
                configureTracing?.Invoke(builder);
            })
            .WithMetrics(builder =>
            {
                builder.AddMeter(TelemetrySignals.Name);
                configureMetrics?.Invoke(builder);
            });
        services.AddLogging(builder => builder.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);
            options.IncludeFormattedMessage = false;
            options.IncludeScopes = false;
            options.ParseStateValues = false;
            configureLogging?.Invoke(options);
        }));
        return services;
    }
}
