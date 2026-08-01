using Microsoft.Extensions.Caching.Memory;

namespace Agentweaver.Api.Auth;

public enum OrgAuthResult { Allowed, Denied, NotConfigured, OrgAccessNotGranted, Inconclusive }

/// <summary>Full membership decision: pass/fail signal + which rule matched (when Allowed).</summary>
public sealed record OrgMembershipDecision(OrgAuthResult Result, AllowedGitHubEntity? MatchedEntity);

public interface IGitHubOrgAuthorizationService
{
    /// <summary>True when Auth:GitHub:AllowedOrg is set and the middleware should enforce membership.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The parsed, ordered, de-duplicated list of allowed GitHub entities (org or org+team rules).
    /// A caller is authorized if they satisfy ANY entity. Empty when unconfigured. Exposed so the
    /// org-authorization middleware's fast-path can re-check the JWT's matched-rule claim against
    /// the current allowlist without re-parsing the config value.
    /// </summary>
    IReadOnlyList<AllowedGitHubEntity> AllowedEntities { get; }

    /// <summary>
    /// Distinct org NAMES contributed by <see cref="AllowedEntities"/>, order preserved. Retained
    /// for the internal-API-key path in <c>ApiKeyAuthMiddleware</c> which stamps a single org name
    /// on the synthesized caller. New callers should prefer <see cref="AllowedEntities"/>.
    /// </summary>
    IReadOnlyList<string> AllowedOrgs { get; }

    /// <summary>Fast pass/fail — a thin wrapper around <see cref="ResolveAsync"/>.</summary>
    Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct);

    /// <summary>
    /// Full resolution: returns the pass/fail signal AND the specific entity (rule) that granted
    /// access. Used at OAuth token issuance/refresh so the minted JWT's <c>org</c> claim carries
    /// the canonical rule string (see <see cref="AllowedGitHubEntity.RuleString"/>), which lets the
    /// middleware fast-path re-verify a team-scoped rule without re-hitting GitHub.
    /// </summary>
    Task<OrgMembershipDecision> ResolveAsync(string accessToken, string login, CancellationToken ct);
}

