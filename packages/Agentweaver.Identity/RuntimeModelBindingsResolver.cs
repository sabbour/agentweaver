using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Resolves enabled model references from a startup snapshot and fingerprints the complete
/// configuration so accepted runs can pin and later verify the exact binding.
/// </summary>
public sealed class RuntimeModelBindingsResolver
{
    /// <summary>Configuration path containing the model binding map.</summary>
    public const string ConfigurationSection = "AgentHost:ModelBindings";

    /// <summary>Configuration path containing the model binding revision.</summary>
    public const string RevisionConfigurationKey = "AgentHost:ModelBindingsRevision";
    private readonly ImmutableDictionary<string, RuntimeModelBinding> _bindings;

    /// <summary>Validates and freezes model bindings, then computes their canonical configuration hash.</summary>
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
        var reasoningEfforts = _bindings.Where(item => !item.Value.SupportedReasoningEfforts.IsEmpty)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new
            {
                Reference = item.Key,
                SupportedReasoningEfforts = item.Value.SupportedReasoningEfforts
                    .OrderBy(effort => effort, StringComparer.Ordinal).ToArray()
            }).ToArray();
        object configuration = (capacities.Length, reasoningEfforts.Length) switch
        {
            (0, 0) => models,
            (_, 0) => new { Models = models, PromptCapacities = capacities },
            (0, _) => new { Models = models, SupportedReasoningEfforts = reasoningEfforts },
            _ => new { Models = models, PromptCapacities = capacities, SupportedReasoningEfforts = reasoningEfforts }
        };
        ConfigurationHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(configuration));
    }

    /// <summary>Revision supplied by the host for this model binding snapshot.</summary>
    public string ConfigurationRevision { get; }

    /// <summary>Stable digest of normalized models, prompt capacities, and supported reasoning efforts.</summary>
    public string ConfigurationHash { get; }

    /// <summary>Enabled hosted model references from this immutable binding snapshot.</summary>
    public ImmutableArray<string> EnabledHostedModelReferences => _bindings
        .Where(item => item.Value.Enabled && item.Value.SourceMode == ModelSourceMode.HostedCopilot)
        .Select(item => item.Key)
        .Order(StringComparer.Ordinal)
        .ToImmutableArray();

    /// <summary>Returns an enabled binding only when its source mode and optional accepted pin match.</summary>
    /// <exception cref="RuntimeAuthorizationException">The model is missing, disabled, or differs from its pin.</exception>
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
            if (acceptedPin != CreatePin(reference, model, acceptedPin.ReasoningEffort))
                throw new RuntimeAuthorizationException("runtime_model_binding_pin_mismatch");
        }
        return model;
    }

    /// <summary>Builds a pin for the exact resolved model and configuration revision.</summary>
    public RuntimeModelBindingPin Pin(
        string reference, ModelSourceMode sourceMode, string reasoningEffort = "high")
    {
        ValidateReasoningEffort(reasoningEffort);
        return CreatePin(reference, Resolve(reference, sourceMode), reasoningEffort);
    }

    private RuntimeModelBindingPin CreatePin(
        string reference, RuntimeModelBinding model, string reasoningEffort = "high") =>
        new(1, reference, model.ModelId, model.SourceMode, ConfigurationRevision, ConfigurationHash, reasoningEffort)
        {
            ProviderType = model.Provider?.Type
        };

    /// <summary>Validates version, model source, provider type, revision, and configuration digest.</summary>
    public static void ValidatePin(RuntimeModelBindingPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.ContractVersion != 1 || !Enum.IsDefined(pin.SourceMode) ||
            pin.SourceMode == ModelSourceMode.HostedCopilot && pin.ProviderType is not null ||
            pin.SourceMode == ModelSourceMode.Byok && pin.ProviderType is not ("azure" or "openai" or "anthropic") ||
            pin.ReasoningEffort is not ("low" or "medium" or "high" or "xhigh"))
            throw new RuntimeAuthorizationException("runtime_model_binding_pin_invalid");
        RuntimeContractValidation.ValidateIdentifier(pin.ModelSelectionReference);
        RuntimeContractValidation.ValidateIdentifier(pin.ModelId);
        RuntimeContractValidation.ValidateIdentifier(pin.ConfigurationRevision);
        RuntimeContractValidation.ValidateHash(pin.ConfigurationHash);
    }

    private static void ValidateReasoningEffort(string reasoningEffort)
    {
        if (reasoningEffort is not ("low" or "medium" or "high" or "xhigh"))
            throw new RuntimeAuthorizationException("runtime_model_reasoning_effort_invalid");
    }
}
