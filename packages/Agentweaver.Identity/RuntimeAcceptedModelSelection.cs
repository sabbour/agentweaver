using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Identity;

/// <summary>
/// Parsed model selection from the accepted run snapshot, including its effort, exact credential
/// or connection reference, and optional validated model-binding pin.
/// </summary>
public sealed record RuntimeAcceptedModelSelection(
    string Reference,
    ModelSourceMode? SourceMode,
    SecretRef? CredentialReference,
    Guid? ConnectionId,
    ProjectAuthorityResourceType? ConnectionScope)
{
    /// <summary>Reasoning effort accepted for this exact model reference.</summary>
    public string ReasoningEffort { get; init; } = "high";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelBindingPin? ModelBindingPin { get; init; }

    /// <summary>
    /// Reads and validates the model selection without accepting unknown credential shapes,
    /// mismatched project overrides, or a BYOK pin for a hosted model.
    /// </summary>
    /// <param name="snapshot">The immutable accepted run snapshot.</param>
    /// <returns>The validated model selection and its optional authority references.</returns>
    /// <exception cref="RuntimeAuthorizationException">The snapshot contains invalid or inconsistent fields.</exception>
    public static RuntimeAcceptedModelSelection Read(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("modelSelection", out var model) ||
            model.ValueKind != JsonValueKind.Object)
            throw Invalid();
        return ReadSelection(snapshot, model, null);
    }

    /// <summary>Reads the exact accepted model/effort variant used by a task route.</summary>
    /// <param name="snapshot">The immutable accepted run snapshot.</param>
    /// <param name="expectedReference">The model reference selected for the task.</param>
    /// <param name="expectedReasoningEffort">The effort selected for the task.</param>
    /// <returns>The validated model selection and its optional authority references.</returns>
    /// <exception cref="RuntimeAuthorizationException">The route is absent, duplicated, or inconsistent.</exception>
    public static RuntimeAcceptedModelSelection Read(
        JsonElement snapshot, string expectedReference, string expectedReasoningEffort)
    {
        RuntimeContractValidation.ValidateIdentifier(expectedReference);
        if (expectedReasoningEffort is not ("low" or "medium" or "high" or "xhigh") ||
            snapshot.ValueKind != JsonValueKind.Object)
            throw Invalid();

        JsonElement selectedModel = default;
        ProjectAuthorityResourceType? selectedConnectionScope = null;
        var matches = 0;
        if (snapshot.TryGetProperty("modelSelections", out var selections))
        {
            if (selections.ValueKind != JsonValueKind.Array)
                throw Invalid();
            foreach (var selectionEntry in selections.EnumerateArray())
            {
                if (selectionEntry.ValueKind != JsonValueKind.Object ||
                    !TryReadEffectiveSelection(selectionEntry, out var candidate, out var connectionScope))
                    throw Invalid();
                if (ReadReference(candidate) != expectedReference ||
                    ReadReasoningEffort(candidate) != expectedReasoningEffort)
                    continue;
                selectedModel = candidate;
                selectedConnectionScope = connectionScope;
                matches++;
            }
        }
        else if (snapshot.TryGetProperty("modelSelection", out var primary))
        {
            selectedModel = primary;
            if (ReadReference(primary) == expectedReference &&
                ReadReasoningEffort(primary) == expectedReasoningEffort)
                matches = 1;
        }

        if (matches != 1)
            throw Invalid();
        return ReadSelection(snapshot, selectedModel, selectedConnectionScope);
    }

    /// <summary>Validates one selected model against its source, credential, connection, and pin.</summary>
    private static RuntimeAcceptedModelSelection ReadSelection(
        JsonElement snapshot, JsonElement model, ProjectAuthorityResourceType? acceptedConnectionScope)
    {
        var fields = ReadModelSelectionFields(model);
        ProjectAuthorityResourceType? connectionScope = null;
        if (fields.ConnectionId is not null)
        {
            if (!snapshot.TryGetProperty("projectConfiguration", out var configuration) ||
                configuration.ValueKind != JsonValueKind.Object)
                throw Invalid();
            var projectModelConfigured = configuration.TryGetProperty("modelSelection", out var projectModel) &&
                projectModel.ValueKind != JsonValueKind.Null;
            var expectedScope = projectModelConfigured
                ? ProjectAuthorityResourceType.Project : ProjectAuthorityResourceType.Platform;
            connectionScope = acceptedConnectionScope ?? expectedScope;
            if (connectionScope != expectedScope ||
                connectionScope == ProjectAuthorityResourceType.Project &&
                ReadModelSelectionFields(projectModel) != fields)
                throw Invalid();
        }
        return new(fields.Reference, fields.SourceMode, fields.CredentialReference,
            fields.ConnectionId, connectionScope)
        {
            ReasoningEffort = fields.ReasoningEffort,
            ModelBindingPin = fields.ModelBindingPin
        };
    }

    /// <summary>Reads exact model settings and requires any BYOK pin to match them.</summary>
    private static ModelSelectionFields ReadModelSelectionFields(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in model.EnumerateObject())
        {
            if (property.Name is not ("reference" or "sourceMode" or "credentialReference" or
                    "connectionId" or "reasoningEffort" or "modelBindingPin") ||
                !properties.Add(property.Name))
                throw Invalid();
        }
        if (!model.TryGetProperty("reference", out var reference) ||
            reference.ValueKind != JsonValueKind.String)
            throw Invalid();
        var referenceValue = reference.GetString()!;
        RuntimeContractValidation.ValidateIdentifier(referenceValue);
        var reasoningEffort = ReadReasoningEffort(model);
        ModelSourceMode? sourceMode = null;
        if (model.TryGetProperty("sourceMode", out var suppliedMode) && suppliedMode.ValueKind != JsonValueKind.Null)
        {
            if (suppliedMode.ValueKind != JsonValueKind.String ||
                suppliedMode.GetString() is not ("hostedCopilot" or "byok"))
                throw Invalid();
            sourceMode = suppliedMode.GetString() == "hostedCopilot"
                ? ModelSourceMode.HostedCopilot : ModelSourceMode.Byok;
        }
        Guid? connectionId = null;
        if (model.TryGetProperty("connectionId", out var suppliedConnection) &&
            suppliedConnection.ValueKind != JsonValueKind.Null)
        {
            if (sourceMode != ModelSourceMode.HostedCopilot ||
                suppliedConnection.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(suppliedConnection.GetString(), "D", out var connection) ||
                connection == Guid.Empty)
                throw Invalid();
            connectionId = connection;
        }
        SecretRef? credential = null;
        if (model.TryGetProperty("credentialReference", out var suppliedCredential) &&
            suppliedCredential.ValueKind != JsonValueKind.Null)
        {
            if (connectionId is not null || suppliedCredential.ValueKind != JsonValueKind.Object ||
                !suppliedCredential.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !suppliedCredential.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String)
                throw Invalid();
            try
            {
                credential = new(id.GetString()!, version.GetString()!);
            }
            catch (ArgumentException)
            {
                throw Invalid();
            }
        }
        RuntimeModelBindingPin? pin = null;
        if (model.TryGetProperty("modelBindingPin", out var suppliedPin) &&
            suppliedPin.ValueKind != JsonValueKind.Null)
        {
            try
            {
                pin = suppliedPin.Deserialize<RuntimeModelBindingPin>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                    }) ?? throw Invalid();
                RuntimeModelBindingsResolver.ValidatePin(pin);
                if (sourceMode is null || pin.SourceMode != sourceMode ||
                    pin.ModelSelectionReference != referenceValue ||
                    pin.ReasoningEffort != reasoningEffort)
                    throw Invalid();
            }
            catch (JsonException)
            {
                throw Invalid();
            }
        }
        return new(referenceValue, reasoningEffort, sourceMode, credential, connectionId, pin);
    }

    /// <summary>Reads the required, supported settings for one accepted model selection.</summary>
    private sealed record ModelSelectionFields(
        string Reference,
        string ReasoningEffort,
        ModelSourceMode? SourceMode,
        SecretRef? CredentialReference,
        Guid? ConnectionId,
        RuntimeModelBindingPin? ModelBindingPin);

    /// <summary>Reads the exact model-selection and authority-scope fields from one effective entry.</summary>
    private static bool TryReadEffectiveSelection(
        JsonElement entry,
        out JsonElement selection,
        out ProjectAuthorityResourceType connectionScope)
    {
        selection = default;
        connectionScope = default;
        var seenSelection = false;
        var seenScope = false;
        foreach (var property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case "selection" when !seenSelection && property.Value.ValueKind == JsonValueKind.Object:
                    selection = property.Value;
                    seenSelection = true;
                    break;
                case "connectionScope" when !seenScope && property.Value.ValueKind == JsonValueKind.String:
                    connectionScope = property.Value.GetString() switch
                    {
                        "project" => ProjectAuthorityResourceType.Project,
                        "platform" => ProjectAuthorityResourceType.Platform,
                        _ => throw Invalid()
                    };
                    seenScope = true;
                    break;
                default:
                    return false;
            }
        }
        return seenSelection && seenScope;
    }

    /// <summary>Reads a required model reference from one accepted selection.</summary>
    private static string ReadReference(JsonElement selection)
    {
        if (selection.ValueKind != JsonValueKind.Object ||
            !selection.TryGetProperty("reference", out var reference) ||
            reference.ValueKind != JsonValueKind.String)
            throw Invalid();
        return reference.GetString()!;
    }

    /// <summary>Reads a supported effort from one accepted selection, defaulting legacy entries to High.</summary>
    private static string ReadReasoningEffort(JsonElement selection)
    {
        if (selection.ValueKind != JsonValueKind.Object)
            throw Invalid();
        if (!selection.TryGetProperty("reasoningEffort", out var value))
        {
            if (!selection.TryGetProperty("modelBindingPin", out var pin) ||
                pin.ValueKind != JsonValueKind.Object ||
                !pin.TryGetProperty("reasoningEffort", out value))
                return "high";
        }
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not ("low" or "medium" or "high" or "xhigh"))
            throw Invalid();
        return value.GetString()!;
    }

    private static RuntimeAuthorizationException Invalid() =>
        new("runtime_accepted_model_selection_invalid");
}
