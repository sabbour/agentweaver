using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Skills;

/// <summary>
/// Raised when a skill import source is rejected during parsing/validation (unsupported host,
/// non-https scheme, unresolvable ref, etc.). Its message is safe to surface to the caller as an
/// Invalid result — it never contains sensitive server-side detail.
/// </summary>
public sealed class SkillImportException : Exception
{
    public SkillImportException(string message) : base(message) { }
}

public enum SkillOutcome
{
    Ok,
    NotFound,
    Invalid,
    SourceUnavailable,
    GitHubConnectionRequired,
}

/// <summary>Per-skill outcome of an acquisition (import/sync/upload) operation.</summary>
public enum SkillUpsertKind { Added, Updated, Unchanged, Rejected }

public sealed record SkillView
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("provenance")] public required string Provenance { get; init; }
    [JsonPropertyName("source_repository")] public string? SourceRepository { get; init; }
    [JsonPropertyName("source_location")] public string? SourceLocation { get; init; }
    [JsonPropertyName("marketplace_name")] public string? MarketplaceName { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("content_hash")] public required string ContentHash { get; init; }
    [JsonPropertyName("resource_count")] public int ResourceCount { get; init; }
    [JsonPropertyName("assigned_agents")] public IReadOnlyList<string> AssignedAgents { get; init; } = Array.Empty<string>();
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }

    public static SkillView From(Skill s, IReadOnlyList<string> agents) => new()
    {
        Id = s.Id.ToString(),
        Name = s.Name,
        Description = s.Description,
        Provenance = s.Provenance.ToApiString(),
        SourceRepository = s.SourceRepository,
        SourceLocation = s.SourceLocation,
        MarketplaceName = s.MarketplaceName,
        Status = s.Status.ToApiString(),
        ContentHash = s.ContentHash,
        ResourceCount = s.Resources.Count,
        AssignedAgents = agents,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}

/// <summary>A skill discovered in a repository, before it is imported into the catalog.</summary>
public sealed record SkillCandidateView
{
    [JsonPropertyName("location")] public required string Location { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("valid")] public bool Valid { get; init; }
    [JsonPropertyName("resource_count")] public int ResourceCount { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>A single page of marketplace browse results plus the paging metadata needed to page through
/// the full candidate list. Every candidate on <see cref="Candidates"/> is fully hydrated with a short
/// definition; <see cref="Total"/> is the full (query-filtered) candidate count across all pages.</summary>
public sealed record MarketplaceBrowsePage(
    IReadOnlyList<SkillCandidateView> Candidates,
    int Total,
    int Page,
    int PageSize,
    bool HasMore);

/// <summary>A marketplace candidate's stable identity (import location + display name) before its
/// short definition is fetched for the current page.</summary>
internal sealed record MarketplaceCandidate(string Location, string Name);
internal sealed record PreviewCloneCacheEntry(string Directory, string? Subpath, DateTimeOffset ExpiresAt);

/// <summary>Result of upserting a single skill during acquisition.</summary>
public sealed record SkillUpsertView
{
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("skill_id")] public string? SkillId { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>Aggregate result of a sync/import/upload operation.</summary>
public sealed record SkillAcquisitionResult
{
    public required SkillOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<SkillUpsertView> Results { get; init; } = Array.Empty<SkillUpsertView>();

    /// <summary>Skills marked missing because their source disappeared (connected-repo sync only).</summary>
    public IReadOnlyList<string> MarkedMissing { get; init; } = Array.Empty<string>();
}

/// <summary>An uploaded file: workspace-relative path (forward slashes) + UTF-8 text content.</summary>
public sealed record UploadedSkillFile(string RelativePath, string Content);

public sealed record CreateSkillRequestDto(string Name, string? DisplayName, string? Description, string Instructions);

public sealed record GeneratedSkillDraft(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("skill_markdown")] string SkillMarkdown);

/// <summary>
/// Acquisition + assignment application service for the per-project skill catalog. Reuses the git
/// clone plumbing (repo import), the connected-repository working directory (sync), and validates all
/// acquired skills through <see cref="SkillParser"/>. Idempotent by content hash: re-importing or
/// re-syncing an unchanged skill is a no-op; a changed source updates the catalog entry.
/// </summary>
public sealed class SkillCatalogService
{
    public const string AcceptedSkillSourceMessage =
        "No skills found. Expected a SKILL.md, a folder of <name>/SKILL.md, or a repo with skills under .github/skills, .copilot/skills, .claude/skills, or .agents/skills. Accepted sources: owner/repo, https://github.com/owner/repo(.git), GitHub tree/blob URLs, or raw https://raw.githubusercontent.com SKILL.md URLs.";

    private static readonly Regex SkillNameRegex = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly HttpClient RawHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Message surfaced when a curated marketplace source could not be reached in time.</summary>
    internal const string MarketplaceTimeoutMessage =
        "Timed out while reading the marketplace source. Please try again in a moment.";

    /// <summary>Message surfaced when a curated marketplace source is unreachable or misconfigured.</summary>
    internal const string MarketplaceUnavailableMessage =
        "Could not reach the marketplace source. Check network/GitHub access and try again.";

    /// <summary>Hard ceiling on how long a marketplace browse/import may spend fetching from GitHub.</summary>
    private static readonly TimeSpan MarketplaceFetchTimeout = TimeSpan.FromSeconds(60);
    private const int MarketplaceFetchConcurrency = 16;
    private const long MaxMarketplaceSubtreeBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan PreviewCloneCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Hard ceiling on how many candidate skills a single marketplace browse will list.</summary>
    private const int MaxMarketplaceCandidates = 500;

    /// <summary>Concurrency used to fetch the current page's SKILL.md descriptions.</summary>
    private const int MarketplaceIndexConcurrency = 32;

    /// <summary>Default number of candidates returned per browse page.</summary>
    internal const int DefaultMarketplacePageSize = 25;

    /// <summary>Hard ceiling on browse page size (a page fetches this many SKILL.md blobs).</summary>
    internal const int MaxMarketplacePageSize = 50;

    /// <summary>
    /// Root for browse's request-scoped placeholder scratch tree. Deliberately LOCAL/ephemeral
    /// (system temp, typically ext4/tmpfs) rather than <see cref="AppPaths.DataDirectory"/>, which in
    /// production is a CIFS/Azure Files SMB mount whose ~16-33ms per-file op latency made browsing large
    /// marketplaces take tens of seconds. Placeholders are empty and deleted within the same request, so
    /// they need no durability — see WriteCandidatePlaceholdersToTempAsync.
    /// </summary>
    internal static string BrowseScratchRoot => Path.Combine(Path.GetTempPath(), "agentweaver-skill-browse");

    private readonly ISkillStore _skills;
    private readonly IProjectStore _projects;
    private readonly ProjectGitInitializer _gitInit;
    private readonly SkillParser _parser;
    private readonly IProjectRoleAuthorizationService _projectRoles;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubAccessTokenProvider? _accessTokenProvider;
    private readonly IGitHubSkillTreeClient? _treeClient;
    private readonly IMarketplaceCatalogIndexer? _catalogIndexer;
    private readonly MarketplaceCopilotCapabilityIssuer? _marketplaceCapabilityIssuer;
    private readonly ILogger<SkillCatalogService> _logger;
    private readonly ConcurrentDictionary<string, PreviewCloneCacheEntry> _previewCloneCache = new(StringComparer.Ordinal);

    public SkillCatalogService(
        ISkillStore skills,
        IProjectStore projects,
        ProjectGitInitializer gitInit,
        SkillParser parser,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubTokenStore tokenStore,
        ILogger<SkillCatalogService> logger,
        IGitHubAccessTokenProvider? accessTokenProvider = null,
        IGitHubSkillTreeClient? treeClient = null,
        IMarketplaceCatalogIndexer? catalogIndexer = null,
        IProjectRoleAuthorizationService? projectRoles = null,
        IConfiguration? configuration = null,
        MarketplaceCopilotCapabilityIssuer? marketplaceCapabilityIssuer = null)
    {
        _skills = skills;
        _projects = projects;
        _gitInit = gitInit;
        _parser = parser;
        _projectRoles = projectRoles ?? new NullProjectRoleAuthorizationService();
        _scopeProvider = scopeProvider;
        _tokenStore = tokenStore;
        _accessTokenProvider = accessTokenProvider;
        _treeClient = treeClient;
        _catalogIndexer = catalogIndexer;
        _marketplaceCapabilityIssuer = marketplaceCapabilityIssuer;
        _logger = logger;
    }

    public SkillCatalogService(
        ISkillStore skills,
        IProjectStore projects,
        ProjectGitInitializer gitInit,
        SkillParser parser,
        IProjectRoleAuthorizationService projectRoles,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubTokenStore tokenStore,
        IConfiguration configuration,
        ILogger<SkillCatalogService> logger,
        IGitHubAccessTokenProvider? accessTokenProvider = null,
        IGitHubSkillTreeClient? treeClient = null,
        IMarketplaceCatalogIndexer? catalogIndexer = null,
        MarketplaceCopilotCapabilityIssuer? marketplaceCapabilityIssuer = null)
        : this(
            skills,
            projects,
            gitInit,
            parser,
            scopeProvider,
            tokenStore,
            logger,
            accessTokenProvider,
            treeClient,
            catalogIndexer,
            projectRoles,
            configuration,
            marketplaceCapabilityIssuer)
    {
    }

    // ── Catalog reads ───────────────────────────────────────────────────────────
    public async Task<(SkillOutcome Outcome, IReadOnlyList<SkillView>? Value)> ListAsync(
        ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Viewer, ct).ConfigureAwait(false);
        if (project is null)
            return (SkillOutcome.NotFound, null);

        var skills = await _skills.ListByProjectAsync(projectId, ct).ConfigureAwait(false);
        var assignments = await _skills.ListAssignmentsByProjectAsync(projectId, ct).ConfigureAwait(false);
        var bySkill = assignments
            .GroupBy(a => a.SkillId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(a => a.AgentName).OrderBy(n => n).ToList());

        var views = skills
            .Select(s => SkillView.From(s, bySkill.TryGetValue(s.Id, out var a) ? a : Array.Empty<string>()))
            .ToList();
        return (SkillOutcome.Ok, views);
    }

    public async Task<(SkillOutcome Outcome, Skill? Value)> GetAsync(
        ProjectId projectId, SkillId id, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Viewer, ct).ConfigureAwait(false);
        if (project is null)
            return (SkillOutcome.NotFound, null);
        var skill = await _skills.GetAsync(projectId, id, ct).ConfigureAwait(false);
        return skill is null ? (SkillOutcome.NotFound, null) : (SkillOutcome.Ok, skill);
    }

