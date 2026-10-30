using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli.Acceptance.TestHost;

/// <summary>Identifies the supplemental test host, not the distributable CLI apphost.</summary>
public sealed class TestHostMarker { }

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        });
        var authentication = new ControlledAuthentication();
        var clients = new GatewayClientFactory(http, authentication);
        // Test-only providers keep this process isolated while exercising the production CLI, runner, and HTTP client path.
        var runner = new ApiAcceptanceRunner(
            clients,
            authentication,
            new ControlledDeploymentEvidenceProvider(),
            Console.Out);
        return await CliApplication.RunAsync(args, new CliRuntimeOptions(
            HttpClient: http,
            GatewayClientFactory: clients,
            Authentication: authentication,
            AcceptanceRunner: runner));
    }

    private sealed class ControlledAuthentication : ICliAuthentication
    {
        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "owner", identity));

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "owner", identity));

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetBearerAsync(
            CliIdentity identity,
            InteractionPolicy interaction,
            CancellationToken ct)
        {
            string actor = identity.Target.TenantId == "tenant-denied" ? "denied" : "owner";
            string scopes = string.Join('+', identity.Scopes.Order(StringComparer.Ordinal));
            return Task.FromResult(string.Join('~', actor, identity.Target.TenantId,
                identity.ProjectId ?? "-", identity.RunId ?? "-", scopes));
        }
    }

    private sealed class ControlledDeploymentEvidenceProvider : IAcceptanceDeploymentEvidenceProvider
    {
        public Task<JsonObject> PrepareAsync(AcceptanceManifest manifest, CancellationToken ct) =>
            Task.FromResult(new JsonObject
            {
                ["source"] = new JsonObject
                {
                    ["sourceSha"] = manifest.Pins["sourceReceipt"]!["sourceSha"]!.DeepClone(),
                    ["sourceTree"] = manifest.Pins["sourceReceipt"]!["sourceTree"]!.DeepClone(),
                },
                ["services"] = new JsonObject { ["status"] = "blocked" },
                ["agentHostImageMeasurement"] = new JsonObject { ["status"] = "blocked" },
            });
    }
}