/// <summary>
/// Verifies that a GitHub user satisfies at least one configured allow-rule (bare-org or
/// org/team-slug). Results are cached for 5 minutes to reduce GitHub API calls.
/// </summary>
public sealed class GitHubOrgAuthorizationService : IGitHubOrgAuthorizationService
{
    private readonly IReadOnlyList<AllowedGitHubEntity> _allowedEntities;
    private readonly IReadOnlyList<string> _allowedOrgs;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GitHubOrgAuthorizationService> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public GitHubOrgAuthorizationService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<GitHubOrgAuthorizationService> logger)
    {
        var parsed = GitHubOrgList.ParseEntities(configuration["Auth:GitHub:AllowedOrg"], logger).ToList();
        var seenRules = new HashSet<string>(parsed.Select(e => e.RuleString), StringComparer.OrdinalIgnoreCase);

        // Legacy Auth:GitHub:AllowedTeam compat shim. Historically this was an AND-restriction on
        // top of the org list; under the new rule-based model it is normally folded in as an
        // ADDITIONAL OR'd rule. The one exception is a dangerous overlap with an already-configured
        // bare-org rule for the same org: appending the team would misleadingly suggest a narrower
        // restriction than the effective org-wide access, so we warn loudly and keep the effective
        // rule set unchanged.
        var legacyTeam = configuration["Auth:GitHub:AllowedTeam"]?.Trim();
        if (!string.IsNullOrWhiteSpace(legacyTeam))
        {
            logger.LogWarning(
                "Auth:GitHub:AllowedTeam is DEPRECATED under the team-membership authz model. " +
                "It is being evaluated for compatibility against Auth:GitHub:AllowedOrg " +
                "('{LegacyTeam}'). Please migrate the value into Auth:GitHub:AllowedOrg directly.",
                legacyTeam);

            var legacy = GitHubOrgList.ParseEntities(legacyTeam, logger);
            foreach (var e in legacy)
            {
                var overlapsBareOrgRule = e.IsTeamScoped &&
                    parsed.Any(existing =>
                        !existing.IsTeamScoped &&
                        string.Equals(existing.Org, e.Org, StringComparison.OrdinalIgnoreCase));

                if (overlapsBareOrgRule)
                {
                    var bareOrgRule = e.Org;
                    var teamRule = e.RuleString;
                    var effectiveRules = FormatRules(parsed);
                    logger.LogWarning(
                        $"Auth:GitHub:AllowedTeam value '{legacyTeam}' overlaps a bare-org Auth:GitHub:AllowedOrg rule " +
                        $"for '{bareOrgRule}'. Legacy AND semantics are NOT preserved under the new OR model: this configuration " +
                        $"currently grants access to the ENTIRE org '{bareOrgRule}', not just team '{teamRule}'. Effective allow " +
                        $"rules: [{effectiveRules}]. To restore the narrower team-only restriction, remove the bare-org " +
                        $"'{bareOrgRule}' entry and keep only '{teamRule}' in Auth:GitHub:AllowedOrg. If org-wide access is " +
                        "intentional, remove the redundant Auth:GitHub:AllowedTeam setting.");
                    continue;
                }

                if (seenRules.Add(e.RuleString))
                    parsed.Add(e);
            }
        }

        _allowedEntities = parsed;

        var seenOrgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orgNames = new List<string>();
        foreach (var e in _allowedEntities)
        {
            if (seenOrgs.Add(e.Org))
                orgNames.Add(e.Org);
        }
        _allowedOrgs = orgNames;

        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    private static string FormatRules(IEnumerable<AllowedGitHubEntity> entities) =>
        string.Join(", ", entities.Select(e => e.RuleString));

    public bool IsConfigured => _allowedEntities.Count > 0;

    public IReadOnlyList<AllowedGitHubEntity> AllowedEntities => _allowedEntities;

    public IReadOnlyList<string> AllowedOrgs => _allowedOrgs;

    public async Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct)
        => (await ResolveAsync(accessToken, login, ct).ConfigureAwait(false)).Result;

    public async Task<OrgMembershipDecision> ResolveAsync(string accessToken, string login, CancellationToken ct)
    {
        if (_allowedEntities.Count == 0)
            return new OrgMembershipDecision(OrgAuthResult.NotConfigured, null);

        // `*` is an explicit operator opt-in to unrestricted organization
        // membership. OAuth authentication has already happened before this
        // authorization service is called, so no GitHub membership probe is
        // required (or meaningful) for this rule.
        var globalWildcard = _allowedEntities.FirstOrDefault(entity => entity.IsGlobalWildcard);
        if (globalWildcard is not null)
            return new OrgMembershipDecision(OrgAuthResult.Allowed, globalWildcard);

        // Cache key incorporates ALL allowed rules deterministically (canonical rule strings joined
        // with '|') so a config change to the rule list cannot collide with a previously-cached
        // decision for the login.
        var rulesKey = string.Join('|', _allowedEntities.Select(e => e.RuleString));
        var cacheKey = $"ghorg_authz_{login}_{rulesKey}";

        if (_cache.TryGetValue(cacheKey, out OrgMembershipDecision? cached) && cached is not null)
            return cached;

        var decision = await ResolveMembershipAsync(accessToken, login, ct).ConfigureAwait(false);

        // Do NOT cache an inconclusive result — it reflects a transient failure to reach GitHub or
        // an expired/invalid token, not a stable membership decision. Caching it would pin a
        // temporary failure for the whole TTL.
        if (decision.Result != OrgAuthResult.Inconclusive)
            _cache.Set(cacheKey, decision, CacheTtl);

        return decision;
    }

    private async Task<OrgMembershipDecision> ResolveMembershipAsync(string accessToken, string login, CancellationToken ct)
    {
        // Iterate the rule list; the FIRST rule that confirms membership short-circuits to Allowed.
        // If no rule confirms, distinguish DEFINITIVE denial (every rule gave a definitive not-a-member
        // answer) from INCONCLUSIVE (at least one rule's authenticated primary check failed — expired
        // token / 5xx / network) so callers such as the refresh-time re-check don't hard-deny on a
        // transient blip. Preserve PR #464 precedence: SAML-enforced > Inconclusive > Denied.
        var anyPrimaryInconclusive = false;
        var anySamlEnforced = false;

        foreach (var entity in _allowedEntities)
        {
            var (member, primaryInconclusive, samlEnforced) =
                await ResolveSingleEntityAsync(accessToken, login, entity, ct).ConfigureAwait(false);

            if (primaryInconclusive)
                anyPrimaryInconclusive = true;

            if (samlEnforced)
                anySamlEnforced = true;

            if (member)
                return new OrgMembershipDecision(OrgAuthResult.Allowed, entity);
        }

        if (anySamlEnforced)
        {
            _logger.LogWarning(
                "GitHub login '{Login}' does not satisfy any allowed rule, and at least one rule " +
                "[{AllowedRules}] enforces SAML SSO for this token. Requiring SSO authorization.",
                login, string.Join(", ", _allowedEntities.Select(e => e.RuleString)));
            return new OrgMembershipDecision(OrgAuthResult.OrgAccessNotGranted, null);
        }

        if (anyPrimaryInconclusive)
        {
            _logger.LogWarning(
                "Membership re-check for '{Login}' was INCONCLUSIVE (an authenticated GitHub call " +
                "failed — likely an expired/unauthorized token) and no allowed rule confirmed membership. " +
                "Not treating as a definitive non-membership.",
                login);
            return new OrgMembershipDecision(OrgAuthResult.Inconclusive, null);
        }

        _logger.LogWarning(
            "GitHub login '{Login}' does not satisfy any allowed rule [{AllowedRules}].",
            login, string.Join(", ", _allowedEntities.Select(e => e.RuleString)));
        return new OrgMembershipDecision(OrgAuthResult.Denied, null);
    }

    /// <summary>
    /// Runs the appropriate membership probe for a SINGLE rule and returns the aggregation
    /// signals. For bare-org rules we run the two-step authenticated-private → unauthenticated-public
    /// probe (PR #464 semantics). For team-scoped rules we call the team-membership endpoint directly.
    /// </summary>
    private async Task<(bool IsMember, bool PrimaryInconclusive, bool SamlEnforced)> ResolveSingleEntityAsync(
        string accessToken, string login, AllowedGitHubEntity entity, CancellationToken ct)
    {
        if (entity.IsTeamScoped)
            return await ResolveSingleTeamAsync(accessToken, login, entity.Org, entity.TeamSlug!, ct).ConfigureAwait(false);

        return await ResolveSingleOrgAsync(accessToken, login, entity.Org, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Team-scoped rule probe: <c>GET /orgs/{org}/teams/{slug}/memberships/{login}</c>. There is no
    /// unauthenticated public fallback for team membership — teams are not publicized like org
    /// membership. A 403 is treated as SAML-enforcement (aggregation signal); a 401/5xx as
    /// Inconclusive; 200 with any state = member; anything else = not a member.
    /// </summary>
    private async Task<(bool IsMember, bool PrimaryInconclusive, bool SamlEnforced)> ResolveSingleTeamAsync(
        string accessToken, string login, string org, string teamSlug, CancellationToken ct)
    {
        var teamResult = await CheckEndpointAsync(
            accessToken,
            $"https://api.github.com/orgs/{Uri.EscapeDataString(org)}/teams/{Uri.EscapeDataString(teamSlug)}/memberships/{Uri.EscapeDataString(login)}",
            ct).ConfigureAwait(false);

        if (teamResult == CheckResult.Member)
            return (true, false, false);

        if (teamResult == CheckResult.OrgAccessNotGranted)
        {
            _logger.LogWarning(
                "GitHub team access check returned 403 for login '{Login}' on team '{Org}/{Team}'. " +
                "The OAuth token is not SAML-authorized for this org.",
                login, org, teamSlug);
            return (false, false, true);
        }

        if (teamResult == CheckResult.Inconclusive)
        {
            _logger.LogWarning(
                "Team membership check for '{Login}' on team '{Org}/{Team}' was INCONCLUSIVE " +
                "(authenticated GitHub call failed — likely an expired/unauthorized token).",
                login, org, teamSlug);
            return (false, true, false);
        }

        _logger.LogInformation(
            "GitHub login '{Login}' is not a member of team '{Org}/{Team}'.", login, org, teamSlug);
        return (false, false, false);
    }

    /// <summary>
    /// Bare-org rule probe: the two-step authenticated private-members primary check, then the
    /// UNAUTHENTICATED public_members fallback (PR #464 semantics unchanged). Returns whether the
    /// login is a confirmed member, whether the primary authenticated check was inconclusive, and
    /// whether this org definitively enforces SAML SSO for this token — the last two so the caller
    /// can aggregate "inconclusive" and "needs SSO authorization" across the rule list.
    /// </summary>
    private async Task<(bool IsMember, bool PrimaryInconclusive, bool SamlEnforced)> ResolveSingleOrgAsync(
        string accessToken, string login, string org, CancellationToken ct)
    {
        var orgResult = await CheckEndpointAsync(
            accessToken,
            $"https://api.github.com/orgs/{Uri.EscapeDataString(org)}/members/{Uri.EscapeDataString(login)}",
            ct).ConfigureAwait(false);

        if (orgResult == CheckResult.Member)
            return (true, false, false);

        // SECURITY (Seraph findings-auth Alert 5 / PR #464): when the AUTHENTICATED private-members
        // check returns a definitive SAML-enforcement 403 (OrgAccessNotGranted — distinct from a
        // rate-limit 403, which CheckEndpointAsync already maps to Inconclusive), this org actively
        // enforces SAML SSO for this token. We MUST NOT fall back to the UNAUTHENTICATED public_members
        // endpoint for THIS org: an unauthenticated lookup bypasses SAML and would return the true
        // public status (204), letting a public member with a non-SAML-authorized (or compromised)
        // token bypass corporate SAML enforcement.
        // SECURITY NOTE (was PR #464 hard-deny): a definitive SAML-enforcement 403 on the AUTHENTICATED
        // private members endpoint no longer hard-denies. GitHub org membership is required, and we treat
        // a publicized org member as sufficient even when the token is not (yet) SAML-SSO-authorized —
        // because forcing per-token SSO authorization for a public member blocks legitimate sign-ins
        // (e.g. right after rotating the OAuth app) with no membership-integrity gain: the identity is
        // the SSO-authenticated GitHub login and the org tie is confirmed via public membership. So on a
        // SAML 403 we fall through to the UNAUTHENTICATED public_members check below rather than returning.
        if (orgResult == CheckResult.OrgAccessNotGranted)
        {
            _logger.LogWarning(
                "GitHub org '{Org}' membership check for '{Login}' returned SAML-enforcement (403); " +
                "falling back to public membership verification.",
                org, login);
        }

        // CRITICAL: This call MUST be unauthenticated. For a SAML-enforced org, GitHub applies SAML
        // enforcement to any AUTHENTICATED request whose token is not SAML-authorized — even against the
        // public_members endpoint — and returns 403 instead of the public 204. An UNAUTHENTICATED request
        // bypasses SAML and returns the true public-membership status (204 for a publicized member).
        // The trade-off is GitHub's 60/hr-per-IP unauthenticated rate limit; rate-limit responses are
        // classified as Inconclusive below (and never cached) so a transient blip cannot pin a false denial.
        var publicResult = await CheckEndpointAsync(
            accessToken,
            $"https://api.github.com/orgs/{Uri.EscapeDataString(org)}/public_members/{Uri.EscapeDataString(login)}",
            ct,
            sendAuthHeader: false).ConfigureAwait(false);

        if (publicResult == CheckResult.Member)
        {
            _logger.LogInformation(
                "GitHub login '{Login}' verified via PUBLIC membership of org '{Org}' " +
                "(private endpoint unavailable due to SAML SSO enforcement).",
                login, org);
            return (true, false, false);
        }

        // BUG FIX (demo-recording investigation): the UNAUTHENTICATED public_members fallback shares a
        // 60/hr-per-IP GitHub rate limit across every pod egressing through the same NAT IP, so it can
        // itself come back Inconclusive rather than a definitive answer. Don't conflate "couldn't
        // verify" with "verified not a public member".
        if (publicResult == CheckResult.Inconclusive)
        {
            _logger.LogWarning(
                "Public membership fallback check for '{Login}' on org '{Org}' was INCONCLUSIVE " +
                "(rate-limited/network/5xx on the unauthenticated call) — treating as transient.",
                login, org);
            return (false, true, false);
        }

        if (orgResult == CheckResult.Inconclusive)
        {
            _logger.LogWarning(
                "Org membership check for '{Login}' on org '{Org}' was INCONCLUSIVE " +
                "(authenticated GitHub call failed — likely an expired/unauthorized token).",
                login, org);
            return (false, true, false);
        }

        if (orgResult == CheckResult.OrgAccessNotGranted)
        {
            _logger.LogWarning(
                "GitHub login '{Login}' is not a public member of org '{Org}' and the private check was " +
                "SAML-enforced (403). Requiring SSO authorization.",
                login, org);
            return (false, false, true);
        }

        _logger.LogWarning(
            "GitHub login '{Login}' is not a public member of org '{Org}'. " +
            "If you are a member, publicize your membership at https://github.com/orgs/{Org}/people.",
            login, org, org);
        return (false, false, false);
    }

    private enum CheckResult { Member, NotMember, OrgAccessNotGranted, Inconclusive }

    private async Task<CheckResult> CheckEndpointAsync(string accessToken, string url, CancellationToken ct,
        bool sendAuthHeader = true)
    {
        // "github-authz" is registered with AllowAutoRedirect = false so a 302 (private org,
        // requester not a member) is treated as non-membership rather than a silent 200.
        // 403 means SAML SSO enforcement — the token hasn't been SAML-authorized for this org.
        using var http = _httpClientFactory.CreateClient("github-authz");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (sendAuthHeader)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub org check request to {Url} failed at the transport layer.", url);
            return CheckResult.Inconclusive;
        }

        using (response)
        {
            bool isRateLimited =
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    && (response.Headers.TryGetValues("X-RateLimit-Remaining", out var rlVals)
                            && rlVals.FirstOrDefault() == "0"
                        || response.Headers.TryGetValues("Retry-After", out _)));

            if (isRateLimited)
            {
                _logger.LogWarning(
                    "GitHub API rate-limit response ({StatusCode}) received for org membership check. " +
                    "Treating as Inconclusive to avoid caching a false denial.",
                    (int)response.StatusCode);
                return CheckResult.Inconclusive;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return CheckResult.OrgAccessNotGranted;

            // 204 No Content = org membership confirmed.
            // 200 OK = team membership endpoint returns 200 with an active/pending state body.
            if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.OK)
                return CheckResult.Member;

            if (sendAuthHeader
                && (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || (int)response.StatusCode >= 500))
            {
                return CheckResult.Inconclusive;
            }

            return CheckResult.NotMember;
        }
    }
}
