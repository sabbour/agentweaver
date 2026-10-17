using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Agentweaver.Telemetry.AzureMonitor;

/// <summary>Connects the shared telemetry pipeline to Azure Monitor exporters.</summary>
public static class AzureMonitorTelemetryExtensions
{
    /// <summary>
    /// Registers Agentweaver tracing, metrics, and logging with Azure Monitor exporters.
    /// The required connection string is validated and then assigned after exporter
    /// customization so callbacks cannot replace the selected destination.
    /// </summary>
    /// <param name="services">The service collection receiving telemetry providers.</param>
    /// <param name="serviceName">The service identity attached to emitted telemetry.</param>
    /// <param name="connectionString">Azure Monitor destination settings.</param>
    /// <param name="credential">Optional token credential used by the exporters.</param>
    /// <param name="configureExporter">Optional exporter-specific settings.</param>
    /// <param name="configureTracing">Optional additional tracing configuration.</param>
    /// <param name="configureMetrics">Optional additional metrics configuration.</param>
    /// <param name="configureLogging">Optional additional logging configuration.</param>
    /// <returns>The same service collection after shared telemetry registration.</returns>
    /// <exception cref="ArgumentException">The connection string is absent or malformed.</exception>
    public static IServiceCollection AddAgentweaverAzureMonitorTelemetry(
        this IServiceCollection services,
        string serviceName,
        string connectionString,
        TokenCredential? credential = null,
        Action<AzureMonitorExporterOptions>? configureExporter = null,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<OpenTelemetryLoggerOptions>? configureLogging = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ValidateConnectionString(connectionString);

        void Configure(AzureMonitorExporterOptions options)
        {
            configureExporter?.Invoke(options);
            options.ConnectionString = connectionString;
            options.Credential = credential;
        }

        return services.AddAgentweaverTelemetry(
            serviceName,
            tracing =>
            {
                tracing.AddAzureMonitorTraceExporter(Configure);
                configureTracing?.Invoke(tracing);
            },
            metrics =>
            {
                metrics.AddAzureMonitorMetricExporter(Configure);
                configureMetrics?.Invoke(metrics);
            },
            logging =>
            {
                logging.AddAzureMonitorLogExporter(Configure);
                configureLogging?.Invoke(logging);
            });
    }

    /// <summary>Rejects malformed or duplicate fields and requires a GUID instrumentation key.</summary>
    private static void ValidateConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("An Azure Monitor connection string is required.", nameof(connectionString));

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instrumentationKey = "";
        foreach (var part in parts)
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
                throw new ArgumentException("Invalid Azure Monitor connection string.", nameof(connectionString));
            var key = part[..separator].Trim();
            if (!keys.Add(key))
                throw new ArgumentException("Duplicate Azure Monitor connection string field.", nameof(connectionString));
            if (key.Equals("InstrumentationKey", StringComparison.OrdinalIgnoreCase))
                instrumentationKey = part[(separator + 1)..].Trim();
        }
        if (parts.Length == 0 || !Guid.TryParse(instrumentationKey, out _))
            throw new ArgumentException("An Azure Monitor instrumentation key is required.", nameof(connectionString));
    }
}
