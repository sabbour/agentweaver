using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

internal static class CoordinatorWorkflowCatalog
{
    private const string BuiltInWorkflowId = "default";

    private static readonly WorkflowDefinition BuiltInDefault = new(
        BuiltInWorkflowId,
        "1.0.0",
        "coordinator-catalog-v1",
        WorkflowDefinitionOrigin.BuiltIn,
        1,
        [
            new WorkflowStepDefinition(
                "build-test",
                "Record a typed platform build-and-test request.",
                WorkflowStepMode.Platform,
                0,
                new WorkflowCardinality(0, 1),
                [],
                [],
                [],
                [],
                [],
                null,
                WorkflowPlatformGate.BuildTest)
        ]);

    private static readonly ImmutableArray<WorkflowDefinition> RegisteredDefinitions =
        [BuiltInDefault];

    public static AuthorizedWorkflowCatalog ForSelection(
        EffectiveRunSelection selection,
        WorkflowDefinition? proposedGeneratedDefinition = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var projectConfiguration = GetProjectConfiguration(selection.Snapshot);
        var configuredDefault = ReadOptionalIdentifier(projectConfiguration, "defaultWorkflowId");
        var allowedIds = ReadBlueprintWorkflowIds(projectConfiguration);
        var available = allowedIds.IsEmpty
            ? RegisteredDefinitions
            : RegisteredDefinitions.Where(definition =>
                    definition.Id.Equals(BuiltInWorkflowId, StringComparison.OrdinalIgnoreCase) ||
                    allowedIds.Contains(definition.Id))
                .ToImmutableArray();

        var selectedDefault = available.FirstOrDefault(definition =>
                                  configuredDefault is not null &&
                                  definition.Id.Equals(configuredDefault, StringComparison.OrdinalIgnoreCase))
                              ?? available.FirstOrDefault(definition =>
                                  definition.Id.Equals(BuiltInWorkflowId, StringComparison.OrdinalIgnoreCase))
                              ?? RegisteredDefinitions.Single(definition =>
                                  definition.Id.Equals(BuiltInWorkflowId, StringComparison.OrdinalIgnoreCase));

        if (proposedGeneratedDefinition is not null)
        {
            if (proposedGeneratedDefinition.Origin != WorkflowDefinitionOrigin.Generated ||
                !IsStableIdentifier(proposedGeneratedDefinition.Id) ||
                available.Any(definition =>
                    definition.Id.Equals(
                        proposedGeneratedDefinition.Id, StringComparison.OrdinalIgnoreCase)))
                throw new CoordinationException(
                    "coordinator_generated_workflow_invalid",
                    StatusCodes.Status400BadRequest);
            available = available.Add(proposedGeneratedDefinition);
        }

        return new AuthorizedWorkflowCatalog(selectedDefault, available);
    }

    public static WorkPlanRunSelectionContext CreateRunSelectionContext(JsonElement selection)
    {
        var projectConfiguration = GetProjectConfiguration(selection);
        var charteredAgents = ReadCharteredAgents(projectConfiguration);
        var casting = ReadCasting(projectConfiguration);
        var models = ReadEffectiveModelReferences(selection);
        var roles = casting
            .Where(item => charteredAgents.Contains((item.AgentId, item.RoleId)))
            .GroupBy(item => item.RoleId, StringComparer.Ordinal)
            .Select(group => new RoleRunSelection(
                group.Key,
                group.Select(item => item.AgentId).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToImmutableArray(),
                models,
                ImmutableArray<string>.Empty))
            .OrderBy(role => role.RoleId, StringComparer.Ordinal)
            .ToImmutableArray();
        return new WorkPlanRunSelectionContext(roles, IsolationProviderBinding: null);
    }

    public static int ReadMaxChildren(JsonElement selection)
        => ReadRunLimit(selection, "maxChildren", minimum: 0, maximum: 100);

    public static int ReadMaxConcurrentChildren(JsonElement selection)
        => ReadRunLimit(selection, "maxConcurrentChildren", minimum: 1, maximum: 32);

    public static int ReadMaxWallTimeSeconds(JsonElement selection)
        => ReadRunLimit(selection, "maxWallTimeSeconds", minimum: 60, maximum: 86400);

    public static bool IsWallTimeLimitReached(
        DateTimeOffset runStartedAt,
        int maxWallTimeSeconds,
        DateTimeOffset now)
    {
        if (runStartedAt == default)
            throw new ArgumentOutOfRangeException(nameof(runStartedAt));
        return now >= runStartedAt.AddSeconds(maxWallTimeSeconds);
    }

    public static (
        int? MaxModelTurns,
        int? MaxToolCalls,
        int? MaxPromptTokens,
        int? MaxRevisionAttempts,
        decimal? CopilotSoftCreditLimit,
        decimal? CopilotHardCreditLimit)
        ReadRuntimeBudgetLimits(JsonElement selection)
    {
        var runLimits = ReadRunLimits(selection);
        var modelTurns = ReadOptionalBoundedInt(runLimits, "maxModelTurns", 1, 1000);
        var toolCalls = ReadOptionalBoundedInt(runLimits, "maxToolCalls", 1, 10000);
        var promptTokens = ReadOptionalBoundedInt(runLimits, "maxPromptTokens", 1024, 200000);
        var revisionAttempts = ReadOptionalBoundedInt(runLimits, "maxRevisionAttempts", 0, int.MaxValue);
        var soft = ReadOptionalNonNegativeDecimal(runLimits, "copilotSoftCreditLimit");
        var hard = ReadOptionalNonNegativeDecimal(runLimits, "copilotHardCreditLimit");
        if (soft is { } softLimit && hard is { } hardLimit && softLimit > hardLimit)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        return (modelTurns, toolCalls, promptTokens, revisionAttempts, soft, hard);
    }

