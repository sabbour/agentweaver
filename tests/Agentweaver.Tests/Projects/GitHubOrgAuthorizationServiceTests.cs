using System.Net;
using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    [Fact]
    public async Task CheckMembershipAsync_AllowsWithoutOrgLookup_WhenGlobalWildcardRuleIsConfigured()
    {
        var handler = new RoutingHttpMessageHandler(_ => HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "*");

        var decision = await service.ResolveAsync("token", "octocat", CancellationToken.None);

        decision.Result.Should().Be(OrgAuthResult.Allowed);
        decision.MatchedEntity!.RuleString.Should().Be("*");
        handler.RequestUris.Should().BeEmpty("the global wildcard must not probe a fictitious '*' organization");
    }

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
    // 3. Team-scoped rule + team endpoint 403 (SAML SSO enforcement) → OrgAccessNotGranted.
    //    Under the team-membership authz model, a team-scoped rule surfaces the token-level SAML
    //    enforcement signal in the same way a bare-org SAML 403 does, so the caller sees the
    //    actionable "authorize SSO" error rather than a plain denial.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_ReturnsOrgAccessNotGranted_WhenTeamScopedRuleAndTeamCheckIsForbidden()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsTeam(req)) return HttpStatusCode.Forbidden;    // SAML SSO enforcement on team endpoint
            return HttpStatusCode.NotFound;
        });
        // Rule list contains ONLY a team-scoped rule — no bare-org rule to short-circuit through.
        var service = BuildService(handler, allowedOrg: "microsoft/cool-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.OrgAccessNotGranted,
            "a SAML 403 on the team endpoint under a team-scoped rule must surface the 'authorize SSO' signal");
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
    // 5a. Team-scoped rule: caller is a team member (team endpoint 200) → Allowed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenTeamScopedRuleAndCallerIsTeamMember()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsTeam(req)) return HttpStatusCode.OK;           // active team member
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedOrg: "microsoft/cool-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().Contain(uri =>
            uri.AbsolutePath == "/orgs/microsoft/teams/cool-team/memberships/octocat");
        // Team-scoped rules probe ONLY the team endpoint — no public/private-members fallback.
        handler.RequestUris.Should().NotContain(uri => uri.AbsolutePath.Contains("/members/"));
    }

    // ---------------------------------------------------------------------
    // 5b. Team-scoped rule: caller is NOT a team member (team endpoint 404) → Denied,
    //     even if they are a member of the org (org membership alone does not satisfy a team rule).
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Denies_WhenTeamScopedRuleAndCallerNotInTeam()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsTeam(req))           return HttpStatusCode.NotFound;     // NOT a team member
            if (IsPrivateMembers(req)) return HttpStatusCode.NoContent;    // BUT is an org member
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedOrg: "microsoft/cool-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Denied);
    }

    // ---------------------------------------------------------------------
    // 5c. Mixed rules — bare-org rule matches BEFORE team-scoped rule is even probed. This proves
    //     the OR aggregation: any rule that matches short-circuits to Allowed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenMixedRulesAndBareOrgRuleMatches()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req)) return HttpStatusCode.NoContent;    // bare-org rule confirms first
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedOrg: "microsoft,contoso/eng-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().NotContain(uri => uri.AbsolutePath.Contains("/teams/"),
            "the team-scoped rule must not be probed after the bare-org rule already matched");
    }

    // ---------------------------------------------------------------------
    // 5d. Mixed rules — bare-org rule doesn't match, team-scoped rule DOES → Allowed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenMixedRulesAndTeamScopedRuleMatches()
    {
        var handler = new RoutingHttpMessageHandler(req =>
        {
            // Not a member of microsoft (bare rule), but IS a member of contoso/eng-team (team rule).
            if (IsTeam(req)) return HttpStatusCode.OK;
            return HttpStatusCode.NotFound;
        });
        var service = BuildService(handler, allowedOrg: "microsoft,contoso/eng-team");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
    }

    // ---------------------------------------------------------------------
    // 5e. `org/*` wildcard is canonicalized to bare-org — behaves identically to `org`.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenWildcardRuleAndOrgMember()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            IsPrivateMembers(req) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "microsoft/*");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().NotContain(uri => uri.AbsolutePath.Contains("/teams/"),
            "an `org/*` rule must not probe the team endpoint; it is a bare-org rule");
    }

    // ---------------------------------------------------------------------
    // 5f. Team display-name with a space (e.g. "AKS PM") is defensively slugified to `aks-pm`
    //     so the request hits the correct GitHub team-membership endpoint.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_SlugifiesTeamDisplayName_ToLowercaseHyphenated()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            IsTeam(req) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "Azure/AKS PM");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().Contain(uri =>
            uri.AbsolutePath == "/orgs/Azure/teams/aks-pm/memberships/octocat",
            "team display names with uppercase or spaces are slugified to GitHub's lowercase-hyphenated form");
    }

    // ---------------------------------------------------------------------
    // 5h. Legacy Auth:GitHub:AllowedTeam shim: when set, its value is appended as an ADDITIONAL
    //     OR'd rule (deprecation warning is logged). Bare-org rule for microsoft + legacy team
    //     rule contoso/eng → caller who is only a contoso/eng team member is Allowed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_LegacyAllowedTeamKey_IsAppendedAsAdditionalRule()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            IsTeam(req) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "microsoft", allowedTeam: "contoso/eng");

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed,
            "the deprecated AllowedTeam value is folded into the rule list as an OR'd team rule");
    }

    [Fact]
    public async Task CheckMembershipAsync_LegacyAllowedTeamOverlappingBareOrg_WarnsAndKeepsOrgWideEffectiveRules()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            IsPrivateMembers(req) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        var logger = new CapturingLogger();
        var service = BuildService(
            handler,
            allowedOrg: "big-org",
            allowedTeam: "big-org/restricted-team",
            logger: new TypedLoggerAdapter(logger));

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed,
            "the overlapping bare-org rule still grants org-wide access under the OR model");
        service.AllowedEntities.Select(e => e.RuleString).Should().Equal(new[] { "big-org" },
            "the legacy team value must not be appended as a misleading independent OR rule when a bare-org rule already widens access");
        handler.RequestUris.Should().NotContain(uri => uri.AbsolutePath.Contains("/teams/"),
            "the team rule should not be probed because it is excluded from the effective rule set");

        var warning = logger.Entries
            .Single(e => e.Level == LogLevel.Warning &&
                         e.Message.Contains("Legacy AND semantics are NOT preserved", StringComparison.Ordinal));
        warning.Message.Should().Contain("big-org");
        warning.Message.Should().Contain("restricted-team");
        warning.Message.Should().Contain("Effective allow rules: [big-org]");
    }

    [Fact]
    public async Task ResolveAsync_ReturnsMatchedEntity_ForMatchingBareOrg()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath == "/orgs/contoso/members/octocat"
                ? HttpStatusCode.NoContent
                : HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "microsoft,contoso");

        var decision = await service.ResolveAsync("token", "octocat", CancellationToken.None);

        decision.Result.Should().Be(OrgAuthResult.Allowed);
        decision.MatchedEntity.Should().NotBeNull();
        decision.MatchedEntity!.RuleString.Should().Be("contoso");
    }

    [Fact]
    public async Task ResolveAsync_ReturnsMatchedEntity_ForMatchingTeamRule()
    {
        var handler = new RoutingHttpMessageHandler(req =>
            IsTeam(req) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        var service = BuildService(handler, allowedOrg: "microsoft,contoso/eng-team");

        var decision = await service.ResolveAsync("token", "octocat", CancellationToken.None);

        decision.Result.Should().Be(OrgAuthResult.Allowed);
        decision.MatchedEntity!.RuleString.Should().Be("contoso/eng-team");
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
    //    order preserved; empty/whitespace => empty list (fail-closed). Under the new mixed-list
    //    model Parse still returns distinct ORG NAMES (for the internal-caller shim); the entity
    //    parser is exercised via CheckMembership above.
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
    // 7b. ParseEntities: mixed bare-org / wildcard / team-scoped entries with the exact syntax
    //     from the user's request. Trailing `/*` canonicalizes to bare-org; team display names
    //     with spaces are slugified.
    // ---------------------------------------------------------------------
    [Fact]
    public void GitHubOrgList_ParseEntities_HandlesMixedRuleSyntax()
    {
        var entities = GitHubOrgList.ParseEntities("Azure/aks,Azure/AKS PM,azure-management-and-platforms/*");

        entities.Should().HaveCount(3);
        entities[0].Org.Should().Be("Azure");
        entities[0].TeamSlug.Should().Be("aks");
        entities[0].RuleString.Should().Be("azure/aks");

        entities[1].Org.Should().Be("Azure");
        entities[1].TeamSlug.Should().Be("aks-pm",
            "team display names with a space are slugified to GitHub's lowercase-hyphenated form");
        entities[1].RuleString.Should().Be("azure/aks-pm");

        entities[2].Org.Should().Be("azure-management-and-platforms");
        entities[2].TeamSlug.Should().BeNull(
            "trailing `/*` is canonicalized to a bare-org rule (any org member satisfies)");
        entities[2].RuleString.Should().Be("azure-management-and-platforms");
    }

    [Fact]
    public void GitHubOrgList_ParseEntities_DedupesOnCanonicalRuleString()
    {
        var entities = GitHubOrgList.ParseEntities("microsoft,MICROSOFT/*,microsoft/eng,microsoft/Eng");
        entities.Should().HaveCount(2);
        entities[0].RuleString.Should().Be("microsoft");
        entities[1].RuleString.Should().Be("microsoft/eng");
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
    // 12. BUG FIX regression: the private endpoint returns a SAML-enforcement 403, and the
    //     UNAUTHENTICATED public_members fallback itself gets rate-limited (403 with
    //     X-RateLimit-Remaining: 0) rather than giving a definitive answer. This must NOT be
    //     reported as "confirmed not a public member, SAML enforced" (OrgAccessNotGranted) — that
    //     conflates "couldn't verify" with "verified not a member" and produces a hard, misleading
    //     403 for what is really a transient rate-limit blip. It must surface as Inconclusive so the
    //     caller retries instead of hard-denying/erroring out a possibly-already-public member.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CheckMembershipAsync_Inconclusive_WhenPrivateSamlForbiddenAndPublicFallbackRateLimited()
    {
        var handler = new HeaderAwareRoutingHttpMessageHandler(req =>
        {
            if (IsPrivateMembers(req))
                return new HttpResponseMessage(HttpStatusCode.Forbidden); // SAML SSO enforcement

            if (IsPublicMembers(req))
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                rateLimited.Headers.Add("X-RateLimit-Remaining", "0");
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Inconclusive,
            "a rate-limited public-membership fallback must not be conflated with a confirmed " +
            "not-a-public-member answer — it must surface as a retryable transient failure");
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
        HttpMessageHandler handler,
        string? allowedTeam = null,
        string allowedOrg = "microsoft",
        ILogger<GitHubOrgAuthorizationService>? logger = null)
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
            logger ?? NullLogger<GitHubOrgAuthorizationService>.Instance);
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

    /// <summary>
    /// Like <see cref="RoutingHttpMessageHandler"/> but lets the router return a full
    /// <see cref="HttpResponseMessage"/> (with headers) so tests can simulate rate-limit
    /// signals such as <c>X-RateLimit-Remaining: 0</c> or <c>Retry-After</c>.
    /// </summary>
    private sealed class HeaderAwareRoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _router;

        public HeaderAwareRoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> router) =>
            _router = router;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_router(request));
    }

    private sealed class TypedLoggerAdapter(CapturingLogger inner) : ILogger<GitHubOrgAuthorizationService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
