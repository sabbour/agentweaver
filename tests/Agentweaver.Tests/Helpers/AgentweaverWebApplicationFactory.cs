namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Configures the API for integration tests: injects a unique temp SQLite
/// database per factory instance and a known test API key so HTTP tests can
/// authenticate without any real secrets.
/// </summary>
public sealed class AgentweaverWebApplicationFactory : ApiWebApplicationFactory
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestUser = "test-user";

    private readonly bool _bypassAuthentication;

    public AgentweaverWebApplicationFactory() : this(bypassAuthentication: true)
    {
    }

    internal AgentweaverWebApplicationFactory(bool bypassAuthentication) : base("agentweaver-waf")
    {
        _bypassAuthentication = bypassAuthentication;
    }

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Testing:BypassGitHubOrgAuthorization"] = "true";
        configuration["Testing:BypassGitHubTokenAuth"] = _bypassAuthentication.ToString();
        configuration["Auth:Mode"] = "GitHubLegacy";
        configuration["Auth:ApiKey"] = TestApiKey;
        configuration["Auth:User"] = TestUser;
        configuration["Auth:OAuth:DynamicRegistration:RequestsPerMinute"] = "100";
    }
}
