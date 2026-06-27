namespace Agentweaver.Api;

/// <summary>
/// Deployment role for this process instance.
/// Read from config key <c>App:Role</c> (env var <c>App__Role</c>).
/// </summary>
public static class AppRole
{
    /// <summary>HTTP API + SSE + frontend. Default when App:Role is unset.</summary>
    public const string Web = "web";

    /// <summary>Background processing loops (coordinator heartbeat, pickup, GC). No public HTTP.</summary>
    public const string Worker = "worker";

    public static string Resolve(IConfiguration configuration) =>
        configuration["App:Role"]?.ToLowerInvariant() switch
        {
            Worker => Worker,
            _ => Web
        };

    public static bool IsWorker(IConfiguration configuration) => Resolve(configuration) == Worker;

    public static bool IsWeb(IConfiguration configuration) => Resolve(configuration) == Web;
}