    public async Task<SkillOutcome> DeleteAsync(ProjectId projectId, SkillId id, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Contributor, ct).ConfigureAwait(false);
        if (project is null)
            return SkillOutcome.NotFound;
        var removed = await _skills.DeleteAsync(projectId, id, ct).ConfigureAwait(false);
        return removed ? SkillOutcome.Ok : SkillOutcome.NotFound;
    }

    // ── Assignments ───────────────────────────────────────────────────────────────
    public async Task<SkillOutcome> AssignAsync(
        ProjectId projectId, SkillId skillId, string agentName, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Contributor, ct).ConfigureAwait(false);
        if (project is null)
            return SkillOutcome.NotFound;
        if (string.IsNullOrWhiteSpace(agentName))
            return SkillOutcome.Invalid;
        var skill = await _skills.GetAsync(projectId, skillId, ct).ConfigureAwait(false);
        if (skill is null)
            return SkillOutcome.NotFound;
        await _skills.AssignAsync(projectId, skillId, agentName.Trim(), DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return SkillOutcome.Ok;
    }

    public async Task<SkillOutcome> UnassignAsync(
        ProjectId projectId, SkillId skillId, string agentName, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Contributor, ct).ConfigureAwait(false);
        if (project is null)
            return SkillOutcome.NotFound;
        var removed = await _skills.UnassignAsync(projectId, skillId, agentName.Trim(), ct).ConfigureAwait(false);
        return removed ? SkillOutcome.Ok : SkillOutcome.NotFound;
    }

    // ── Connected-repo sync ───────────────────────────────────────────────────────
    public async Task<SkillAcquisitionResult> SyncConnectedRepoAsync(
        ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Contributor, ct).ConfigureAwait(false);
        if (project is null)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.NotFound };

        if (!Directory.Exists(project.WorkingDirectory))
            return new SkillAcquisitionResult { Outcome = SkillOutcome.SourceUnavailable, Error = "Project working directory is unavailable." };

        var sourceRepo = project.Origin.SourceRepository;
        var discovered = DiscoverSkills(project.WorkingDirectory);
        var results = new List<SkillUpsertView>();
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in discovered)
        {
            seenLocations.Add(raw.RelativeLocation);
            var upsert = await UpsertAsync(projectId, raw, SkillProvenance.ConnectedRepoSync, sourceRepo, raw.RelativeLocation, ct)
                .ConfigureAwait(false);
            results.Add(upsert);
        }

        // Mark previously-synced skills whose source location disappeared as Missing (never silently
        // keep them active). Skills from other provenances are untouched.
        var existing = await _skills.ListByProjectAsync(projectId, ct).ConfigureAwait(false);
        var missing = new List<string>();
        foreach (var s in existing.Where(s => s.Provenance == SkillProvenance.ConnectedRepoSync
                                              && s.Status == SkillStatus.Active
                                              && s.SourceLocation is not null
                                              && !seenLocations.Contains(s.SourceLocation)))
        {
            await _skills.UpdateAsync(s with { Status = SkillStatus.Missing, UpdatedAt = DateTimeOffset.UtcNow }, ct)
                .ConfigureAwait(false);
            missing.Add(s.Name);
        }

        return new SkillAcquisitionResult { Outcome = SkillOutcome.Ok, Results = results, MarkedMissing = missing };
    }

    // ── Repo import ───────────────────────────────────────────────────────────────
    public async Task<(SkillOutcome Outcome, string? Error, IReadOnlyList<SkillCandidateView>? Candidates)> PreviewRepoCandidatesAsync(
        ProjectId projectId, string repoUrl, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Viewer, ct).ConfigureAwait(false);
        if (project is null)
            return (SkillOutcome.NotFound, null, null);
        if (string.IsNullOrWhiteSpace(repoUrl))
            return (SkillOutcome.Invalid, "Repository URL is required.", null);

        string? cloneDir = null;
        var cacheClone = false;
        try
        {
            PurgeExpiredPreviewClones();
            var source = SkillImportSource.Parse(repoUrl);
            IReadOnlyList<RawSkill> discovered;
            if (source.RawSkillUri is not null)
            {
                discovered = new[] { await FetchRawSkillAsync(source.RawSkillUri, source.Subpath ?? "SKILL.md", ct).ConfigureAwait(false) };
            }
            else
            {
                cloneDir = await CloneToTempAsync(
                    source.CloneUrl!, repoUrl, ResolveGitHubPrincipal(caller, project), project.Id, ct).ConfigureAwait(false);
                var (checkoutRef, subpath) = await ResolveRefAsync(cloneDir, source, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(checkoutRef))
                    await Task.Run(() => CheckoutRef(cloneDir, checkoutRef!), ct).ConfigureAwait(false);
                discovered = DiscoverSkills(cloneDir, subpath);
                CachePreviewClone(project.Id, repoUrl, cloneDir, subpath);
                cacheClone = true;
            }
            var candidates = BuildCandidates(discovered);
            if (candidates.Count == 0)
                return (SkillOutcome.Invalid, AcceptedSkillSourceMessage, null);
            return (SkillOutcome.Ok, null, candidates);
        }
        catch (SkillImportException ex)
        {
            return (SkillOutcome.Invalid, ex.Message, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clone/scan repository {Repo} for skill import", repoUrl);
            return (SkillOutcome.SourceUnavailable, "Could not access repository (check the URL is a public GitHub repo).", null);
        }
        finally
        {
            if (!cacheClone)
                TryDeleteDirectory(cloneDir);
        }
    }

    public async Task<SkillAcquisitionResult> ImportFromRepoAsync(
        ProjectId projectId, string repoUrl, IReadOnlyList<string>? locations, CallerContext caller, CancellationToken ct, string? marketplaceName = null)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Contributor, ct).ConfigureAwait(false);
        if (project is null)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.NotFound };
        if (string.IsNullOrWhiteSpace(repoUrl))
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = "Repository URL is required." };

        string? cloneDir = null;
        try
        {
            PurgeExpiredPreviewClones();
            var source = SkillImportSource.Parse(repoUrl);
            IReadOnlyList<RawSkill> discovered;
            if (source.RawSkillUri is not null)
            {
                discovered = new[] { await FetchRawSkillAsync(source.RawSkillUri, source.Subpath ?? "SKILL.md", ct).ConfigureAwait(false) };
            }
            else
            {
                var cachedClone = TakePreviewClone(project.Id, repoUrl);
                if (cachedClone is not null && Directory.Exists(cachedClone.Directory))
                {
                    cloneDir = cachedClone.Directory;
                    discovered = DiscoverSkills(cloneDir, cachedClone.Subpath);
                }
                else
                {
                    cloneDir = await CloneToTempAsync(
                        source.CloneUrl!, repoUrl, ResolveGitHubPrincipal(caller, project), project.Id, ct).ConfigureAwait(false);
                    var (checkoutRef, subpath) = await ResolveRefAsync(cloneDir, source, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(checkoutRef))
                        await Task.Run(() => CheckoutRef(cloneDir, checkoutRef!), ct).ConfigureAwait(false);
                    discovered = DiscoverSkills(cloneDir, subpath);
                }
            }
            if (discovered.Count == 0)
                return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = AcceptedSkillSourceMessage };

            IEnumerable<RawSkill> chosen = discovered;
            if (locations is { Count: > 0 })
            {
                var set = new HashSet<string>(locations, StringComparer.OrdinalIgnoreCase);
                chosen = discovered.Where(d => set.Contains(d.RelativeLocation));
            }
            else if (discovered.Count > 1)
            {
                return new SkillAcquisitionResult
                {
                    Outcome = SkillOutcome.Invalid,
                    Error = "Repository contains multiple skills; specify which location(s) to import.",
                };
            }

            var results = new List<SkillUpsertView>();
            foreach (var raw in chosen)
            {
                var upsert = await UpsertAsync(projectId, raw, marketplaceName is null ? SkillProvenance.RepoImport : SkillProvenance.Marketplace, source.SourceRepository, raw.RelativeLocation, ct, marketplaceName)
                    .ConfigureAwait(false);
                results.Add(upsert);
            }
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Ok, Results = results };
        }
        catch (SkillImportException ex)
        {
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import skills from repository {Repo}", repoUrl);
            return new SkillAcquisitionResult { Outcome = SkillOutcome.SourceUnavailable, Error = "Could not access repository (check the URL is a public GitHub repo)." };
        }
        finally
        {
            TryDeleteDirectory(cloneDir);
        }
    }

    // ── Curated marketplaces ───────────────────────────────────────────────────────
    // Marketplace repos are large (tens of MB + full history), so a LibGit2Sharp clone of them can
    // take ~100s and used to hang the "Browse marketplaces" dialog with no timeout. Instead of
    // cloning, fetch only the blobs under the marketplace's subpath via the GitHub Trees API, bound
    // the whole operation by a hard timeout, and surface a clear error rather than an infinite spin.
    //
    // Browse is a lightweight INDEX (skill name + short definition), never a bulk clone. It reads the
    // recursive Git Trees metadata once to enumerate every candidate skill + its location (zero blob
    // downloads), then hydrates each candidate's short description from its SKILL.md frontmatter ALONE
    // — concurrently and bounded by a soft time budget — so the endpoint returns in a few seconds even
    // for a large marketplace like github/awesome-copilot (~400 skills). It NEVER downloads a skill's
    // other resource files at browse time. The full skill payload (SKILL.md + resources) is downloaded
    // only at IMPORT time, for the one skill the user selects.

    public async Task<(SkillOutcome Outcome, string? Error, MarketplaceBrowsePage? Page)> BrowseMarketplaceAsync(
        ProjectId projectId, string owner, string repo, string branch, string subpath,
        string? query, int page, int pageSize, CallerContext caller, CancellationToken ct)
    {
        // Ownership is enforced by the endpoint via ProjectAuthorization (owner OR the trusted
        // agentweaver-internal loopback identity). A second caller.Owns here would silently defeat that
        // exemption, so we keep only a cheap project-existence guard as defense-in-depth.
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return (SkillOutcome.NotFound, null, null);
        if (_treeClient is null)
            return (SkillOutcome.SourceUnavailable, MarketplaceUnavailableMessage, null);

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize <= 0 ? DefaultMarketplacePageSize : Math.Min(pageSize, MaxMarketplacePageSize);
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        string? tempDir = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(MarketplaceFetchTimeout);
        try
        {
            // Curated marketplaces are PUBLIC repos, so browse goes ANONYMOUS on the happy path: no
            // user token is attached to the tree call or the per-page description fetches. This avoids a
            // slow token round-trip (and a token→403/hang→anon-retry) on every request; anonymous reads
            // are fast and 26 requests/page is far under the 60/hr unauthenticated limit. The tree
            // client still keeps an anonymous fallback as a safety net. (Import keeps its auth behavior.)
            var blobs = await _treeClient.ListSubtreeBlobsAsync(owner, repo, branch, subpath, token: null, cts.Token).ConfigureAwait(false);

            // Build the FULL candidate list (name + location) from the tree metadata alone — zero blob
            // downloads. Placeholders let DiscoverSkills compute locations byte-identically to import.
            tempDir = await WriteCandidatePlaceholdersToTempAsync(blobs, cts.Token).ConfigureAwait(false);
            var allCandidates = DiscoverSkills(tempDir, subpath)
                .Select(raw => new MarketplaceCandidate(raw.RelativeLocation, MarketplaceCandidateName(raw.RelativeLocation)))
                .OrderBy(c => c.Location, StringComparer.Ordinal)
                .ToList();

            // An empty UNFILTERED list means the source/subpath is misconfigured; an empty FILTERED list
            // is simply a query with no matches (a valid empty page).
            if (allCandidates.Count == 0)
                return (SkillOutcome.Invalid, AcceptedSkillSourceMessage, null);

            var matched = normalizedQuery is null
                ? allCandidates
                : allCandidates
                    .Where(c => c.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                             || c.Location.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var total = matched.Count;
            var pageItems = matched.Skip((normalizedPage - 1) * normalizedSize).Take(normalizedSize).ToList();

            // Fetch SKILL.md frontmatter descriptions ONLY for this page's items (never resource blobs,
            // never off-page candidates). A page is at most MaxMarketplacePageSize small fetches, so the
            // whole page is fully hydrated in a few seconds for any marketplace — no partial rows.
            var descriptions = await HydratePageDescriptionsAsync(owner, repo, branch, pageItems, token: null, cts.Token).ConfigureAwait(false);
            var candidates = pageItems.Select(c => BuildPagedCandidate(c, descriptions)).ToList();

            var hasMore = (long)normalizedPage * normalizedSize < total;
            return (SkillOutcome.Ok, null, new MarketplaceBrowsePage(candidates, total, normalizedPage, normalizedSize, hasMore));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Marketplace browse timed out reading {Owner}/{Repo} subpath {Subpath}", owner, repo, subpath);
            return (SkillOutcome.SourceUnavailable, MarketplaceTimeoutMessage, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Marketplace browse failed reading {Owner}/{Repo} subpath {Subpath}", owner, repo, subpath);
            return (SkillOutcome.SourceUnavailable, MarketplaceUnavailableMessage, null);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // ── URL-source (auto-detected) marketplaces ────────────────────────────────────
    // A marketplace source added by just a repo URL has NO configured subpath, so its skill layout is
    // auto-detected instead of hardcoded. Browse lists the FULL recursive tree once (anonymous, cheap),
    // then the catalog indexer derives the candidate skills from that tree — the deterministic SKILL.md
    // heuristic covers both flat (github/awesome-copilot) and nested (microsoft/skills plugin) layouts
    // with zero blob downloads; a bounded, fail-closed LLM classifier is the fallback only when the tree
    // has no SKILL.md at all. The parsed catalog is cached per repo revision (tree fingerprint), so the
    // LLM fires at most once per revision, never per page. Pagination, query-before-paginate, anonymous
    // reads and page-only description hydration all match the config-source browse path exactly; import
    // of a selected candidate needs no change (its location is passed as the import subpath).
    public async Task<(SkillOutcome Outcome, string? Error, MarketplaceBrowsePage? Page)> BrowseMarketplaceAutoAsync(
        ProjectId projectId, string owner, string repo, string branch,
        string? query, int page, int pageSize, CallerContext caller, CancellationToken ct, string? parseStrategy = null)
    {
        // Ownership is enforced by the endpoint via ProjectAuthorization (owner OR the trusted
        // agentweaver-internal loopback identity); keep only a project-existence guard here.
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return (SkillOutcome.NotFound, null, null);
        if (_treeClient is null || _catalogIndexer is null)
            return (SkillOutcome.SourceUnavailable, MarketplaceUnavailableMessage, null);

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize <= 0 ? DefaultMarketplacePageSize : Math.Min(pageSize, MaxMarketplacePageSize);
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(MarketplaceFetchTimeout);
        try
        {
            if (_marketplaceCapabilityIssuer is not null)
                await _marketplaceCapabilityIssuer.PruneAsync(cts.Token).ConfigureAwait(false);

            // Anonymous-first, full recursive tree (subpath ""), no placeholder scratch files: candidates
            // are derived in-memory from the tree by the indexer, so browse never touches the filesystem.
            var blobs = await _treeClient.ListSubtreeBlobsAsync(owner, repo, branch, subpath: string.Empty, token: null, cts.Token).ConfigureAwait(false);
            var index = await _catalogIndexer.GetOrBuildForProjectWithCapabilityIssuerAsync(
                owner,
                repo,
                branch,
                blobs,
                capabilityReference: null,
                parseStrategy: parseStrategy,
                cts.Token,
                projectId: project.Id,
                caller: caller,
                issueCapabilityAsync: _marketplaceCapabilityIssuer is null
                    ? null
                    : issueCt => _marketplaceCapabilityIssuer.TryIssueAsync(project.Id, caller, issueCt),
                hasCapabilityAsync: _marketplaceCapabilityIssuer is null
                    ? null
                    : checkCt => _marketplaceCapabilityIssuer.HasActiveBindingAsync(project.Id, caller, checkCt))
                .ConfigureAwait(false);

            if (index.RequiresGitHubConnection)
                return (SkillOutcome.GitHubConnectionRequired, GitHubCopilotConnectionRequirement.RequirementMessage, null);

            if (index.Entries.Count == 0)
                return (SkillOutcome.Invalid, AcceptedSkillSourceMessage, null);

            var matched = normalizedQuery is null
                ? index.Entries
                : index.Entries
                    .Where(e => e.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                             || e.Location.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                             || (e.Description is not null && e.Description.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

            var total = matched.Count;
            var pageEntries = matched.Skip((normalizedPage - 1) * normalizedSize).Take(normalizedSize).ToList();

            // Prefer a description already present in the cached catalog (LLM path); otherwise hydrate the
            // page's SKILL.md frontmatter — only for entries missing a description, only for this page.
            var toHydrate = pageEntries
                .Where(e => string.IsNullOrWhiteSpace(e.Description))
                .Select(e => new MarketplaceCandidate(e.Location, e.Name))
                .ToList();
            var descriptions = await HydratePageDescriptionsAsync(owner, repo, branch, toHydrate, token: null, cts.Token).ConfigureAwait(false);

            var candidates = pageEntries.Select(e => BuildAutoCandidate(e, descriptions)).ToList();
            var hasMore = (long)normalizedPage * normalizedSize < total;
            return (SkillOutcome.Ok, null, new MarketplaceBrowsePage(candidates, total, normalizedPage, normalizedSize, hasMore));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Marketplace auto-browse timed out reading {Owner}/{Repo}", owner, repo);
            return (SkillOutcome.SourceUnavailable, MarketplaceTimeoutMessage, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Marketplace auto-browse failed reading {Owner}/{Repo}", owner, repo);
            return (SkillOutcome.SourceUnavailable, MarketplaceUnavailableMessage, null);
        }
    }

    /// <summary>
    /// Builds a browse-page candidate for an auto-detected catalog entry, preferring the catalog's own
    /// description (LLM path) and otherwise the page-hydrated SKILL.md frontmatter (heuristic path).
    /// </summary>
    private SkillCandidateView BuildAutoCandidate(MarketplaceCatalogEntry entry, IReadOnlyDictionary<string, string> descriptions)
    {
        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            return new SkillCandidateView
            {
                Location = entry.Location,
                Name = entry.Name,
                Description = entry.Description,
                Valid = true,
                ResourceCount = 0,
                Errors = Array.Empty<string>(),
            };
        }

        if (descriptions.TryGetValue(ManifestPathFor(entry.Location), out var markdown) && !string.IsNullOrWhiteSpace(markdown))
        {
            var parsed = _parser.Parse(markdown);
            if (!string.IsNullOrWhiteSpace(parsed.Name) || !string.IsNullOrWhiteSpace(parsed.Description))
            {
                return new SkillCandidateView
                {
                    Location = entry.Location,
                    Name = string.IsNullOrWhiteSpace(parsed.Name) ? entry.Name : parsed.Name,
                    Description = parsed.Description,
                    Valid = parsed.IsValid,
                    ResourceCount = 0,
                    Errors = parsed.IsValid ? Array.Empty<string>() : parsed.Errors,
                };
            }
        }

        return new SkillCandidateView
        {
            Location = entry.Location,
            Name = entry.Name,
            Description = null,
            Valid = true,
            ResourceCount = 0,
            Errors = Array.Empty<string>(),
        };
    }

    public async Task<SkillAcquisitionResult> ImportMarketplaceAsync(
        ProjectId projectId, string owner, string repo, string branch, string subpath,
        IReadOnlyList<string>? locations, CallerContext caller, string marketplaceName, CancellationToken ct)
    {
        // Ownership is enforced by the endpoint via ProjectAuthorization (owner OR the trusted
        // agentweaver-internal loopback identity); keep only a project-existence guard here.
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.NotFound };
        if (_treeClient is null)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.SourceUnavailable, Error = MarketplaceUnavailableMessage };

        string? tempDir = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(MarketplaceFetchTimeout);
        try
        {
            tempDir = await FetchSubtreeToTempAsync(
                owner,
                repo,
                branch,
                subpath,
                ResolveGitHubPrincipal(caller, project),
                project.Id,
                cts.Token).ConfigureAwait(false);
            var discovered = DiscoverSkills(tempDir, subpath);
            if (discovered.Count == 0)
                return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = AcceptedSkillSourceMessage };

            IEnumerable<RawSkill> chosen = discovered;
            if (locations is { Count: > 0 })
            {
                var set = new HashSet<string>(locations, StringComparer.OrdinalIgnoreCase);
                chosen = discovered.Where(d => set.Contains(d.RelativeLocation));
            }
            else if (discovered.Count > 1)
            {
                return new SkillAcquisitionResult
                {
                    Outcome = SkillOutcome.Invalid,
                    Error = "Repository contains multiple skills; specify which location(s) to import.",
                };
            }

            var sourceRepo = $"{owner}/{repo}";
            var results = new List<SkillUpsertView>();
            foreach (var raw in chosen)
            {
                var upsert = await UpsertAsync(projectId, raw, SkillProvenance.Marketplace, sourceRepo, raw.RelativeLocation, cts.Token, marketplaceName)
                    .ConfigureAwait(false);
                results.Add(upsert);
            }
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Ok, Results = results };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Marketplace import timed out reading {Owner}/{Repo} subpath {Subpath}", owner, repo, subpath);
            return new SkillAcquisitionResult { Outcome = SkillOutcome.SourceUnavailable, Error = MarketplaceTimeoutMessage };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Marketplace import failed reading {Owner}/{Repo} subpath {Subpath}", owner, repo, subpath);
            return new SkillAcquisitionResult { Outcome = SkillOutcome.SourceUnavailable, Error = MarketplaceUnavailableMessage };
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private IReadOnlyList<SkillCandidateView> BuildCandidates(IReadOnlyList<RawSkill> discovered) =>
        discovered.Select(raw =>
        {
            var parsed = _parser.Parse(raw.SkillMarkdown, raw.Resources);
            return new SkillCandidateView
            {
                Location = raw.RelativeLocation,
                Name = parsed.Name,
                Description = parsed.Description,
                Valid = parsed.IsValid,
                ResourceCount = raw.Resources.Count,
                Errors = parsed.Errors,
            };
        }).ToList();

    /// <summary>
    /// Builds a single browse-page candidate: its location plus a short definition parsed from the
    /// page-hydrated <c>SKILL.md</c> frontmatter (keyed by repo-root-relative <c>SKILL.md</c> path in
    /// <paramref name="descriptions"/>). Because descriptions are fetched for every item on the page
    /// before this runs, each row carries a real definition; if a manifest genuinely lacks a
    /// description, the directory-derived name is still shown. Import always re-downloads and
    /// re-validates the selected skill, so optimistic validity here never lets a bad skill through.
    /// </summary>
    private SkillCandidateView BuildPagedCandidate(MarketplaceCandidate candidate, IReadOnlyDictionary<string, string> descriptions)
    {
        if (descriptions.TryGetValue(ManifestPathFor(candidate.Location), out var markdown) && !string.IsNullOrWhiteSpace(markdown))
        {
            var parsed = _parser.Parse(markdown);
            if (!string.IsNullOrWhiteSpace(parsed.Name) || !string.IsNullOrWhiteSpace(parsed.Description))
            {
                return new SkillCandidateView
                {
                    Location = candidate.Location,
                    Name = string.IsNullOrWhiteSpace(parsed.Name) ? candidate.Name : parsed.Name,
                    Description = parsed.Description,
                    Valid = parsed.IsValid,
                    ResourceCount = 0,
                    Errors = parsed.IsValid ? Array.Empty<string>() : parsed.Errors,
                };
            }
        }

        return new SkillCandidateView
        {
            Location = candidate.Location,
            Name = candidate.Name,
            Description = null,
            Valid = true,
            ResourceCount = 0,
            Errors = Array.Empty<string>(),
        };
    }

    /// <summary>Display name for a marketplace candidate: the skill's directory name from its location.</summary>
    private static string MarketplaceCandidateName(string location)
    {
        var trimmed = location.EndsWith("/SKILL.md", StringComparison.Ordinal)
            ? location[..^"/SKILL.md".Length]
            : location;
        if (trimmed.Length == 0)
            trimmed = location;
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>Repo-root-relative <c>SKILL.md</c> path for a candidate location (as the tree lists it).</summary>
    private static string ManifestPathFor(string location) =>
        location.Equals("SKILL.md", StringComparison.Ordinal) || location.EndsWith("/SKILL.md", StringComparison.Ordinal)
            ? location
            : location + "/SKILL.md";

    /// <summary>True for a repository-root-relative path that is a skill manifest (<c>SKILL.md</c>).</summary>
    private static bool IsSkillManifest(GitHubTreeBlob blob) =>
        blob.Path.Equals("SKILL.md", StringComparison.Ordinal)
        || blob.Path.EndsWith("/SKILL.md", StringComparison.Ordinal);

    /// <summary>
    /// Writes an empty placeholder for every <c>SKILL.md</c> the tree lists (capped at
    /// <see cref="MaxMarketplaceCandidates"/>) into a fresh temp directory, preserving repo-root-relative
    /// paths, so <see cref="DiscoverSkills"/> reports the exact same candidate locations a clone would —
    /// without downloading a single blob. Descriptions are fetched separately, only for the requested
    /// page.
    /// </summary>
    private async Task<string> WriteCandidatePlaceholdersToTempAsync(IReadOnlyList<GitHubTreeBlob> blobs, CancellationToken ct)
    {
        // Browse writes an empty placeholder SKILL.md for EVERY skill in the tree just so DiscoverSkills
        // can derive candidate locations byte-identically to import. These files are request-scoped
        // throwaways with zero durability need, so they go to a LOCAL ephemeral scratch dir
        // (BrowseScratchRoot) — never the CIFS-backed AppPaths.DataDirectory, whose per-file latency made
        // browsing large marketplaces take tens of seconds. See BrowseScratchRoot for the full rationale.
        var dir = Path.Combine(BrowseScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var written = 0;
        foreach (var blob in blobs)
        {
            if (!IsSkillManifest(blob))
                continue;
            if (written >= MaxMarketplaceCandidates)
                break;
            var safe = SkillPaths.NormalizeRelative(blob.Path);
            if (safe is null)
                continue;
            var full = Path.Combine(dir, safe.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, string.Empty, ct).ConfigureAwait(false);
            written++;
        }

        return dir;
    }

    /// <summary>
    /// Downloads the <c>SKILL.md</c> frontmatter for exactly the candidates on the current page,
    /// concurrently, and returns a map keyed by repo-root-relative <c>SKILL.md</c> path. Only SKILL.md
    /// manifests are fetched — never a skill's other resource files, and never off-page candidates — so
    /// a page hydrates in a few seconds no matter how large the marketplace is. The whole batch is
    /// bounded by the caller's marketplace-fetch timeout.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> HydratePageDescriptionsAsync(
        string owner, string repo, string branch, IReadOnlyList<MarketplaceCandidate> pageItems, string? token, CancellationToken ct)
    {
        var descriptions = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        if (pageItems.Count == 0)
            return descriptions;

        var perFileCap = (long)SkillParser.MaxResourceBytes * 2;
        using var gate = new SemaphoreSlim(MarketplaceIndexConcurrency);
        var fetches = pageItems.Select(async candidate =>
        {
            var manifestPath = ManifestPathFor(candidate.Location);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var text = await _treeClient!.GetRawTextAsync(owner, repo, branch, manifestPath, token, perFileCap, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(text))
                    descriptions[manifestPath] = text;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(fetches).ConfigureAwait(false);
        return descriptions;
    }

    /// <summary>
    /// Downloads the text blobs under <paramref name="subpath"/> into a fresh temp directory,
    /// preserving repo-root-relative paths so <see cref="DiscoverSkills"/> can scan it exactly as if
    /// the repository had been cloned. Used at IMPORT time, where the full skill payload (SKILL.md +
    /// bundled resources) is needed. Oversized and binary blobs are skipped (they never contribute to
    /// skill discovery). Runs bounded by <paramref name="ct"/> and a total-byte ceiling.
    /// </summary>
    private async Task<string> FetchSubtreeToTempAsync(
        string owner,
        string repo,
        string branch,
        string subpath,
        string projectOwner,
        ProjectId projectId,
        CancellationToken ct)
    {
        var token = await ResolveTokenAsync(projectOwner, projectId, ct).ConfigureAwait(false);
        var blobs = await _treeClient!.ListSubtreeBlobsAsync(owner, repo, branch, subpath, token, ct).ConfigureAwait(false);

        var dir = Path.Combine(AppPaths.DataDirectory, "skill-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        using var gate = new SemaphoreSlim(MarketplaceFetchConcurrency);
        var totalBytes = 0L;
        var perFileCap = (long)SkillParser.MaxResourceBytes * 2;

        var downloads = blobs
            .Where(b => b.Size <= perFileCap)
            .Select(async blob =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Interlocked.Read(ref totalBytes) > MaxMarketplaceSubtreeBytes)
                        return;
                    var text = await _treeClient!.GetRawTextAsync(owner, repo, branch, blob.Path, token, perFileCap, ct)
                        .ConfigureAwait(false);
                    if (text is null)
                        return;
                    if (Interlocked.Add(ref totalBytes, Encoding.UTF8.GetByteCount(text)) > MaxMarketplaceSubtreeBytes)
                        return;
                    var safe = SkillPaths.NormalizeRelative(blob.Path);
                    if (safe is null)
                        return;
                    var full = Path.Combine(dir, safe.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllTextAsync(full, text, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

        await Task.WhenAll(downloads).ConfigureAwait(false);
        return dir;
    }

    // ── Upload ────────────────────────────────────────────────────────────────────
    public async Task<SkillAcquisitionResult> UploadFilesAsync(
        ProjectId projectId, IReadOnlyList<UploadedSkillFile> files, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Contributor, ct).ConfigureAwait(false);
        if (project is null)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.NotFound };
        if (files.Count == 0)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = "No files were uploaded." };

        var raws = GroupUploadedFilesIntoSkills(files);
        if (raws.Count == 0)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = "No SKILL.md found in the upload." };

        var results = new List<SkillUpsertView>();
        foreach (var raw in raws)
        {
            var upsert = await UpsertAsync(projectId, raw, SkillProvenance.FileUpload, null, null, ct).ConfigureAwait(false);
            results.Add(upsert);
        }
        return new SkillAcquisitionResult { Outcome = SkillOutcome.Ok, Results = results };
    }

    public async Task<SkillAcquisitionResult> CreateManualSkillAsync(
        ProjectId projectId, CreateSkillRequestDto request, CallerContext caller, CancellationToken ct)
    {
        var project = await LoadAuthorizedProjectAsync(projectId, caller, ProjectRole.Contributor, ct).ConfigureAwait(false);
        if (project is null)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.NotFound };

        var validation = ValidateCreateRequest(request);
        if (validation is not null)
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = validation };

        var markdown = ComposeSkillMarkdown(request.Name.Trim(), request.Description?.Trim() ?? "", request.Instructions.Trim());
        var raw = new RawSkill("SKILL.md", markdown, Array.Empty<SkillResource>());
        var result = await UpsertAsync(projectId, raw, SkillProvenance.Manual, null, null, ct).ConfigureAwait(false);
        return new SkillAcquisitionResult { Outcome = SkillOutcome.Ok, Results = new[] { result } };
    }

    // ── Core upsert (idempotent by content hash, name-keyed) ──────────────────────
    private async Task<SkillUpsertView> UpsertAsync(
        ProjectId projectId, RawSkill raw, SkillProvenance provenance, string? sourceRepo, string? location, CancellationToken ct, string? marketplaceName = null)
    {
        var parsed = _parser.Parse(raw.SkillMarkdown, raw.Resources);
        if (!parsed.IsValid)
        {
            // Malformed skills are rejected with feedback and never silently added. Only flag an EXISTING
            // active skill Malformed when the failing candidate comes from the SAME source (provenance +
            // repo + location) — i.e. a skill that previously synced/imported cleanly has now broken.
            // An unrelated import/upload that merely collides by name must NOT deactivate a valid skill.
            if (!string.IsNullOrWhiteSpace(parsed.Name))
            {
                var existingSameName = await _skills.GetByNameAsync(projectId, parsed.Name!, ct).ConfigureAwait(false);
                if (existingSameName is not null
                    && existingSameName.Status == SkillStatus.Active
                    && existingSameName.Provenance == provenance
                    && string.Equals(existingSameName.SourceRepository, sourceRepo, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existingSameName.SourceLocation, location, StringComparison.OrdinalIgnoreCase))
                {
                    await _skills.UpdateAsync(existingSameName with { Status = SkillStatus.Malformed, UpdatedAt = DateTimeOffset.UtcNow }, ct)
                        .ConfigureAwait(false);
                }
            }
            return new SkillUpsertView { Location = location, Name = parsed.Name, Kind = SkillUpsertKind.Rejected.ToString().ToLowerInvariant(), Errors = parsed.Errors };
        }

        var name = parsed.Name!;
        var hash = SkillParser.ComputeContentHash(name, parsed.Description!, parsed.Instructions, parsed.Resources);
        var now = DateTimeOffset.UtcNow;
        var existing = await _skills.GetByNameAsync(projectId, name, ct).ConfigureAwait(false);

        if (existing is null)
        {
            var skill = new Skill
            {
                Id = SkillId.New(),
                ProjectId = projectId,
                Name = name,
                Description = parsed.Description!,
                Instructions = parsed.Instructions,
                Resources = parsed.Resources,
                Provenance = provenance,
                SourceRepository = sourceRepo,
                SourceLocation = location,
                MarketplaceName = marketplaceName,
                ContentHash = hash,
                Status = SkillStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _skills.InsertAsync(skill, ct).ConfigureAwait(false);
            return new SkillUpsertView { Location = location, Name = name, Kind = SkillUpsertKind.Added.ToString().ToLowerInvariant(), SkillId = skill.Id.ToString() };
        }

        if (existing.ContentHash == hash && existing.Status == SkillStatus.Active)
            return new SkillUpsertView { Location = location, Name = name, Kind = SkillUpsertKind.Unchanged.ToString().ToLowerInvariant(), SkillId = existing.Id.ToString() };

        var updated = existing with
        {
            Description = parsed.Description!,
            Instructions = parsed.Instructions,
            Resources = parsed.Resources,
            Provenance = provenance,
            SourceRepository = sourceRepo,
            SourceLocation = location,
            MarketplaceName = marketplaceName,
            ContentHash = hash,
            Status = SkillStatus.Active,
            UpdatedAt = now,
        };
        await _skills.UpdateAsync(updated, ct).ConfigureAwait(false);
        return new SkillUpsertView { Location = location, Name = name, Kind = SkillUpsertKind.Updated.ToString().ToLowerInvariant(), SkillId = existing.Id.ToString() };
    }

    // ── Discovery / IO helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Scans recognized skill directories one level deep (SKILL.md per skill dir) under a root and
    /// returns the raw skill payloads. Bundled resources are the other text files under the skill dir.
    /// </summary>
    public IReadOnlyList<RawSkill> DiscoverSkills(string root, string? subpath = null)
    {
        var results = new List<RawSkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseRoot = root;
        var prefix = "";
        if (!string.IsNullOrWhiteSpace(subpath))
        {
            var safe = SkillPaths.NormalizeRelative(subpath);
            if (safe is null) return results;
            baseRoot = Path.Combine(root, safe.Replace('/', Path.DirectorySeparatorChar));
            prefix = safe;
        }

        if (File.Exists(baseRoot) && string.Equals(Path.GetFileName(baseRoot), "SKILL.md", StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(baseRoot)!;
            if (!IsReparsePoint(baseRoot) && !IsReparsePoint(dir))
            {
                var markdown = SafeReadText(baseRoot);
                if (markdown is not null)
                    results.Add(new RawSkill(string.IsNullOrWhiteSpace(prefix) ? "SKILL.md" : prefix, markdown, ReadResources(dir)));
            }
            return results;
        }

        if (!Directory.Exists(baseRoot) || IsReparsePoint(baseRoot))
            return results;

        AddSkillDirectory(baseRoot, string.IsNullOrWhiteSpace(prefix) ? "SKILL.md" : $"{prefix}/SKILL.md");

        foreach (var skillDir in Directory.EnumerateDirectories(baseRoot))
        {
            if (IsReparsePoint(skillDir)) continue;
            var location = string.IsNullOrWhiteSpace(prefix)
                ? Path.GetFileName(skillDir)
                : $"{prefix}/{Path.GetFileName(skillDir)}";
            AddSkillDirectory(skillDir, location);
        }

        foreach (var recognized in SkillParser.RecognizedSkillDirectories)
        {
            var baseDir = Path.Combine(baseRoot, recognized.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(baseDir))
                continue;

            foreach (var skillDir in Directory.EnumerateDirectories(baseDir))
            {
                if (IsReparsePoint(skillDir))
                    continue;
                var skillMd = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillMd))
                    continue;

                var location = string.IsNullOrWhiteSpace(prefix)
                    ? $"{recognized}/{Path.GetFileName(skillDir)}"
                    : $"{prefix}/{recognized}/{Path.GetFileName(skillDir)}";
                AddSkillDirectory(skillDir, location);
            }
        }
        return results;

        void AddSkillDirectory(string skillDir, string location)
        {
            if (!seen.Add(location)) return;
            var skillMd = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillMd) || IsReparsePoint(skillMd))
                return;
            var markdown = SafeReadText(skillMd);
            if (markdown is null)
                return;
            results.Add(new RawSkill(location, markdown, ReadResources(skillDir)));
        }
    }

    private static IReadOnlyList<SkillResource> ReadResources(string skillDir)
    {
        var resources = new List<SkillResource>();
        if (IsReparsePoint(skillDir))
            return resources;

        var directories = new Stack<string>();
        directories.Push(skillDir);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                // Do not descend into directory symlinks/junctions or read file symlinks.
                if (IsReparsePoint(entry))
                    continue;

                if (Directory.Exists(entry))
                {
                    if (WorkspacePathGuard.TryResolveContainedPath(skillDir, entry, out var safeDirectory))
                        directories.Push(safeDirectory);
                    continue;
                }

                if (!File.Exists(entry) ||
                    !WorkspacePathGuard.TryResolveContainedPath(skillDir, entry, out var safeFile))
                    continue;

                if (string.Equals(Path.GetFileName(safeFile), "SKILL.md", StringComparison.Ordinal)
                    && string.Equals(Path.GetDirectoryName(safeFile), skillDir, StringComparison.Ordinal))
                    continue;

                var text = SafeReadText(safeFile);
                if (text is null)
                    continue; // unreadable/binary — skipped; validation size caps still apply
                var rel = Path.GetRelativePath(skillDir, safeFile).Replace(Path.DirectorySeparatorChar, '/');
                resources.Add(new SkillResource { RelativePath = rel, Content = text });
            }
        }
        return resources;
    }

    /// <summary>Groups a flat uploaded file list into raw skills keyed by the dir containing SKILL.md.</summary>
    internal static IReadOnlyList<RawSkill> GroupUploadedFilesIntoSkills(IReadOnlyList<UploadedSkillFile> files)
    {
        var normalized = files
            .Select(f => (Safe: SkillPaths.NormalizeRelative(f.RelativePath), File: f))
            .Where(x => x.Safe is not null)
            .Select(x => x.File with { RelativePath = x.Safe! })
            .ToList();

        var skillRoots = normalized
            .Where(f => f.RelativePath.Equals("SKILL.md", StringComparison.Ordinal)
                     || f.RelativePath.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .Select(f => f.RelativePath.Length == "SKILL.md".Length
                ? ""
                : f.RelativePath[..^"/SKILL.md".Length])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var raws = new List<RawSkill>();
        foreach (var rootPrefix in skillRoots)
        {
            var prefix = rootPrefix.Length == 0 ? "" : rootPrefix + "/";
            var skillMdPath = prefix + "SKILL.md";
            var md = normalized.First(f => f.RelativePath.Equals(skillMdPath, StringComparison.Ordinal)).Content;

            var resources = normalized
                .Where(f => f.RelativePath.StartsWith(prefix, StringComparison.Ordinal)
                         && !f.RelativePath.Equals(skillMdPath, StringComparison.Ordinal))
                // exclude nested skills (their own SKILL.md subtree)
                .Where(f => !f.RelativePath[prefix.Length..].Contains("/SKILL.md", StringComparison.Ordinal)
                          || !f.RelativePath.EndsWith("/SKILL.md", StringComparison.Ordinal))
                .Select(f => new SkillResource { RelativePath = f.RelativePath[prefix.Length..], Content = f.Content })
                .Where(r => r.RelativePath.Length > 0)
                .ToList();

            var location = rootPrefix.Length == 0 ? "SKILL.md" : rootPrefix;
            raws.Add(new RawSkill(location, md, resources));
        }
        return raws;
    }

    private async Task<string> CloneToTempAsync(
        string cloneUrl,
        string sourceRepository,
        string owner,
        ProjectId projectId,
        CancellationToken ct)
    {
        // Defense in depth: only wire the caller's GitHub token as a credential when the clone
        // target is exactly github.com. The Parse allowlist already guarantees this, but scoping
        // here ensures a token is never offered (and thus never leaked) to any other host.
        var token = SkillImportSource.IsAllowedCloneHost(cloneUrl)
            ? await ResolveTokenAsync(owner, projectId, ct).ConfigureAwait(false)
            : null;
        var dir = Path.Combine(AppPaths.DataDirectory, "skill-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        // Clone runs synchronously in LibGit2Sharp; offload so we don't block the request thread.
        await Task.Run(
            () => _gitInit.Clone(dir, sourceRepository, token ?? string.Empty, GitClonePurpose.SkillImport),
            ct).ConfigureAwait(false);
        return dir;
    }

    private void CachePreviewClone(ProjectId projectId, string repoUrl, string cloneDir, string? subpath)
    {
        var key = BuildPreviewCloneCacheKey(projectId, repoUrl);
        var entry = new PreviewCloneCacheEntry(cloneDir, subpath, DateTimeOffset.UtcNow.Add(PreviewCloneCacheTtl));
        if (_previewCloneCache.TryGetValue(key, out var existing))
            _previewCloneCache[key] = entry;
        else
            _previewCloneCache.TryAdd(key, entry);

        if (existing is not null && !string.Equals(existing.Directory, cloneDir, StringComparison.Ordinal))
            TryDeleteDirectory(existing.Directory);
    }

    private PreviewCloneCacheEntry? TakePreviewClone(ProjectId projectId, string repoUrl)
    {
        var key = BuildPreviewCloneCacheKey(projectId, repoUrl);
        if (!_previewCloneCache.TryRemove(key, out var entry))
            return null;

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            TryDeleteDirectory(entry.Directory);
            return null;
        }

        return entry;
    }

    private void PurgeExpiredPreviewClones()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _previewCloneCache)
        {
            if (entry.ExpiresAt > now)
                continue;
            if (_previewCloneCache.TryRemove(key, out var removed))
                TryDeleteDirectory(removed.Directory);
        }
    }

    private static string BuildPreviewCloneCacheKey(ProjectId projectId, string repoUrl) =>
        $"{projectId}:{repoUrl.Trim()}";

    /// <summary>
    /// Resolves the checkout ref + subpath for an import source against a freshly cloned repo.
    /// For tree/blob URLs the ref boundary is ambiguous (a branch/tag name may itself contain
    /// slashes), so we enumerate the repo's actual refs and greedily match the LONGEST ref that
    /// is a prefix of the URL segments; the remainder is the subpath. If no ref matches we fail
    /// loudly rather than silently importing the wrong ref.
    /// </summary>
    private async Task<(string? CheckoutRef, string? Subpath)> ResolveRefAsync(string dir, SkillImportSource source, CancellationToken ct)
    {
        if (source.RefSegments is null || source.RefSegments.Count == 0)
            return (source.CheckoutRef, source.Subpath);

        var segments = source.RefSegments;
        return await Task.Run(() =>
        {
            using var repo = new Repository(dir);
            var refNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in repo.Branches)
            {
                var name = b.IsRemote && b.FriendlyName.StartsWith("origin/", StringComparison.Ordinal)
                    ? b.FriendlyName["origin/".Length..]
                    : b.FriendlyName;
                if (!string.Equals(name, "HEAD", StringComparison.Ordinal))
                    refNames.Add(name);
            }
            foreach (var t in repo.Tags)
                refNames.Add(t.FriendlyName);

            for (var take = segments.Count; take >= 1; take--)
            {
                var candidate = string.Join('/', segments.Take(take));
                if (refNames.Contains(candidate))
                {
                    var subpath = take < segments.Count ? string.Join('/', segments.Skip(take)) : null;
                    return ((string?)candidate, subpath);
                }
            }
            throw new SkillImportException(
                "Could not resolve a branch or tag from the URL — the ref does not exist in the repository.");
        }, ct).ConfigureAwait(false);
    }

    private static void CheckoutRef(string dir, string checkoutRef)
    {
        using var repo = new Repository(dir);
        var trimmed = checkoutRef.Trim();
        var branch = repo.Branches[trimmed]
            ?? repo.Branches[$"origin/{trimmed}"];
        if (branch is not null)
        {
            Commands.Checkout(repo, branch);
            return;
        }
        var tag = repo.Tags[trimmed];
        if (tag?.Target is not null)
        {
            Commands.Checkout(repo, tag.Target.Sha);
            return;
        }
        Commands.Checkout(repo, trimmed);
    }

    private static async Task<RawSkill> FetchRawSkillAsync(Uri uri, string location, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.ParseAdd("Agentweaver-SkillImporter/1.0");
        using var resp = await RawHttp.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var markdown = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new RawSkill(location, markdown, Array.Empty<SkillResource>());
    }

    internal static string? ValidateCreateRequest(CreateSkillRequestDto request)
    {
        if (request is null) return "Request body is required.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "name is required.";
        var name = request.Name.Trim();
        if (!SkillNameRegex.IsMatch(name) || SkillPaths.NormalizeRelative(name) != name)
            return "name must be a slug command: lowercase letters, numbers, and hyphens only, up to 64 characters.";
        if (string.IsNullOrWhiteSpace(request.Instructions)) return "instructions is required.";
        if (Encoding.UTF8.GetByteCount(request.Instructions) > SkillParser.MaxInstructionsBytes)
            return $"instructions exceed {SkillParser.MaxInstructionsBytes / 1024} KB.";
        if ((request.Description?.Length ?? 0) > SkillParser.MaxDescriptionLength)
            return $"description exceeds {SkillParser.MaxDescriptionLength} characters.";
        return null;
    }

    public static string ComposeSkillMarkdown(string name, string description, string instructions)
    {
        return $"---\nname: {EscapeYamlScalar(name)}\ndescription: {EscapeYamlScalar(string.IsNullOrWhiteSpace(description) ? name : description)}\n---\n\n{instructions.Trim()}\n";
    }

    private static string EscapeYamlScalar(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal sealed record SkillImportSource(
        string? CloneUrl,
        string? CheckoutRef,
        string? Subpath,
        Uri? RawSkillUri,
        string SourceRepository,
        // Segments after tree/blob (ref + subpath) whose ref boundary can only be resolved
        // against the cloned repo's actual refs — null for non-tree/blob sources.
        IReadOnlyList<string>? RefSegments = null)
    {
        private const string GitHubHost = "github.com";
        private const string RawGitHubHost = "raw.githubusercontent.com";

        private static readonly Regex OwnerRepo = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

        internal static bool IsAllowedCloneHost(string? repoUrl) =>
            Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase);

        public static SkillImportSource Parse(string input)
        {
            var raw = input.Trim();
            // Short "owner/repo" form always maps to the canonical public GitHub HTTPS clone URL.
            if (OwnerRepo.IsMatch(raw))
                return new SkillImportSource($"https://github.com/{raw}.git", null, null, null, raw);

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                throw new SkillImportException(
                    "Skill source must be owner/repo, a public https://github.com repo/tree/blob URL, or a raw https://raw.githubusercontent.com SKILL.md URL.");

            // SSRF guard: only https to the exact GitHub hosts is allowed. Reject http/git/ssh/file/ftp,
            // userinfo tricks (https://github.com@evil.com -> Host=evil.com), and non-default ports.
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new SkillImportException("Only https:// GitHub URLs are supported as a skill source.");
            if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
                throw new SkillImportException("Only public https://github.com URLs (default port, no credentials) are supported.");

            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (string.Equals(uri.Host, RawGitHubHost, StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length >= 4 && string.Equals(parts[^1], "SKILL.md", StringComparison.Ordinal))
                {
                    var sourceRepo = $"{parts[0]}/{parts[1]}";
                    var path = string.Join('/', parts.Skip(3));
                    return new SkillImportSource(null, null, path, uri, sourceRepo);
                }
                throw new SkillImportException("Raw GitHub URLs must point directly at a SKILL.md file.");
            }

            if (string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length >= 2)
                {
                    var repoName = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
                    var clone = $"https://github.com/{parts[0]}/{repoName}.git";
                    var sourceRepo = $"{parts[0]}/{repoName}";
                    if (parts.Length == 2)
                        return new SkillImportSource(clone, null, null, null, sourceRepo);
                    if (parts.Length >= 4 && (parts[2] is "tree" or "blob"))
                    {
                        // Defer ref-vs-subpath boundary to post-clone resolution (slash-containing branches).
                        var refSegments = parts.Skip(3).ToArray();
                        return new SkillImportSource(clone, null, null, null, sourceRepo, refSegments);
                    }
                }
                throw new SkillImportException("Unsupported GitHub URL. Use owner/repo, a repo URL, or a tree/blob URL.");
            }

            // Any other host (internal services, attacker-controlled, etc.) is rejected — never cloned.
            throw new SkillImportException(
                $"Unsupported skill source host '{uri.Host}'. Only github.com and raw.githubusercontent.com are allowed.");
        }
    }

    private async Task<string?> ResolveTokenAsync(
        string owner,
        ProjectId projectId,
        CancellationToken ct)
    {
        try
        {
            var scope = await _scopeProvider.ResolveAsync(owner, projectId.ToString(), ct).ConfigureAwait(false);
            if (_accessTokenProvider is not null)
                return await _accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);
            var entry = await _tokenStore.GetAsync(scope, ct).ConfigureAwait(false);
            return entry.Status == GitHubTokenStatus.SignedIn ? entry.AccessToken : null;
        }
        catch
        {
            return null; // fall back to unauthenticated clone (public repositories)
        }
    }

    private async Task<Project?> LoadAuthorizedProjectAsync(
        ProjectId projectId,
        CallerContext caller,
        ProjectRole minimumRole,
        CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return null;

        return await _projectRoles.HasRoleAsync(caller, projectId, minimumRole, ct).ConfigureAwait(false)
            ? project
            : null;
    }

    private static string ResolveGitHubPrincipal(CallerContext caller, Project project) => caller.User;

    private static string? SafeReadText(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > SkillParser.MaxResourceBytes * 2)
                return null;
            // Reject content with NUL bytes (binary).
            if (Array.IndexOf(bytes, (byte)0) >= 0)
                return null;
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    private static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            // Git objects are marked read-only; clear before delete on Windows.
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception) { /* best-effort temp cleanup */ }
    }
}
