using System.Text.Json.Nodes;

namespace Agentweaver.Cli;

public sealed record CliTarget(
    Uri Gateway,
    Uri Issuer,
    string ClientId,
    string Audience,
    string TenantId);

public sealed record CliIdentity(
    CliTarget Target,
    IReadOnlyList<string> Scopes,
    string? ProjectId = null,
    string? RunId = null);

public sealed record CliAuthStatus(
    bool IsSignedIn,
    string? ActorId,
    CliIdentity Identity);

public enum InteractionPolicy
{
    Never,
    Allow,
}

public interface ICliAuthentication
{
    Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct);
    Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct);
    Task LogoutAsync(CliIdentity identity, CancellationToken ct);
    Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct);
}

public sealed record GatewayInvocation(
    IReadOnlyDictionary<string, string>? PathParameters = null,
    IReadOnlyDictionary<string, string>? Query = null,
    JsonNode? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public Func<string, ValueTask>? OnEventChunk { get; init; }
}

public sealed record GatewayResponse(
    int Status,
    JsonNode? Body,
    IReadOnlyDictionary<string, string> Headers,
    JsonObject Evidence,
    byte[]? BinaryBody = null);

public interface IGatewayClient
{
    CliIdentity Identity { get; }
    IReadOnlyList<JsonObject> Calls { get; }
    Task<JsonObject> DiscoverAsync(CancellationToken ct);
    Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct);
    Task<GatewayResponse> InvokeAsync(
        string operationId,
        GatewayInvocation invocation,
        CancellationToken ct);
}

public interface IGatewayClientFactory
{
    IGatewayClient Create(CliIdentity identity);
}

public sealed record ApiTestOptions(
    string InputPath,
    string OutputPath,
    bool DryRun = false,
    string? NativeProbeEnvelopePath = null,
    string? DispatchRecoveryEnvelopePath = null,
    InteractionPolicy Interaction = InteractionPolicy.Never);

public interface IApiAcceptanceRunner
{
    Task<int> RunAsync(ApiTestOptions options, CancellationToken ct);
}
