using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Platform-registered classifier implementation, typed options, and provisioned resource for one Guardrails check.</summary>
public interface IGuardrailsCheckRegistration
{
    string ProviderId { get; }
    string ResourceId { get; }
    long ResourceGeneration { get; }
    GuardrailsProviderOptions Options { get; }
    ImmutableHashSet<string> Capabilities { get; }

    /// <summary>Binds the classifier and options to an immutable run pin.</summary>
    IGuardrailsClassifierBinding Bind(PinnedProviderBinding pin);
}

/// <summary>Typed registration that keeps a classifier and its options together.</summary>
public sealed class GuardrailsCheckRegistration<TOptions> : IGuardrailsCheckRegistration
    where TOptions : GuardrailsProviderOptions
{
    private readonly TOptions _options;
    private readonly IGuardrailsClassifier<TOptions> _classifier;

    /// <summary>Creates a registration for one provisioned classifier resource.</summary>
    public GuardrailsCheckRegistration(
        TOptions options,
        IGuardrailsClassifier<TOptions> classifier,
        string resourceId,
        long resourceGeneration)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        // The resource identity becomes part of the run pin, so it must be a valid identifier with a
        // positive generation. Generation starts at 1; 0 or less would never match a provisioned resource.
        if (!AgtPolicyProvider.IsIdentifier(resourceId) || resourceGeneration < 1)
            throw new ArgumentException("The Guardrails check resource identity is invalid.");
        ResourceId = resourceId;
        ResourceGeneration = resourceGeneration;
    }

    public string ProviderId => _classifier.ProviderId;
    public string ResourceId { get; }
    public long ResourceGeneration { get; }
    public GuardrailsProviderOptions Options => _options;
    public ImmutableHashSet<string> Capabilities => _classifier.Capabilities;

    public IGuardrailsClassifierBinding Bind(PinnedProviderBinding pin) =>
        new GuardrailsClassifierBinding<TOptions>(pin, _options, _classifier);
}

/// <summary>Provider identity and revision that the accepted run selection recorded for one ordered check.</summary>
public sealed record GuardrailsAcceptedCheck(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision);

/// <summary>Stage plan, or the reason the plan cannot be built.</summary>
public sealed record GuardrailsStagePlanResult(
    GuardrailsAcceptedStagePlan? Plan,
    GuardrailsReasonCode ReasonCode)
{
    public bool IsSuccess => Plan is not null;
}

/// <summary>
/// Builds the ordered Guardrails stage plan from the platform catalog. Only platform-enabled checks resolve,
/// in platform order. A project or accepted selection can't add a disabled or unknown check or change the order.
/// Each check is pinned to its adapter version, options schema, options revision, resource, and negotiated capabilities.
/// </summary>
public sealed class GuardrailsStagePlanResolver
{
    /// <summary>Adapter contract version that every Guardrails classifier implements.</summary>
    public static readonly Version ContractAdapterVersion = new(1, 0, 0);

    /// <summary>Options schema version that every Guardrails classifier uses.</summary>
    public const int ContractOptionsSchemaVersion = 1;

    private readonly ProviderCatalog _catalog;
    private readonly ImmutableDictionary<string, IGuardrailsCheckRegistration> _registrations;

