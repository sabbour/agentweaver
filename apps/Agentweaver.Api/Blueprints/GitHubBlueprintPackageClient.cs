using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;
using Agentweaver.Domain.BlueprintPackages;

namespace Agentweaver.Api.Blueprints;

/// <summary>Authenticated github.com Git-object reader for Blueprint package acquisition.</summary>
public sealed class GitHubBlueprintPackageClient : IGitHubBlueprintPackageClient
{
    private const int MaximumErrorMetadataBytes = 8 * 1024;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubAccessTokenProvider _accessTokenProvider;
    private readonly IAuthenticatedOwnerContext _ownerContext;

    public GitHubBlueprintPackageClient(
        IHttpClientFactory httpClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubAccessTokenProvider accessTokenProvider,
        IAuthenticatedOwnerContext ownerContext)
    {
        _httpClientFactory = httpClientFactory;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
        _ownerContext = ownerContext;
    }

    public async Task<GitHubBlueprintPackageCommit> ResolveCommitAsync(
        GitHubBlueprintPackageLocator locator,
        CancellationToken ct = default)
    {
        locator.Validate();
        var reference = locator.Ref ?? "HEAD";
        var resolved = await GetAsync<CommitResponse>(
            locator,
            $"commits/{Uri.EscapeDataString(reference)}",
            ct).ConfigureAwait(false);
        var commitSha = resolved.Sha;
        if (!GitHubBlueprintPackagePath.IsFullSha(commitSha))
            throw Malformed("GitHub returned an invalid commit SHA.");

        var immutable = await GetAsync<GitCommitResponse>(
            locator,
            $"git/commits/{commitSha}",
            ct).ConfigureAwait(false);
        var treeSha = immutable.Tree?.Sha;
        if (!string.Equals(immutable.Sha, commitSha, StringComparison.Ordinal))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.RefMoved,
                "GitHub returned a different commit while resolving the ref.");
        if (!GitHubBlueprintPackagePath.IsFullSha(treeSha))
            throw Malformed("GitHub returned an invalid tree SHA.");

