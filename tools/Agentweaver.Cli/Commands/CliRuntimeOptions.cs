namespace Agentweaver.Cli;

public sealed record CliRuntimeOptions(
    HttpClient? HttpClient = null,
    IGatewayClientFactory? GatewayClientFactory = null,
    ICliAuthentication? Authentication = null,
    IApiAcceptanceRunner? AcceptanceRunner = null,
    TextWriter? Output = null,
    TextWriter? Error = null);
