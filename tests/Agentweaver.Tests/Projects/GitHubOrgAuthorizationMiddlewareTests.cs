using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Fast-path tests for <see cref="GitHubOrgAuthorizationMiddleware"/>: an Agentweaver-minted OAuth JWT
/// caller is trusted when its org claim matches ANY allowed org in the (now multi-org) allowlist.
/// </summary>
public sealed class GitHubOrgAuthorizationMiddlewareTests
{
    // A caller whose OAuth token org claim is the SECOND allowed org is accepted by the fast-path.
    [Fact]
    public async Task OAuthFastPath_Allows_WhenOrgClaimIsSecondAllowedOrg()
    {
        var nextCalled = false;
        var context = BuildContext(callerOrg: "contoso");
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            allowedOrg: "microsoft,contoso");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue("the OAuth caller's org claim matches the second allowed org");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // A caller whose OAuth token org claim is NOT in the allowlist is denied with 403.
    [Fact]
    public async Task OAuthFastPath_Denies_WhenOrgClaimNotInAllowlist()
    {
        var nextCalled = false;
        var context = BuildContext(callerOrg: "intruder-org");
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            allowedOrg: "microsoft,contoso");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // A caller whose OAuth token org claim carries a TEAM-SCOPED rule string ("org/team-slug") is
    // accepted by the fast-path only if the current allow-list contains the same team-scoped rule.
    [Fact]
    public async Task OAuthFastPath_Allows_WhenClaimIsTeamScopedRuleInAllowList()
    {
        var nextCalled = false;
        var context = BuildContext(callerOrg: "azure/aks");
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            allowedOrg: "Azure/aks,Azure/aks-pm");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue("the OAuth caller's team-scoped rule claim matches an allowed team rule");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // A bare-org JWT claim must NOT be accepted by a team-scoped-only allow-list — this is the
    // anti-grandfathering property that prevents a legacy `org`-only token from satisfying a rule
    // that was later demoted to team-scoped-only.
    [Fact]
    public async Task OAuthFastPath_Denies_WhenBareOrgClaimAgainstTeamScopedOnlyAllowList()
    {
        var nextCalled = false;
        var context = BuildContext(callerOrg: "azure");
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            allowedOrg: "Azure/aks,Azure/aks-pm");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse(
            "a bare-org JWT must not be able to satisfy a team-scoped-only rule after a config demotion");
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // A team-scoped JWT claim must NOT be accepted when the current allow-list contains only
    // an unrelated bare-org rule.
    [Fact]
    public async Task OAuthFastPath_Denies_WhenTeamScopedClaimAgainstUnrelatedBareOrgAllowList()
    {
        var nextCalled = false;
        var context = BuildContext(callerOrg: "contoso/eng");
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            allowedOrg: "microsoft,azure");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // A legacy JWT (minted before the team-membership change) with a bare-org claim is still
    // accepted when the current allow-list contains that bare org (or an `org/*` wildcard, which
    // canonicalizes to bare-org).
    [Fact]
    public async Task OAuthFastPath_Allows_LegacyBareOrgClaim_AgainstWildcardAllowList()
    {
        var nextCalled = false;
        var context = BuildContext(callerOrg: "microsoft");
        var middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            allowedOrg: "microsoft/*");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue("a bare-org claim must satisfy an `org/*` wildcard rule (canonicalized to bare-org)");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    private static DefaultHttpContext BuildContext(string callerOrg)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/projects";
        context.Response.Body = new MemoryStream();
        context.Items["agentweaver.caller"] = new CallerContext
        {
            User = "octocat",
            GitHubLogin = "octocat",
            IsOAuthJwt = true,
            Org = callerOrg,
        };
        return context;
    }

    private static GitHubOrgAuthorizationMiddleware BuildMiddleware(RequestDelegate next, string allowedOrg)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:GitHub:AllowedOrg"] = allowedOrg })
            .Build();

        var authzService = new GitHubOrgAuthorizationService(
            config,
            new ThrowingHttpClientFactory(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GitHubOrgAuthorizationService>.Instance);

        return new GitHubOrgAuthorizationMiddleware(
            next,
            authzService,
            config,
            new FakeHostEnvironment(),
            NullLogger<GitHubOrgAuthorizationMiddleware>.Instance);
    }

    // The fast-path must NOT make any GitHub HTTP call; fail loudly if it tries.
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("The OAuth fast-path must not call GitHub.");
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Agentweaver.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
