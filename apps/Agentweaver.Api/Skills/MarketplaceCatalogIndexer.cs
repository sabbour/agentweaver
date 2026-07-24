using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Api.Skills;

/// <summary>
/// A single indexed skill in a marketplace catalog: its import <see cref="Location"/> (repo-root-relative
/// directory containing the skill), a short display <see cref="Name"/>, and an optional short
/// <see cref="Description"/>. The heuristic path leaves <see cref="Description"/> null (browse hydrates it
/// per page from SKILL.md frontmatter); the LLM path fills it in directly.
/// </summary>
public sealed record MarketplaceCatalogEntry(string Location, string Name, string? Description);

/// <summary>
/// The parsed catalog for one marketplace repo revision. <see cref="Fingerprint"/> is a content hash of
/// the repo tree (equivalent to the tree SHA) so the cache invalidates automatically when the tree
/// changes. <see cref="Strategy"/> records how the catalog was derived (<c>skillmd</c> heuristic or
/// <c>llm</c>).
/// </summary>
public sealed record MarketplaceCatalogIndex(
    string Repository,
    string Branch,
    string Fingerprint,
    string Strategy,
    IReadOnlyList<MarketplaceCatalogEntry> Entries);

/// <summary>
/// Cache of parsed catalog indexes keyed by <c>owner/repo@branch#fingerprint</c>. Because the fingerprint
/// pins the exact tree content, a cached entry for a public curated repo is safe to share across projects
/// (step-1 sources are PUBLIC). Serving from cache is what makes browse near-instant and — critically —
/// guarantees the LLM classifier is invoked at most once per repo revision, never per browse page.
/// </summary>
public interface IMarketplaceCatalogCache
{
    bool TryGet(string key, out MarketplaceCatalogIndex index);
    void Set(string key, MarketplaceCatalogIndex index);
}

/// <summary>
/// Bounded in-memory <see cref="IMarketplaceCatalogCache"/>. Step-1 keeps the cache in-process (content-
/// addressed, cheap to rebuild on a miss); a DB-backed cache table is a documented fast-follow. When the
/// cache is full the oldest inserted entry is evicted.
/// </summary>
public sealed class MarketplaceCatalogCache : IMarketplaceCatalogCache
{
    private const int MaxEntries = 256;
    private readonly ConcurrentDictionary<string, MarketplaceCatalogIndex> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    public bool TryGet(string key, out MarketplaceCatalogIndex index) => _entries.TryGetValue(key, out index!);

    public void Set(string key, MarketplaceCatalogIndex index)
    {
        if (_entries.TryAdd(key, index))
            _insertionOrder.Enqueue(key);
        else
            _entries[key] = index;

        while (_entries.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldest))
            _entries.TryRemove(oldest, out _);
    }
}

/// <summary>
/// Resolves a marketplace repo tree into a parsed catalog index. Fallback ladder (per revision, cached):
/// heuristic SKILL.md discovery (free, deterministic) → bounded, fail-closed LLM classifier (only when the
/// heuristic finds nothing and the caller has a Copilot-entitled user token) → empty. Never a hard gate.
/// </summary>
public interface IMarketplaceCatalogIndexer
{
    Task<MarketplaceCatalogIndex> GetOrBuildAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? submittingUser,
        string? parseStrategy,
        CancellationToken ct);
}

/// <summary>
/// Default <see cref="IMarketplaceCatalogIndexer"/>. The heuristic derives candidates directly from the
/// tree metadata (every <c>SKILL.md</c> at any depth → its containing directory), so it handles both flat
/// (<c>github/awesome-copilot</c>: <c>skills/&lt;name&gt;/SKILL.md</c>) and nested
/// (<c>microsoft/skills</c>: <c>.github/plugins/&lt;plugin&gt;/skills/&lt;name&gt;/SKILL.md</c>) layouts
/// with zero blob downloads. The LLM classifier is a bounded, fail-closed fallback used only when no
/// <c>SKILL.md</c> exists anywhere in the tree; its proposed locations are validated against the real tree
/// (and, for step-1 import compatibility, must contain a <c>SKILL.md</c>) before being cached.
/// </summary>
public sealed class MarketplaceCatalogIndexer : IMarketplaceCatalogIndexer
{
    private const int MaxEntries = 500;

    private readonly IMarketplaceCatalogCache _cache;
    private readonly IMarketplaceCatalogClassifier? _classifier;

    public MarketplaceCatalogIndexer(IMarketplaceCatalogCache cache, IMarketplaceCatalogClassifier? classifier = null)
    {
        _cache = cache;
        _classifier = classifier;
    }

