using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// The discovered + validated workflows for a single project: the built-in default plus every
/// <c>.agentweaver/workflows/</c> file, each with its validation status (Feature 010). Immutable: a
/// Sync produces a fresh set, so a run that captured the previous set completes on the definition it
/// started with (FR-006).
/// </summary>
public sealed record ProjectWorkflowSet
{
    public required IReadOnlyList<WorkflowLoadResult> Results { get; init; }

    /// <summary>The valid, available workflows (validation passed).</summary>
    public IEnumerable<WorkflowLoadResult> Available => Results.Where(r => r.IsValid);

    public WorkflowLoadResult? FindById(string id) =>
        Results.FirstOrDefault(r => r.IsValid && r.Definition is not null &&
                                    string.Equals(r.Definition.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// Discovers, validates, and caches the workflows available to each project (Feature 010,
/// FR-001/002/003/006/007). Definitions are loaded once per project on first access and cached;
/// <see cref="Sync"/> is the ONLY refresh path (no file-watch, no per-heartbeat reload). All
/// discovery, validation, and resolution is server-side (Principles III, IV). Reads only from the
/// project's own <c>.agentweaver/workflows/</c> directory and never follows references that escape the
/// project sandbox (FR-007, Principle X).
/// </summary>
public sealed class WorkflowRegistry
{
    public const string WorkflowsRelativePath = ".agentweaver/workflows";

    private readonly ConcurrentDictionary<ProjectId, ProjectWorkflowSet> _cache = new();

    /// <summary>Returns the cached set for the project, loading it once on first access (FR-006).</summary>
    public ProjectWorkflowSet GetOrLoad(Project project) =>
        _cache.GetOrAdd(project.Id, _ => Build(project));

    /// <summary>Re-reads <c>.agentweaver/workflows/</c> from disk and replaces the cached set
    /// (FR-006 explicit Sync). Returns the refreshed set.</summary>
    public ProjectWorkflowSet Sync(Project project)
    {
        var set = Build(project);
        _cache[project.Id] = set;
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

    private static ProjectWorkflowSet Build(Project project)
    {
        var results = new List<WorkflowLoadResult>();
        var idToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        // The built-in default is always available so a project always has at least one workflow
        // (FR-005). A project-authored file with the same id replaces it (project customization).
        results.Add(BuiltInWorkflows.Default);
        idToIndex[BuiltInWorkflows.Default.Definition!.Id] = 0;

        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows");
        foreach (var file in EnumerateWorkflowFiles(dir))
        {
            var source = Path.GetFileName(file);
            WorkflowLoadResult result;
            try
            {
                var yaml = File.ReadAllText(file);
                result = WorkflowDefinitionLoader.Load(yaml, source);
            }
            catch (IOException ex)
            {
                result = WorkflowLoadResult.Invalid(source, $"{source}: could not read file — {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                result = WorkflowLoadResult.Invalid(source, $"{source}: could not read file — {ex.Message}");
            }

            if (!result.IsValid || result.Definition is null)
            {
                results.Add(result);
                continue;
            }

            var id = result.Definition.Id;
            if (idToIndex.TryGetValue(id, out var existingIndex))
            {
                if (results[existingIndex].IsBuiltIn)
                {
                    // Project file overrides the built-in default deterministically.
                    results[existingIndex] = result;
                }
                else
                {
                    // Deterministic conflict resolution: first valid file wins; later duplicate excluded.
                    results.Add(WorkflowLoadResult.Invalid(
                        source,
                        $"{source}: duplicate workflow id '{id}' already defined by '{results[existingIndex].Source}'."));
                }
                continue;
            }

            idToIndex[id] = results.Count;
            results.Add(result);
        }

        return new ProjectWorkflowSet { Results = results };
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