        return new(commitSha!, treeSha!);
    }

    public async Task<GitHubBlueprintPackageTree> ReadTreeAsync(
        GitHubBlueprintPackageLocator locator,
        string commitSha,
        string treeSha,
        bool recursive,
        CancellationToken ct = default)
    {
        locator.Validate();
        if (!GitHubBlueprintPackagePath.IsFullSha(commitSha) || !GitHubBlueprintPackagePath.IsFullSha(treeSha))
            throw Malformed("An immutable Git object SHA is invalid.");
        var tree = await GetAsync<TreeResponse>(
            locator,
            $"git/trees/{treeSha}{(recursive ? "?recursive=1" : string.Empty)}",
            ct).ConfigureAwait(false);
        if (!string.Equals(tree.Sha, treeSha, StringComparison.Ordinal))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.ObjectChanged,
                "GitHub returned a tree different from the requested immutable object.");
        if (tree.Tree is null)
            throw Malformed("GitHub returned no tree entries.");

        return new(
            tree.Sha!,
            tree.Tree.Select(item => new GitHubBlueprintPackageTreeEntry(
                item.Path ?? string.Empty,
                item.Type ?? string.Empty,
                item.Mode ?? string.Empty,
                item.Sha ?? string.Empty,
                item.Size)).ToArray(),
            tree.Truncated);
    }

    public async Task<GitHubBlueprintPackageBlob> ReadBlobAsync(
        GitHubBlueprintPackageLocator locator,
        string commitSha,
        string blobSha,
        CancellationToken ct = default)
    {
        locator.Validate();
        if (!GitHubBlueprintPackagePath.IsFullSha(commitSha) || !GitHubBlueprintPackagePath.IsFullSha(blobSha))
            throw Malformed("An immutable Git object SHA is invalid.");
        var blob = await GetAsync<BlobResponse>(locator, $"git/blobs/{blobSha}", ct).ConfigureAwait(false);
        var returnedSha = blob.Sha;
        if (!string.Equals(returnedSha, blobSha, StringComparison.Ordinal)
            || blob.Content is null
            || !string.Equals(blob.Encoding, "base64", StringComparison.Ordinal))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.ObjectChanged,
                "GitHub returned a blob different from the requested immutable object.");

        try
        {
            return new(returnedSha!, Convert.FromBase64String(blob.Content.Replace("\n", string.Empty, StringComparison.Ordinal)));
        }
        catch (FormatException)
        {
            throw Malformed("GitHub returned malformed blob content.");
        }
    }

    private async Task<T> GetAsync<T>(GitHubBlueprintPackageLocator locator, string path, CancellationToken ct)
    {
        var token = await _accessTokenProvider.GetValidAccessTokenAsync(
            _scopeProvider.Resolve(_ownerContext.OwnerId),
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.AuthenticationRequired,
                "An authenticated GitHub credential is required.");

        try
        {
            using var client = _httpClientFactory.CreateClient("github");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{Uri.EscapeDataString(locator.Owner)}/{Uri.EscapeDataString(locator.Repository)}/{path}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return result ?? throw Malformed("GitHub returned an empty object response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.Transport,
                "GitHub request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.Transport,
                "GitHub could not be reached.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw Malformed("GitHub returned malformed JSON.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var rateLimited = response.StatusCode == HttpStatusCode.Forbidden
            && (response.Headers.Contains("Retry-After")
                || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                    && remaining.Any(value => string.Equals(value.Trim(), "0", StringComparison.Ordinal))
                || await HasSecondaryRateLimitMessageAsync(response.Content, ct).ConfigureAwait(false));
        var failure = response.StatusCode switch
        {
            HttpStatusCode.NotFound => GitHubBlueprintPackageAcquisitionFailure.NotFound,
            HttpStatusCode.Unauthorized => GitHubBlueprintPackageAcquisitionFailure.AuthenticationRequired,
            HttpStatusCode.Forbidden when rateLimited => GitHubBlueprintPackageAcquisitionFailure.RateLimited,
            HttpStatusCode.Forbidden => GitHubBlueprintPackageAcquisitionFailure.Forbidden,
            (HttpStatusCode)429 => GitHubBlueprintPackageAcquisitionFailure.RateLimited,
            HttpStatusCode.Conflict => GitHubBlueprintPackageAcquisitionFailure.RefMoved,
            _ => GitHubBlueprintPackageAcquisitionFailure.Transport,
        };
        throw new GitHubBlueprintPackageAcquisitionException(failure, failure switch
        {
            GitHubBlueprintPackageAcquisitionFailure.NotFound => "GitHub repository, ref, or object was not found.",
            GitHubBlueprintPackageAcquisitionFailure.AuthenticationRequired => "GitHub authentication was rejected.",
            GitHubBlueprintPackageAcquisitionFailure.Forbidden => "GitHub access was forbidden.",
            GitHubBlueprintPackageAcquisitionFailure.RateLimited => "GitHub API rate limit was reached.",
            GitHubBlueprintPackageAcquisitionFailure.RefMoved => "GitHub ref changed while it was being resolved.",
            _ => "GitHub API request failed.",
        });
    }

    private static async Task<bool> HasSecondaryRateLimitMessageAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaximumErrorMetadataBytes)
            return false;

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaximumErrorMetadataBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumErrorMetadataBytes)
            return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != System.Text.Json.JsonValueKind.String)
                return false;
            var message = messageElement.GetString();
            return message is not null && message.Length <= 1_024
                && (message.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("abuse detection", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("abuse rate limit", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static GitHubBlueprintPackageAcquisitionException Malformed(string message) =>
        new(GitHubBlueprintPackageAcquisitionFailure.MalformedContent, message);

    private sealed record CommitResponse([property: JsonPropertyName("sha")] string? Sha);
    private sealed record GitCommitResponse(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("tree")] GitTreeReference? Tree);
    private sealed record GitTreeReference([property: JsonPropertyName("sha")] string? Sha);
    private sealed record TreeResponse(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("tree")] IReadOnlyList<TreeEntryResponse>? Tree,
        [property: JsonPropertyName("truncated")] bool Truncated);
    private sealed record TreeEntryResponse(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("size")] long? Size);
    private sealed record BlobResponse(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("encoding")] string? Encoding);
}
