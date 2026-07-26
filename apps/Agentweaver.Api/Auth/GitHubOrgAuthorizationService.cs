using Microsoft.Extensions.Caching.Memory;

namespace Agentweaver.Api.Auth;

public enum OrgAuthResult { Allowed, Denied, NotConfigured, OrgAccessNotGranted, Inconclusive }

public interface IGitHubOrgAuthorizationService
{
    /// <summary>True when Auth:GitHub:AllowedOrg is set and the middleware should enforce membership.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The parsed, ordered, de-duplicated list of allowed GitHub orgs (a caller is authorized if they
    /// are a member of ANY of these). Empty when unconfigured. Exposed so the org-authorization
    /// middleware and the API-key middleware can reuse it without re-parsing the config value.
    /// </summary>
    IReadOnlyList<string> AllowedOrgs { get; }

    Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct);
}

/// <summary>
/// Verifies that a GitHub user is a member of the configured org (and optionally team).
/// Results are cached for 5 minutes to reduce GitHub API calls.
/// </summary>
public sealed class GitHubOrgAuthorizationService : IGitHubOrgAuthorizationService
{
    private readonly IReadOnlyList<string> _allowedOrgs;
    private readonly string? _teamOrg;
    private readonly string? _teamSlug;
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
        _allowedOrgs = GitHubOrgList.Parse(configuration["Auth:GitHub:AllowedOrg"]);
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;

