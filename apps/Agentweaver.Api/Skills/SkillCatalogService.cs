using System.Text;
using System.Text.Json.Serialization;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Skills;

public enum SkillOutcome
{
    Ok,
    NotFound,
    Invalid,
    SourceUnavailable,
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

/// <summary>
/// Acquisition + assignment application service for the per-project skill catalog. Reuses the git
/// clone plumbing (repo import), the connected-repository working directory (sync), and validates all
/// acquired skills through <see cref="SkillParser"/>. Idempotent by content hash: re-importing or
/// re-syncing an unchanged skill is a no-op; a changed source updates the catalog entry.
/// </summary>
public sealed class SkillCatalogService
{
    private readonly ISkillStore _skills;
    private readonly IProjectStore _projects;
    private readonly ProjectGitInitializer _gitInit;
    private readonly SkillParser _parser;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubAccessTokenProvider? _accessTokenProvider;
    private readonly ILogger<SkillCatalogService> _logger;

    public SkillCatalogService(
        ISkillStore skills,
        IProjectStore projects,
        ProjectGitInitializer gitInit,
        SkillParser parser,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubTokenStore tokenStore,
        ILogger<SkillCatalogService> logger,
        IGitHubAccessTokenProvider? accessTokenProvider = null)
    {
        _skills = skills;
        _projects = projects;
        _gitInit = gitInit;
        _parser = parser;
        _scopeProvider = scopeProvider;
        _tokenStore = tokenStore;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
    }

    // ── Catalog reads ───────────────────────────────────────────────────────────
    public async Task<(SkillOutcome Outcome, IReadOnlyList<SkillView>? Value)> ListAsync(
        ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
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
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return (SkillOutcome.NotFound, null);
        var skill = await _skills.GetAsync(projectId, id, ct).ConfigureAwait(false);
        return skill is null ? (SkillOutcome.NotFound, null) : (SkillOutcome.Ok, skill);
    }

    public async Task<SkillOutcome> DeleteAsync(ProjectId projectId, SkillId id, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return SkillOutcome.NotFound;
        var removed = await _skills.DeleteAsync(projectId, id, ct).ConfigureAwait(false);
        return removed ? SkillOutcome.Ok : SkillOutcome.NotFound;
    }

    // ── Assignments ───────────────────────────────────────────────────────────────
    public async Task<SkillOutcome> AssignAsync(
        ProjectId projectId, SkillId skillId, string agentName, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
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
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return SkillOutcome.NotFound;
        var removed = await _skills.UnassignAsync(projectId, skillId, agentName.Trim(), ct).ConfigureAwait(false);
        return removed ? SkillOutcome.Ok : SkillOutcome.NotFound;
    }

    // ── Connected-repo sync ───────────────────────────────────────────────────────
    public async Task<SkillAcquisitionResult> SyncConnectedRepoAsync(
        ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
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
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return (SkillOutcome.NotFound, null, null);
        if (string.IsNullOrWhiteSpace(repoUrl))
            return (SkillOutcome.Invalid, "Repository URL is required.", null);

        string? cloneDir = null;
        try
        {
            cloneDir = await CloneToTempAsync(repoUrl, project.Owner, ct).ConfigureAwait(false);
            var discovered = DiscoverSkills(cloneDir);
            var candidates = discovered.Select(raw =>
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
            return (SkillOutcome.Ok, null, candidates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clone/scan repository {Repo} for skill import", repoUrl);
            return (SkillOutcome.SourceUnavailable, $"Could not access repository: {ex.Message}", null);
        }
        finally
        {
            TryDeleteDirectory(cloneDir);
        }
    }

    public async Task<SkillAcquisitionResult> ImportFromRepoAsync(
        ProjectId projectId, string repoUrl, IReadOnlyList<string>? locations, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return new SkillAcquisitionResult { Outcome = SkillOutcome.NotFound };
        if (string.IsNullOrWhiteSpace(repoUrl))
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = "Repository URL is required." };

        string? cloneDir = null;
        try
        {
            cloneDir = await CloneToTempAsync(repoUrl, project.Owner, ct).ConfigureAwait(false);
            var discovered = DiscoverSkills(cloneDir);
            if (discovered.Count == 0)
                return new SkillAcquisitionResult { Outcome = SkillOutcome.Invalid, Error = "No recognized skills found in the repository." };

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
                var upsert = await UpsertAsync(projectId, raw, SkillProvenance.RepoImport, repoUrl, raw.RelativeLocation, ct)
                    .ConfigureAwait(false);
                results.Add(upsert);
            }
            return new SkillAcquisitionResult { Outcome = SkillOutcome.Ok, Results = results };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import skills from repository {Repo}", repoUrl);
            return new SkillAcquisitionResult { Outcome = SkillOutcome.SourceUnavailable, Error = $"Could not access repository: {ex.Message}" };
        }
        finally
        {
            TryDeleteDirectory(cloneDir);
        }
    }

    // ── Upload ────────────────────────────────────────────────────────────────────
    public async Task<SkillAcquisitionResult> UploadFilesAsync(
        ProjectId projectId, IReadOnlyList<UploadedSkillFile> files, CallerContext caller, CancellationToken ct)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
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

    // ── Core upsert (idempotent by content hash, name-keyed) ──────────────────────
    private async Task<SkillUpsertView> UpsertAsync(
        ProjectId projectId, RawSkill raw, SkillProvenance provenance, string? sourceRepo, string? location, CancellationToken ct)
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
    public IReadOnlyList<RawSkill> DiscoverSkills(string root)
    {
        var results = new List<RawSkill>();
        foreach (var recognized in SkillParser.RecognizedSkillDirectories)
        {
            var baseDir = Path.Combine(root, recognized.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(baseDir))
                continue;

            foreach (var skillDir in Directory.EnumerateDirectories(baseDir))
            {
                if (IsReparsePoint(skillDir))
                    continue;
                var skillMd = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillMd))
                    continue;

                var location = $"{recognized}/{Path.GetFileName(skillDir)}";
                var markdown = SafeReadText(skillMd);
                if (markdown is null)
                    continue;

                var resources = ReadResources(skillDir);
                results.Add(new RawSkill(location, markdown, resources));
            }
        }
        return results;
    }

    private static IReadOnlyList<SkillResource> ReadResources(string skillDir)
    {
        var resources = new List<SkillResource>();
        foreach (var file in Directory.EnumerateFiles(skillDir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(file), skillDir, StringComparison.Ordinal))
                continue;
            if (IsReparsePoint(file))
                continue;
            var text = SafeReadText(file);
            if (text is null)
                continue; // unreadable/binary — skipped; validation size caps still apply
            var rel = Path.GetRelativePath(skillDir, file).Replace(Path.DirectorySeparatorChar, '/');
            resources.Add(new SkillResource { RelativePath = rel, Content = text });
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

    private async Task<string> CloneToTempAsync(string repoUrl, string owner, CancellationToken ct)
    {
        var token = await ResolveTokenAsync(owner, ct).ConfigureAwait(false);
        var dir = Path.Combine(AppPaths.DataDirectory, "skill-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        // Clone runs synchronously in LibGit2Sharp; offload so we don't block the request thread.
        await Task.Run(() => _gitInit.Clone(dir, repoUrl, token ?? string.Empty), ct).ConfigureAwait(false);
        return dir;
    }

    private async Task<string?> ResolveTokenAsync(string owner, CancellationToken ct)
    {
        try
        {
            var scope = _scopeProvider.Resolve(owner);
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
