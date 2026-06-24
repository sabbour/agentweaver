using Microsoft.Extensions.Caching.Memory;

namespace Agentweaver.Api.Auth;

public enum OrgAuthResult { Allowed, Denied, NotConfigured }

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
        // Check org membership — 204 = member, anything else = not a member.
        var orgMember = await CheckEndpointAsync(
            accessToken,
            $"https://api.github.com/orgs/{Uri.EscapeDataString(_allowedOrg!)}/members/{Uri.EscapeDataString(login)}",
            ct).ConfigureAwait(false);

        if (!orgMember)
        {
            _logger.LogInformation("GitHub login '{Login}' is not a member of org '{Org}'.", login, _allowedOrg);
            return OrgAuthResult.Denied;
        }

        // If team restriction is configured, also verify team membership.
        if (_teamOrg is not null && _teamSlug is not null)
        {
            var teamMember = await CheckEndpointAsync(
                accessToken,
                $"https://api.github.com/orgs/{Uri.EscapeDataString(_teamOrg)}/teams/{Uri.EscapeDataString(_teamSlug)}/memberships/{Uri.EscapeDataString(login)}",
                ct).ConfigureAwait(false);

            if (!teamMember)
            {
                _logger.LogInformation(
                    "GitHub login '{Login}' is not a member of team '{Org}/{Team}'.",
                    login, _teamOrg, _teamSlug);
                return OrgAuthResult.Denied;
            }
        }

        return OrgAuthResult.Allowed;
    }

    private async Task<bool> CheckEndpointAsync(string accessToken, string url, CancellationToken ct)
    {
        // "github-authz" is registered with AllowAutoRedirect = false so a 302 (private org,
        // requester not an org member) is treated as non-membership rather than a silent 200.
        using var http = _httpClientFactory.CreateClient("github-authz");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        // 204 No Content = org membership confirmed.
        // 200 OK = team membership endpoint returns 200 with an active/pending state body.
        return response.StatusCode is System.Net.HttpStatusCode.NoContent
                                   or System.Net.HttpStatusCode.OK;
    }
}
