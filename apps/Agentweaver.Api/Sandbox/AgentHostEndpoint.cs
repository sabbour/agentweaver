namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Single source of truth for building the per-run AgentHost A2A endpoint URL (spec-018 P1.5).
/// Shared by <see cref="KubernetesSandboxExecutor"/> (which registers the endpoint at claim
/// time) and <see cref="KubernetesPodAgentEndpointResolver"/> (which rebuilds it from the live
/// pod IP at <c>SetupAsync</c> time) so the two cannot drift on scheme/host/port/path.
///
/// <para>
/// The scheme is derived from <c>requireMtls</c>: <c>https</c> (mTLS, production default) or
/// <c>http</c> (PoC only, <c>Sandbox:AgentHost:RequireMtls=false</c>).
/// </para>
/// </summary>
internal static class AgentHostEndpoint
{
    /// <summary>Returns the URL scheme for the A2A endpoint given the mTLS requirement.</summary>
    public static string Scheme(bool requireMtls) => requireMtls ? "https" : "http";

    /// <summary>Builds the full A2A endpoint URL <c>scheme://host:port/path</c>.</summary>
    /// <remarks>
    /// When <paramref name="path"/> is <see langword="null"/> or empty the URL is the bare AgentHost
    /// ORIGIN (<c>scheme://host:port</c>) with NO trailing path — used for the root-mounted
    /// <c>/preview-runner/*</c> endpoints, which must NOT carry the A2A path segment.
    /// </remarks>
    public static string Build(bool requireMtls, string host, int port, string? path) =>
        string.IsNullOrEmpty(path)
            ? $"{Scheme(requireMtls)}://{host}:{port}"
            : $"{Scheme(requireMtls)}://{host}:{port}{path}";
}
