using System.Text.RegularExpressions;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Skills;

/// <summary>
/// A marketplace source resolved for browse/import, unifying the two source kinds behind one shape:
/// administrator-curated CONFIG definitions (image-baked, with a hardcoded <see cref="Subpath"/>) and
/// project-scoped URL sources added at runtime (subpath optional → <see cref="IsAuto"/> when blank).
/// </summary>
public sealed record ResolvedMarketplace(
    string Name,
    string Owner,
    string Repo,
    string Branch,
    string? Subpath,
    string? ParseStrategy,
    bool IsProjectSource)
{
    /// <summary>True when the skill layout must be auto-detected (no configured subpath).</summary>
    public bool IsAuto => string.IsNullOrWhiteSpace(Subpath);
}

/// <summary>Outcome of adding a project marketplace source.</summary>
public enum AddSourceOutcome { Ok, Invalid, Conflict, NotPublic, NotFound, Unavailable }

public sealed record AddSourceResult(AddSourceOutcome Outcome, string? Error = null, ResolvedMarketplace? Source = null);

/// <summary>
/// Merges the administrator-curated config marketplace registry with project-scoped, user-added URL
/// sources, and resolves a marketplace NAME (used in the browse/import routes) to a
/// <see cref="ResolvedMarketplace"/>. Config definitions win on a name clash (they are trusted and
/// cannot be shadowed by a project source). Enforces project ownership on every project-scoped read or
/// mutation via the same <c>caller.Owns(project.Owner)</c> pattern the skill endpoints use.
/// </summary>
public sealed class MarketplaceSourceService
{
    // Accepts: "owner/repo", "https://github.com/owner/repo", ".../owner/repo.git",
    // and tree/blob URLs ".../owner/repo/tree/<branch>/<subpath...>".
    private static readonly Regex GitHubUrlRegex = new(
        @"^(?:https?://github\.com/)?(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+?)(?:\.git)?(?:/(?:tree|blob)/(?<branch>[^/]+)(?:/(?<subpath>.+?))?)?/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NameRegex = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.Compiled);

    private readonly SkillMarketplaceRegistry _registry;
    private readonly IProjectMarketplaceSourceStore _sources;
    private readonly IProjectStore _projects;
    private readonly IGitHubSkillTreeClient? _treeClient;
    private readonly ILogger<MarketplaceSourceService> _logger;

    public MarketplaceSourceService(
        SkillMarketplaceRegistry registry,
        IProjectMarketplaceSourceStore sources,
        IProjectStore projects,
        ILogger<MarketplaceSourceService> logger,
        IGitHubSkillTreeClient? treeClient = null)
    {
        _registry = registry;
        _sources = sources;
        _projects = projects;
        _treeClient = treeClient;
        _logger = logger;
    }

    /// <summary>Config definitions + this project's URL sources, or null when the project is not owned.</summary>
    public async Task<IReadOnlyList<ResolvedMarketplace>?> ListForProjectAsync(
        ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return null;

        var merged = new List<ResolvedMarketplace>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in _registry.ListEnabled())
        {
            merged.Add(ToResolved(def));
            names.Add(def.Name);
        }
        foreach (var src in await _sources.ListByProjectAsync(projectId, ct).ConfigureAwait(false))
        {
            if (!src.Enabled || names.Contains(src.Name))
                continue;
            merged.Add(ToResolved(src));
        }
        return merged.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Resolves a marketplace name to its source, preferring a config definition over a project source
    /// of the same name. Returns null when the project is not owned or no source matches. The
    /// <c>projectOwned</c> out flag distinguishes "not owned/not found project" (→404) from "no such
    /// marketplace" (→404) at the endpoint if needed.
    /// </summary>
    public async Task<ResolvedMarketplace?> ResolveAsync(
        ProjectId projectId, string name, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return null;

        var def = _registry.FindEnabled(name);
        if (def is not null)
            return ToResolved(def);

        var src = await _sources.GetByNameAsync(projectId, name, ct).ConfigureAwait(false);
        return src is { Enabled: true } ? ToResolved(src) : null;
    }

    /// <summary>
    /// Adds a project-scoped marketplace source from a repo URL/slug. Rejects clashes with a config
    /// definition name, duplicate project names, malformed URLs, and — best-effort — non-public repos
    /// (an anonymous tree read must succeed). Step-1 only supports PUBLIC GitHub repos.
    /// </summary>
    public async Task<AddSourceResult> AddSourceAsync(
        ProjectId projectId, string repositoryUrl, string? name, string? branch, string? subpath,
        string? parseStrategy, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return new AddSourceResult(AddSourceOutcome.NotFound);

        if (!TryParseRepositoryUrl(repositoryUrl, out var owner, out var repo, out var urlBranch, out var urlSubpath))
            return new AddSourceResult(AddSourceOutcome.Invalid, "Provide a GitHub repo as owner/repo or a https://github.com/owner/repo URL.");

        var effectiveBranch = FirstNonBlank(branch, urlBranch) ?? "main";
        var effectiveSubpath = FirstNonBlank(subpath, urlSubpath);
        var effectiveName = string.IsNullOrWhiteSpace(name) ? repo : name.Trim();
        if (!NameRegex.IsMatch(effectiveName))
            return new AddSourceResult(AddSourceOutcome.Invalid, "Source name must be 1-64 chars of letters, digits, spaces, '.', '_' or '-'.");

        var strategy = NormalizeStrategy(parseStrategy);
        if (strategy is null)
            return new AddSourceResult(AddSourceOutcome.Invalid, "parse_strategy must be one of: auto, skillmd, llm.");

        if (_registry.FindEnabled(effectiveName) is not null)
            return new AddSourceResult(AddSourceOutcome.Conflict, $"'{effectiveName}' is a built-in marketplace name; choose a different name.");

        // Best-effort public-repo check: an anonymous recursive tree read succeeds only for reachable
        // PUBLIC repos. Private/non-existent repos 404 anonymously and throw here.
        if (_treeClient is not null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                await _treeClient.ListSubtreeBlobsAsync(owner, repo, effectiveBranch, effectiveSubpath ?? string.Empty, token: null, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new AddSourceResult(AddSourceOutcome.Unavailable, "Timed out reaching the repository. Try again in a moment.");
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Rejected marketplace source {Owner}/{Repo}: anonymous tree read failed (private/missing).", owner, repo);
                return new AddSourceResult(AddSourceOutcome.NotPublic, "Repository must be a public GitHub repo reachable anonymously.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var source = new ProjectMarketplaceSource
        {
            ProjectId = projectId,
            SourceId = Guid.NewGuid().ToString("N"),
            Name = effectiveName,
            Repository = $"{owner}/{repo}",
            Branch = effectiveBranch,
            Subpath = effectiveSubpath,
            ParseStrategy = strategy,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var inserted = await _sources.InsertAsync(source, ct).ConfigureAwait(false);
        if (!inserted)
            return new AddSourceResult(AddSourceOutcome.Conflict, $"A marketplace source named '{effectiveName}' already exists in this project.");

        return new AddSourceResult(AddSourceOutcome.Ok, Source: ToResolved(source));
    }

    /// <summary>Removes a project source by name. Returns NotFound when the project isn't owned or no row matched.</summary>
    public async Task<AddSourceOutcome> RemoveSourceAsync(ProjectId projectId, string name, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return AddSourceOutcome.NotFound;

        var removed = await _sources.DeleteByNameAsync(projectId, name, ct).ConfigureAwait(false);
        return removed ? AddSourceOutcome.Ok : AddSourceOutcome.NotFound;
    }

    internal static bool TryParseRepositoryUrl(
        string? input, out string owner, out string repo, out string? branch, out string? subpath)
    {
        owner = repo = string.Empty;
        branch = subpath = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var match = GitHubUrlRegex.Match(input.Trim());
        if (!match.Success)
            return false;

        owner = match.Groups["owner"].Value;
        repo = match.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        branch = match.Groups["branch"].Success ? match.Groups["branch"].Value : null;
        subpath = match.Groups["subpath"].Success ? match.Groups["subpath"].Value.Trim('/') : null;
        if (string.IsNullOrEmpty(subpath))
            subpath = null;
        return owner.Length > 0 && repo.Length > 0;
    }

    private static string? NormalizeStrategy(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy))
            return "auto";
        var s = strategy.Trim().ToLowerInvariant();
        return s is "auto" or "skillmd" or "llm" ? s : null;
    }

    private static string? FirstNonBlank(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a!.Trim() : (!string.IsNullOrWhiteSpace(b) ? b!.Trim() : null);

    private static ResolvedMarketplace ToResolved(SkillMarketplaceDefinition def)
    {
        var (owner, repo) = SplitRepo(def.Repository);
        return new ResolvedMarketplace(def.Name, owner, repo, def.Branch ?? "main",
            string.IsNullOrWhiteSpace(def.Subpath) ? null : def.Subpath, ParseStrategy: null, IsProjectSource: false);
    }

    private static ResolvedMarketplace ToResolved(ProjectMarketplaceSource src)
    {
        var (owner, repo) = SplitRepo(src.Repository);
        return new ResolvedMarketplace(src.Name, owner, repo, string.IsNullOrWhiteSpace(src.Branch) ? "main" : src.Branch!,
            string.IsNullOrWhiteSpace(src.Subpath) ? null : src.Subpath, src.ParseStrategy, IsProjectSource: true);
    }

    private static (string Owner, string Repo) SplitRepo(string repository)
    {
        var trimmed = (repository ?? string.Empty).Trim().Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? (trimmed, string.Empty) : (trimmed[..slash], trimmed[(slash + 1)..]);
    }
}
