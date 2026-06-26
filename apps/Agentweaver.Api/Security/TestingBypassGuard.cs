namespace Agentweaver.Api.Security;

/// <summary>
/// F1 release-blocker guard. Fails the process FAST at startup if any <c>Testing:Bypass*</c> flag is
/// enabled while running under the Production environment. This makes it impossible for an injected
/// configuration value (env var, ConfigMap, Secret) to silently disable GitHub token or org
/// authorization in a production deployment: the app refuses to start instead of serving traffic with
/// authentication disabled.
///
/// The middleware constructors independently ignore the bypass outside Development; this guard is the
/// fail-fast complement so the misconfiguration surfaces loudly at boot rather than as a silent open door.
/// </summary>
public static class TestingBypassGuard
{
    /// <summary>
    /// The full set of test-only bypass flags that must never be active in Production. Add any future
    /// <c>Testing:Bypass*</c> flag here so it is automatically covered by the production guard.
    /// </summary>
    private static readonly string[] BypassFlags =
    [
        "Testing:BypassGitHubTokenAuth",
        "Testing:BypassGitHubOrgAuthorization",
    ];

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the environment is Production and any
    /// bypass flag is set to true. No-op in every other environment.
    /// </summary>
    public static void EnsureNotEnabledInProduction(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsProduction())
            return;

        var enabled = BypassFlags
            .Where(flag => configuration.GetValue<bool>(flag))
            .ToArray();

        if (enabled.Length == 0)
            return;

        throw new InvalidOperationException(
            "Refusing to start: the following test-only authentication bypass flag(s) are enabled in a " +
            $"Production environment: {string.Join(", ", enabled)}. These flags disable GitHub token " +
            "and/or organization authorization and are permitted only in Development. Remove them from " +
            "the production configuration (env vars, ConfigMap, Secret) and redeploy.");
    }
}
