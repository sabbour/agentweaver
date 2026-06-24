using Microsoft.Extensions.Caching.Memory;

namespace Agentweaver.Api.Auth;

public enum OrgAuthResult { Allowed, Denied, NotConfigured, OrgAccessNotGranted }

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

        _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    private async Task<OrgAuthResult> ResolveMembershipAsync(string accessToken, string login, CancellationToken ct)
    {
        var orgResult = await CheckEndpointAsync(
            accessToken,
            $"https://api.github.com/orgs/{Uri.EscapeDataString(_allowedOrg!)}/members/{Uri.EscapeDataString(login)}",
            ct).ConfigureAwait(false);

        if (orgResult == CheckResult.OrgAccessNotGranted)
        {
            // The OAuth app is not approved for this org (third-party app restrictions active).
            // /user/orgs is NOT a valid fallback — restricted orgs are also filtered out there.
            // Fall back to GET /orgs/{org}/public_members/{login}: public data, not subject to
            // OAuth app restrictions. Returns 204 when the user has publicized their org membership.
            // Self-service fix for the user: github.com/orgs/{org}/people → set membership to Public.
            //
            // NOTE: no public equivalent exists for team membership; if AllowedTeam is also set,
            // the team check below will still 403 and must be resolved via app approval or GitHub App.
            var publicResult = await CheckEndpointAsync(
                accessToken,
                $"https://api.github.com/orgs/{Uri.EscapeDataString(_allowedOrg!)}/public_members/{Uri.EscapeDataString(login)}",
                ct).ConfigureAwait(false);

            if (publicResult != CheckResult.Member)
            {
                _logger.LogWarning(
                    "GitHub login '{Login}' could not be verified as a member of org '{Org}'. " +
                    "The OAuth app is not approved for this org and the user does not have public membership. " +
                    "Fix: publicize your org membership at https://github.com/orgs/{Org}/people, " +
                    "or have an org owner approve the app under Org Settings → Third-party Access.",
                    login, _allowedOrg, _allowedOrg);
                return OrgAuthResult.OrgAccessNotGranted;
            }

            _logger.LogInformation(
                "GitHub login '{Login}' verified via PUBLIC membership of org '{Org}' " +
                "(OAuth app not approved; private endpoint returned 403).",
                login, _allowedOrg);
            // Public membership confirmed — fall through to team check / Allowed.
        }
        else if (orgResult != CheckResult.Member)
        {
            _logger.LogInformation("GitHub login '{Login}' is not a member of org '{Org}'.", login, _allowedOrg);
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
                    "The OAuth app has not been granted access to this org.",
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

    private enum CheckResult { Member, NotMember, OrgAccessNotGranted }

    private async Task<CheckResult> CheckEndpointAsync(string accessToken, string url, CancellationToken ct)
    {
        // "github-authz" is registered with AllowAutoRedirect = false so a 302 (private org,
        // requester not an org member) is treated as non-membership rather than a silent 200.
        using var http = _httpClientFactory.CreateClient("github-authz");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        // 403 = OAuth app not authorized for this org (org has third-party restrictions).
        // Distinct from 302/404 which mean the user is simply not a member.
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return CheckResult.OrgAccessNotGranted;

        // 204 No Content = org membership confirmed.
        // 200 OK = team membership endpoint returns 200 with an active/pending state body.
        return response.StatusCode is System.Net.HttpStatusCode.NoContent
                                   or System.Net.HttpStatusCode.OK
            ? CheckResult.Member
            : CheckResult.NotMember;
    }
}
