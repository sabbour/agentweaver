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
    private readonly ILogger<BlueprintService> _logger;

    public BlueprintService(
        CatalogReader catalog,
        CastingService casting,
        IProjectStore projectStore,
        ISandboxPolicyStore sandboxPolicyStore,
        WorkflowRegistry workflowRegistry,
        ReviewPolicyRegistry reviewPolicyRegistry,
        IBlueprintGenerator generator,
        ILogger<BlueprintService> logger)
    {
        _catalog = catalog;
        _casting = casting;
        _projectStore = projectStore;
        _sandboxPolicyStore = sandboxPolicyStore;
        _workflowRegistry = workflowRegistry;
        _reviewPolicyRegistry = reviewPolicyRegistry;
        _generator = generator;
        _logger = logger;
    }

    public IReadOnlyList<Blueprint> GetPredefined() => _catalog.LoadAllBlueprints();

    public Blueprint? GetPredefinedById(string id) => _catalog.LoadBlueprint(id);

    /// <summary>
    /// Validates a blueprint against the schema and the role constraint: every roster role id must
    /// resolve in the catalog. Blueprints never mint roles, so a roster role that is not in the
    /// catalog is rejected with a clear error.
    /// </summary>
    public BlueprintValidationResult Validate(Blueprint blueprint, Project? project = null)
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
            foreach (var roleId in blueprint.Roster)
            {
                if (string.IsNullOrWhiteSpace(roleId))
                {
                    errors.Add("roster contains an empty role id.");
                    continue;
                }
                if (_catalog.HasRole(roleId)) continue;
                errors.Add($"role '{roleId}' is not in the catalog. " +
                           "Blueprints may roster only existing catalog roles.");
            }
        }

        return errors.Count == 0 ? BlueprintValidationResult.Ok() : new BlueprintValidationResult(false, errors);
    }

    /// <summary>
    /// Applies a blueprint to a project: seeds the roster via the casting pipeline (reusing
    /// <see cref="CastingService"/>) and sets the project's default workflow, active review policy,
    /// and sandbox profile. The blueprint must already be valid (all roster roles in the catalog).
    /// </summary>
    public async Task ApplyAsync(string projectId, Blueprint blueprint, CancellationToken ct)
    {
        var (proposal, _) = await _casting
            .ProposeManualCastAsync(projectId, blueprint.Roster, universeOverride: null, ct)
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

        var validation = Validate(parsed.Blueprint!);
        return validation.Valid
            ? parsed
            : new BlueprintGenerationResult(parsed.Blueprint, validation.Errors);
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
