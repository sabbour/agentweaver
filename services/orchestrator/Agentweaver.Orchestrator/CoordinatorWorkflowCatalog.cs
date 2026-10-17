using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Builds the workflow catalog and typed run limits from an accepted Projects selection.
/// </summary>
internal static class CoordinatorWorkflowCatalog
{
    private const string BuiltInWorkflowId = "default";

    private static readonly WorkflowDefinition BuiltInDefault = new(
        BuiltInWorkflowId,
        "1.0.0",
        "coordinator-catalog-v1",
        WorkflowDefinitionOrigin.BuiltIn,
        32,
        [
            new WorkflowStepDefinition(
                "implementation",
                "Complete bounded implementation work for the confirmed outcome.",
                WorkflowStepMode.Open,
                0,
                new WorkflowCardinality(0, 8),
                [],
                ["engineer", "researcher"],
                ["execution", "research", "validation"],
                ["worktree", "sandboxed"],
                ["sandbox.workspace.write"],
                null,
                null)
            {
                ModelTaskKind = ModelTaskKind.CodeWriting
            },
            new WorkflowStepDefinition(
                "build-test",
                "Record a typed platform build-and-test request.",
                WorkflowStepMode.Platform,
                1,
                new WorkflowCardinality(0, 1),
                ["implementation"],
                [],
                [],
                [],
                [],
                null,
                WorkflowPlatformGate.BuildTest)
        ]);

    private static readonly ImmutableArray<WorkflowDefinition> RegisteredDefinitions =
        [BuiltInDefault];

    /// <summary>
    /// Returns the workflows available to this run, applying the configured default and any proposed generated workflow.
    /// </summary>
    /// <exception cref="CoordinationException">The project configuration or generated workflow reference is invalid.</exception>
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

    /// <summary>
    /// Converts accepted casting, charters, model references, and task routes into the immutable run-selection context.
    /// </summary>
    /// <exception cref="CoordinationException">Required project-configuration fields are missing or malformed.</exception>
    public static WorkPlanRunSelectionContext CreateRunSelectionContext(JsonElement selection)
    {
        var projectConfiguration = GetProjectConfiguration(selection);
        var charteredAgents = ReadCharteredAgents(projectConfiguration);
        var casting = ReadCasting(projectConfiguration);
        var (models, modelTaskSelections) = ReadEffectiveModelSelections(selection);
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
        return new WorkPlanRunSelectionContext(roles, IsolationProviderBinding: null)
        {
            ModelTaskSelections = modelTaskSelections
        };
    }

    /// <summary>
    /// Reads the configured maximum number of child sessions, bounded to the supported range.
    /// </summary>
    public static int ReadMaxChildren(JsonElement selection)
        => ReadRunLimit(selection, "maxChildren", minimum: 0, maximum: 100);

    /// <summary>
    /// Reads the configured maximum concurrent child count, bounded to the supported range.
    /// </summary>
    public static int ReadMaxConcurrentChildren(JsonElement selection)
        => ReadRunLimit(selection, "maxConcurrentChildren", minimum: 1, maximum: 32);

    /// <summary>
    /// Reads the configured wall-time limit in seconds, bounded to the supported range.
    /// </summary>
    public static int ReadMaxWallTimeSeconds(JsonElement selection)
        => ReadRunLimit(selection, "maxWallTimeSeconds", minimum: 60, maximum: 86400);

    /// <summary>
    /// Checks whether the run's elapsed wall time has reached its configured limit.
    /// </summary>
    public static bool IsWallTimeLimitReached(
        DateTimeOffset runStartedAt,
        int maxWallTimeSeconds,
        DateTimeOffset now)
    {
        if (runStartedAt == default)
            throw new ArgumentOutOfRangeException(nameof(runStartedAt));
        return now >= runStartedAt.AddSeconds(maxWallTimeSeconds);
    }

    /// <summary>
    /// Reads optional model, tool, token, revision, and Copilot-credit limits from the accepted selection.
    /// </summary>
    /// <exception cref="CoordinationException">A configured limit is malformed or the soft credit limit exceeds the hard limit.</exception>
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

    /// <summary>
    /// Reads a required integer run limit and enforces its inclusive supported range.
    /// </summary>
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