    /// <summary>Creates a resolver over the platform catalog and the registered classifier implementations.</summary>
    public GuardrailsStagePlanResolver(
        ProviderCatalog catalog,
        IEnumerable<IGuardrailsCheckRegistration> registrations)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(registrations);
        // Index registrations by provider ID with ordinal comparison. A duplicate ID would make the lookup
        // ambiguous, so it is a startup error instead of a silent "last one wins".
        var builder = ImmutableDictionary.CreateBuilder<string, IGuardrailsCheckRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!builder.TryAdd(registration.ProviderId, registration))
                throw new ArgumentException($"Guardrails provider '{registration.ProviderId}' is registered more than once.");
        }
        _registrations = builder.ToImmutable();
    }

    /// <summary>
    /// Returns true when the accepted run recorded a Guardrails selection, or the platform catalog defines a
    /// non-empty ordered Guardrails set. When neither is true, no Guardrails checks apply.
    /// </summary>
    public bool IsActive(IReadOnlyList<GuardrailsAcceptedCheck>? acceptedChecks) =>
        acceptedChecks is not null ||
        (_catalog.TryGetOrdered(ProviderSeam.Guardrails, out var ids) && !ids.IsDefaultOrEmpty);

    /// <summary>Resolves, validates, and pins the ordered checks for one stage and content class.</summary>
    public GuardrailsStagePlanResult Resolve(
        string runId,
        GuardrailsContentStage stage,
        GuardrailsContentClass contentClass,
        IReadOnlyList<GuardrailsAcceptedCheck>? acceptedChecks)
    {
        // A null entry in the accepted list is malformed input, not "no check", so it is rejected.
        if (!AgtPolicyProvider.IsIdentifier(runId) ||
            (acceptedChecks is not null && acceptedChecks.Any(item => item is null)))
            return Fail(GuardrailsReasonCode.InvalidRequest);
        // Each stage and content class pair maps to one required capability. A pair with no mapping can't be
        // checked by any classifier, so it fails instead of passing unchecked.
        if (GuardrailsProviderCapabilities.For(stage, contentClass) is not { } stageCapability)
            return Fail(GuardrailsReasonCode.UnsupportedStageOrContent);

        // Ask the shared provider resolver for the ordered Guardrails set. The catalog decides which checks are
        // enabled and in what order. The accepted IDs, when present, can only narrow or confirm that set; they
        // can't add or reorder checks. Every check must support the adapter contract, the options schema,
        // prompt injection detection, and this stage's capability.
        var resolver = new ProviderResolver(_catalog);
        var resolution = resolver.ResolveOrdered(new OrderedProviderResolutionRequest(
            ProviderSeam.Guardrails,
            acceptedChecks?.Select(item => item.ProviderId).ToImmutableArray(),
            ContractAdapterVersion,
            ContractOptionsSchemaVersion,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                GuardrailsProviderCapabilities.PromptInjectionDetection,
                stageCapability)));
        if (!resolution.IsSuccess)
            return Fail(Map(resolution.Error!.Code));

        var candidates = resolution.Value!.Candidates;
        // An empty plan would admit content with no checks, and the gate runs at most a fixed number of
        // checks. Both cases are configuration errors, so the plan fails closed.
        if (candidates.IsDefaultOrEmpty || candidates.Length > GuardrailsContentGate.MaximumChecks)
            return Fail(GuardrailsReasonCode.InvalidConfiguration);

        // Build one pin input per candidate. The two lists stay in candidate order so that pin N later binds
        // to registration N.
        var inputs = new List<ProviderPinInput>(candidates.Length);
        var registrations = new List<IGuardrailsCheckRegistration>(candidates.Length);
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            // The accepted run recorded exact provider revisions. If the catalog now resolves something
            // different at the same position, the run's authority is stale and must not silently use the new one.
            if (acceptedChecks is not null && !Matches(acceptedChecks[index], candidate))
                return Fail(GuardrailsReasonCode.AuthorityStale);
            // The catalog enabled a provider that this host has no implementation for.
            if (!_registrations.TryGetValue(candidate.ProviderId, out var registration))
                return Fail(GuardrailsReasonCode.ProviderUnavailable);
            try
            {
                registration.Options.Validate();
            }
            catch (ArgumentException)
            {
                return Fail(GuardrailsReasonCode.InvalidConfiguration);
            }
            // The registered options must be the exact revision the catalog advertises. Otherwise the pin would
            // record one revision while the classifier runs with another.
            if (registration.Options.ProviderId != candidate.ProviderId ||
                registration.Options.AdapterVersion != candidate.AdapterVersion ||
                registration.Options.OptionsSchemaVersion != candidate.OptionsSchemaVersion ||
                registration.Options.OptionsRevision != candidate.OptionsRevision)
                return Fail(GuardrailsReasonCode.InvalidConfiguration);

            // Pin only capabilities that both the implementation and the catalog entry support.
            var negotiated = registration.Capabilities
                .Intersect(candidate.AdvertisedCapabilities)
                .ToImmutableHashSet(StringComparer.Ordinal);
            inputs.Add(new ProviderPinInput(
                registration.ResourceId,
                new ResourceNegotiation(
                    new ProviderResourceRef(
                        ProviderSeam.Guardrails,
                        candidate.ProviderId,
                        registration.ResourceId,
                        registration.ResourceGeneration),
                    negotiated)));
            registrations.Add(registration);
        }

        // Pin the whole ordered set to the run in one step, then bind each pin to its classifier and options.
        var pinned = resolver.PinOrdered(runId, resolution.Value, inputs);
        if (!pinned.IsSuccess)
            return Fail(Map(pinned.Error!.Code));

        // Bindings come back in the same order as the inputs, so the index pairs each pin with its registration.
        var checks = pinned.Value!.Bindings
            .Select((pin, index) => registrations[index].Bind(pin))
            .ToImmutableArray();
        return new GuardrailsStagePlanResult(
            new GuardrailsAcceptedStagePlan(stage, contentClass, pinned.Value, checks),
            GuardrailsReasonCode.Allowed);
    }

    // Compares every recorded field. A match on provider ID alone would let a changed adapter or options
    // revision run under an older acceptance.
    private static bool Matches(GuardrailsAcceptedCheck accepted, ProviderCandidate candidate) =>
        accepted.ProviderId == candidate.ProviderId &&
        accepted.AdapterVersion == candidate.AdapterVersion.ToString() &&
        accepted.OptionsSchemaVersion == candidate.OptionsSchemaVersion &&
        accepted.OptionsRevision == candidate.OptionsRevision;

    // Maps shared provider resolver errors to Guardrails reason codes. Any unknown error is treated as a
    // configuration problem, so a new error code still fails closed.
    private static GuardrailsReasonCode Map(ProviderErrorCode code) => code switch
    {
        ProviderErrorCode.ProviderDisabled or ProviderErrorCode.ProviderNotFound =>
            GuardrailsReasonCode.ProviderUnavailable,
        ProviderErrorCode.CapabilityUnavailable => GuardrailsReasonCode.UnsupportedStageOrContent,
        _ => GuardrailsReasonCode.InvalidConfiguration
    };

    private static GuardrailsStagePlanResult Fail(GuardrailsReasonCode reason) => new(null, reason);
}
