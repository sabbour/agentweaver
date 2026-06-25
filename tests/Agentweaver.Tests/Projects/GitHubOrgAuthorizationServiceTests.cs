using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Tests for <see cref="GitHubOrgAuthorizationService"/>.
///
/// The real implementation does NOT query <c>/user/orgs</c>. It verifies membership against:
///   • <c>GET /orgs/{org}/members/{login}</c>        — private membership (204 = member)
///   • <c>GET /orgs/{org}/public_members/{login}</c> — public-membership fallback for SAML orgs
///   • <c>GET /orgs/{org}/teams/{slug}/memberships/{login}</c> — optional team restriction
///
/// The "github-authz" HttpClient is registered with <c>AllowAutoRedirect = false</c>, so a 302
/// (private org, requester not a member / SAML redirect) is treated as non-membership, never a 200.
/// A 403 means the token is not SAML-authorized for the org.
/// </summary>
public sealed class GitHubOrgAuthorizationServiceTests
{
    // ---------------------------------------------------------------------
    // 1. Authorized member: private members endpoint returns 204 → Allowed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenUserIsPrivateOrgMember()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            IsPrivateMembers(req) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().ContainSingle(uri =>
            uri.AbsolutePath == "/orgs/microsoft/members/octocat");
        // A confirmed private member must NOT trigger the public-members fallback.
        handler.RequestUris.Should().NotContain(uri =>
            uri.AbsolutePath.Contains("/public_members/", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------
    // 2. Non-member privately (404) but PUBLIC member (204) → Allowed (SAML case).
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenUserIsPublicMemberOnly()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.NotFound;     // not visible privately
            if (IsPublicMembers(req))  return HttpStatusCode.NoContent;    // confirmed publicly
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().Contain(uri => uri.AbsolutePath == "/orgs/microsoft/members/octocat");
        handler.RequestUris.Should().Contain(uri => uri.AbsolutePath == "/orgs/microsoft/public_members/octocat");
    }

    // ---------------------------------------------------------------------
    // 3. 403 on the team endpoint (SAML SSO not authorized) → OrgAccessNotGranted.
    //    A 403 is the signal that the token is not SAML-authorized for the org;
    //    the service surfaces it as OrgAccessNotGranted rather than a plain Denied.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_ReturnsOrgAccessNotGranted_WhenTeamCheckIsForbidden()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.NoContent;    // member of the org
            if (IsTeam(req))           return HttpStatusCode.Forbidden;    // SAML SSO enforcement
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedTeam: "microsoft/cool-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.OrgAccessNotGranted);
    }

    // ---------------------------------------------------------------------
    // 4. 302 redirect must NOT be treated as success (AllowAutoRedirect=false → 302 ≠ 200).
    //    Private endpoint 302 (SAML redirect) + public 404 → Denied, proving the 302
    //    was treated as non-membership rather than a silent 200.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Denies_WhenPrivateEndpointRedirectsAndNoPublicMembership()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.Redirect;     // 302 SAML redirect
            if (IsPublicMembers(req))  return HttpStatusCode.NotFound;     // not a public member
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Denied);
    }

    // ---------------------------------------------------------------------
    // 5a. Team configured + team membership confirmed (200) → Allowed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenOrgAndTeamMembershipConfirmed()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.NoContent;    // org member
            if (IsTeam(req))           return HttpStatusCode.OK;           // active team member
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedTeam: "microsoft/cool-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().Contain(uri =>
            uri.AbsolutePath == "/orgs/microsoft/teams/cool-team/memberships/octocat");
    }

    // ---------------------------------------------------------------------
    // 5b. Team configured but caller is NOT a team member (404) → Denied,
    //     even though org membership passed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Denies_WhenOrgMemberButNotTeamMember()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.NoContent;    // org member
            if (IsTeam(req))           return HttpStatusCode.NotFound;     // NOT a team member
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedTeam: "microsoft/cool-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Denied);
    }

    // ---------------------------------------------------------------------
    // 6. 5-minute cache: a second call within the TTL must NOT hit GitHub again.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_CachesResult_WithinTtl()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            IsPrivateMembers(req) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        var service = BuildService(handler);

        var first = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);
        var requestsAfterFirst = handler.RequestUris.Count;

        var second = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        first.Should().Be(OrgAuthResult.Allowed);
        second.Should().Be(OrgAuthResult.Allowed);
        requestsAfterFirst.Should().BeGreaterThan(0, "the first call must reach GitHub");
        handler.RequestUris.Should().HaveCount(requestsAfterFirst,
            "the second call within the cache TTL must not make any new HTTP requests");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static bool IsPublicMembers(HttpRequestMessage req) =>
        req.RequestUri!.AbsolutePath.Contains("/public_members/", StringComparison.Ordinal);

    private static bool IsPrivateMembers(HttpRequestMessage req) =>
        !IsPublicMembers(req) && req.RequestUri!.AbsolutePath.Contains("/members/", StringComparison.Ordinal);

    private static bool IsTeam(HttpRequestMessage req) =>
        req.RequestUri!.AbsolutePath.Contains("/teams/", StringComparison.Ordinal) &&
        req.RequestUri!.AbsolutePath.Contains("/memberships/", StringComparison.Ordinal);

    private static GitHubOrgAuthorizationService BuildService(HttpMessageHandler handler, string? allowedTeam = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:GitHub:AllowedOrg"] = "microsoft",
        };
        if (allowedTeam is not null)
            settings["Auth:GitHub:AllowedTeam"] = allowedTeam;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new GitHubOrgAuthorizationService(
            config,
            new SingleClientHttpClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GitHubOrgAuthorizationService>.Instance);
    }

    /// <summary>
    /// Records every request URI and returns a status code chosen by <paramref name="router"/>
    /// based on the request, so a single test can simulate the members / public_members / team
    /// endpoints independently.
    /// </summary>
    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpStatusCode> _router;

        public List<Uri> RequestUris { get; } = [];

        public RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpStatusCode> router) => _router = router;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(_router(request)));
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
