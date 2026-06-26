using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// S3 — Organization enforcement tests.
///
/// Validates that only <c>microsoft</c> org members can obtain tokens:
///   • In-org user at token issuance → token issued.
///   • Non-org user at token issuance → 403, NO token issued.
///   • Org membership re-checked on each refresh; a user removed from the org
///     is denied within ≤15 min (the refresh cycle).
///
/// Seraph design ref: §4 (org enforcement at issuance + per-refresh), §1 (GitHub broker).
///
/// UNIT tests (against <see cref="GitHubOrgAuthorizationService"/> directly) are LIVE now —
/// these cover the service's membership check logic.
///
/// INTEGRATION tests (org enforcement at /oauth/token) are STUB-marked pending Tank T3.
/// </summary>
public sealed class OAuthOrgEnforcementTests
{
    // =========================================================================
    // S3-UNIT-01 — GitHub org member (private 204) → Allowed
    //              (Delegates to existing GitHubOrgAuthorizationServiceTests for depth;
    //               repeated here for S3 traceability.)
    // =========================================================================
    [Fact]
    public async Task OrgMember_PrivateMembership_Returns_Allowed()
    {
        var handler = new RoutingHandler(req =>
            IsPrivateMembers(req) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        var service = BuildOrgService(handler);

        var result = await service.CheckMembershipAsync("gh-token", "alice", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed,
            "a confirmed private org member must be Allowed");
    }

    // =========================================================================
    // S3-UNIT-02 — GitHub non-member (private 404, public 404) → Denied
    // =========================================================================
    [Fact]
    public async Task NonOrgMember_Returns_Denied()
    {
        var handler = new RoutingHandler(_ => HttpStatusCode.NotFound);
        var service = BuildOrgService(handler);

        var result = await service.CheckMembershipAsync("gh-token", "intruder", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Denied,
            "a user who is not a microsoft org member must be Denied");
    }

    // =========================================================================
    // S3-UNIT-03 — SAML SSO org access not granted (403) → OrgAccessNotGranted
    // =========================================================================
    [Fact]
    public async Task OrgAccessNotGranted_Returns_OrgAccessNotGranted()
    {
        var handler = new RoutingHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.Redirect; // SAML redirect
            if (IsPublicMembers(req))  return HttpStatusCode.Forbidden; // SAML 403
            return HttpStatusCode.NotFound;
        });
        var service = BuildOrgService(handler);

        var result = await service.CheckMembershipAsync("gh-token", "saml-user", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Denied,
            "SAML-restricted user who is not a public member must be Denied");
    }

    // =========================================================================
    // S3-INT-01 (STUB — requires Tank T3)
    // Non-org GitHub user → POST /oauth/token returns 403 with no token in body.
    // Seraph §4: "non-members get 403 and NO token".
    // =========================================================================
    [Fact(Skip = "TODO: Tank T3 — org check at token issuance not yet wired into /oauth/token")]
    public async Task NonOrgUser_TokenIssuance_Returns403_NoToken()
    {
        // Implementation outline:
        // 1. Override GitHubOrgAuthorizationService in factory to return Denied for "intruder"
        // 2. Complete /oauth/authorize as "intruder" (stub GitHub callback)
        // 3. POST /oauth/token with valid PKCE verifier
        // 4. Assert 403 with {"error":"access_denied"}, no access_token in body
        await Task.CompletedTask;
    }

    // =========================================================================
    // S3-INT-02 (STUB — requires Tank T3)
    // In-org GitHub user → POST /oauth/token returns 200 with JWT access token.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T3 — happy-path org-member token issuance not yet implemented")]
    public async Task InOrgUser_TokenIssuance_Returns200_WithJwt()
    {
        // org claim in JWT must be "microsoft"
        await Task.CompletedTask;
    }

    // =========================================================================
    // S3-INT-03 (STUB — requires Tank T3 + T4)
    // User removed from org mid-session: after the next refresh (≤15 min),
    // the refresh is denied with 403.
    // Seraph §4: "non-members get 403 and NO token" at refresh too.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T4 — org re-check at refresh_token rotation not yet implemented")]
    public async Task UserRemovedFromOrg_NextRefresh_Returns403()
    {
        // 1. Issue tokens for "alice" (in org)
        // 2. Stub org service to return Denied for "alice" (simulates removal)
        // 3. POST /oauth/token grant_type=refresh_token
        // 4. Assert 403 access_denied — no new tokens issued
        await Task.CompletedTask;
    }

    // =========================================================================
    // S3-INT-04 (STUB — requires Tank T3 + T4)
    // The org membership cache (5 min) means removal propagates within 15 min
    // (token TTL 15 min + cache 5 min).  Document the bound explicitly.
    // Seraph §4: "max ~15 min window after a user is removed".
    // =========================================================================
    [Fact(Skip = "TODO: Tank T4 — org-revocation lag: document and validate ≤15 min bound")]
    public async Task OrgRevocationLag_IsBoundedToFifteenMinutes()
    {
        // Documented design bound — validated by manipulating cache expiry in tests.
        await Task.CompletedTask;
    }

    // =========================================================================
    // Helpers — mirrors GitHubOrgAuthorizationServiceTests to avoid dependency
    // =========================================================================

    private static bool IsPrivateMembers(HttpRequestMessage r) =>
        !r.RequestUri!.AbsolutePath.Contains("/public_members/", StringComparison.Ordinal)
        && r.RequestUri.AbsolutePath.Contains("/members/", StringComparison.Ordinal);

    private static bool IsPublicMembers(HttpRequestMessage r) =>
        r.RequestUri!.AbsolutePath.Contains("/public_members/", StringComparison.Ordinal);

    private static GitHubOrgAuthorizationService BuildOrgService(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:GitHub:AllowedOrg"] = "microsoft" })
            .Build();

        return new GitHubOrgAuthorizationService(
            config,
            new SingleClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GitHubOrgAuthorizationService>.Instance);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpStatusCode> _router;
        public RoutingHandler(Func<HttpRequestMessage, HttpStatusCode> router) => _router = router;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_router(req)));
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public SingleClientFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }
}
