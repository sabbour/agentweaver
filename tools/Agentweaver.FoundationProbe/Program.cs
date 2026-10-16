extern alias AzureIdentity;

using System.Runtime.InteropServices;
using System.Text.Json;
using Agentweaver.Telemetry.AzureMonitor;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.FoundationProbe;

/// <summary>Command-line entry point for safe planning and explicit target-bound acceptance execution.</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Runs the selected command and emits stable JSON failure codes without printing credential values.</summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("""{"status":"failed","code":"cancelled"}""");
            return 130;
        }
        catch (ProbeException exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = exception.Code }, JsonOptions));
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                status = "failed",
                code = "probe_failed",
                failureType = exception.GetType().Name,
            }, JsonOptions));
            return 1;
        }
    }

    /// <summary>Sets up cancellation, source/target checks, Azure clients, tracing, and the probe runner.</summary>
    private static async Task<int> RunAsync(string[] args)
    {
        var command = ProbeCommand.Parse(args);
        if (command is ProbeCommand.Help)
        {
            Console.WriteLine("Usage: Agentweaver.FoundationProbe [--plan] | --execute --target <deployment-receipt.json>");
            return 0;
        }
        if (command is ProbeCommand.Plan)
        {
            Console.WriteLine("""{"status":"planned","operation":"foundation-probe","mutations":false}""");
            return 0;
        }
        if (command is not ProbeCommand.Execute execute)
            throw new ProbeException("usage");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        using var signal = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, signal.Token);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            signal.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            signal.Cancel();
        });

        try
        {
            var source = ProbeSource.FromAssembly();
            var target = ProbeTarget.Read(execute.TargetPath);
            ProbeTargetValidator.Validate(target, source);
            var tokenFile = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
            var identity = await WorkloadIdentityEvidence.ReadAndValidateAsync(
                target, tokenFile ?? "", clientId, tenantId, linked.Token).ConfigureAwait(false);

            var connectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING") ?? string.Empty;
            var monitorConfiguration = ProbeMonitorConfiguration.Validate(connectionString, target);

            var credential = new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
            {
                TenantId = target.TenantId,
                ClientId = target.FoundationProbeIdentity.ClientId,
                TokenFilePath = tokenFile!,
            });
            await using var operations = new AzureProbeOperations(target, credential, tokenFile!);
            var services = new ServiceCollection()
                .AddAgentweaverAzureMonitorTelemetry("foundation-probe", connectionString, credential,
                    configureTracing: ConfigureTracing)
                .BuildServiceProvider();
            try
            {
                var tracerProvider = services.GetRequiredService<TracerProvider>();
                var runner = new ProbeRunner(operations, () => tracerProvider.ForceFlush());
                var receipt = await runner.RunAsync(
                    target, source, identity, monitorConfiguration, linked.Token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(receipt, JsonOptions));
                return 0;
            }
            finally
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>Samples every trace so the probe can require a recorded span and confirm export.</summary>
    internal static void ConfigureTracing(TracerProviderBuilder tracing)
        => tracing.SetSampler(new AlwaysOnSampler());
}