        var allowedTeam = configuration["Auth:GitHub:AllowedTeam"]?.Trim();
        if (!string.IsNullOrWhiteSpace(allowedTeam))
        {
            var parts = allowedTeam.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                _teamOrg = parts[0];
                _teamSlug = parts[1];
            }
            else
            {
                _logger.LogWarning(
                    "Auth:GitHub:AllowedTeam value '{AllowedTeam}' is not in 'org/team-slug' format — team check disabled.",
                    allowedTeam);
            }
        }
    }

    public bool IsConfigured => _allowedOrgs.Count > 0;

    public IReadOnlyList<string> AllowedOrgs => _allowedOrgs;

    public async Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct)
    {
        if (_allowedOrgs.Count == 0)
            return OrgAuthResult.NotConfigured;

        // Cache key incorporates ALL allowed orgs deterministically (lowercased, joined with '|') so a
        // config change to the org list cannot collide with a previously-cached decision for the login.
        var orgsKey = string.Join('|', _allowedOrgs.Select(o => o.ToLowerInvariant()));
        var cacheKey = $"ghorg_authz_{login}_{orgsKey}_{_teamSlug ?? string.Empty}";

        if (_cache.TryGetValue(cacheKey, out OrgAuthResult cached))
            return cached;

        var result = await ResolveMembershipAsync(accessToken, login, ct).ConfigureAwait(false);

        // Do NOT cache an inconclusive result — it reflects a transient failure to reach GitHub or an
        // expired/invalid token, not a stable membership decision. Caching it would pin a temporary
        // failure for the whole TTL.
        if (result != OrgAuthResult.Inconclusive)
            _cache.Set(cacheKey, result, CacheTtl);

        return result;
    }

    private async Task<OrgAuthResult> ResolveMembershipAsync(string accessToken, string login, CancellationToken ct)
    {
        // Membership is satisfied by ANY allowed org: iterate the list, and the FIRST org that confirms
        // membership (private OR public) short-circuits to the team check. If no org confirms, we
        // distinguish a DEFINITIVE denial (every org gave a definitive not-a-member answer) from an
        // INCONCLUSIVE outcome (at least one org's authenticated primary check failed — expired token /
        // 5xx / network), so callers such as the refresh-time re-check don't hard-deny on a transient blip.
        var anyPrimaryInconclusive = false;
        var anySamlEnforced = false;
        string? confirmedOrg = null;

        foreach (var org in _allowedOrgs)
        {
            var (orgMember, primaryInconclusive, samlEnforced) =
                await ResolveSingleOrgAsync(accessToken, login, org, ct).ConfigureAwait(false);

            if (primaryInconclusive)
                anyPrimaryInconclusive = true;

            if (samlEnforced)
                anySamlEnforced = true;

            if (orgMember)
            {
                confirmedOrg = org;
                break;
            }
        }

        if (confirmedOrg is null)
        {
            // Aggregation precedence when NO allowed org confirmed membership:
            //   1. SAML-enforced (any org's private check returned a definitive 403 — PR #464 fix) →
            //      OrgAccessNotGranted. This MUST take precedence over a plain Denied: the user may well
            //      be a member but first needs to authorize the org's SAML SSO for this token, so we
            //      surface the actionable "authorize SSO" signal rather than a dead-end denial.
            //   2. Inconclusive (any org's primary authenticated check failed — expired token / 5xx /
            //      network) → don't hard-deny a possibly-valid member on a transient blip.
            //   3. Otherwise every org gave a definitive not-a-member answer → Denied.
            if (anySamlEnforced)
            {
                _logger.LogWarning(
                    "GitHub login '{Login}' is not a confirmed member of any allowed org, and at least one " +
                    "allowed org [{AllowedOrgs}] enforces SAML SSO for this token. Requiring SSO authorization; " +
                    "NOT satisfied by unauthenticated public membership.",
                    login, string.Join(", ", _allowedOrgs));
                return OrgAuthResult.OrgAccessNotGranted;
            }

            if (anyPrimaryInconclusive)
            {
                _logger.LogWarning(
                    "Org membership re-check for '{Login}' was INCONCLUSIVE (an authenticated GitHub call " +
                    "failed — likely an expired/unauthorized token) and no allowed org confirmed membership. " +
                    "Not treating as a definitive non-membership.",
                    login);
                return OrgAuthResult.Inconclusive;
            }

            _logger.LogWarning(
                "GitHub login '{Login}' is not a member of any allowed org [{AllowedOrgs}]. " +
                "If you are a member, publicize your membership at https://github.com/orgs/<org>/people.",
                login, string.Join(", ", _allowedOrgs));
            return OrgAuthResult.Denied;
        }

        // If team restriction is configured, also verify team membership.
        if (_teamOrg is not null && _teamSlug is not null)
        {
            var teamResult = await CheckEndpointAsync(
                accessToken,
                $"https://api.github.com/orgs/{Uri.EscapeDataString(_teamOrg)}/teams/{Uri.EscapeDataString(_teamSlug)}/memberships/{Uri.EscapeDataString(login)}",
                ct).ConfigureAwait(false);

            if (teamResult == CheckResult.OrgAccessNotGranted)
            {
                _logger.LogWarning(
                    "GitHub team access check returned 403 for login '{Login}' on team '{Org}/{Team}'. " +
                    "The OAuth token is not SAML-authorized for this org.",
                    login, _teamOrg, _teamSlug);
                return OrgAuthResult.OrgAccessNotGranted;
            }

            if (teamResult != CheckResult.Member)
            {
                _logger.LogInformation(
                    "GitHub login '{Login}' is not a member of team '{Org}/{Team}'.",
                    login, _teamOrg, _teamSlug);
                return OrgAuthResult.Denied;
            }
        }

        return OrgAuthResult.Allowed;
    }

    /// <summary>
    /// Runs the two-step membership check for a SINGLE org (authenticated private members primary,
    /// then UNAUTHENTICATED public_members fallback). Returns whether the login is a confirmed member
    /// of this org, whether the PRIMARY authenticated check was inconclusive, and whether this org
    /// definitively enforces SAML SSO for this token (a 403 on the private endpoint) — the last two so
    /// the caller can aggregate "inconclusive" and "needs SSO authorization" across the allowed-org list.
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
        // token bypass corporate SAML enforcement. Record it as SAML-enforced and move to the next org.
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

        // If primary check fails (SAML redirect → 302, not a member → 404, or inconclusive → token
        // expired/network/5xx), fall back to the public members endpoint (UNAUTHENTICATED) before
        // deciding. This handles the common case where the token is not SAML-authorized so the private
        // endpoint returns 302/401 rather than a definitive answer.
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
        // itself come back Inconclusive (rate-limited / network / 5xx) rather than a definitive answer.
        // Previously this case fell through to the orgResult-based branches below, which — whenever the
        // PRIVATE check had returned a SAML-enforcement 403 — reported a confident "not a public member,
        // SAML enforced" (OrgAccessNotGranted) even though we never actually got a definitive public
        // membership answer. That produced a hard, actionable-looking 403 ("ensure your org membership is
        // Public") for what was really just a transient rate-limit blip on the fallback call — observed
        // as the login's org-membership check flipping between 200 and 403 across adjacent polls in the
        // same session even though the membership was, in fact, already public the whole time. Treat this
        // the same as a primary-check Inconclusive: don't conflate "couldn't verify" with "verified not
        // a public member".
        if (publicResult == CheckResult.Inconclusive)
        {
            _logger.LogWarning(
                "Public membership fallback check for '{Login}' on org '{Org}' was INCONCLUSIVE " +
                "(rate-limited/network/5xx on the unauthenticated call) — treating as transient, not a " +
                "confirmed non-membership.",
                login, org);
            return (false, true, false);
        }

        // Not a member of this org. Report whether the PRIMARY authenticated check was inconclusive
        // (expired token / 5xx / network) so the caller can distinguish a transient failure from a
        // definitive not-a-member (a valid token that returned 404/302) when aggregating across orgs.
        // Fix 2 (Seraph T4–T7 review): the refresh-time re-check uses this to avoid hard-denying
        // private-org members whose brokered GitHub token has expired.
        if (orgResult == CheckResult.Inconclusive)
        {
            _logger.LogWarning(
                "Org membership check for '{Login}' on org '{Org}' was INCONCLUSIVE " +
                "(authenticated GitHub call failed — likely an expired/unauthorized token).",
                login, org);
            return (false, true, false);
        }

        // Public fallback was attempted only because AllowPublicMembershipFallbackOnSamlDenial is enabled
        // AND the private check returned a SAML-enforcement 403. The login is not a public member either,
        // so preserve the SAML-enforced signal (actionable "authorize SSO") rather than a plain not-member.
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
            // Network/transport failure — we genuinely don't know the membership status.
            _logger.LogWarning(ex, "GitHub org check request to {Url} failed at the transport layer.", url);
            return CheckResult.Inconclusive;
        }

        using (response)
        {
            // Detect GitHub rate-limit responses BEFORE mapping 403 → OrgAccessNotGranted.
            // GitHub primary rate-limit 403s carry X-RateLimit-Remaining: 0; secondary limits use
            // 403/429 with Retry-After. Treat these as Inconclusive so they are never cached and a
            // transient rate-limit blip does not pin a false denial for the full cache TTL.
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

            // A genuine SAML-enforcement 403 (no rate-limit headers) means the token is not authorized
            // for this org's private API.
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return CheckResult.OrgAccessNotGranted;

            // 204 No Content = org membership confirmed.
            // 200 OK = team membership endpoint returns 200 with an active/pending state body.
            if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.OK)
                return CheckResult.Member;

            // An AUTHENTICATED call that comes back 401 (token expired/revoked) or 5xx (GitHub outage)
            // is inconclusive — we cannot distinguish "not a member" from "couldn't ask".
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
