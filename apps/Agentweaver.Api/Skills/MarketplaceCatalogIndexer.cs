using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

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
/// <c>llm</c>). <see cref="RequiresGitHubConnection"/> signals that an LLM classification was
/// requested without a redeemable explicit capability, so the caller can present a connect-GitHub
/// requirement rather than treating the absence as an empty catalog.
/// </summary>
public sealed record MarketplaceCatalogIndex(
    string Repository,
    string Branch,
    string Fingerprint,
    string Strategy,
    IReadOnlyList<MarketplaceCatalogEntry> Entries,
    bool RequiresGitHubConnection = false);

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
/// Resolves a marketplace repo tree into a parsed catalog index. It first performs free, deterministic
/// SKILL.md discovery, then uses the bounded LLM classifier only when that discovery finds nothing and the
/// caller explicitly supplies a Copilot capability. When model classification is needed but that capability
/// cannot be supplied, it reports a GitHub connection requirement rather than silently returning an empty
/// catalog.
/// </summary>
public interface IMarketplaceCatalogIndexer
{
    Task<MarketplaceCatalogIndex> GetOrBuildAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? capabilityRunId,
        string? parseStrategy,
        CancellationToken ct);

    Task<MarketplaceCatalogIndex> GetOrBuildForProjectAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? capabilityReference,
        string? parseStrategy,
        CancellationToken ct,
        ProjectId? projectId = null,
        CallerContext? caller = null) =>
        GetOrBuildAsync(owner, repo, branch, blobs, capabilityReference, parseStrategy, ct);

    Task<MarketplaceCatalogIndex> GetOrBuildForProjectWithCapabilityIssuerAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? capabilityReference,
        string? parseStrategy,
        CancellationToken ct,
        ProjectId? projectId = null,
        CallerContext? caller = null,
        Func<CancellationToken, Task<string?>>? issueCapabilityAsync = null,
        Func<CancellationToken, Task<bool>>? hasCapabilityAsync = null,
        bool useByok = false) =>
        GetOrBuildForProjectAsync(owner, repo, branch, blobs, capabilityReference, parseStrategy, ct, projectId, caller);
}

