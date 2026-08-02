using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Auth;

public sealed record AccessibleGitHubRepository(
    string FullName,
    string? Description,
    bool Private,
    string DefaultBranch,
    string? HtmlUrl,
    string AccessibleViaLogin,
    string Permission,
    string? AccessibleViaAvatarUrl,
    bool AccessibleViaIsDefault);

public sealed class LinkedGitHubAccountService(
    MemoryDbContext db,
    GitHubOAuthRedirectService oauthService,
    IGitHubTokenStore tokenStore,
    IGitHubAccessTokenProvider accessTokenProvider,
    ProjectGitHubIdentityOverrideStore overrideStore,
    IGitHubCopilotEntitlementProbe entitlementProbe,
    IHttpClientFactory httpClientFactory,
    ILogger<LinkedGitHubAccountService> logger)
{
    private static readonly TimeSpan LinkStateLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CopilotProbeTtl = TimeSpan.FromHours(12);

    public async Task<string> BeginLinkAuthorizationAsync(string entraUserId, CancellationToken ct = default)
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        db.GitHubAccountLinkStates.Add(new GitHubAccountLinkStateRecord
        {
            State = state,
            EntraUserId = entraUserId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(LinkStateLifetime),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return oauthService.CreateAuthorizationUrl(state);
    }

    public async Task<bool> IsPendingStateAsync(string state, CancellationToken ct = default)
    {
        return await db.GitHubAccountLinkStates
            .AsNoTracking()
            .AnyAsync(x => x.State == state, ct)
            .ConfigureAwait(false);
    }

    public async Task<GitHubIdentityLink> CompleteLinkAsync(string code, string state, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string entraUserId;

        var existing = await db.GitHubAccountLinkStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.State == state, ct)
            .ConfigureAwait(false);
        var claimed = existing is not null
            && await db.GitHubAccountLinkStates
                .Where(x => x.State == state)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false) > 0;

        if (existing is null || !claimed || now > existing.ExpiresAt)
            throw new InvalidOperationException("Invalid or expired GitHub account link state.");

        entraUserId = existing.EntraUserId;

        var token = await oauthService.ExchangeCodeForTokenAsync(code, ct).ConfigureAwait(false);
        var multi = RequireMultiIdentityStore();
        var copilotEntitled = await entitlementProbe.ProbeAsync(token.AccessToken, ct).ConfigureAwait(false);
        var checkedAt = copilotEntitled.HasValue ? now : (DateTimeOffset?)null;
        await multi.LinkIdentityAsync(
            entraUserId,
            token,
            isDefault: false,
            copilotEntitled: copilotEntitled,
            copilotEntitledCheckedAt: checkedAt,
            ct: ct).ConfigureAwait(false);

        return (await multi.GetLinkedIdentityAsync(entraUserId, token.Login, ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<GitHubIdentityLink>> ListLinkedAccountsAsync(string entraUserId, CancellationToken ct = default)
    {
        var multi = RequireMultiIdentityStore();
        var links = await multi.ListLinkedIdentitiesAsync(entraUserId, ct).ConfigureAwait(false);
        var refreshed = false;
        foreach (var link in links)
        {
            if (!NeedsCopilotRefresh(link))
                continue;

            refreshed |= await TryRefreshCopilotEntitlementAsync(entraUserId, link, ct).ConfigureAwait(false);
        }

        return refreshed
            ? await multi.ListLinkedIdentitiesAsync(entraUserId, ct).ConfigureAwait(false)
            : links;
    }

    public async Task<bool> SetDefaultAsync(string entraUserId, string githubLogin, CancellationToken ct = default) =>
        await RequireMultiIdentityStore().SetDefaultLinkedIdentityAsync(entraUserId, githubLogin, ct).ConfigureAwait(false);

    public async Task<(bool Removed, string? NewDefaultLogin)> UnlinkAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var multi = RequireMultiIdentityStore();
        var removed = await multi.UnlinkIdentityAsync(entraUserId, githubLogin, ct).ConfigureAwait(false);
        if (!removed)
            return (false, null);

        await overrideStore.RemoveOverridesForLinkedLoginAsync(entraUserId, githubLogin, ct).ConfigureAwait(false);
        var newDefault = await multi.GetDefaultLinkedIdentityAsync(entraUserId, ct).ConfigureAwait(false);
        return (true, newDefault?.GitHubLogin);
    }

    public async Task<IReadOnlyList<AccessibleGitHubRepository>> ListAccessibleRepositoriesAsync(
        string entraUserId,
        CancellationToken ct = default)
    {
        var links = await ListLinkedAccountsAsync(entraUserId, ct).ConfigureAwait(false);
        var results = new List<AccessibleGitHubRepository>();
        using var http = httpClientFactory.CreateClient("github");

        foreach (var link in links)
        {
            var scope = GitHubTokenScope.ForLinkedIdentity(entraUserId, link.GitHubLogin);
            var accessToken = await accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
                continue;

            var page = 1;
            const int perPage = 100;
            while (true)
            {
                var url = $"https://api.github.com/user/repos?sort=pushed&per_page={perPage}&page={page}&affiliation=owner,collaborator,organization_member";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));

                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    break;
                response.EnsureSuccessStatusCode();

                var repos = await response.Content.ReadFromJsonAsync<GitHubAccessibleRepoApiResponse[]>(ct).ConfigureAwait(false);
                if (repos is null || repos.Length == 0)
                    break;

                results.AddRange(repos.Select(repo => new AccessibleGitHubRepository(
                    repo.FullName ?? string.Empty,
                    repo.Description,
                    repo.Private,
                    repo.DefaultBranch ?? "main",
                    repo.HtmlUrl,
                    link.GitHubLogin,
                    ResolvePermission(repo.Permissions),
                    link.AvatarUrl,
                    link.IsDefault)));

                if (repos.Length < perPage)
                    break;

                page++;
            }
        }

        return results
            .OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AccessibleViaLogin, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool NeedsCopilotRefresh(GitHubIdentityLink link) =>
        link.CopilotEntitledCheckedAt is null
        || DateTimeOffset.UtcNow - link.CopilotEntitledCheckedAt.Value >= CopilotProbeTtl;

    private async Task<bool> TryRefreshCopilotEntitlementAsync(
        string entraUserId,
        GitHubIdentityLink link,
        CancellationToken ct)
    {
        try
        {
            var scope = GitHubTokenScope.ForLinkedIdentity(entraUserId, link.GitHubLogin);
            var accessToken = await accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
                return false;

            var token = await tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
            if (token is null)
                return false;

            var probe = await entitlementProbe.ProbeAsync(accessToken, ct).ConfigureAwait(false);
            if (!probe.HasValue)
                return false;

            await RequireMultiIdentityStore().LinkIdentityAsync(
                entraUserId,
                token,
                isDefault: link.IsDefault,
                copilotEntitled: probe.Value,
                copilotEntitledCheckedAt: DateTimeOffset.UtcNow,
                ct: ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to refresh Copilot entitlement for linked GitHub account {GitHubLogin}.", link.GitHubLogin);
            return false;
        }
    }

    private IMultiIdentityGitHubTokenStore RequireMultiIdentityStore() =>
        tokenStore as IMultiIdentityGitHubTokenStore
        ?? throw new InvalidOperationException("Configured GitHub token store does not support linked identities.");

    private static string ResolvePermission(GitHubRepoPermissions? permissions)
    {
        if (permissions?.Admin == true) return "admin";
        if (permissions?.Push == true) return "write";
        if (permissions?.Pull == true) return "read";
        return "unknown";
    }

    private sealed class GitHubAccessibleRepoApiResponse
    {
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("private")] public bool Private { get; set; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("permissions")] public GitHubRepoPermissions? Permissions { get; set; }
    }

    private sealed class GitHubRepoPermissions
    {
        [JsonPropertyName("admin")] public bool Admin { get; set; }
        [JsonPropertyName("push")] public bool Push { get; set; }
        [JsonPropertyName("pull")] public bool Pull { get; set; }
    }
}
