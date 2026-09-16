using Agentweaver.Api.Security;
using Agentweaver.Api.Auth;
namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory that registers two API keys so review-endpoint
/// ownership tests can submit as one user and attempt to review as another,
/// exercising the 403 Forbidden path without mocking identity.
/// </summary>
public sealed class ReviewWebApplicationFactory : ApiWebApplicationFactory
{
    public const string OwnerApiKey = "review-test-owner-key-12345";
    public const string OwnerUser   = "review-owner-user";
    public const string OtherApiKey = "review-test-other-key-99999";
    public const string OtherUser   = "review-other-user";
    public const string InternalServiceApiKey = "review-test-internal-service-key";

    public ReviewWebApplicationFactory() : base("agentweaver-rv")
    {
        // Program.cs computes SandboxAgentOptions.RequireMtls from builder.Configuration
        // *before* builder.Build() runs, at the top level of the minimal-hosting Program.cs.
        // WebApplicationFactory's ConfigureWebHost/ConfigureAppConfiguration additions (see
        // below) are only visible to configuration reads that happen at/after Build() -- they
        // do NOT reach this early read. Environment variables, in contrast, are loaded by
        // WebApplication.CreateBuilder(args) itself, so they ARE visible to that early read.
        // This fixture is the only one whose tests actually resolve the named
        // "a2a-sandbox-pod"/streaming HttpClients (A2ATransportTimeoutTests), which triggers
        // AgentHostMtlsClientHandler.Create(). RequireMtls defaults to true (production-safe
        // default), which would make the handler try to load client cert files that don't exist
        // outside a real cluster. These tests only assert HttpClient timeout wiring, not mTLS
        // behavior (see AgentHostMtlsClientHandlerTests for that), so disable it here via an
        // env var set before the host is built.
        Environment.SetEnvironmentVariable("Sandbox__AgentHost__RequireMtls", "false");
    }

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Testing:BypassGitHubOrgAuthorization"] = "true";
        configuration["Testing:BypassGitHubTokenAuth"] = "true";
        configuration["Auth:Mode"] = "GitHubLegacy";
        configuration["Auth:ApiKey"] = OwnerApiKey;
        configuration["Auth:User"] = OwnerUser;
        configuration["Auth:Keys:0:Token"] = OtherApiKey;
        configuration["Auth:Keys:0:User"] = OtherUser;
        configuration["Auth:Keys:0:PlatformRoles"] = PlatformRoles.Viewer;
        configuration["Auth:Keys:1:Token"] = InternalServiceApiKey;
        configuration["Auth:Keys:1:User"] = ProjectAuthorization.InternalServiceUser;
    }
}
