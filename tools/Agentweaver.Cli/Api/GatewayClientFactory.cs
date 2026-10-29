namespace Agentweaver.Cli.Api;

public sealed class GatewayClientFactory(
    HttpClient httpClient,
    ICliAuthentication? authentication = null) : IGatewayClientFactory
{
    public IGatewayClient Create(CliIdentity identity) =>
        new GatewayClient(httpClient, identity, authentication);
}
