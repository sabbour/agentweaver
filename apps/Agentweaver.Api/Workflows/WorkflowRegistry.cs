using System.Collections.Concurrent;
using Agentweaver.Api;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// The discovered + validated workflows for a single project: the built-in default, catalog library
/// workflows, and every <c>.agentweaver/workflows/</c> file, each with its validation status
/// (Feature 010). Immutable: a Sync produces a fresh set, so a run that captured the previous set
/// completes on the definition it started with (FR-006).
/// </summary>
public sealed record ProjectWorkflowSet
{
    public required IReadOnlyList<WorkflowLoadResult> Results { get; init; }

    /// <summary>The valid, available workflows (validation passed).</summary>
    public IEnumerable<WorkflowLoadResult> Available => Results.Where(r => r.IsValid);

    public WorkflowLoadResult? FindById(string id) =>
        Results.FirstOrDefault(r => r.IsValid && r.Definition is not null &&
                                    string.Equals(r.Definition.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Discovers, validates, and caches the workflows available to each project (Feature 010,
/// FR-001/002/003/006/007). Includes the built-in default, all catalog library workflows (Feature
/// 015 US3), and any project-authored <c>.agentweaver/workflows/</c> files. Definitions are loaded
/// cached per project, and invalidated when the project workflow directory or allowed workflow
/// set changes so every API replica observes the same shared project files. All discovery,
/// validation, and resolution is server-side (Principles III, IV).
/// </summary>
public sealed class WorkflowRegistry
{
    public const string WorkflowsRelativePath = ".agentweaver/workflows";

    private sealed record CachedSet(string Signature, ProjectWorkflowSet Set);

    private readonly ConcurrentDictionary<ProjectId, CachedSet> _cache = new();
    private readonly CatalogReader? _catalog;
    private readonly ILogger<WorkflowRegistry>? _logger;

    /// <summary>Parameterless constructor for tests and back-compat; no catalog library workflows loaded.</summary>
    public WorkflowRegistry() { }

    /// <summary>Production constructor: catalog library workflows are loaded alongside the built-in default.</summary>
    public WorkflowRegistry(CatalogReader catalog, ILogger<WorkflowRegistry>? logger = null)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>Returns the cached set for the project, reloading when shared project files changed.</summary>
    public ProjectWorkflowSet GetOrLoad(Project project)
    {
        var signature = GetSignature(project);
        var cached = _cache.GetOrAdd(project.Id, _ => new CachedSet(signature, Build(project)));
        if (cached.Signature == signature)
            return cached.Set;

        var refreshed = new CachedSet(signature, Build(project));
        _cache[project.Id] = refreshed;
        return refreshed.Set;
    }

    /// <summary>Re-reads <c>.agentweaver/workflows/</c> from disk and replaces the cached set
    /// (FR-006 explicit Sync). Returns the refreshed set.</summary>
    public ProjectWorkflowSet Sync(Project project)
    {
        var signature = GetSignature(project);
        var set = Build(project);
        if (set.Results.Any(r => !r.IsValid))
        {
            _logger?.LogError(
                "Workflow sync for project {ProjectId} found validation errors; caching validation results for replica coherence.",
                project.Id);
        }

        _cache[project.Id] = new CachedSet(signature, set);
        return set;
    }

    /// <summary>Loads (once) and lists the project's discovered workflows with their validation status.</summary>
    public IReadOnlyList<WorkflowLoadResult> List(Project project) => GetOrLoad(project).Results;

    /// <summary>Loads (once) and returns a single valid workflow by id, or null when not found/invalid.</summary>
    public WorkflowLoadResult? Get(Project project, string id) => GetOrLoad(project).FindById(id);

    /// <summary>
    /// Resolves the project's effective default workflow: the configured id when valid, otherwise the
    /// built-in default. A missing configured workflow fails safe to the built-in definition.
    /// </summary>
    public WorkflowLoadResult ResolveDefault(Project project)
    {
        var set = GetOrLoad(project);
        var id = string.IsNullOrWhiteSpace(project.DefaultWorkflowId)
            ? BuiltInWorkflows.DefaultWorkflowId
            : project.DefaultWorkflowId!;

        return set.FindById(id)
            ?? set.FindById(BuiltInWorkflows.DefaultWorkflowId)
            ?? BuiltInWorkflows.Default;
    }

    private ProjectWorkflowSet Build(Project project)
    {
        var results = new List<WorkflowLoadResult>();
        var idToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuiltInWorkflows.DefaultWorkflowId,
        };

        // Canonical YAML per reserved (built-in/catalog) id, keyed by workflow id. This is the single
        // source of truth for a built-in workflow's content: a project-materialized copy that has
        // drifted from it is treated as stale and refreshed back to this canonical text (#168).
        var canonicalYaml = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInWorkflows.DefaultWorkflowId] = DefaultWorkflowTemplate.Yaml,
        };

        // The built-in default is always available so a project always has at least one workflow
        // (FR-005). Its content always comes from the shipped template — a project file with the same
        // id never shadows it (built-in wins, #168).
        results.Add(BuiltInWorkflows.Default);
        idToIndex[BuiltInWorkflows.Default.Definition!.Id] = 0;

        // Catalog library workflows (Feature 015 US3) are available to every project without
        // requiring any project-local files. The catalog version ALWAYS wins over a project-level
        // copy of the same id: a project created before a catalog update must not run the old version
        // forever (#168). Project copies of these ids are treated as stale below.
        if (_catalog is not null)
        {
            foreach (var (yaml, source) in _catalog.LoadAllWorkflowYamls())
            {
                var catalogResult = ValidateBindable(
                    WorkflowDefinitionLoader.Load(yaml, source, isBuiltIn: true));
                if (catalogResult.Definition is not null)
                {
                    reservedIds.Add(catalogResult.Definition.Id);
                    canonicalYaml[catalogResult.Definition.Id] = yaml;
                }
                AddResult(catalogResult, results, idToIndex, _logger);
            }
        }

        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows");
        foreach (var file in EnumerateWorkflowFiles(dir))
        {
            var source = Path.GetFileName(file);
            WorkflowLoadResult result;
            string? rawYaml = null;
            try
            {
                rawYaml = File.ReadAllText(file);
                result = WorkflowDefinitionLoader.Load(rawYaml, source);
            }
            catch (IOException ex)
            {
                result = WorkflowLoadResult.Invalid(source, $"{source}: could not read file — {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                result = WorkflowLoadResult.Invalid(source, $"{source}: could not read file — {ex.Message}");
            }

            result = ValidateBindable(result);
            if (result.Definition is not null && reservedIds.Contains(result.Definition.Id))
            {
                // A project-materialized copy of a built-in/catalog workflow (same id) is expected on
                // disk: it is written into each project's .agentweaver/workflows/ so the directory is
                // visible/editable in the Workspace. The catalog/built-in version is authoritative, so
                // this on-disk copy is NEVER loaded as an override — it is skipped (catalog wins).
                // When the on-disk copy has drifted from the canonical catalog text (e.g. a project
                // created before a catalog update, or a hand-edit of a built-in), refresh it in place
                // so the Workspace reflects the version that will actually run instead of silent stale
                // state (#168).
                RefreshStaleBuiltInCopy(file, result.Definition.Id, rawYaml, canonicalYaml);
                continue;
            }

            AddResult(result, results, idToIndex, _logger);
        }

        return new ProjectWorkflowSet { Results = FilterByAllowedSet(results, project) };
    }

    private static string GetSignature(Project project)
    {
        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows");
        var allowed = project.AllowedWorkflowIds?.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            ?? Enumerable.Empty<string>();
        return DefinitionRegistryCacheSignature.ForDirectory(dir, allowed);
    }

    /// <summary>
    /// Restricts the loaded results to the project's allowed workflow set (the ids declared by the
    /// applied blueprint). When the project has no allowed set (null/empty) ALL workflows are returned
    /// (backward compatible). When a set is present, only valid workflows whose id is in the set are
    /// kept — PLUS the built-in default, which is always available so a project never has zero
    /// workflows (FR-005). Invalid results are preserved so validation status remains visible.
    /// </summary>
    private static IReadOnlyList<WorkflowLoadResult> FilterByAllowedSet(
        IReadOnlyList<WorkflowLoadResult> results, Project project)
    {
        var allowed = project.AllowedWorkflowIds;
        if (allowed is null || allowed.Count == 0) return results;

        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase)
        {
            BuiltInWorkflows.DefaultWorkflowId,
        };

        return results
            .Where(r => !r.IsValid || r.Definition is null || allowedSet.Contains(r.Definition.Id))
            .ToList();
    }

    private static void AddResult(
        WorkflowLoadResult result,
        List<WorkflowLoadResult> results,
        Dictionary<string, int> idToIndex,
        ILogger<WorkflowRegistry>? logger)
    {
        if (!result.IsValid || result.Definition is null)
        {
            results.Add(result);
            return;
        }

        var id = result.Definition.Id;
        if (idToIndex.TryGetValue(id, out var existingIndex))
        {
            if (results[existingIndex].IsBuiltIn && result.IsBuiltIn)
            {
                // Catalog collision rule: higher semantic version wins; when versions tie or cannot be
                // parsed, the source loaded first wins. This makes embedded resource collisions stable.
                var comparison = CompareVersions(result.Definition.Version, results[existingIndex].Definition?.Version);
                if (comparison > 0)
                {
                    logger?.LogInformation(
                        "Workflow catalog id collision for {WorkflowId}: source {NewSource} version {NewVersion} replaces {OldSource} version {OldVersion}.",
                        id, result.Source, result.Definition.Version, results[existingIndex].Source,
                        results[existingIndex].Definition?.Version);
                    results[existingIndex] = result;
                }
                else
                {
                    results.Add(WorkflowLoadResult.Invalid(
                        result.Source,
                        $"{result.Source}: duplicate catalog workflow id '{id}' ignored; '{results[existingIndex].Source}' wins by catalog collision rule.",
                        result.Definition,
                        isBuiltIn: true));
                }
            }
            else
            {
                // Deterministic conflict resolution: first valid non-built-in file wins.
                results.Add(WorkflowLoadResult.Invalid(
                    result.Source,
                    $"{result.Source}: duplicate workflow id '{id}' already defined by '{results[existingIndex].Source}'."));
            }
            return;
        }

        idToIndex[id] = results.Count;
        results.Add(result);
    }

    /// <summary>
    /// Ensures a project-materialized copy of a built-in/catalog workflow on disk matches the
    /// canonical catalog text. If the on-disk copy has drifted (e.g. the project was created before a
    /// catalog update, or the built-in file was hand-edited), logs a warning and overwrites it with
    /// the canonical version so the Workspace never shows silent stale state (#168). Best-effort: any
    /// IO/permission failure is swallowed — the catalog version still wins at runtime regardless.
    /// </summary>
    private void RefreshStaleBuiltInCopy(
        string file,
        string workflowId,
        string? onDiskYaml,
        IReadOnlyDictionary<string, string> canonicalYaml)
    {
        if (onDiskYaml is null) return;
        if (!canonicalYaml.TryGetValue(workflowId, out var canonical)) return;

        // Compare on normalized line endings so a CRLF/LF difference alone is not treated as drift.
        if (NormalizeContent(onDiskYaml) == NormalizeContent(canonical)) return;

        _logger?.LogWarning(
            "Workflow '{WorkflowId}' project copy '{File}' is stale (differs from the built-in/catalog version); refreshing it from the catalog.",
            workflowId, Path.GetFileName(file));

        try
        {
            File.WriteAllText(file, canonical);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex,
                "Could not refresh stale workflow copy '{File}' from the catalog; the catalog version still runs.",
                Path.GetFileName(file));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex,
                "Could not refresh stale workflow copy '{File}' from the catalog; the catalog version still runs.",
                Path.GetFileName(file));
        }
    }

    private static string NormalizeContent(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static WorkflowLoadResult ValidateBindable(WorkflowLoadResult result)
    {
        if (!result.IsValid || result.Definition is null) return result;

        var errors = RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition);
        if (errors.Count == 0) return result;

        return WorkflowLoadResult.Invalid(
            result.Source,
            $"{result.Source}: workflow cannot be bound to the runtime graph: {string.Join(" ", errors)}",
            result.Definition,
            result.IsBuiltIn,
            result.Warnings);
    }

    private static int CompareVersions(string? left, string? right)
    {
        var l = ParseVersion(left);
        var r = ParseVersion(right);
        for (var i = 0; i < Math.Max(l.Length, r.Length); i++)
        {
            var lv = i < l.Length ? l[i] : 0;
            var rv = i < r.Length ? r[i] : 0;
            var cmp = lv.CompareTo(rv);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    private static int[] ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [0];
        return value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var versionPart) ? versionPart : 0)
            .ToArray();
    }

    /// <summary>
    /// Returns the top-level <c>*.yaml</c>/<c>*.yml</c> files in the project's workflows directory,
    /// sorted by name for deterministic conflict resolution. Top-level only (no recursion) and within
    /// the project's own directory — references that would escape the project sandbox are never
    /// followed (FR-007, Principle X). A missing/empty directory yields no files (FR-005 fallback).
    /// </summary>
    private static IEnumerable<string> EnumerateWorkflowFiles(string dir)
    {
        if (!Directory.Exists(dir)) return [];

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return files.OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
    }
}
