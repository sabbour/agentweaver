using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agentweaver.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
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
