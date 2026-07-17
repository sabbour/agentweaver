using System.Diagnostics;

namespace Agentweaver.Tests.PostgresIntegration;

/// <summary>
/// Marks Postgres/Testcontainers tests as explicit Docker-dependent tests. When Docker is not
/// reachable, xUnit reports them as skipped with the environmental prerequisite instead of failing
/// every fixture during container startup.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        Skip = PostgresTestEnvironment.SkipReason;
    }
}

/// <summary>
/// Postgres/Testcontainers test that MUST run (or fail) when selected. Unlike
/// <see cref="PostgresFactAttribute"/>, this attribute never sets <see cref="FactAttribute.Skip"/>,
/// so missing/unreachable Docker fails the fixture instead of producing a skip.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresRequiredFactAttribute : FactAttribute;

internal static class PostgresTestEnvironment
{
    private static readonly Lazy<string?> _skipReason = new(DetectSkipReason);

    public static string? SkipReason => _skipReason.Value;

    private static string? DetectSkipReason()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("AGENTWEAVER_FORCE_POSTGRES_TESTCONTAINERS"), "1", StringComparison.Ordinal))
            return null;

        if (!DockerEndpointLooksAvailable())
            return "Docker/Testcontainers is required for PostgresIntegration tests but no local Docker endpoint was detected.";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return "Docker/Testcontainers is required for PostgresIntegration tests but the docker CLI could not be started.";

            if (!process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "Docker/Testcontainers is required for PostgresIntegration tests but 'docker info' timed out.";
            }

            return process.ExitCode == 0
                ? null
                : "Docker/Testcontainers is required for PostgresIntegration tests but 'docker info' could not reach a running daemon.";
        }
        catch (Exception ex)
        {
            return $"Docker/Testcontainers is required for PostgresIntegration tests but Docker is unavailable: {ex.Message}";
        }
    }

    private static bool DockerEndpointLooksAvailable()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost))
            return true;

        if (OperatingSystem.IsWindows())
            return File.Exists(@"\\.\pipe\docker_engine");

        return File.Exists("/var/run/docker.sock");
    }
}
