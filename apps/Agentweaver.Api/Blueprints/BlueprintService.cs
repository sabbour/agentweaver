using Microsoft.Extensions.Logging;
using Agentweaver.Api.Casting;
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Blueprints;

/// <summary>The result of validating a blueprint against the schema and the role constraint.</summary>
public sealed record BlueprintValidationResult(bool Valid, IReadOnlyList<string> Errors)
{
    public static BlueprintValidationResult Ok() => new(true, []);
}

/// <summary>
/// Application service for blueprints (Feature 012): list predefined, validate against the schema
/// and the role constraint (rosters may reference only catalog roles), apply a blueprint to a
/// project (seed the roster via the casting pipeline and set the project's workflow/review-policy/
/// sandbox defaults), and generate a blueprint from a description via the model. Blueprints never
/// mint roles.
/// </summary>
public sealed class BlueprintService
{
    /// <summary>Sandbox profiles a blueprint may select. Kept bounded so a typo is rejected.</summary>
    public static readonly IReadOnlyList<string> KnownSandboxProfiles = ["default", "restricted"];

    private static readonly HashSet<string> _knownSandbox =
        new(KnownSandboxProfiles, StringComparer.OrdinalIgnoreCase);

    private readonly CatalogReader _catalog;
    private readonly CastingService _casting;
    private readonly IProjectStore _projectStore;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly WorkflowRegistry _workflowRegistry;
    private readonly ReviewPolicyRegistry _reviewPolicyRegistry;
    private readonly IBlueprintGenerator _generator;
    private readonly IWorkflowGenerator _workflowGenerator;
    private readonly ILogger<BlueprintService> _logger;

    public BlueprintService(
        CatalogReader catalog,
        CastingService casting,
        IProjectStore projectStore,
        ISandboxPolicyStore sandboxPolicyStore,
        WorkflowRegistry workflowRegistry,
        ReviewPolicyRegistry reviewPolicyRegistry,
        IBlueprintGenerator generator,
        IWorkflowGenerator workflowGenerator,
        ILogger<BlueprintService> logger)
    {
        _catalog = catalog;
        _casting = casting;
        _projectStore = projectStore;
        _sandboxPolicyStore = sandboxPolicyStore;
        _workflowRegistry = workflowRegistry;
        _reviewPolicyRegistry = reviewPolicyRegistry;
        _generator = generator;
        _workflowGenerator = workflowGenerator;
        _logger = logger;
    }

    public IReadOnlyList<Blueprint> GetPredefined() => _catalog.LoadAllBlueprints();

    public Blueprint? GetPredefinedById(string id) => _catalog.LoadBlueprint(id);

