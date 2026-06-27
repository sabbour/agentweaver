using Microsoft.Extensions.Caching.Memory;

namespace Agentweaver.Api.Auth;

public enum OrgAuthResult { Allowed, Denied, NotConfigured, OrgAccessNotGranted, Inconclusive }

public interface IGitHubOrgAuthorizationService
{
    /// <summary>True when Auth:GitHub:AllowedOrg is set and the middleware should enforce membership.</summary>
    bool IsConfigured { get; }

    Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct);
}

/// <summary>
/// Verifies that a GitHub user is a member of the configured org (and optionally team).
/// Results are cached for 5 minutes to reduce GitHub API calls.
/// </summary>
public sealed class GitHubOrgAuthorizationService : IGitHubOrgAuthorizationService
{
    private readonly string? _allowedOrg;
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
        _allowedOrg = configuration["Auth:GitHub:AllowedOrg"]?.Trim();
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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_allowedOrg);

    public async Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_allowedOrg))
            return OrgAuthResult.NotConfigured;

        var cacheKey = $"ghorg_authz_{login}_{_allowedOrg}_{_teamSlug ?? string.Empty}";

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
        var orgResult = await CheckEndpointAsync(
            accessToken,
            $"https://api.github.com/orgs/{Uri.EscapeDataString(_allowedOrg!)}/members/{Uri.EscapeDataString(login)}",
            ct).ConfigureAwait(false);

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
        if (orgResult != CheckResult.Member)
        {
            var publicResult = await CheckEndpointAsync(
                accessToken,
                $"https://api.github.com/orgs/{Uri.EscapeDataString(_allowedOrg!)}/public_members/{Uri.EscapeDataString(login)}",
                ct,
                sendAuthHeader: false).ConfigureAwait(false);

            if (publicResult != CheckResult.Member)
            {
                // Fix 2 (Seraph T4–T7 review): distinguish INCONCLUSIVE (we could not determine private
                // membership because the authenticated call failed — expired token / 5xx / network)
                // from a DEFINITIVE not-a-member (a valid token that returned 404/302). Callers such as
                // the refresh-time re-check use this to avoid hard-denying private-org members whose
                // brokered GitHub token has expired.
                if (orgResult == CheckResult.Inconclusive)
                {
                    _logger.LogWarning(
                        "Org membership re-check for '{Login}' on org '{Org}' was INCONCLUSIVE " +
                        "(authenticated GitHub call failed — likely an expired/unauthorized token). " +
                        "Not treating as a definitive non-membership.",
                        login, _allowedOrg);
                    return OrgAuthResult.Inconclusive;
                }

                _logger.LogWarning(
                    "GitHub login '{Login}' is not a public member of org '{Org}'. " +
                    "If you are a member, publicize your membership at https://github.com/orgs/{Org}/people.",
                    login, _allowedOrg, _allowedOrg);
                return OrgAuthResult.Denied;
            }

            _logger.LogInformation(
                "GitHub login '{Login}' verified via PUBLIC membership of org '{Org}' " +
                "(private endpoint unavailable due to SAML SSO enforcement).",
                login, _allowedOrg);
            // Public membership confirmed — fall through to team check / Allowed.
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
