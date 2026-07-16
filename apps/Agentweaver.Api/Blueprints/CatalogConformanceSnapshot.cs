using Agentweaver.Api.Workflows;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Blueprints;

/// <summary>
/// Immutable, production-loaded view of the embedded catalog. It is deliberately built through the
/// real workflow loader and binder so an asset that cannot execute is never offered to a project,
/// prompt, or picker.
/// </summary>
public sealed class CatalogConformanceSnapshot
{
    private readonly IReadOnlyList<WorkflowLoadResult> _workflows;
    private readonly IReadOnlyList<CatalogBlueprintEntry> _blueprints;
    private readonly IReadOnlyList<CatalogBlueprintLoadResult> _blueprintLoadFailures;

    public CatalogConformanceSnapshot(CatalogReader catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var workflows = new List<WorkflowLoadResult> { ValidateWorkflow(BuiltInWorkflows.Default) };
        workflows.AddRange(catalog.LoadAllWorkflowYamls()
            .Select(asset => ValidateWorkflow(
                WorkflowDefinitionLoader.Load(asset.Yaml, asset.Source, isBuiltIn: true))));

        // A duplicate id is ambiguous, so neither copy is considered an exportable catalog asset.
        var duplicateWorkflowIds = workflows
            .Where(item => item.Definition is not null)
            .GroupBy(item => item.Definition!.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _workflows = workflows.Select(item =>
            item.Definition is not null && duplicateWorkflowIds.Contains(item.Definition.Id)
                ? WorkflowLoadResult.Invalid(item.Source, $"{item.Source}: duplicate catalog workflow id.", item.Definition, true, item.Warnings)
                : item).ToList();

        var exportableWorkflowIds = _workflows
            .Where(item => item.IsValid && item.Definition is not null)
            .Select(item => item.Definition!.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = catalog.LoadAllBlueprintLoadResults();
        _blueprintLoadFailures = candidates.Where(item => item.Blueprint is null).ToList();
        var duplicateBlueprintIds = candidates
            .Where(item => item.Blueprint is not null)
            .GroupBy(item => item.Blueprint!.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _blueprints = candidates
            .Where(item => item.Blueprint is not null)
            .Select(item => CreateBlueprintEntry(
                item.Source, item.Blueprint!, catalog, exportableWorkflowIds, duplicateBlueprintIds))
            .OrderBy(item => item.Blueprint.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>All parsed catalog blueprints, including unavailable entries for diagnostics.</summary>
    public IReadOnlyList<CatalogBlueprintEntry> Blueprints => _blueprints;

    /// <summary>Malformed or missing embedded blueprint resources, retaining only their safe source scope.</summary>
    public IReadOnlyList<CatalogBlueprintLoadResult> BlueprintLoadFailures => _blueprintLoadFailures;

    /// <summary>All default/catalog workflows after production loading and runtime bindability checks.</summary>
    public IReadOnlyList<WorkflowLoadResult> Workflows => _workflows;

    public CatalogBlueprintEntry? FindBlueprint(string id) =>
        _blueprints.FirstOrDefault(item => string.Equals(item.Blueprint.Id, id, StringComparison.OrdinalIgnoreCase));

    private static WorkflowLoadResult ValidateWorkflow(WorkflowLoadResult result)
    {
        if (!result.IsValid || result.Definition is null)
            return result;

        var idError = CatalogIdentifier.ValidationError(result.Definition.Id, "workflow id");
        if (idError is not null)
            return WorkflowLoadResult.Invalid(result.Source, $"{result.Source}: {idError}", result.Definition, result.IsBuiltIn, result.Warnings);

        var errors = RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition);
        return errors.Count == 0
            ? result
            : WorkflowLoadResult.Invalid(
                result.Source,
                $"{result.Source}: workflow cannot be bound to the runtime graph: {string.Join(" ", errors)}",
                result.Definition,
                result.IsBuiltIn,
                result.Warnings);
    }

    private static CatalogBlueprintEntry CreateBlueprintEntry(
        string source,
        Blueprint blueprint,
        CatalogReader catalog,
        IReadOnlySet<string> exportableWorkflowIds,
        IReadOnlySet<string> duplicateBlueprintIds)
    {
        var codes = new List<string>();
        if (CatalogIdentifier.ValidationError(blueprint.Id, "blueprint id") is not null)
            codes.Add("blueprint_invalid_id");
        if (duplicateBlueprintIds.Contains(blueprint.Id))
            codes.Add("blueprint_duplicate_id");
        if (blueprint.Roster.Count == 0)
            codes.Add("blueprint_missing_roster");
        if (blueprint.Workflows.Count == 0)
            codes.Add("blueprint_missing_workflow");

        AddDuplicateCode(blueprint.Roster, "blueprint_duplicate_role", codes);
        AddDuplicateCode(blueprint.Workflows, "blueprint_duplicate_workflow", codes);
        foreach (var roleId in blueprint.Roster)
        {
            if (CatalogIdentifier.ValidationError(roleId, "role id") is not null)
                codes.Add("role_invalid_id");
            else if (ReservedRoles.IsReserved(roleId))
                codes.Add("role_reserved");
            else
            {
                var roleResult = catalog.LoadRoleResult(roleId);
                if (roleResult.Role is not { } role ||
                    !string.Equals(role.Id, roleId, StringComparison.Ordinal) ||
                    !CatalogIdentifier.IsSafe(role.Id))
                    codes.Add(roleResult.ErrorCode == "malformed_json" ? "role_malformed" : "role_missing");
                else if (catalog.LoadCharterTemplate(roleId) is null)
                    codes.Add("charter_missing");
            }
        }

        foreach (var workflowId in blueprint.Workflows)
        {
            if (CatalogIdentifier.ValidationError(workflowId, "workflow id") is not null)
                codes.Add("workflow_invalid_id");
            else if (!exportableWorkflowIds.Contains(workflowId))
                codes.Add("workflow_unavailable");
        }

        return new CatalogBlueprintEntry(blueprint, source, BlueprintExportability.FromCodes(codes));
    }

    private static void AddDuplicateCode(IReadOnlyList<string> values, string code, List<string> codes)
    {
        if (values.Where(CatalogIdentifier.IsSafe)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            codes.Add(code);
    }
}

/// <summary>Stable, intentionally non-sensitive explanation of whether a catalog blueprint can run.</summary>
public sealed record BlueprintExportability(string Status, IReadOnlyList<string> Codes)
{
    public static BlueprintExportability FromCodes(IEnumerable<string> codes)
    {
        var normalized = codes
            .Where(code => code.Length is > 0 and <= 64 && code.All(c => c is >= 'a' and <= 'z' or '_' or >= '0' and <= '9'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .Take(16)
            .ToList();
        return new BlueprintExportability(normalized.Count == 0 ? "exportable" : "unavailable", normalized);
    }
}

/// <summary>A parsed embedded blueprint together with its source-scoped availability result.</summary>
public sealed record CatalogBlueprintEntry(Blueprint Blueprint, string Source, BlueprintExportability Exportability)
{
    public bool IsExportable => Exportability.Status == "exportable";
}
