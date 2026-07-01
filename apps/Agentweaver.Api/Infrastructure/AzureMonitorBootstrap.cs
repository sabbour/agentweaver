using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Isolated bootstrap helper so that <c>using Azure.Monitor.OpenTelemetry.AspNetCore</c> and
/// <c>using Azure.Identity</c> (which both expose <c>DefaultAzureCredential</c> via different
/// assembly versions) are never in the same compilation unit.
/// </summary>
internal static class AzureMonitorBootstrap
{
    internal static void Configure(IServiceCollection services)
    {
        services.AddOpenTelemetry().UseAzureMonitor();
    }
}
