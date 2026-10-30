using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var gatewayHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var gatewayClient = new HttpClient(gatewayHandler);
        // Keep Broker OAuth transport separate so cookies or redirects never cross the Gateway boundary.
        using var brokerHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var brokerClient = new HttpClient(brokerHandler);
        var authentication = CliAuthService.CreateDefault(brokerClient);
        using var authenticationLifetime = authentication as IDisposable;
        var gatewayClients = new GatewayClientFactory(gatewayClient, authentication);
        var acceptanceRunner = new ApiAcceptanceRunner(gatewayClient, gatewayClients, authentication);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return await CliApplication.RunAsync(
            args,
            new CliRuntimeOptions(
                HttpClient: gatewayClient,
                GatewayClientFactory: gatewayClients,
                Authentication: authentication,
                AcceptanceRunner: acceptanceRunner),
            cancellation.Token).ConfigureAwait(false);
    }
}
