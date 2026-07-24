using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Skills;

/// <summary>A blob entry inside a repository subtree: its repo-root-relative path and byte size.</summary>
public sealed record GitHubTreeBlob(string Path, long Size);

/// <summary>
/// Fetches only the files under a marketplace's subpath directly from GitHub, instead of cloning the
/// whole repository. The curated marketplace repos (<c>github/awesome-copilot</c>,
/// <c>microsoft/skills</c>) carry tens of megabytes of unrelated assets and full history; a
/// LibGit2Sharp full clone of either takes ~100 seconds and made the "Browse marketplaces" dialog
/// appear frozen — the browse request had no timeout and never returned. Listing the subtree via the
/// Git Trees API and pulling raw blob content transfers only the handful of megabytes that actually
/// hold skills, and completes in a few seconds.
/// </summary>
public interface IGitHubSkillTreeClient
{
    /// <summary>
    /// Lists every blob (file) whose path is at or under <paramref name="subpath"/> on
    /// <paramref name="branch"/>. Returns an empty list if the tree is empty or was truncated.
    /// </summary>
    Task<IReadOnlyList<GitHubTreeBlob>> ListSubtreeBlobsAsync(
        string owner, string repo, string branch, string subpath, string? token, CancellationToken ct);

    /// <summary>
    /// Downloads the raw UTF-8 text of a single blob. Returns <c>null</c> when the blob is missing,
    /// larger than <paramref name="maxBytes"/>, or binary (contains a NUL byte).
    /// </summary>
    Task<string?> GetRawTextAsync(
        string owner, string repo, string branch, string path, string? token, long maxBytes, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IGitHubSkillTreeClient"/> backed by the shared "github" named
/// <see cref="HttpClient"/>: the Git Trees REST API for listing and raw.githubusercontent.com for
/// content. The caller's GitHub token (when available) is attached only to raise rate limits and to
/// reach the same public repos the rest of the app already reads; anonymous access still works.
/// </summary>
public sealed class GitHubSkillTreeClient : IGitHubSkillTreeClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubSkillTreeClient(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<GitHubTreeBlob>> ListSubtreeBlobsAsync(
        string owner, string repo, string branch, string subpath, string? token, CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient("github");
        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}"
            + $"/git/trees/{Uri.EscapeDataString(branch)}?recursive=1";
        using var response = await SendWithAnonymousFallbackAsync(
            http, tok => { var r = new HttpRequestMessage(HttpMethod.Get, url); AddApiHeaders(r, tok); return r; }, token, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tree = await response.Content.ReadFromJsonAsync<TreeResponse>(ct).ConfigureAwait(false);
        if (tree?.Tree is null || tree.Tree.Count == 0)
            return Array.Empty<GitHubTreeBlob>();

        var normalized = subpath.Trim('/');
        var prefix = normalized.Length == 0 ? string.Empty : normalized + "/";

        var blobs = new List<GitHubTreeBlob>();
        foreach (var entry in tree.Tree)
        {
            if (!string.Equals(entry.Type, "blob", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Path))
                continue;
            if (normalized.Length == 0
                || string.Equals(entry.Path, normalized, StringComparison.Ordinal)
                || entry.Path!.StartsWith(prefix, StringComparison.Ordinal))
            {
                blobs.Add(new GitHubTreeBlob(entry.Path!, entry.Size ?? 0));
            }
        }
        return blobs;
    }

    public async Task<string?> GetRawTextAsync(
        string owner, string repo, string branch, string path, string? token, long maxBytes, CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient("github");
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var url =
            $"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}"
            + $"/{Uri.EscapeDataString(branch)}/{encodedPath}";
        using var response = await SendWithAnonymousFallbackAsync(
            http, tok => { var r = new HttpRequestMessage(HttpMethod.Get, url); AddRawHeaders(r, tok); return r; }, token, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.LongLength > maxBytes)
            return null;
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            return null; // binary — skip (matches on-disk resource reading rules)
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Sends the request built by <paramref name="buildRequest"/> and, when an <em>authenticated</em>
    /// attempt fails with ANY non-success status, retries the identical request once anonymously. The
    /// curated marketplaces are PUBLIC repos, but a user's OAuth-App access token that has not been
    /// SSO-granted for the repo's org (e.g. the SAML-enforced <c>microsoft</c> org) is refused on org
    /// resources even though anonymous reads succeed — and the refusal is not always a 401/403: the
    /// REST Trees API returns 403 while <c>raw.githubusercontent.com</c> can return 404 for the same
    /// un-authorized token, which is why the fallback triggers on any non-2xx rather than only
    /// 401/403. The unauthenticated 60/hr budget is ample for a browse, so falling back keeps public
    /// marketplaces readable; the token is still tried first to give SSO-authorized users the higher
    /// authenticated rate limit.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithAnonymousFallbackAsync(
        HttpClient http, Func<string?, HttpRequestMessage> buildRequest, string? token, CancellationToken ct)
    {
        var request = buildRequest(token);
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        request.Dispose();
        if (!string.IsNullOrWhiteSpace(token) && !response.IsSuccessStatusCode)
        {
            response.Dispose();
            var anonymous = buildRequest(null);
            response = await http.SendAsync(anonymous, ct).ConfigureAwait(false);
            anonymous.Dispose();
        }
        return response;
    }

    private static void AddApiHeaders(HttpRequestMessage request, string? token)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver-SkillImporter", "1.0"));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static void AddRawHeaders(HttpRequestMessage request, string? token)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver-SkillImporter", "1.0"));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record TreeResponse(
        [property: JsonPropertyName("tree")] IReadOnlyList<TreeEntry>? Tree,
        [property: JsonPropertyName("truncated")] bool Truncated);

    private sealed record TreeEntry(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("size")] long? Size);
}
