using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Identity;

public sealed record RuntimeAcceptedModelSelection(
    string Reference,
    ModelSourceMode? SourceMode,
    SecretRef? CredentialReference,
    Guid? ConnectionId,
    ProjectAuthorityResourceType? ConnectionScope)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelBindingPin? ModelBindingPin { get; init; }

    public static RuntimeAcceptedModelSelection Read(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("modelSelection", out var model) ||
            model.ValueKind != JsonValueKind.Object ||
            !model.TryGetProperty("reference", out var reference) ||
            reference.ValueKind != JsonValueKind.String)
            throw Invalid();
        RuntimeContractValidation.ValidateIdentifier(reference.GetString()!);
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
        ProjectAuthorityResourceType? connectionScope = null;
        if (model.TryGetProperty("connectionId", out var suppliedConnection) &&
            suppliedConnection.ValueKind != JsonValueKind.Null)
        {
            if (sourceMode != ModelSourceMode.HostedCopilot ||
                suppliedConnection.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(suppliedConnection.GetString(), "D", out var connection) ||
                connection == Guid.Empty ||
                !snapshot.TryGetProperty("projectConfiguration", out var configuration) ||
                configuration.ValueKind != JsonValueKind.Object)
                throw Invalid();
            connectionId = connection;
            connectionScope = configuration.TryGetProperty("modelSelection", out var projectModel) &&
                projectModel.ValueKind != JsonValueKind.Null
                ? ProjectAuthorityResourceType.Project : ProjectAuthorityResourceType.Platform;
            if (connectionScope == ProjectAuthorityResourceType.Project &&
                projectModel.GetRawText() != model.GetRawText())
                throw Invalid();
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
                if (sourceMode != ModelSourceMode.Byok || pin.SourceMode != sourceMode ||
                    pin.ModelSelectionReference != reference.GetString())
                    throw Invalid();
            }
            catch (JsonException)
            {
                throw Invalid();
            }
        }
        return new(reference.GetString()!, sourceMode, credential, connectionId, connectionScope)
        {
            ModelBindingPin = pin
        };
    }

    private static RuntimeAuthorizationException Invalid() =>
        new("runtime_accepted_model_selection_invalid");
}