    private static int ReadRunLimit(
        JsonElement selection,
        string propertyName,
        int minimum,
        int maximum)
    {
        var runLimits = ReadRunLimits(selection);
        if (!runLimits.TryGetProperty(propertyName, out var limitValue) ||
            limitValue.ValueKind != JsonValueKind.Number || !limitValue.TryGetInt32(out var value) ||
            value < minimum ||
            value > maximum)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid",
                StatusCodes.Status502BadGateway);
        return value;
    }

    private static JsonElement ReadRunLimits(JsonElement selection)
    {
        if (selection.ValueKind != JsonValueKind.Object ||
            !selection.TryGetProperty("runLimits", out var runLimits) ||
            runLimits.ValueKind != JsonValueKind.Object)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        return runLimits;
    }

    private static int? ReadOptionalBoundedInt(
        JsonElement limits, string propertyName, int minimum, int maximum)
    {
        if (!limits.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) ||
            result < minimum || result > maximum)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        return result;
    }

    private static decimal? ReadOptionalNonNegativeDecimal(JsonElement limits, string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result) || result < 0)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        return result;
    }

    private static JsonElement GetProjectConfiguration(JsonElement selection)
    {
        if (selection.ValueKind != JsonValueKind.Object ||
            !selection.TryGetProperty("projectConfiguration", out var projectConfiguration) ||
            projectConfiguration.ValueKind != JsonValueKind.Object)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid",
                StatusCodes.Status502BadGateway);
        return projectConfiguration;
    }

    private static string? ReadOptionalIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String ||
            !IsStableIdentifier(value.GetString()))
            throw new CoordinationException(
                "projects_run_selection_contract_invalid",
                StatusCodes.Status502BadGateway);
        return value.GetString();
    }

    private static ImmutableHashSet<string> ReadBlueprintWorkflowIds(JsonElement configuration)
    {
        if (!configuration.TryGetProperty("blueprintWorkflowReferences", out var references) ||
            references.ValueKind != JsonValueKind.Array)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid",
                StatusCodes.Status502BadGateway);
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.Object ||
                !reference.TryGetProperty("workflowId", out var workflowId) ||
                workflowId.ValueKind != JsonValueKind.String ||
                !IsStableIdentifier(workflowId.GetString()) ||
                !builder.Add(workflowId.GetString()!))
                throw new CoordinationException(
                    "projects_run_selection_contract_invalid",
                    StatusCodes.Status502BadGateway);
        }
        return builder.ToImmutable();
    }

    private static ImmutableHashSet<(string AgentId, string RoleId)> ReadCharteredAgents(
        JsonElement configuration)
    {
        if (!configuration.TryGetProperty("agentCharters", out var charters) ||
            charters.ValueKind != JsonValueKind.Array)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid",
                StatusCodes.Status502BadGateway);
        var builder = ImmutableHashSet.CreateBuilder<(string AgentId, string RoleId)>();
        foreach (var charter in charters.EnumerateArray())
        {
            if (charter.ValueKind != JsonValueKind.Object ||
                !TryReadIdentifier(charter, "agentId", out var agentId) ||
                !TryReadIdentifier(charter, "role", out var roleId) ||
                !charter.TryGetProperty("charter", out var text) ||
                text.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(text.GetString()) ||
                !builder.Add((agentId, roleId)))
                throw new CoordinationException(
                    "projects_run_selection_contract_invalid",
                    StatusCodes.Status502BadGateway);
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<(string AgentId, string RoleId)> ReadCasting(JsonElement configuration)
    {
        if (!configuration.TryGetProperty("casting", out var casting) ||
            casting.ValueKind != JsonValueKind.Array)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid",
                StatusCodes.Status502BadGateway);
        var builder = ImmutableArray.CreateBuilder<(string AgentId, string RoleId)>();
        foreach (var cast in casting.EnumerateArray())
        {
            if (cast.ValueKind != JsonValueKind.Object ||
                !TryReadIdentifier(cast, "agentId", out var agentId) ||
                !TryReadIdentifier(cast, "role", out var roleId))
                throw new CoordinationException(
                    "projects_run_selection_contract_invalid",
                    StatusCodes.Status502BadGateway);
            builder.Add((agentId, roleId));
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ReadEffectiveModelReferences(JsonElement selection)
    {
        if (!selection.TryGetProperty("modelSelection", out var modelSelection) ||
            modelSelection.ValueKind != JsonValueKind.Object ||
            !TryReadIdentifier(modelSelection, "reference", out var reference))
            return [];
        return [reference];
    }

    private static bool TryReadIdentifier(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var propertyValue) ||
            propertyValue.ValueKind != JsonValueKind.String ||
            !IsStableIdentifier(propertyValue.GetString()))
            return false;
        value = propertyValue.GetString()!;
        return true;
    }

    private static bool IsStableIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
