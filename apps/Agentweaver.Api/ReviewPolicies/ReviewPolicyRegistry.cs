using System.Collections.Concurrent;
using Agentweaver.Api;
using Agentweaver.Domain;

namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// The discovered + validated review policies for a single project: the built-in default plus every
/// <c>.agentweaver/review-policies/</c> file, each with its validation status (Feature 010). Immutable:
/// a Sync produces a fresh set so a run that resolved the previous set is unaffected.
/// </summary>
public sealed record ProjectReviewPolicySet
{
    public required IReadOnlyList<ReviewPolicyLoadResult> Results { get; init; }

    /// <summary>The valid, available policies (validation passed).</summary>
    public IEnumerable<ReviewPolicyLoadResult> Available => Results.Where(r => r.IsValid);

    /// <summary>Resolves a policy by name (case-sensitive, matching the bound name), or null.</summary>
    public ReviewPolicyLoadResult? FindByName(string name) =>
        Results.FirstOrDefault(r => r.IsValid && r.Policy is not null &&
                                    string.Equals(r.Policy.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// Discovers, validates, and caches the review policies available to each project (Feature 010,
/// FR-025/026/033). Policies are cached per project and invalidated when the shared project
/// policy directory changes so every API replica observes the same definitions. All discovery,
/// validation, and resolution is server-side (Principles III, IV). Reads only from the project's own
/// <c>.agentweaver/review-policies/</c> directory and never follows references that escape the project
/// sandbox (FR-034, Principle X).
///
/// Name-resolution precedence (FR-033): a project binds to its policy BY NAME via
/// <see cref="Project.ActiveReviewPolicyName"/> (the API-stored setting). The NAME is resolved to a
/// definition from the project's <c>.agentweaver/review-policies/</c> files first; a project file named
/// the same as the built-in default REPLACES the built-in (project customization). When nothing on disk
/// matches, the built-in default (RAI + human-review, absorbed by the built-in workflow) is used so every project always has a
/// usable, safe policy even with nothing materialized.
/// </summary>
public sealed class ReviewPolicyRegistry
{
    public const string ReviewPoliciesRelativePath = ".agentweaver/review-policies";

    private sealed record CachedSet(string Signature, ProjectReviewPolicySet Set);

    private readonly ConcurrentDictionary<ProjectId, CachedSet> _cache = new();

    /// <summary>Returns the cached set for the project, reloading when shared project files changed.</summary>
    public ProjectReviewPolicySet GetOrLoad(Project project)
    {
        var signature = GetSignature(project);
        var cached = _cache.GetOrAdd(project.Id, _ => new CachedSet(signature, Build(project)));
        if (cached.Signature == signature)
            return cached.Set;

        var refreshed = new CachedSet(signature, Build(project));
        _cache[project.Id] = refreshed;
        return refreshed.Set;
    }

    /// <summary>Re-reads <c>.agentweaver/review-policies/</c> from disk and replaces the cached set
    /// (explicit Sync). Returns the refreshed set.</summary>
    public ProjectReviewPolicySet Sync(Project project)
    {
        var signature = GetSignature(project);
        var set = Build(project);
        _cache[project.Id] = new CachedSet(signature, set);
        return set;
    }

    /// <summary>Loads (once) and lists the project's discovered policies with their validation status.</summary>
    public IReadOnlyList<ReviewPolicyLoadResult> List(Project project) => GetOrLoad(project).Results;

    /// <summary>Loads (once) and returns a single valid policy by name, or null when not found/invalid.</summary>
    public ReviewPolicyLoadResult? Get(Project project, string name) => GetOrLoad(project).FindByName(name);

    /// <summary>
    /// Resolves the project's EFFECTIVE active policy (FR-027/032/033): the policy named by
    /// <see cref="Project.ActiveReviewPolicyName"/> if it resolves, otherwise the built-in default. A
    /// project that selects a name that no longer resolves still gets the safe default rather than no
    /// policy (FR-032).
    /// </summary>
    public ReviewPolicyLoadResult ResolveActive(Project project)
    {
        var set = GetOrLoad(project);
        var name = string.IsNullOrWhiteSpace(project.ActiveReviewPolicyName)
            ? BuiltInReviewPolicies.DefaultPolicyName
            : project.ActiveReviewPolicyName!;

        return set.FindByName(name)
            ?? set.FindByName(BuiltInReviewPolicies.DefaultPolicyName)
            ?? BuiltInReviewPolicies.Default;
    }

    private static ProjectReviewPolicySet Build(Project project)
    {
        var results = new List<ReviewPolicyLoadResult>();
        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        // The built-in default is always available so a project always has at least one safe policy
        // (FR-032). A project-authored file with the same name replaces it (project customization).
        results.Add(BuiltInReviewPolicies.Default);
        nameToIndex[BuiltInReviewPolicies.Default.Policy!.Name] = 0;

        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "review-policies");
        foreach (var file in EnumeratePolicyFiles(dir))
        {
            var source = Path.GetFileName(file);
            ReviewPolicyLoadResult result;
            try
            {
                var yaml = File.ReadAllText(file);
                if (DefaultReviewPolicyTemplate.TryNormalizeLegacyMaterializedDefault(
                        file, yaml, out var normalizedYaml, out _))
                    yaml = normalizedYaml;
                result = ReviewPolicyLoader.Load(yaml, source);
            }
            catch (IOException ex)
            {
                result = ReviewPolicyLoadResult.Invalid(source, $"{source}: could not read file — {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                result = ReviewPolicyLoadResult.Invalid(source, $"{source}: could not read file — {ex.Message}");
            }

            if (!result.IsValid || result.Policy is null)
            {
                results.Add(result);
                continue;
            }

            var name = result.Policy.Name;
            if (nameToIndex.TryGetValue(name, out var existingIndex))
            {
                if (results[existingIndex].IsBuiltIn)
                {
                    // Project file overrides the built-in default deterministically.
                    results[existingIndex] = result;
                }
                else
                {
                    // Deterministic conflict resolution: first valid file wins; later duplicate excluded.
                    results.Add(ReviewPolicyLoadResult.Invalid(
                        source,
                        $"{source}: duplicate review-policy name '{name}' already defined by '{results[existingIndex].Source}'."));
                }
                continue;
            }

            nameToIndex[name] = results.Count;
            results.Add(result);
        }

        return new ProjectReviewPolicySet { Results = results };
    }

    private static string GetSignature(Project project)
    {
        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "review-policies");
        return DefinitionRegistryCacheSignature.ForDirectory(dir);
    }

    /// <summary>
    /// Returns the top-level <c>*.yaml</c>/<c>*.yml</c> files in the project's review-policies directory,
    /// sorted by name for deterministic conflict resolution. Top-level only (no recursion) and within the
    /// project's own directory — references that would escape the project sandbox are never followed
    /// (FR-034, Principle X). A missing/empty directory yields no files (FR-032 fallback).
    /// </summary>
    private static IEnumerable<string> EnumeratePolicyFiles(string dir)
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
