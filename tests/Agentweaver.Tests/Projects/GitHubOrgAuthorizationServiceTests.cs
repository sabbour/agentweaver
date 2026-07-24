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
    // 4b. A SAML-enforcement 403 on the AUTHENTICATED private-members endpoint falls back to the
    //     UNAUTHENTICATED public-members endpoint. A publicized org member is therefore admitted even
    //     when their token is not (yet) SAML-SSO-authorized (the identity is the SSO-authenticated
    //     GitHub login and the org tie is confirmed via public membership). This intentionally relaxes
    //     the former PR #464 hard-deny, which blocked legitimate public members after OAuth-app rotation.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenPrivateSamlForbiddenButPublicMember()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.Forbidden;   // 403 SAML SSO enforcement
            if (IsPublicMembers(req))  return HttpStatusCode.NoContent;   // publicized member
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed,
            "a SAML-enforcement 403 on the private endpoint now falls back to public membership, which confirms the member");
        handler.RequestUris.Should().Contain(uri =>
            uri.AbsolutePath.Contains("/public_members/", StringComparison.Ordinal),
            "the public-membership fallback must run after a SAML 403 on the private endpoint");
    }

    // ---------------------------------------------------------------------
    // 4c. A SAML-enforcement 403 on the private endpoint AND not a public member either → the actionable
    //     SAML-enforced signal (OrgAccessNotGranted) is preserved so the user is told to authorize SSO,
    //     rather than a dead-end plain denial.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_ReturnsOrgAccessNotGranted_WhenPrivateSamlForbiddenAndNotPublicMember()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.Forbidden;   // 403 SAML SSO enforcement
            if (IsPublicMembers(req))  return HttpStatusCode.NotFound;    // not a public member
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.OrgAccessNotGranted,
            "a SAML 403 with no confirming public membership should still surface the 'authorize SSO' signal");
    }
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
    // 7. GitHubOrgList.Parse: comma + semicolon + whitespace + case-insensitive dedupe,
    //    order preserved; empty/whitespace => empty list (fail-closed).
    // ---------------------------------------------------------------------
    [Fact]
    public void GitHubOrgList_Parse_SplitsTrimsDedupesPreservingOrder()
    {
        GitHubOrgList.Parse("microsoft, contoso ; microsoft ;Contoso, azure ")
            .Should().Equal("microsoft", "contoso", "azure");

        GitHubOrgList.Parse("microsoft").Should().Equal("microsoft");
        GitHubOrgList.Parse("").Should().BeEmpty();
        GitHubOrgList.Parse("   ").Should().BeEmpty();
        GitHubOrgList.Parse(null).Should().BeEmpty();
        GitHubOrgList.Parse(" , ; ,").Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // 8. Member of the SECOND allowed org only (private 204 for contoso) => Allowed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenMemberOfSecondOrgOnly()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath == "/orgs/contoso/members/octocat"
                ? HttpStatusCode.NoContent
                : HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "microsoft,contoso");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        // The first org (microsoft) is checked and definitively not-a-member before contoso confirms.
        handler.RequestUris.Should().Contain(uri => uri.AbsolutePath == "/orgs/microsoft/members/octocat");
        handler.RequestUris.Should().Contain(uri => uri.AbsolutePath == "/orgs/contoso/members/octocat");
    }

    // ---------------------------------------------------------------------
    // 9. Member of NEITHER org, all checks definitive (404 everywhere) => Denied.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Denies_WhenMemberOfNeitherOrg_AllDefinitive()
    {
        var handler = new RoutingHttpMessageHandler(_ => HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "microsoft,contoso");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Denied);
    }

    // ---------------------------------------------------------------------
    // 10. Member of neither, but ONE org's primary authenticated check is inconclusive
    //     (401 on the private endpoint + public 404) => Inconclusive, not a hard Denied.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Inconclusive_WhenOnePrimaryCheckIsInconclusive()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            // microsoft: definitive not-a-member. contoso: private 401 (token expired) → inconclusive,
            // public 404 → cannot confirm. No org confirms, but one primary check was inconclusive.
            if (req.RequestUri!.AbsolutePath == "/orgs/contoso/members/octocat")
                return HttpStatusCode.Unauthorized;
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedOrg: "microsoft,contoso");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Inconclusive);
    }

    // ---------------------------------------------------------------------
    // 11. Multi-org SAML: org A (microsoft) enforces SAML SSO (private 403) and the caller is not a
    //     public member of microsoft either (public 404), and is definitively not a member of org B
    //     (contoso, 404 everywhere) => OrgAccessNotGranted. SAML-enforcement precedence beats a plain
    //     Denied. The microsoft public_members fallback IS attempted (and fails), preserving the
    //     actionable "authorize SSO" signal.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_ReturnsOrgAccessNotGranted_WhenFirstOrgSamlEnforcedAndNotMemberOfSecond()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            // microsoft: private 403 (SAML SSO enforcement). contoso: definitive not-a-member (404).
            if (req.RequestUri!.AbsolutePath == "/orgs/microsoft/members/octocat")
                return HttpStatusCode.Forbidden;
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedOrg: "microsoft,contoso");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.OrgAccessNotGranted,
            "a SAML-enforced org takes precedence over a plain not-a-member denial from another allowed org");
        handler.RequestUris.Should().Contain(uri =>
            uri.AbsolutePath == "/orgs/microsoft/public_members/octocat",
            "the public-membership fallback is attempted for the SAML-enforced org before preserving the SSO signal");
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

    private static GitHubOrgAuthorizationService BuildService(
        HttpMessageHandler handler, string? allowedTeam = null, string allowedOrg = "microsoft")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:GitHub:AllowedOrg"] = allowedOrg,
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