    public async Task<MarketplaceCatalogIndex> GetOrBuildAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? submittingUser,
        string? parseStrategy,
        CancellationToken ct)
    {
        var repository = $"{owner}/{repo}";
        var fingerprint = ComputeFingerprint(blobs);
        var key = $"{repository}@{branch}#{fingerprint}";
        if (_cache.TryGet(key, out var cached))
            return cached;

        var strategy = string.IsNullOrWhiteSpace(parseStrategy) ? "auto" : parseStrategy.Trim().ToLowerInvariant();

        MarketplaceCatalogIndex index;
        var heuristic = strategy is "auto" or "skillmd" ? BuildHeuristic(repository, branch, fingerprint, blobs) : null;
        if (heuristic is { Entries.Count: > 0 })
        {
            index = heuristic;
        }
        else if (strategy is "auto" or "llm" && _classifier is not null)
        {
            var llmEntries = await BuildWithLlmAsync(owner, repo, branch, blobs, submittingUser, ct).ConfigureAwait(false);
            index = new MarketplaceCatalogIndex(repository, branch, fingerprint, "llm", llmEntries);
        }
        else
        {
            index = heuristic ?? new MarketplaceCatalogIndex(repository, branch, fingerprint, "skillmd", Array.Empty<MarketplaceCatalogEntry>());
        }

        _cache.Set(key, index);
        return index;
    }

    /// <summary>Derives catalog entries from every <c>SKILL.md</c> manifest in the tree (metadata only).</summary>
    private static MarketplaceCatalogIndex BuildHeuristic(
        string repository, string branch, string fingerprint, IReadOnlyList<GitHubTreeBlob> blobs)
    {
        var entries = new List<MarketplaceCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var blob in blobs)
        {
            if (!IsSkillManifest(blob.Path))
                continue;
            var location = LocationOf(blob.Path);
            if (!seen.Add(location))
                continue;
            entries.Add(new MarketplaceCatalogEntry(location, NameOf(location), Description: null));
            if (entries.Count >= MaxEntries)
                break;
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Location, b.Location));
        return new MarketplaceCatalogIndex(repository, branch, fingerprint, "skillmd", entries);
    }

    private async Task<IReadOnlyList<MarketplaceCatalogEntry>> BuildWithLlmAsync(
        string owner, string repo, string branch, IReadOnlyList<GitHubTreeBlob> blobs, string? submittingUser, CancellationToken ct)
    {
        var treePaths = blobs.Select(b => b.Path).ToList();
        var proposed = await _classifier!.ClassifyAsync(owner, repo, branch, treePaths, submittingUser, ct).ConfigureAwait(false);
        if (proposed is null || proposed.Count == 0)
            return Array.Empty<MarketplaceCatalogEntry>();

        // Validate every proposed location against the real tree. For step-1 import compatibility the
        // location MUST contain a SKILL.md (import only understands the SKILL.md layout); hallucinated or
        // non-SKILL.md locations are dropped so browse never lists a skill that import cannot fetch.
        var manifests = new HashSet<string>(
            blobs.Where(b => IsSkillManifest(b.Path)).Select(b => LocationOf(b.Path)), StringComparer.Ordinal);
        var kept = new List<MarketplaceCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in proposed)
        {
            var location = entry.Location?.Trim('/').Trim();
            if (string.IsNullOrEmpty(location) || !manifests.Contains(location) || !seen.Add(location))
                continue;
            var name = string.IsNullOrWhiteSpace(entry.Name) ? NameOf(location) : entry.Name.Trim();
            var description = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description.Trim();
            kept.Add(new MarketplaceCatalogEntry(location, name, description));
            if (kept.Count >= MaxEntries)
                break;
        }
        kept.Sort((a, b) => string.CompareOrdinal(a.Location, b.Location));
        return kept;
    }

    internal static bool IsSkillManifest(string path) =>
        path.Equals("SKILL.md", StringComparison.Ordinal) || path.EndsWith("/SKILL.md", StringComparison.Ordinal);

    /// <summary>Repo-root-relative directory location for a manifest path (byte-identical to import).</summary>
    internal static string LocationOf(string manifestPath) =>
        manifestPath.Equals("SKILL.md", StringComparison.Ordinal)
            ? "SKILL.md"
            : manifestPath[..^"/SKILL.md".Length];

    internal static string NameOf(string location)
    {
        if (location.Equals("SKILL.md", StringComparison.Ordinal))
            return "SKILL.md";
        var slash = location.LastIndexOf('/');
        return slash >= 0 ? location[(slash + 1)..] : location;
    }

    /// <summary>
    /// Content fingerprint of the tree: a hash over the sorted <c>path|size</c> of every blob. Changes iff
    /// the tree content changes, so it serves as the cache-invalidation key (equivalent to the tree SHA)
    /// without an extra API call.
    /// </summary>
    internal static string ComputeFingerprint(IReadOnlyList<GitHubTreeBlob> blobs)
    {
        var sb = new StringBuilder();
        foreach (var blob in blobs.OrderBy(b => b.Path, StringComparer.Ordinal))
            sb.Append(blob.Path).Append('|').Append(blob.Size).Append('\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
