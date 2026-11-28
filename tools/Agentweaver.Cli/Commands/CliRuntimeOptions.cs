using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli;

public sealed record CliRuntimeOptions(
    HttpClient? HttpClient = null,
    IGatewayClientFactory? GatewayClientFactory = null,
    ICliAuthentication? Authentication = null,
    TextWriter? Output = null,
    TextWriter? Error = null,
    INativeRepoAppSelectionHandoff? RepositorySelection = null)
{
    internal Stream? StandardInput { get; init; }
    internal bool? StandardInputRedirected { get; init; }
    internal string? PolicyApplyStateDirectory { get; init; }
    internal Action<string>? DecisionExportStageHook { get; init; }
}