/// <summary>
/// Default <see cref="IMarketplaceCatalogIndexer"/>. The heuristic derives candidates directly from the
/// tree metadata (every <c>SKILL.md</c> at any depth → its containing directory), so it handles both flat
/// (<c>github/awesome-copilot</c>: <c>skills/&lt;name&gt;/SKILL.md</c>) and nested
/// (<c>microsoft/skills</c>: <c>.github/plugins/&lt;plugin&gt;/skills/&lt;name&gt;/SKILL.md</c>) layouts
/// with zero blob downloads. The LLM classifier is used only when no
/// <c>SKILL.md</c> exists anywhere in the tree; its proposed locations are validated against the real tree
/// (and, for step-1 import compatibility, must contain a <c>SKILL.md</c>) before being cached. A caller
/// must provide the capability explicitly or through a trusted server-side issuer callback; the indexer
/// never treats a submitting user or an ambient GitHub installation scope as model authorization. If an LLM
/// classification is needed but that capability is absent or cannot be redeemed, it returns
/// <see cref="MarketplaceCatalogIndex.RequiresGitHubConnection"/> instead of a silent empty result.
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

    public Task<MarketplaceCatalogIndex> GetOrBuildAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? capabilityRunId,
        string? parseStrategy,
        CancellationToken ct) =>
        GetOrBuildForProjectAsync(
            owner, repo, branch, blobs, capabilityRunId, parseStrategy, ct, projectId: null);

    public Task<MarketplaceCatalogIndex> GetOrBuildForProjectAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? capabilityReference,
        string? parseStrategy,
        CancellationToken ct,
        ProjectId? projectId = null,
        CallerContext? caller = null) =>
        GetOrBuildForProjectWithCapabilityIssuerAsync(
            owner, repo, branch, blobs, capabilityReference, parseStrategy, ct, projectId, caller);

    public async Task<MarketplaceCatalogIndex> GetOrBuildForProjectWithCapabilityIssuerAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string? capabilityReference,
        string? parseStrategy,
        CancellationToken ct,
        ProjectId? projectId = null,
        CallerContext? caller = null,
        Func<CancellationToken, Task<string?>>? issueCapabilityAsync = null,
        Func<CancellationToken, Task<bool>>? hasCapabilityAsync = null,
        bool useByok = false)
    {
        var repository = $"{owner}/{repo}";
        var fingerprint = ComputeFingerprint(blobs);
        var strategy = string.IsNullOrWhiteSpace(parseStrategy) ? "auto" : parseStrategy.Trim().ToLowerInvariant();
        var classifierRequested = strategy is "auto" or "llm" && _classifier is not null;
        var key = $"{repository}@{branch}#{fingerprint}#strategy={strategy}#classifier={classifierRequested}";
        if (_cache.TryGet(key, out var cached))
        {
            // A cached LLM result still requires the caller's active binding. Validate it without
            // creating a capability record; heuristic indexes remain freely cacheable. The
            // deployment-wide BYOK provider is not project-scoped credential material, so a BYOK
            // caller may always read a cached LLM result.
            if (cached.Strategy != "llm" || useByok ||
                (hasCapabilityAsync is null && !string.IsNullOrWhiteSpace(capabilityReference)) ||
                (hasCapabilityAsync is not null && await hasCapabilityAsync(ct).ConfigureAwait(false)))
                return cached;

            return new MarketplaceCatalogIndex(
                repository, branch, fingerprint, "capability-required", Array.Empty<MarketplaceCatalogEntry>(),
                RequiresGitHubConnection: true);
        }

        MarketplaceCatalogIndex index;
        var heuristic = strategy is "auto" or "skillmd" ? BuildHeuristic(repository, branch, fingerprint, blobs) : null;
        if (heuristic is { Entries.Count: > 0 })
        {
            index = heuristic;
        }
        else if (blobs.Count == 0)
        {
            // The classifier rejects an empty tree without opening a model client. Return its
            // deterministic empty result before issuing a capability that could never be redeemed.
            index = new MarketplaceCatalogIndex(
                repository, branch, fingerprint, "skillmd", Array.Empty<MarketplaceCatalogEntry>());
        }
        else if (classifierRequested && useByok)
        {
            // BYOK bypasses Copilot capability issuance entirely — it is the deployment-wide
            // default, not project- or caller-scoped credential material.
            try
            {
                var treePaths = blobs.Select(b => b.Path).ToList();
                var llmEntries = await BuildWithLlmByokAsync(owner, repo, branch, blobs, treePaths, ct).ConfigureAwait(false);
                index = new MarketplaceCatalogIndex(repository, branch, fingerprint, "llm", llmEntries);
            }
            catch (GitHubCopilotUnauthorizedException)
            {
                return new MarketplaceCatalogIndex(
                    repository, branch, fingerprint, "capability-required", Array.Empty<MarketplaceCatalogEntry>(),
                    RequiresGitHubConnection: true);
            }
        }
        else if (classifierRequested)
        {
            // Issue only after every cache and deterministic path has returned, immediately before the
            // one uncached model call. The callback keeps the capability server-side and avoids durable
            // unused records for heuristic and cache-hit browses.
            if (string.IsNullOrWhiteSpace(capabilityReference) && issueCapabilityAsync is not null)
                capabilityReference = await issueCapabilityAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(capabilityReference))
            {
                return new MarketplaceCatalogIndex(
                    repository, branch, fingerprint, "capability-required", Array.Empty<MarketplaceCatalogEntry>(),
                    RequiresGitHubConnection: true);
            }

            try
            {
                var llmEntries = await BuildWithLlmAsync(
                    owner, repo, branch, blobs, capabilityReference!, ct, projectId, caller).ConfigureAwait(false);
                index = new MarketplaceCatalogIndex(repository, branch, fingerprint, "llm", llmEntries);
            }
            catch (GitHubCopilotUnauthorizedException)
            {
                return new MarketplaceCatalogIndex(
                    repository, branch, fingerprint, "capability-required", Array.Empty<MarketplaceCatalogEntry>(),
                    RequiresGitHubConnection: true);
            }
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
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        string capabilityReference,
        CancellationToken ct,
        ProjectId? projectId,
        CallerContext? caller)
    {
        var treePaths = blobs.Select(b => b.Path).ToList();
        var proposed = await _classifier!
            // A capability is supplied by a trusted caller only. Never reinterpret the caller identity
            // as authorization: the no-run browse path remains deterministic heuristic/empty.
            .ClassifyForProjectAsync(
                owner, repo, branch, treePaths, capabilityReference, ct: ct, projectId: projectId, caller: caller)
            .ConfigureAwait(false);
        return ValidateProposedEntries(blobs, proposed);
    }

    private async Task<IReadOnlyList<MarketplaceCatalogEntry>> BuildWithLlmByokAsync(
        string owner,
        string repo,
        string branch,
        IReadOnlyList<GitHubTreeBlob> blobs,
        IReadOnlyList<string> treePaths,
        CancellationToken ct)
    {
        var proposed = await _classifier!
            .ClassifyWithByokAsync(owner, repo, branch, treePaths, ct)
            .ConfigureAwait(false);
        return ValidateProposedEntries(blobs, proposed);
    }

    /// <summary>
    /// Validates every proposed location against the real tree. For step-1 import compatibility the
    /// location MUST contain a SKILL.md (import only understands the SKILL.md layout); hallucinated or
    /// non-SKILL.md locations are dropped so browse never lists a skill that import cannot fetch.
    /// </summary>
    private static IReadOnlyList<MarketplaceCatalogEntry> ValidateProposedEntries(
        IReadOnlyList<GitHubTreeBlob> blobs, IReadOnlyList<MarketplaceCatalogEntry>? proposed)
    {
        if (proposed is null || proposed.Count == 0)
            return Array.Empty<MarketplaceCatalogEntry>();

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
