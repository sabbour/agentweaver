using System;
using System.Runtime.InteropServices;
using Agentweaver.SandboxExec;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Gate for the tests that need a real bubblewrap runtime (and therefore a real Linux
/// kernel with unprivileged user namespaces).
///
/// Every one of these tests used to return early — and therefore report success — when
/// bubblewrap was missing. That is fine on a developer's Windows box, but it silently
/// turned the bwrap-enabled CI job into a no-op if the runtime ever disappeared. The CI
/// job now sets <c>AGENTWEAVER_REQUIRE_BWRAP=1</c>, which converts "runtime missing" from
/// a silent skip into a hard failure.
/// </summary>
internal static class KataRuntimeGate
{
    /// <summary>Trait category the bwrap-enabled CI job filters on.</summary>
    public const string Category = "KataRuntime";

    public const string RequireVariable = "AGENTWEAVER_REQUIRE_BWRAP";

    /// <summary>True when this run must not skip the bubblewrap regressions.</summary>
    public static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "1", StringComparison.Ordinal);

    public static bool BwrapAvailable(out string detail)
    {
        if (!OperatingSystem.IsLinux())
        {
            detail = $"bubblewrap isolation is Linux-only; this run is on {RuntimeInformation.OSDescription}.";
            return false;
        }

        if (!KataBwrapExecutor.TryProbeAvailability(out var reason))
        {
            detail = $"bubblewrap is not usable here: {reason}";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns true when the calling test may run against a real bubblewrap runtime.
    /// Returns false only when the runtime is genuinely unavailable *and* this run does
    /// not require it; when <see cref="RequireVariable"/> is set the missing runtime
    /// fails the test instead of quietly passing it.
    /// </summary>
    public static bool Available()
    {
        if (BwrapAvailable(out var detail))
            return true;

        if (IsRequired)
            throw new InvalidOperationException(
                $"{RequireVariable}=1, so this test may not be skipped, but {detail} " +
                "Install bubblewrap (and run on Linux) or fix the runtime before re-running the gate.");

        return false;
    }
}
