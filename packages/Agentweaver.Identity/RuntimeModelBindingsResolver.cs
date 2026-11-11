using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.Extensions.Configuration;

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

    /// <summary>Reads the model binding map, including supported reasoning efforts, from host configuration.</summary>
    /// <exception cref="InvalidOperationException">The model binding map is missing.</exception>
    public static Dictionary<string, RuntimeModelBinding> ReadBindings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigurationSection);
        var bindings = section.Get<Dictionary<string, RuntimeModelBinding>>()
            ?? throw new InvalidOperationException("The AgentHost:ModelBindings configuration is required.");
        // The configuration binder does not populate ImmutableArray properties.
        foreach (var reference in bindings.Keys.ToArray())
        {
            var efforts = section.GetSection(reference).GetSection("SupportedReasoningEfforts").GetChildren()
                .OrderBy(child => int.TryParse(child.Key, out var index) ? index : int.MaxValue)
                .Select(child => child.Value ?? string.Empty).ToImmutableArray();
            if (!efforts.IsEmpty)
                bindings[reference] = bindings[reference] with { SupportedReasoningEfforts = efforts };
        }
        return bindings;
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

    /// <summary>Resolves the provider revision carried by one accepted BYOK run selection.</summary>
    /// <exception cref="RuntimeAuthorizationException">The selection, credential version, revision, or pin does not match.</exception>
    public RuntimeModelBinding ResolveAcceptedRuntimeBinding(
        RuntimeAcceptedModelSelection selection,
        ModelProviderBindingRevision providerBinding)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(providerBinding);
        if (selection.SourceMode != ModelSourceMode.Byok ||
            selection.ModelBindingPin is not { } pin ||
            selection.CredentialReference is not { } credential ||
            selection.ConnectionId is not null || selection.ConnectionScope is not null ||
            selection.Reference != providerBinding.ProviderId ||
            credential.Version != providerBinding.CredentialVersion)
            throw new RuntimeAuthorizationException("runtime_model_binding_pin_mismatch");

        ValidatePin(pin);
        ValidateRuntimeProviderBinding(providerBinding);
        if (pin.ModelSelectionReference != selection.Reference ||
            pin.ModelId != providerBinding.Deployment ||
            pin.SourceMode != ModelSourceMode.Byok ||
            pin.ProviderType != providerBinding.ProviderType ||
            pin.ConfigurationRevision != providerBinding.ProviderRevision.ToString(CultureInfo.InvariantCulture) ||
            pin.ConfigurationHash != providerBinding.ConfigurationHash ||
            pin.ReasoningEffort != selection.ReasoningEffort ||
            !providerBinding.ReasoningEfforts.Contains(selection.ReasoningEffort, StringComparer.Ordinal))
            throw new RuntimeAuthorizationException("runtime_model_binding_pin_mismatch");

        var provider = new RuntimeByokProvider(
            providerBinding.ProviderType,
            new Uri(providerBinding.BaseUrl, UriKind.Absolute),
            providerBinding.WireApi ?? "responses",
            providerBinding.AzureApiVersion);
        var model = new RuntimeModelBinding(providerBinding.Deployment, ModelSourceMode.Byok, provider)
        {
            PromptCapacityTokens = providerBinding.PromptCapacityTokens,
            SupportedReasoningEfforts = providerBinding.ReasoningEfforts
        };
        try
        {
            model.Validate();
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_model_binding_revision_invalid");
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
            !ModelReasoningEfforts.IsSupported(pin.ReasoningEffort))
            throw new RuntimeAuthorizationException("runtime_model_binding_pin_invalid");
        RuntimeContractValidation.ValidateIdentifier(pin.ModelSelectionReference);
        RuntimeContractValidation.ValidateIdentifier(pin.ModelId);
        RuntimeContractValidation.ValidateIdentifier(pin.ConfigurationRevision);
        RuntimeContractValidation.ValidateHash(pin.ConfigurationHash);
    }

    private static void ValidateRuntimeProviderBinding(ModelProviderBindingRevision binding)
    {
        if (binding.ContractVersion != ModelProviderBindingRevision.CurrentContractVersion ||
            binding.ProviderRevision <= 0 ||
            !Enum.IsDefined(binding.Scope) ||
            (binding.Scope == ModelProviderBindingScope.Project) != (binding.ProjectId is not null) ||
            string.IsNullOrWhiteSpace(binding.DisplayName) || binding.DisplayName.Length > 128 ||
            string.IsNullOrWhiteSpace(binding.Deployment) || binding.Deployment.Length > 256 ||
            binding.ReasoningEfforts.IsDefault ||
            binding.ReasoningEfforts.Length > 6 ||
            binding.ReasoningEfforts.Distinct(StringComparer.Ordinal).Count() != binding.ReasoningEfforts.Length)
            throw new RuntimeAuthorizationException("runtime_model_binding_revision_invalid");

        RuntimeContractValidation.ValidateIdentifier(binding.ProviderId);
        RuntimeContractValidation.ValidateIdentifier(binding.Deployment);
        if (binding.ProjectId is { } projectId)
            RuntimeContractValidation.ValidateIdentifier(projectId);
        RuntimeContractValidation.ValidateHash(binding.ConfigurationHash);
        if (!Uri.TryCreate(binding.BaseUrl, UriKind.Absolute, out var baseUrl) ||
            !RuntimeContractValidation.IsHttpsEndpoint(baseUrl) ||
            binding.ProviderType == "azure" &&
                (!string.IsNullOrEmpty(baseUrl.PathAndQuery.Trim('/')) ||
                 !string.IsNullOrEmpty(baseUrl.Fragment)))
            throw new RuntimeAuthorizationException("runtime_model_binding_revision_invalid");
    }

    private static void ValidateReasoningEffort(string reasoningEffort)
    {
        if (!ModelReasoningEfforts.IsSupported(reasoningEffort))
            throw new RuntimeAuthorizationException("runtime_model_reasoning_effort_invalid");
    }
}