    /// <summary>
    /// Requires the accepted selection to contain an object-valued run-limits section.
    /// </summary>
    private static JsonElement ReadRunLimits(JsonElement selection)
    {
        if (selection.ValueKind != JsonValueKind.Object ||
            !selection.TryGetProperty("runLimits", out var runLimits) ||
            runLimits.ValueKind != JsonValueKind.Object)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        return runLimits;
    }

    /// <summary>
    /// Reads an optional integer limit, returning null when it is absent or explicitly null.
    /// </summary>
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

    /// <summary>
    /// Reads an optional non-negative decimal limit.
    /// </summary>
    private static decimal? ReadOptionalNonNegativeDecimal(JsonElement limits, string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result) || result < 0)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        return result;
    }

    /// <summary>
    /// Requires an object-valued project-configuration section in the accepted selection.
    /// </summary>
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

    /// <summary>
    /// Reads an optional stable identifier from a JSON property.
    /// </summary>
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

    /// <summary>
    /// Reads unique workflow identifiers from the project's blueprint references.
    /// </summary>
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

    /// <summary>
    /// Reads unique agent and role pairs whose project charters contain non-empty text.
    /// </summary>
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

    /// <summary>
    /// Reads agent-to-role assignments from the project's casting configuration.
    /// </summary>
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

    /// <summary>Reads accepted model references and task routes, rejecting malformed or unaccepted route data.</summary>
    private static (ImmutableArray<string> References, ImmutableArray<ModelTaskSelectionReference> TaskSelections)
        ReadEffectiveModelSelections(JsonElement selection)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<(string Reference, string ReasoningEffort)>();
        var seenModelSelectionIdentities = new HashSet<(string Reference, string ReasoningEffort)>();
        (string Reference, string ReasoningEffort)? primarySelection = null;
        if (selection.TryGetProperty("modelSelection", out var modelSelection))
        {
            primarySelection = ReadModelSelectionIdentity(modelSelection);
            references.Add(primarySelection.Value.Reference);
            identities.Add(primarySelection.Value);
        }

        if (selection.TryGetProperty("modelSelections", out var modelSelections))
        {
            if (modelSelections.ValueKind != JsonValueKind.Array ||
                modelSelections.GetArrayLength() > ModelTaskSelectionLimits.MaximumAcceptedSelections)
                throw InvalidSelectionSnapshot();
            foreach (var model in modelSelections.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object ||
                    !model.TryGetProperty("selection", out var configuredSelection))
                    throw InvalidSelectionSnapshot();
                var identity = ReadModelSelectionIdentity(configuredSelection);
                if (!seenModelSelectionIdentities.Add(identity))
                    throw InvalidSelectionSnapshot();
                references.Add(identity.Reference);
                identities.Add(identity);
            }
        }

        var acceptedIdentities = identities.ToImmutableHashSet();
        if (!selection.TryGetProperty("modelTaskSelections", out var routes))
        {
            if (primarySelection is null && acceptedIdentities.Count == 1)
                primarySelection = acceptedIdentities.Single();
            var legacyRoutes = primarySelection is null
                ? ImmutableArray<ModelTaskSelectionReference>.Empty
                : Enum.GetValues<ModelTaskKind>()
                    .Select(taskKind => new ModelTaskSelectionReference(
                        taskKind,
                        null,
                        null,
                        primarySelection.Value.Reference,
                        primarySelection.Value.ReasoningEffort))
                    .ToImmutableArray();
            return (references.Order(StringComparer.Ordinal).ToImmutableArray(), legacyRoutes);
        }

        if (routes.ValueKind != JsonValueKind.Array ||
            routes.GetArrayLength() > ModelTaskSelectionLimits.MaximumRoutes)
            throw InvalidSelectionSnapshot();

        var taskSelections = ImmutableArray.CreateBuilder<ModelTaskSelectionReference>();
        var routeScopes = new HashSet<(ModelTaskKind TaskKind, string? RoleId, string? AgentId)>();
        foreach (var route in routes.EnumerateArray())
        {
            var parsed = ReadModelTaskSelection(route);
            if (!acceptedIdentities.Contains((parsed.ModelSelectionReference, parsed.ReasoningEffort)) ||
                !routeScopes.Add((parsed.TaskKind, parsed.RoleId, parsed.AgentId)))
                throw InvalidSelectionSnapshot();
            taskSelections.Add(parsed);
        }
        return (references.Order(StringComparer.Ordinal).ToImmutableArray(),
            taskSelections.ToImmutable());
    }

    /// <summary>Parses one route with exact field names and a supported task-kind token.</summary>
    private static ModelTaskSelectionReference ReadModelTaskSelection(JsonElement route)
    {
        if (route.ValueKind != JsonValueKind.Object)
            throw InvalidSelectionSnapshot();
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in route.EnumerateObject())
        {
            if (property.Name is not ("taskKind" or "roleId" or "agentId" or
                    "modelSelectionReference" or "reasoningEffort") ||
                !properties.TryAdd(property.Name, property.Value))
                throw InvalidSelectionSnapshot();
        }

        if (!properties.TryGetValue("taskKind", out var taskKindValue) ||
            taskKindValue.ValueKind != JsonValueKind.String ||
            !TryReadModelTaskKind(taskKindValue.GetString(), out var taskKind) ||
            !properties.TryGetValue("modelSelectionReference", out var referenceValue) ||
            referenceValue.ValueKind != JsonValueKind.String ||
            !IsOpaqueReference(referenceValue.GetString()))
            throw InvalidSelectionSnapshot();

        var roleId = ReadOptionalRouteIdentifier(properties, "roleId");
        var agentId = ReadOptionalRouteIdentifier(properties, "agentId");
        var reasoningEffort = ReadOptionalReasoningEffort(properties);
        return new ModelTaskSelectionReference(
            taskKind, roleId, agentId, referenceValue.GetString()!, reasoningEffort);
    }

    /// <summary>Reads an optional stable selector from one task route.</summary>
    private static string? ReadOptionalRouteIdentifier(
        IReadOnlyDictionary<string, JsonElement> properties,
        string propertyName)
    {
        if (!properties.TryGetValue(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String ||
            !IsStableIdentifier(value.GetString()))
            throw InvalidSelectionSnapshot();
        return value.GetString();
    }

    /// <summary>Reads a route's optional effort, defaulting omitted legacy values to High.</summary>
    private static string ReadOptionalReasoningEffort(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!properties.TryGetValue("reasoningEffort", out var value))
            return "high";
        if (value.ValueKind != JsonValueKind.String ||
            !ModelTaskSelectionResolver.IsValidReasoningEffort(value.GetString()))
            throw InvalidSelectionSnapshot();
        return value.GetString()!;
    }

    /// <summary>Parses only named task-kind values supported by the workflow contract.</summary>
    private static bool TryReadModelTaskKind(string? value, out ModelTaskKind taskKind)
    {
        taskKind = default;
        if (!Enum.TryParse(value, ignoreCase: true, out taskKind) ||
            !Enum.IsDefined(taskKind))
            return false;
        return string.Equals(
            JsonNamingPolicy.CamelCase.ConvertName(taskKind.ToString()),
            value,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the accepted reference and effort identity from one model-selection object.</summary>
    private static (string Reference, string ReasoningEffort) ReadModelSelectionIdentity(
        JsonElement modelSelection)
    {
        if (modelSelection.ValueKind != JsonValueKind.Object ||
            !modelSelection.TryGetProperty("reference", out var reference) ||
            reference.ValueKind != JsonValueKind.String ||
            !IsOpaqueReference(reference.GetString()))
            throw InvalidSelectionSnapshot();
        var reasoningEffort = "high";
        if (modelSelection.TryGetProperty("reasoningEffort", out var suppliedEffort))
        {
            if (suppliedEffort.ValueKind != JsonValueKind.String ||
                !ModelTaskSelectionResolver.IsValidReasoningEffort(suppliedEffort.GetString()))
                throw InvalidSelectionSnapshot();
            reasoningEffort = suppliedEffort.GetString()!;
        }
        return (reference.GetString()!, reasoningEffort);
    }

    /// <summary>Creates the fail-closed error for malformed accepted run-selection snapshots.</summary>
    private static CoordinationException InvalidSelectionSnapshot() =>
        new("projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);

    /// <summary>
    /// Reads a stable identifier property without throwing when the property is absent or invalid.
    /// </summary>
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

    /// <summary>
    /// Checks the shared bounded identifier syntax used by workflow and project references.
    /// </summary>
    private static bool IsStableIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    /// <summary>Checks bounded, nonblank reference text without constraining provider syntax.</summary>
    private static bool IsOpaqueReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}
