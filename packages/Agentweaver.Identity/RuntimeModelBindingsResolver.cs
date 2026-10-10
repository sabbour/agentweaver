using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeModelBindingsResolver
{
    public const string ConfigurationSection = "AgentHost:ModelBindings";
    public const string RevisionConfigurationKey = "AgentHost:ModelBindingsRevision";
    private readonly ImmutableDictionary<string, RuntimeModelBinding> _bindings;

    public RuntimeModelBindingsResolver(
        string configurationRevision, IReadOnlyDictionary<string, RuntimeModelBinding> bindings)
    {
        RuntimeContractValidation.ValidateIdentifier(configurationRevision);
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (var (reference, model) in bindings)
        {
            RuntimeContractValidation.ValidateIdentifier(reference);
            ArgumentNullException.ThrowIfNull(model);
            model.Validate();
        }
        ConfigurationRevision = configurationRevision;
        _bindings = bindings.ToImmutableDictionary(StringComparer.Ordinal);
        var models = _bindings.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new
            {
                Reference = item.Key,
                item.Value.ModelId,
                item.Value.SourceMode,
                item.Value.Enabled,
                Provider = item.Value.Provider is { } provider ? new
                {
                    provider.Type,
                    BaseUrl = provider.BaseUrl.AbsoluteUri,
                    provider.WireApi,
                    provider.AzureApiVersion,
                    Headers = provider.Headers.OrderBy(header => header.Key, StringComparer.Ordinal).ToArray()
                } : null
            }).ToArray();
        var capacities = _bindings.Where(item => item.Value.PromptCapacityTokens is not null)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new { Reference = item.Key, item.Value.PromptCapacityTokens }).ToArray();
        ConfigurationHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            capacities.Length == 0 ? (object)models : new { Models = models, PromptCapacities = capacities }));
    }

    public string ConfigurationRevision { get; }
    public string ConfigurationHash { get; }

    public RuntimeModelBinding Resolve(
        string reference, ModelSourceMode? sourceMode, RuntimeModelBindingPin? acceptedPin = null)
    {
        RuntimeContractValidation.ValidateIdentifier(reference);
        if (!_bindings.TryGetValue(reference, out var model))
            throw new RuntimeAuthorizationException("runtime_model_reference_unavailable");
        if (!model.Enabled)
            throw new RuntimeAuthorizationException("runtime_model_disabled");
        if (sourceMode != model.SourceMode)
            throw new RuntimeAuthorizationException("runtime_model_source_mode_mismatch");
        if (acceptedPin is not null)
        {
            ValidatePin(acceptedPin);
            if (acceptedPin != CreatePin(reference, model))
                throw new RuntimeAuthorizationException("runtime_model_binding_pin_mismatch");
        }
        return model;
    }

    public RuntimeModelBindingPin Pin(string reference, ModelSourceMode sourceMode) =>
        CreatePin(reference, Resolve(reference, sourceMode));

    private RuntimeModelBindingPin CreatePin(string reference, RuntimeModelBinding model) =>
        new(1, reference, model.ModelId, model.SourceMode, ConfigurationRevision, ConfigurationHash)
        {
            ProviderType = model.Provider?.Type
        };

    public static void ValidatePin(RuntimeModelBindingPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.ContractVersion != 1 || !Enum.IsDefined(pin.SourceMode) ||
            pin.SourceMode == ModelSourceMode.HostedCopilot && pin.ProviderType is not null ||
            pin.SourceMode == ModelSourceMode.Byok && pin.ProviderType is not ("azure" or "openai" or "anthropic"))
            throw new RuntimeAuthorizationException("runtime_model_binding_pin_invalid");
        RuntimeContractValidation.ValidateIdentifier(pin.ModelSelectionReference);
        RuntimeContractValidation.ValidateIdentifier(pin.ModelId);
        RuntimeContractValidation.ValidateIdentifier(pin.ConfigurationRevision);
        RuntimeContractValidation.ValidateHash(pin.ConfigurationHash);
    }
}
