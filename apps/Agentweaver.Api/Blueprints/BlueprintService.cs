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
    public async Task ApplyAsync(
        string projectId,
        Blueprint blueprint,
        string? generatedWorkflowYaml = null,
        CancellationToken ct = default)
    {
        // Materialize a generated (custom) workflow to the project workspace before applying so the
        // coordinator can select it immediately (FR-063). We fetch the project early for the path.
        if (!string.IsNullOrWhiteSpace(generatedWorkflowYaml))
        {
            var loadResult = WorkflowDefinitionLoader.Load(generatedWorkflowYaml, "generated");
            if (loadResult.IsValid && loadResult.Definition is not null)
            {
                var pid2 = ProjectId.Parse(projectId);
                var project2 = await _projectStore.GetAsync(pid2, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");

                var workflowsDir = Path.Combine(
                    project2.WorkingDirectory,
                    WorkflowRegistry.WorkflowsRelativePath);
                Directory.CreateDirectory(workflowsDir);

                var workflowFile = Path.Combine(workflowsDir, $"{loadResult.Definition.Id}.yaml");
                await File.WriteAllTextAsync(workflowFile, generatedWorkflowYaml, ct).ConfigureAwait(false);
                _workflowRegistry.Sync(project2);

                _logger.LogInformation(
                    "Materialized generated workflow '{WorkflowId}' to {WorkflowFile} for project {ProjectId}",
                    loadResult.Definition.Id, workflowFile, projectId);
            }
            else
            {
                _logger.LogWarning(
                    "Generated workflow YAML failed to parse during ApplyAsync for project {ProjectId}: {Error}",
                    projectId, loadResult.Error);
            }
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

        var pid = ProjectId.Parse(projectId);
        var now = DateTimeOffset.UtcNow;
        // PARTIAL-APPLY RISK: if any of the following store operations fail, previously-applied steps
        // are NOT automatically reverted. The casting proposal was already confirmed above; if a step
        // below fails the roster will have been seeded but project settings may be partially updated.
        // The endpoint wraps ApplyAsync in a try/catch that rolls back the entire project on failure.
        await _projectStore.UpdateDefaultWorkflowAsync(pid, blueprint.Workflow, now, ct).ConfigureAwait(false);
        // PARTIAL-APPLY RISK: review policy update; prior step (workflow) is not reverted on failure.
        await _projectStore.UpdateActiveReviewPolicyAsync(pid, blueprint.ReviewPolicy, now, ct).ConfigureAwait(false);
        // PARTIAL-APPLY RISK: sandbox profile update; prior steps not reverted on failure.
        await _projectStore.UpdateSandboxProfileAsync(pid, blueprint.SandboxProfile, now, ct).ConfigureAwait(false);
        var project = await _projectStore.GetAsync(pid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");
        // PARTIAL-APPLY RISK: sandbox policy file write; prior steps not reverted on failure.
        await _sandboxPolicyStore.SetPolicyAsync(
            CreateSandboxPolicy(blueprint.SandboxProfile, project.WorkingDirectory),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Applied blueprint '{BlueprintId}' to project {ProjectId}: {RoleCount} roles, workflow={Workflow}, review={Review}, sandbox={Sandbox}",
            blueprint.Id, projectId, blueprint.Roster.Count, blueprint.Workflow, blueprint.ReviewPolicy, blueprint.SandboxProfile);
    }

    /// <summary>
    /// Generates a blueprint from a free-text description via the model, then parses and validates it.
    /// The model must roster only catalog roles; output that references an unknown role fails
    /// validation so the endpoint answers 422 (no role is minted).
    /// Library-first workflow matching (FR-062): the LLM selects from the library workflow catalog.
    /// Fallback (FR-063): if the LLM returns no library match, <see cref="IWorkflowGenerator"/> is
    /// invoked to produce a custom workflow draft included in <see cref="BlueprintGenerationResult"/>.
    /// </summary>
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
                    blueprint.SandboxProfile);

                _logger.LogInformation(
                    "Blueprint generation fallback produced workflow '{WorkflowId}' (corrected={Corrected})",
                    generatedWorkflow.Id, wfResult.WasCorrected);
            }
            catch (WorkflowGenerationException ex)
            {
                _logger.LogWarning(ex, "Blueprint generation: IWorkflowGenerator fallback failed");
                // Fall back gracefully: use the built-in default workflow instead.
                blueprint = new Blueprint(
                    blueprint.Id,
                    blueprint.Name,
                    blueprint.Description,
                    blueprint.Roster,
                    [BuiltInWorkflows.DefaultWorkflowId],
                    blueprint.ReviewPolicy,
                    blueprint.SandboxProfile);
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