    /// <summary>
    /// Validates a blueprint against the schema and the role constraint: every roster role id must
    /// resolve in the catalog. Blueprints never mint roles, so a roster role that is not in the
    /// catalog is rejected with a clear error.
    /// </summary>
    /// <param name="blueprint">The blueprint to validate.</param>
    /// <param name="project">Optional project for workflow/review-policy registry lookups.</param>
    /// <param name="extraKnownWorkflowIds">Additional workflow ids treated as valid (e.g. a freshly
    /// generated workflow not yet on disk). Avoids rejecting a blueprint whose workflows array contains
    /// an id that the registry cannot find because the file hasn't been materialized yet.</param>
    public BlueprintValidationResult Validate(
        Blueprint blueprint,
        Project? project = null,
        IReadOnlySet<string>? extraKnownWorkflowIds = null)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(blueprint.Id)) errors.Add("id is required.");
        if (string.IsNullOrWhiteSpace(blueprint.Name)) errors.Add("name is required.");
        if (string.IsNullOrWhiteSpace(blueprint.ReviewPolicy)) errors.Add("review_policy is required.");

        project ??= ValidationProject();

        // Validate all workflows in the set (Feature 015 US3: blueprints bundle a set of workflows).
        if (blueprint.Workflows.Count == 0)
        {
            errors.Add("workflows must contain at least one workflow id.");
        }
        else
        {
            foreach (var wfId in blueprint.Workflows)
            {
                if (string.IsNullOrWhiteSpace(wfId))
                {
                    errors.Add("workflows list contains an empty id.");
                    continue;
                }
                // A freshly generated workflow is valid even before it is materialized to disk.
                if (extraKnownWorkflowIds?.Contains(wfId) == true) continue;
                if (_workflowRegistry.Get(project, wfId) is null)
                    errors.Add($"workflow '{wfId}' is not available for this project.");
            }
        }
        if (!string.IsNullOrWhiteSpace(blueprint.ReviewPolicy) &&
            _reviewPolicyRegistry.Get(project, blueprint.ReviewPolicy) is null)
            errors.Add($"review_policy '{blueprint.ReviewPolicy}' is not available for this project.");

        if (string.IsNullOrWhiteSpace(blueprint.SandboxProfile))
            errors.Add("sandbox_profile is required.");
        else if (!_knownSandbox.Contains(blueprint.SandboxProfile))
            errors.Add($"sandbox_profile '{blueprint.SandboxProfile}' is not a known profile. " +
                       $"Use one of: {string.Join(", ", KnownSandboxProfiles)}.");

        if (blueprint.Roster is null || blueprint.Roster.Count == 0)
        {
            errors.Add("roster must contain at least one role.");
        }
        else
        {
            // Bespoke roles are minted by generation when no catalog role fits; their ids are valid
            // roster entries that resolve to an inline charter rather than a catalog role.
            var bespokeIds = new HashSet<string>(
                blueprint.BespokeRoles.Select(b => b.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var roleId in blueprint.Roster)
            {
                if (string.IsNullOrWhiteSpace(roleId))
                {
                    errors.Add("roster contains an empty role id.");
                    continue;
                }
                if (_catalog.HasRole(roleId)) continue;
                if (bespokeIds.Contains(roleId)) continue;
                errors.Add($"role '{roleId}' is not in the catalog and has no bespoke definition. " +
                           "Roster roles must be catalog roles or declared in 'bespoke_roles'.");
            }

            // Validate each bespoke role's shape and that it is actually rostered.
            var rosterSet = new HashSet<string>(blueprint.Roster, StringComparer.OrdinalIgnoreCase);
            foreach (var b in blueprint.BespokeRoles)
            {
                if (string.IsNullOrWhiteSpace(b.Id))
                {
                    errors.Add("a bespoke role is missing its 'id'.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(b.Charter))
                    errors.Add($"bespoke role '{b.Id}' is missing its 'charter'.");
                if (_catalog.HasRole(b.Id))
                    errors.Add($"bespoke role '{b.Id}' collides with an existing catalog role id.");
                if (!rosterSet.Contains(b.Id))
                    errors.Add($"bespoke role '{b.Id}' is not referenced in the roster.");
            }
        }

        return errors.Count == 0 ? BlueprintValidationResult.Ok() : new BlueprintValidationResult(false, errors);
    }

    /// <summary>
    /// Applies a blueprint to a project: seeds the roster via the casting pipeline (reusing
    /// <see cref="CastingService"/>) and sets the project's default workflow, active review policy,
    /// and sandbox profile. The blueprint must already be valid (all roster roles in the catalog).
    /// When <paramref name="generatedWorkflowYaml"/> is supplied, the YAML is parsed, written to the
    /// project's <c>.agentweaver/workflows/</c> directory, and the registry is synced so the workflow
    /// is immediately selectable by the coordinator (FR-063).
    /// </summary>
    public async Task<BlueprintValidationResult> ApplyAsync(
        string projectId,
        Blueprint blueprint,
        string? generatedWorkflowYaml = null,
        CancellationToken ct = default)
    {
        var pid = ProjectId.Parse(projectId);
        var projectBefore = await _projectStore.GetAsync(pid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");

        IReadOnlySet<string>? extraKnownWorkflowIds = null;
        WorkflowDefinition? generatedDefinition = null;
        if (!string.IsNullOrWhiteSpace(generatedWorkflowYaml))
        {
            var loadResult = WorkflowDefinitionLoader.Load(generatedWorkflowYaml, "generated");
            if (loadResult.IsValid && loadResult.Definition is not null)
            {
                var wfIdError = ValidateWorkflowFileId(loadResult.Definition.Id);
                if (wfIdError is not null)
                    return new BlueprintValidationResult(false, [wfIdError]);
                generatedDefinition = loadResult.Definition;
                extraKnownWorkflowIds = new HashSet<string>([generatedDefinition.Id], StringComparer.Ordinal);
            }
            else
            {
                return new BlueprintValidationResult(false, [$"generated_workflow_yaml failed to parse: {loadResult.Error}"]);
            }
        }

        var validation = Validate(blueprint, projectBefore, extraKnownWorkflowIds);
        if (!validation.Valid)
            return validation;

        var charterErrors = ValidateBespokeCharterReferences(blueprint, projectBefore.WorkingDirectory);
        if (charterErrors.Count > 0)
            return new BlueprintValidationResult(false, charterErrors);

        var fileSnapshot = FileSnapshot.Capture(projectBefore.WorkingDirectory);
        string? generatedWorkflowFile = null;
        SandboxPolicy? oldSandboxPolicy = null;
        try
        {
            if (generatedDefinition is not null && !string.IsNullOrWhiteSpace(generatedWorkflowYaml))
            {
                var workflowsDir = Path.Combine(
                    projectBefore.WorkingDirectory,
                    WorkflowRegistry.WorkflowsRelativePath);
                Directory.CreateDirectory(workflowsDir);

                generatedWorkflowFile = Path.Combine(workflowsDir, $"{Path.GetFileName(generatedDefinition.Id)}.yaml");
                await File.WriteAllTextAsync(generatedWorkflowFile, generatedWorkflowYaml, ct).ConfigureAwait(false);
                _workflowRegistry.Sync(projectBefore);

                _logger.LogInformation(
                    "Materialized generated workflow '{WorkflowId}' to {WorkflowFile} for project {ProjectId}",
                    generatedDefinition.Id, generatedWorkflowFile, projectId);
            }

        var bespokeById = blueprint.BespokeRoles.Count == 0
            ? null
            : blueprint.BespokeRoles.ToDictionary(b => b.Id, b => b, StringComparer.OrdinalIgnoreCase);

        var (proposal, _) = await _casting
            .ProposeManualCastAsync(projectId, blueprint.Roster, universeOverride: null, ct, bespokeById)
            .ConfigureAwait(false);
        await _casting
            .ConfirmProposalAsync(projectId, proposal.ProposalId, intent: "new", ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await _projectStore.UpdateDefaultWorkflowAsync(pid, blueprint.Workflow, now, ct).ConfigureAwait(false);
        var allowedWorkflowIds = blueprint.Workflows.Count > 0
            ? blueprint.Workflows.ToList()
            : null;
        await _projectStore.UpdateAllowedWorkflowIdsAsync(pid, allowedWorkflowIds, now, ct).ConfigureAwait(false);
        await _projectStore.UpdateActiveReviewPolicyAsync(pid, blueprint.ReviewPolicy, now, ct).ConfigureAwait(false);
        await _projectStore.UpdateSandboxProfileAsync(pid, blueprint.SandboxProfile, now, ct).ConfigureAwait(false);
        var project = await _projectStore.GetAsync(pid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");
        _workflowRegistry.Sync(project);
        oldSandboxPolicy = await _sandboxPolicyStore.GetPolicyAsync(project.WorkingDirectory, ct).ConfigureAwait(false);
        await _sandboxPolicyStore.SetPolicyAsync(
            CreateSandboxPolicy(blueprint.SandboxProfile, project.WorkingDirectory),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Applied blueprint '{BlueprintId}' to project {ProjectId}: {RoleCount} roles, workflow={Workflow}, review={Review}, sandbox={Sandbox}",
            blueprint.Id, projectId, blueprint.Roster.Count, blueprint.Workflow, blueprint.ReviewPolicy, blueprint.SandboxProfile);
            return BlueprintValidationResult.Ok();
        }
        catch
        {
            fileSnapshot.Restore();
            if (generatedWorkflowFile is not null && !fileSnapshot.Contains(generatedWorkflowFile))
                TryDeleteFile(generatedWorkflowFile);

            var rollbackAt = DateTimeOffset.UtcNow;
            await _projectStore.UpdateDefaultWorkflowAsync(pid, projectBefore.DefaultWorkflowId, rollbackAt, CancellationToken.None).ConfigureAwait(false);
            await _projectStore.UpdateAllowedWorkflowIdsAsync(pid, projectBefore.AllowedWorkflowIds, rollbackAt, CancellationToken.None).ConfigureAwait(false);
            await _projectStore.UpdateActiveReviewPolicyAsync(pid, projectBefore.ActiveReviewPolicyName, rollbackAt, CancellationToken.None).ConfigureAwait(false);
            await _projectStore.UpdateSandboxProfileAsync(pid, projectBefore.SandboxProfile, rollbackAt, CancellationToken.None).ConfigureAwait(false);
            if (oldSandboxPolicy is not null)
                await _sandboxPolicyStore.SetPolicyAsync(oldSandboxPolicy, CancellationToken.None).ConfigureAwait(false);
            _workflowRegistry.Sync(projectBefore);
            throw;
        }
    }

    /// <summary>
    /// Generates a blueprint from a free-text description via the model, then parses and validates it.
    /// The model must roster only catalog roles; output that references an unknown role fails
    /// validation so the endpoint answers 422 (no role is minted).
    /// Library-first workflow matching (FR-062): the LLM selects from the library workflow catalog.
    /// Fallback (FR-063): if the LLM returns no library match, <see cref="IWorkflowGenerator"/> is
    /// invoked to produce a custom workflow draft included in <see cref="BlueprintGenerationResult"/>.
    /// </summary>
    private static string? ValidateWorkflowFileId(string workflowId)
    {
        var fileName = Path.GetFileName(workflowId);
        if (!string.Equals(fileName, workflowId, StringComparison.Ordinal) ||
            workflowId.Contains('.', StringComparison.Ordinal) ||
            workflowId.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            return $"workflow id '{workflowId}' is not safe for use as a filename.";
        return null;
    }

    private static IReadOnlyList<string> ValidateBespokeCharterReferences(Blueprint blueprint, string projectRoot)
    {
        var errors = new List<string>();
        foreach (var role in blueprint.BespokeRoles)
        {
            var value = role.Charter.Trim();
            string? path = null;
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                path = value["file:".Length..].Trim();
            else if (!value.Contains('\n') &&
                     (value.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                      value.Contains(Path.DirectorySeparatorChar) ||
                      value.Contains(Path.AltDirectorySeparatorChar)))
                path = value;

            if (path is null) continue;
            var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(projectRoot, path));
            if (!File.Exists(fullPath))
                errors.Add($"bespoke role '{role.Id}' references missing charter file '{path}'.");
        }
        return errors;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private sealed class FileSnapshot
    {
        private readonly string _root;
        private readonly Dictionary<string, byte[]?> _files;
        private readonly IReadOnlyList<string> _watchedDirectories;

        private FileSnapshot(string root, Dictionary<string, byte[]?> files, IReadOnlyList<string> watchedDirectories)
        {
            _root = root;
            _files = files;
            _watchedDirectories = watchedDirectories;
        }

        public static FileSnapshot Capture(string root)
        {
            var files = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            CapturePath(root, ".squad", files);
            CapturePath(root, ".agentweaver", files);
            CapturePath(root, Path.Combine(".github", "agents"), files);
            CaptureFile(root, ".gitattributes", files);
            CaptureFile(root, ".gitignore", files);
            return new FileSnapshot(root, files, [".squad", ".agentweaver", Path.Combine(".github", "agents")]);
        }

        public bool Contains(string path) =>
            _files.ContainsKey(Path.GetRelativePath(_root, path));

        public void Restore()
        {
            foreach (var relDir in _watchedDirectories)
            {
                var fullDir = Path.Combine(_root, relDir);
                if (!Directory.Exists(fullDir)) continue;
                foreach (var file in Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(_root, file);
                    if (!_files.ContainsKey(rel))
                        File.Delete(file);
                }
            }

            foreach (var rel in _files.Keys)
            {
                var full = Path.Combine(_root, rel);
                if (_files[rel] is null)
                {
                    if (File.Exists(full)) File.Delete(full);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, _files[rel]!);
            }
        }

        private static void CapturePath(string root, string relativePath, Dictionary<string, byte[]?> files)
        {
            var full = Path.Combine(root, relativePath);
            if (!Directory.Exists(full)) return;
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                files[Path.GetRelativePath(root, file)] = File.ReadAllBytes(file);
        }

        private static void CaptureFile(string root, string relativePath, Dictionary<string, byte[]?> files)
        {
            var full = Path.Combine(root, relativePath);
            files[relativePath] = File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
    }

    public async Task<BlueprintGenerationResult> GenerateAsync(string description, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await _generator.GenerateRawAsync(description, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blueprint generation model run failed");
            return new BlueprintGenerationResult(null, ["The blueprint generation model run failed to complete."]);
        }

        var parsed = BlueprintGenerationParser.Parse(raw);
        if (!parsed.Succeeded)
            return parsed;

        var blueprint = parsed.Blueprint!;

        // FR-063: if the LLM selected no library workflow (empty array or only the sentinel "custom"),
        // delegate to IWorkflowGenerator to produce a bespoke workflow draft.
        WorkflowDefinition? generatedWorkflow = null;
        string? generatedWorkflowYaml = null;
        var warnings = new List<string>(parsed.Warnings);

        // FR-063: the LLM signals "no library workflow fits" with an empty array. Treat an empty set,
        // an all-blank/"custom" set, or the legacy "default" sentinel as needing the fallback generator
        // so a stale ["default"] never suppresses CopilotWorkflowGenerator.
        bool needsFallback = blueprint.Workflows.Count == 0 ||
            blueprint.Workflows.All(id => string.IsNullOrWhiteSpace(id) ||
                string.Equals(id, "custom", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "default", StringComparison.OrdinalIgnoreCase));

        if (needsFallback)
        {
            _logger.LogInformation(
                "Blueprint generation: no library workflow matched; invoking IWorkflowGenerator fallback");
            try
            {
                var wfRequest = new WorkflowGenerationRequest(
                    Description: description,
                    TeamRoles: blueprint.Roster.Count > 0 ? blueprint.Roster.ToList() : null);
                var wfResult = await _workflowGenerator.GenerateAsync(wfRequest, ct).ConfigureAwait(false);
                generatedWorkflow = wfResult.Workflow;
                generatedWorkflowYaml = wfResult.GeneratedYaml;

                // Rebuild the blueprint with the generated workflow id so it references the right workflow.
                blueprint = new Blueprint(
                    blueprint.Id,
                    blueprint.Name,
                    blueprint.Description,
                    blueprint.Roster,
                    [generatedWorkflow.Id],
                    blueprint.ReviewPolicy,
                    blueprint.SandboxProfile)
                {
                    BespokeRoles = blueprint.BespokeRoles,
                };

                _logger.LogInformation(
                    "Blueprint generation fallback produced workflow '{WorkflowId}' (corrected={Corrected})",
                    generatedWorkflow.Id, wfResult.WasCorrected);
            }
            catch (WorkflowGenerationException ex)
            {
                _logger.LogWarning(ex, "Blueprint generation: IWorkflowGenerator fallback failed");
                // Fall back gracefully: use the built-in default workflow instead.
                warnings.Add("Workflow generation failed; the built-in default workflow was selected. Review the blueprint before applying.");
                blueprint = new Blueprint(
                    blueprint.Id,
                    blueprint.Name,
                    blueprint.Description,
                    blueprint.Roster,
                    [BuiltInWorkflows.DefaultWorkflowId],
                    blueprint.ReviewPolicy,
                    blueprint.SandboxProfile)
                {
                    BespokeRoles = blueprint.BespokeRoles,
                };
            }
        }

        // Treat the generated workflow id (if any) as known so Validate doesn't reject it for not
        // being on disk yet — it will be materialized at project creation time (FR-063).
        IReadOnlySet<string>? extraKnown = generatedWorkflow is not null
            ? new HashSet<string>([generatedWorkflow.Id], StringComparer.Ordinal)
            : null;

        var validation = Validate(blueprint, extraKnownWorkflowIds: extraKnown);
        var result = validation.Valid
            ? new BlueprintGenerationResult(blueprint, [])
            : new BlueprintGenerationResult(blueprint, validation.Errors);

        return result with
        {
            GeneratedWorkflow = generatedWorkflow,
            GeneratedWorkflowYaml = generatedWorkflowYaml,
            Warnings = warnings,
        };
    }

    public static SandboxPolicy CreateSandboxPolicy(string sandboxProfile, string repositoryPath) =>
        sandboxProfile.Trim().ToLowerInvariant() switch
        {
            "default" => SandboxPolicy.Default(repositoryPath),
            "restricted" => SandboxPolicy.Default(repositoryPath) with { RequireApprovalForAllShell = true },
            _ => throw new ArgumentException($"Unknown sandbox profile '{sandboxProfile}'.", nameof(sandboxProfile)),
        };

    public static Project ValidationProject(string? workingDirectory = null) => new()
    {
        Id = ProjectId.New(),
        Name = "Blueprint validation",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        DefaultBranch = "main",
        Owner = "system",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
