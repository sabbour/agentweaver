using System.Text.Json.Nodes;

namespace Agentweaver.Cli;

/// <summary>Configured origins and OAuth resource selectors for one CLI sign-in.</summary>
/// <param name="Gateway">HTTPS origin used for API requests.</param>
/// <param name="Issuer">HTTPS origin of the Identity Broker.</param>
/// <param name="ClientId">Registered public OAuth client identifier.</param>
/// <param name="Audience">Configured API audience selector used to isolate the stored sign-in.</param>
/// <param name="TenantId">Tenant selector; owners still determine current membership and authority.</param>
public sealed record CliTarget(
    Uri Gateway,
    Uri Issuer,
    string ClientId,
    string Audience,
    string TenantId);

/// <summary>OAuth scopes and optional exact project/run binding used for a CLI operation.</summary>
/// <remarks>A project binding is paired with its run binding; neither selector grants authority by itself.</remarks>
public sealed record CliIdentity(
    CliTarget Target,
    IReadOnlyList<string> Scopes,
    string? ProjectId = null,
    string? RunId = null);

/// <summary>Local sign-in state for the selected Broker, Gateway, tenant, scopes, and binding.</summary>
public sealed record CliAuthStatus(
    bool IsSignedIn,
    string? ActorId,
    CliIdentity Identity);

/// <summary>Controls whether token acquisition may start an interactive browser sign-in.</summary>
public enum InteractionPolicy
{
    /// <summary>Do not open a browser; report that sign-in is required.</summary>
    Never,

    /// <summary>Allow browser sign-in only when a usable local sign-in is unavailable.</summary>
    Allow,
}

/// <summary>Owns local Broker sign-in, token refresh, and sign-in state for a CLI identity.</summary>
public interface ICliAuthentication
{
    Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct);
    Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct);

    /// <summary>Removes the selected local sign-in; it does not revoke a Broker session remotely.</summary>
    Task LogoutAsync(CliIdentity identity, CancellationToken ct);

    /// <summary>Gets a short-lived access token, optionally starting browser sign-in if needed.</summary>
    Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct);
}

/// <summary>Caller-supplied path, query, header, and JSON body for one Gateway request.</summary>
public sealed record GatewayInvocation(
    IReadOnlyDictionary<string, string>? PathParameters = null,
    IReadOnlyDictionary<string, string>? Query = null,
    JsonNode? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>Receives complete SSE frames after best-effort redaction, not arbitrary network chunks.</summary>
    public Func<string, ValueTask>? OnEventChunk { get; init; }
}

/// <summary>Gateway result; response data and diagnostic evidence have different handling.</summary>
/// <remarks>
/// <see cref="Body"/> and <see cref="BinaryBody"/> contain response data and may be sensitive.
/// <see cref="Evidence"/> applies diagnostic redaction filters; do not treat it as raw output or complete secret scrubbing.
/// </remarks>
public sealed record GatewayResponse(
    int Status,
    JsonNode? Body,
    IReadOnlyDictionary<string, string> Headers,
    JsonObject Evidence,
    byte[]? BinaryBody = null)
{
    public bool HasBody { get; init; }
}

/// <summary>Discovers, validates, and invokes Gateway operations and guarded same-origin requests.</summary>
public interface IGatewayClient
{
    /// <summary>The selected identity used for calls; it is not itself proof of current owner authority.</summary>
    CliIdentity Identity { get; }

    /// <summary>Request diagnostics captured after the client's redaction filters.</summary>
    /// <remarks>Each read is a deep copy; redaction does not guarantee that arbitrary sensitive data was removed.</remarks>
    IReadOnlyList<JsonObject> Calls { get; }

    /// <summary>Discovers and validates the supported live Gateway contract.</summary>
    Task<JsonObject> DiscoverAsync(CancellationToken ct);

    /// <summary>Checks the Gateway's current issuer, tenant, actor, and exact selection context.</summary>
    Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct);

    /// <summary>Invokes a live-contract operation after validating the selected authorization context.</summary>
    Task<GatewayResponse> InvokeAsync(
        string operationId,
        GatewayInvocation invocation,
        CancellationToken ct);

    /// <summary>Sends an explicit same-origin request without OpenAPI dispatch or dual-credential escalation.</summary>
    Task<GatewayResponse> InvokeRawAsync(
        string method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string>? headers,
        bool outputRequested,
        Func<string, ValueTask>? onEventChunk,
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
