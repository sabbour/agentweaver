using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Projects.Config;

public sealed class SkillMarketplaceBrowseService
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 50;

    private const int MaxTreeResponseBytes = 8 * 1024 * 1024;
    private const int MaxTreeEntries = 100_000;
    private const int MaxCandidates = 5_000;
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxSkillMarkdownBytes = 512 * 1024;
    private const int MaxSkillResourceCount = 64;
    private const int MaxSkillResourceBytes = 256 * 1024;
    private const int MaxSkillResourceTotalBytes = 1024 * 1024;
    private const int MaxQueryLength = 128;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri GitHubApiBase = new("https://api.github.com/");
    private static readonly Uri GitHubRawBase = new("https://raw.githubusercontent.com/");

    private readonly HttpClient _httpClient;
    private readonly IProjectMarketplaceSourceStore _sources;
    private readonly TimeSpan _timeout;

    public SkillMarketplaceBrowseService(
        HttpClient httpClient,
        IProjectMarketplaceSourceStore sources,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient;
        _sources = sources;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<MarketplaceBrowsePage> BrowseAsync(
        string projectId,
        ProjectMarketplaceSourceRecord source,
        MarketplaceBrowseRequest request,
        Func<CancellationToken, Task> verifyCurrentReadAuthorityAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifyCurrentReadAuthorityAsync);

        ValidateRequest(projectId, source, request);
        await verifyCurrentReadAuthorityAsync(cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            var repository = source.Repository.Split('/');
            var owner = repository[0];
            var repo = repository[1];
            var commitSha = await ResolveCommitAsync(owner, repo, source.RequestedRef, timeout.Token)
                .ConfigureAwait(false);
            var tree = await ReadTreeAsync(owner, repo, commitSha, timeout.Token).ConfigureAwait(false);
            var allCandidates = IndexCandidates(tree, source.Subpath);
            if (allCandidates.Count == 0)
                throw MarketplaceSourceException.InvalidUpstream(
                    "The selected source path does not contain any SKILL.md manifests.");

            var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
            var matched = query is null
                ? allCandidates
                : allCandidates.Where(candidate =>
                        candidate.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        candidate.Location.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var offset = (long)(request.Page - 1) * request.PageSize;
            var pageItems = matched.Skip((int)Math.Min(offset, int.MaxValue))
                .Take(request.PageSize)
                .ToArray();
            var descriptions = await ReadPageManifestsAsync(
                    owner, repo, commitSha, pageItems, timeout.Token)
                .ConfigureAwait(false);

            await verifyCurrentReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
            var currentSource = await _sources.GetAsync(projectId, source.SourceId, cancellationToken)
                .ConfigureAwait(false);
            if (currentSource != source)
                throw MarketplaceSourceException.RevisionConflict();

            return new MarketplaceBrowsePage(
                source.SourceId,
                source.Revision,
                source.RequestedRef,
                commitSha,
                pageItems.Select(item => new MarketplaceBrowseCandidate(
                    item.Location,
                    item.Name,
                    descriptions[item.ManifestPath])).ToArray(),
                matched.Count,
                request.Page,
                request.PageSize,
                offset + request.PageSize < matched.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw MarketplaceSourceException.TimedOut();
        }
        catch (HttpRequestException)
        {
            throw MarketplaceSourceException.Unavailable();
        }
    }

    public async Task<MarketplaceSkillSourceFiles> ReadSelectedSkillFilesAsync(
        string projectId,
        ProjectMarketplaceSourceRecord source,
        long expectedSourceRevision,
        string resolvedCommitSha,
        string selectedPath,
        Func<CancellationToken, Task> verifyCurrentReadAuthorityAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(verifyCurrentReadAuthorityAsync);

        ValidateSelectedSkillRequest(
            projectId, source, expectedSourceRevision, resolvedCommitSha, selectedPath);
        await verifyCurrentReadAuthorityAsync(cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            var repository = source.Repository.Split('/');
            var owner = repository[0];
            var repo = repository[1];
            var commitSha = resolvedCommitSha.ToLowerInvariant();
            var tree = await ReadTreeAsync(owner, repo, commitSha, timeout.Token)
                .ConfigureAwait(false);
            var candidates = IndexCandidates(tree, source.Subpath);
            var selected = candidates.SingleOrDefault(candidate =>
                string.Equals(candidate.Location, selectedPath, StringComparison.Ordinal));
            if (selected is null)
                throw MarketplaceSourceException.InvalidRequest(
                    "The selected skill is not present at the resolved commit.");

            var nestedSkillLocations = candidates
                .Where(candidate => IsDescendantLocation(selected.Location, candidate.Location))
                .Select(candidate => candidate.Location)
                .ToArray();
            var manifest = FindManifestEntry(tree.Entries, selected.ManifestPath);
            ValidateTreeFile(manifest, MaxSkillMarkdownBytes);
            var resources = SelectResourceEntries(tree.Entries, selected, nestedSkillLocations);
            var resourceBytes = 0L;
            if (resources.Count > MaxSkillResourceCount)
                throw MarketplaceSourceException.UnsupportedSourceSize();
            foreach (var resource in resources)
            {
                ValidateTreeFile(resource.Entry, MaxSkillResourceBytes);
                if (resource.Entry.Size!.Value > MaxSkillResourceTotalBytes - resourceBytes)
                    throw MarketplaceSourceException.UnsupportedSourceSize();
                resourceBytes += resource.Entry.Size.Value;
            }

            var manifestBytes = await ReadRawFileAsync(
                    owner, repo, commitSha, manifest.Path!, MaxSkillMarkdownBytes, timeout.Token)
                .ConfigureAwait(false);
            if (manifestBytes.LongLength != manifest.Size!.Value)
                throw MarketplaceSourceException.InvalidUpstream(
                    "GitHub returned a skill manifest whose size did not match its tree entry.");
            var resourceBytesByPath = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(
                    resources,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4,
                        CancellationToken = timeout.Token,
                    },
                    async (resource, token) =>
                    {
                        var bytes = await ReadRawFileAsync(
                                owner,
                                repo,
                                commitSha,
                                resource.Entry.Path!,
                                checked((int)resource.Entry.Size!.Value),
                                token)
                            .ConfigureAwait(false);
                        if (bytes.LongLength != resource.Entry.Size.Value)
                            throw MarketplaceSourceException.InvalidUpstream(
                                "GitHub returned a skill file whose size did not match its tree entry.");
                        resourceBytesByPath[resource.RelativePath] = bytes;
                    })
                .ConfigureAwait(false);

            await verifyCurrentReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
            var currentSource = await _sources.GetAsync(projectId, source.SourceId, cancellationToken)
                .ConfigureAwait(false);
            if (currentSource != source)
                throw MarketplaceSourceException.RevisionConflict();

            var resourceFiles = resources
                .Select(resource => new MarketplaceSkillSourceResource(
                    resource.RelativePath,
                    resourceBytesByPath[resource.RelativePath]))
                .ToArray();
            return new MarketplaceSkillSourceFiles(manifestBytes, resourceFiles);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw MarketplaceSourceException.TimedOut();
        }
        catch (HttpRequestException)
        {
            throw MarketplaceSourceException.Unavailable();
        }
    }

    private static void ValidateRequest(
        string projectId,
        ProjectMarketplaceSourceRecord source,
        MarketplaceBrowseRequest request)
    {
        if (!string.Equals(source.ProjectId, projectId, StringComparison.Ordinal))
            throw MarketplaceSourceException.NotFound();
        if (source.State != ProjectMarketplaceSourceState.Active)
            throw MarketplaceSourceException.NotFound();
        if (request.ExpectedSourceRevision <= 0 || source.Revision != request.ExpectedSourceRevision)
            throw MarketplaceSourceException.RevisionConflict();
        if (request.Page < 1 || request.Page > 1_000_000)
            throw MarketplaceSourceException.InvalidRequest("Page must be between 1 and 1000000.");
        if (request.PageSize < 1 || request.PageSize > MaxPageSize)
            throw MarketplaceSourceException.InvalidRequest($"Page size must be between 1 and {MaxPageSize}.");
        if (request.Query is { Length: > MaxQueryLength } ||
            request.Query?.Any(char.IsControl) is true)
            throw MarketplaceSourceException.InvalidRequest(
                $"Search query must be at most {MaxQueryLength} characters and contain no control characters.");
        if (!MarketplaceSourceDefinition.TryParseRepository(source.Repository, out var owner, out var repository) ||
            !string.Equals(source.Repository, $"{owner}/{repository}", StringComparison.Ordinal) ||
            !MarketplaceSourceDefinition.IsValidRef(source.RequestedRef) ||
            MarketplaceSourceDefinition.NormalizeSubpath(source.Subpath) != source.Subpath)
            throw MarketplaceSourceException.InvalidUpstream("The stored marketplace source is invalid.");
    }

    private static void ValidateSelectedSkillRequest(
        string projectId,
        ProjectMarketplaceSourceRecord source,
        long expectedSourceRevision,
        string resolvedCommitSha,
        string selectedPath)
    {
        if (!string.Equals(source.ProjectId, projectId, StringComparison.Ordinal) ||
            source.State != ProjectMarketplaceSourceState.Active)
            throw MarketplaceSourceException.NotFound();
        if (expectedSourceRevision <= 0 || source.Revision != expectedSourceRevision)
            throw MarketplaceSourceException.RevisionConflict();
        if (string.IsNullOrWhiteSpace(resolvedCommitSha) || !IsCommitSha(resolvedCommitSha))
            throw MarketplaceSourceException.InvalidRequest("A full resolved commit SHA is required.");
        var normalizedPath = MarketplaceSourceDefinition.NormalizeSubpath(selectedPath);
        if (!string.Equals(normalizedPath ?? string.Empty, selectedPath, StringComparison.Ordinal))
            throw MarketplaceSourceException.InvalidRequest("The selected skill path must be a safe relative path.");
        var manifestPath = selectedPath.Length == 0 ? "SKILL.md" : $"{selectedPath}/SKILL.md";
        if (!MarketplaceSourceDefinition.TryParseRepository(source.Repository, out var owner, out var repository) ||
            !string.Equals(source.Repository, $"{owner}/{repository}", StringComparison.Ordinal) ||
            !MarketplaceSourceDefinition.IsValidRef(source.RequestedRef) ||
            MarketplaceSourceDefinition.NormalizeSubpath(source.Subpath) != source.Subpath)
            throw MarketplaceSourceException.InvalidUpstream("The stored marketplace source is invalid.");
        if (!IsUnderSubpath(manifestPath, source.Subpath))
            throw MarketplaceSourceException.InvalidRequest(
                "The selected skill path is outside the configured source subpath.");
    }

    private async Task<string> ResolveCommitAsync(
        string owner,
        string repository,
        string requestedRef,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            GitHubApiBase,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/commits/{Uri.EscapeDataString(requestedRef)}");
        var bytes = await GetBytesAsync(uri, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("sha", out var shaElement) ||
                shaElement.ValueKind != JsonValueKind.String)
                throw MarketplaceSourceException.InvalidUpstream("GitHub returned a commit without a SHA.");
            var sha = shaElement.GetString();
            if (sha is null || !IsCommitSha(sha))
                throw MarketplaceSourceException.InvalidUpstream("GitHub returned an invalid commit SHA.");
            return sha.ToLowerInvariant();
        }
        catch (JsonException)
        {
            throw MarketplaceSourceException.InvalidUpstream("GitHub returned malformed commit metadata.");
        }
    }

    private async Task<GitTreeIndex> ReadTreeAsync(
        string owner,
        string repository,
        string commitSha,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            GitHubApiBase,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/git/trees/{Uri.EscapeDataString(commitSha)}?recursive=1");
        var bytes = await GetBytesAsync(uri, MaxTreeResponseBytes, cancellationToken).ConfigureAwait(false);
        GitHubTreeResponse? tree;
        try
        {
            tree = JsonSerializer.Deserialize<GitHubTreeResponse>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            throw MarketplaceSourceException.InvalidUpstream("GitHub returned malformed tree metadata.");
        }

        if (tree is null)
            throw MarketplaceSourceException.InvalidUpstream("GitHub returned an incomplete tree response.");
        if (tree.Truncated)
            throw MarketplaceSourceException.UnsupportedSourceSize();
        var entries = tree.Tree;
        if (entries is null)
            throw MarketplaceSourceException.InvalidUpstream("GitHub returned an incomplete tree response.");
        if (entries.Count > MaxTreeEntries)
            throw MarketplaceSourceException.UnsupportedSourceSize();
        return new GitTreeIndex(tree.Truncated, entries);
    }

    private static List<IndexedCandidate> IndexCandidates(GitTreeIndex tree, string? subpath)
    {
        var prefix = MarketplaceSourceDefinition.NormalizeSubpath(subpath);
        var candidates = new List<IndexedCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in tree.Entries)
        {
            if (!string.Equals(entry.Type, "blob", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(entry.Path) ||
                !entry.Path.EndsWith("/SKILL.md", StringComparison.Ordinal) &&
                !string.Equals(entry.Path, "SKILL.md", StringComparison.Ordinal))
                continue;

            var path = entry.Path;
            if (!IsSafeRelativePath(path))
                throw MarketplaceSourceException.InvalidUpstream("GitHub returned an invalid skill manifest path.");
            if (!IsUnderSubpath(path, prefix))
                continue;
            if (!seen.Add(path))
                throw MarketplaceSourceException.InvalidUpstream("GitHub returned a duplicate skill manifest path.");
            var location = path[..^"/SKILL.md".Length];
            if (string.Equals(path, "SKILL.md", StringComparison.Ordinal))
                location = string.Empty;
            var name = location.Length == 0
                ? "root"
                : location[(location.LastIndexOf('/') + 1)..];
            candidates.Add(new IndexedCandidate(path, location, name));
            if (candidates.Count > MaxCandidates)
                throw MarketplaceSourceException.UnsupportedSourceSize();
        }

        return candidates.OrderBy(candidate => candidate.Location, StringComparer.Ordinal).ToList();
    }

    private async Task<Dictionary<string, string?>> ReadPageManifestsAsync(
        string owner,
        string repository,
        string commitSha,
        IReadOnlyList<IndexedCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var descriptions = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (candidate, token) =>
            {
                var bytes = await ReadRawFileAsync(
                        owner, repository, commitSha, candidate.ManifestPath, MaxManifestBytes, token)
                    .ConfigureAwait(false);
                descriptions[candidate.ManifestPath] = ReadDescription(bytes);
            }).ConfigureAwait(false);
        return descriptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private Task<byte[]> ReadRawFileAsync(
        string owner,
        string repository,
        string commitSha,
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri(
            GitHubRawBase,
            $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/{commitSha}/{encodedPath}");
        return GetBytesAsync(uri, maxBytes, cancellationToken);
    }

    private static GitTreeEntry FindManifestEntry(List<GitTreeEntry> entries, string manifestPath)
    {
        var entry = entries.SingleOrDefault(candidate =>
            string.Equals(candidate.Path, manifestPath, StringComparison.Ordinal) &&
            string.Equals(candidate.Type, "blob", StringComparison.Ordinal));
        return entry ?? throw MarketplaceSourceException.InvalidUpstream(
            "The selected skill manifest is missing from the resolved commit.");
    }

    private static List<SelectedResourceEntry> SelectResourceEntries(
        List<GitTreeEntry> entries,
        IndexedCandidate selected,
        IReadOnlyList<string> nestedSkillLocations)
    {
        var resources = new List<SelectedResourceEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "blob", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(entry.Path) ||
                string.Equals(entry.Path, selected.ManifestPath, StringComparison.Ordinal))
                continue;
            if (!IsUnderLocation(entry.Path, selected.Location) ||
                nestedSkillLocations.Any(location => IsUnderLocation(entry.Path, location)))
                continue;

            var relativePath = selected.Location.Length == 0
                ? entry.Path
                : entry.Path[(selected.Location.Length + 1)..];
            if (!IsSafeRelativePath(relativePath))
                throw MarketplaceSourceException.InvalidUpstream("GitHub returned an invalid skill resource path.");
            if (relativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
                throw MarketplaceSourceException.InvalidUpstream(
                    "A selected skill contains a nested SKILL.md that cannot be imported as a resource.");
            if (!seen.Add(relativePath))
                throw MarketplaceSourceException.InvalidUpstream(
                    "GitHub returned duplicate or case-aliased skill resource paths.");
            resources.Add(new SelectedResourceEntry(relativePath, entry));
            if (resources.Count > MaxSkillResourceCount)
                throw MarketplaceSourceException.UnsupportedSourceSize();
        }

        return resources.OrderBy(resource => resource.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static void ValidateTreeFile(GitTreeEntry entry, int maxBytes)
    {
        if (entry.Mode is not ("100644" or "100755"))
            throw MarketplaceSourceException.InvalidUpstream(
                "The selected skill contains a symbolic link or unsupported file type.");
        if (entry.Size is not long size || size < 0)
            throw MarketplaceSourceException.InvalidUpstream("GitHub returned a skill file without a valid size.");
        if (size > maxBytes)
            throw MarketplaceSourceException.UnsupportedSourceSize();
    }

    private static bool IsDescendantLocation(string parent, string candidate) =>
        candidate.Length > parent.Length &&
        (parent.Length == 0
            ? candidate.Length > 0
            : candidate.StartsWith($"{parent}/", StringComparison.Ordinal));

    private static bool IsUnderLocation(string path, string location) =>
        location.Length == 0 || path.StartsWith($"{location}/", StringComparison.Ordinal);

    private async Task<byte[]> GetBytesAsync(
        Uri uri,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.Forbidden ||
            (int)response.StatusCode == 429 ||
            (int)response.StatusCode >= 500)
            throw MarketplaceSourceException.Unavailable();
        if (response.StatusCode == HttpStatusCode.RequestTimeout)
            throw MarketplaceSourceException.TimedOut();
        if (!response.IsSuccessStatusCode)
            throw MarketplaceSourceException.InvalidUpstream(
                $"GitHub rejected a marketplace read with HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw MarketplaceSourceException.UnsupportedSourceSize();

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
            var buffer = new byte[8192];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maxBytes)
                    throw MarketplaceSourceException.UnsupportedSourceSize();
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return output.ToArray();
        }
        catch (IOException)
        {
            throw MarketplaceSourceException.InvalidUpstream("GitHub returned a truncated marketplace response.");
        }
    }

    private static string? ReadDescription(byte[] manifest)
    {
        string content;
        try
        {
            content = new UTF8Encoding(false, true).GetString(manifest);
        }
        catch (DecoderFallbackException)
        {
            throw MarketplaceSourceException.InvalidUpstream("A skill manifest is not valid UTF-8.");
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
            return null;
        for (var index = 1; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].Trim(), "---", StringComparison.Ordinal))
                break;
            var separator = lines[index].IndexOf(':');
            if (separator <= 0 ||
                !string.Equals(lines[index][..separator].Trim(), "description", StringComparison.Ordinal))
                continue;

            var description = lines[index][(separator + 1)..].Trim();
            if (description.Length >= 2 &&
                ((description[0] == '"' && description[^1] == '"') ||
                 (description[0] == '\'' && description[^1] == '\'')))
                description = description[1..^1];
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        return null;
    }

    private static bool IsCommitSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsSafeRelativePath(string path) =>
        !path.StartsWith('/') &&
        !path.Contains('\\') &&
        !path.Any(char.IsControl) &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static bool IsUnderSubpath(string path, string? subpath) =>
        string.IsNullOrEmpty(subpath) ||
        string.Equals(path, $"{subpath}/SKILL.md", StringComparison.Ordinal) ||
        path.StartsWith($"{subpath}/", StringComparison.Ordinal);

    private sealed record IndexedCandidate(string ManifestPath, string Location, string Name);

    private sealed record SelectedResourceEntry(string RelativePath, GitTreeEntry Entry);

    private sealed record GitTreeIndex(bool Truncated, List<GitTreeEntry> Entries);

    private sealed class GitHubTreeResponse
    {
        public bool Truncated { get; init; }
        public List<GitTreeEntry>? Tree { get; init; }
    }

    private sealed class GitTreeEntry
    {
        public string? Path { get; init; }
        public string? Type { get; init; }
        public string? Mode { get; init; }
        public long? Size { get; init; }
    }
}

public sealed record MarketplaceSkillSourceFiles(
    ReadOnlyMemory<byte> SkillMarkdown,
    IReadOnlyList<MarketplaceSkillSourceResource> Resources);

public sealed record MarketplaceSkillSourceResource(
    string RelativePath,
    ReadOnlyMemory<byte> Content);
