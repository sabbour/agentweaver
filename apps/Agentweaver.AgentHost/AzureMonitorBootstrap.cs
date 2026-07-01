using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace Agentweaver.AgentHost;

/// <summary>
/// Isolated bootstrap helper so that <c>using Azure.Monitor.OpenTelemetry.Exporter</c> and
/// <c>using Azure.Identity</c> (which both expose <c>DefaultAzureCredential</c> via different
/// assembly versions) are never in the same compilation unit.
/// </summary>
internal static class AzureMonitorBootstrap
{
    internal static void Configure(IServiceCollection services, ILoggingBuilder logging)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("agentweaver-agent-host"))
            .WithTracing(t => t
                .AddSource("Microsoft.Extensions.AI")
                .AddSource("OpenAI")
                .AddSource("Azure.AI.OpenAI")
                .AddAzureMonitorTraceExporter())
            .WithMetrics(m => m
                .AddMeter("Microsoft.Extensions.AI")
                .AddMeter("OpenAI")
                .AddMeter("Azure.AI.OpenAI")
                .AddAzureMonitorMetricExporter());
        logging.AddOpenTelemetry(o => o.AddAzureMonitorLogExporter());
    }
}
