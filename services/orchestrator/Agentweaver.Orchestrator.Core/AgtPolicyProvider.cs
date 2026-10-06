using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentGovernance.Policy;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.Orchestrator.Core;

public static class PolicyProviderCapabilities
{
    public const string AgentActionEvaluation = "policy.agent-actions.evaluate";

    public static ImmutableHashSet<string> All { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, AgentActionEvaluation);
}

public sealed record AgtPolicyProviderOptions(
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    ImmutableArray<string> PlatformPolicyDocuments,
    int OptionsSchemaVersion = 1)
{
    public void Validate()
    {
        if (!AgtPolicyProvider.IsIdentifier(ResourceId) ||
            ResourceGeneration < 1 ||
            !AgtPolicyProvider.IsIdentifier(OptionsRevision) ||
            OptionsSchemaVersion != AgtPolicyProvider.OptionsSchemaVersion)
            throw new ArgumentException("AGT Policy provider options are invalid.");

        AgtPolicyProvider.ValidatePolicyDocuments(PlatformPolicyDocuments, requireDefaultDeny: true);
    }
}

public sealed record AgtPolicyEvaluationRequest(
    string ActorId,
    string ActionId,
    IReadOnlyDictionary<string, object>? Context = null,
    ImmutableArray<string> ProjectPolicyDocuments = default);

public sealed record AgtPolicyEvaluationResult(
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode,
    string ProviderId,
    Version AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision);

public sealed class AgtPolicyProvider
{
    private sealed record DocumentDecision(bool Allowed);

    private const string AgentGovernancePrincipalId = "did:mesh:agentweaver-policy-adapter";
    private const int MaximumPolicyDocumentLength = 262_144;
    private const int MaximumPolicyDocumentCount = 32;
    private const int MaximumPolicySetLength = 1_048_576;
    private const int MaximumContextEntries = 64;
    private const int MaximumContextStringLength = 4096;
    private static readonly ActivitySource Activities = new("Agentweaver");

    public const string ProviderId = "agt.dotnet-yaml";
    public static Version AdapterVersion { get; } = new(1, 0, 0);
    public const int OptionsSchemaVersion = 1;

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderSeam.Policy,
        ProviderId,
        AdapterVersion,
        OptionsSchemaVersion,
        ProviderHostingPattern.InProcess,
        PolicyProviderCapabilities.All);

    public ProviderRegistration CreateRegistration(AgtPolicyProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ProviderRegistration(
            Descriptor,
            Enabled: true,
            options.OptionsRevision,
            OptionsSchemaVersion);
    }

    public Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
        ProviderCandidate candidate,
        AgtPolicyProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            options.Validate();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Task.FromResult(ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.InvalidConfiguration,
                "The configured AGT Policy provider options are invalid."));
        }

        if (candidate.Seam != ProviderSeam.Policy ||
            candidate.ProviderId != ProviderId ||
            candidate.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            candidate.OptionsRevision != options.OptionsRevision)
            return Task.FromResult(ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.InvalidConfiguration,
                "The Policy candidate does not match the configured AGT options."));

        return Task.FromResult(ProviderResult<ResourceNegotiation>.Success(new ResourceNegotiation(
            new ProviderResourceRef(ProviderSeam.Policy, ProviderId, options.ResourceId, options.ResourceGeneration),
            PolicyProviderCapabilities.All)));
    }

    public async Task<ProviderResult<PinnedProviderBinding>> ResolveNegotiateAndPinAsync(
        ProviderResolver resolver,
        AgtPolicyProviderOptions options,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsIdentifier(runId))
            return ProviderResult<PinnedProviderBinding>.Failure(
                ProviderErrorCode.InvalidConfiguration,
                "A valid run identity is required to pin the Policy provider.");

        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Policy,
            null,
            AdapterVersion,
            OptionsSchemaVersion,
            PolicyProviderCapabilities.All));
        if (!resolution.IsSuccess || resolution.Value?.Candidate is not { } candidate)
            return ProviderResult<PinnedProviderBinding>.Failure(
                resolution.Error?.Code ?? ProviderErrorCode.MissingDefault,
                resolution.Error?.Message ?? "A Policy provider is required.");

        var negotiation = await NegotiateAsync(candidate, options, cancellationToken).ConfigureAwait(false);
        if (!negotiation.IsSuccess)
            return ProviderResult<PinnedProviderBinding>.Failure(
                negotiation.Error!.Code, negotiation.Error.Message);

        var pinned = resolver.Pin(runId, candidate, options.ResourceId, negotiation.Value!);
        if (pinned.IsSuccess)
            RecordPinnedBinding(pinned.Value!);
        return pinned;
    }

    public AgtPolicyEvaluationResult Evaluate(
        AgtPolicyProviderOptions options,
        AgtPolicyEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            options.Validate();
            ValidateRequest(request);

            var context = CreateContext(request);
            var platformDecision = EvaluateDocuments(options.PlatformPolicyDocuments, context);
            if (!platformDecision.IsSuccess)
                return Result(PolicyEvaluationOutcome.Error, PolicyEvaluationReasonCode.EvaluationFailed, options);
            if (!platformDecision.Value!.Allowed)
                return Result(PolicyEvaluationOutcome.Deny, PolicyEvaluationReasonCode.PlatformRuleDenied, options);

            if (!request.ProjectPolicyDocuments.IsDefaultOrEmpty)
            {
                ValidatePolicyDocuments(request.ProjectPolicyDocuments, requireDefaultDeny: false);
                var projectDecision = EvaluateDocuments(request.ProjectPolicyDocuments, context);
                if (!projectDecision.IsSuccess)
                    return Result(PolicyEvaluationOutcome.Error, PolicyEvaluationReasonCode.EvaluationFailed, options);
                if (!projectDecision.Value!.Allowed)
                    return Result(PolicyEvaluationOutcome.Deny, PolicyEvaluationReasonCode.ProjectRuleNarrowed, options);
            }

            return Result(PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed, options);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result(PolicyEvaluationOutcome.Error, PolicyEvaluationReasonCode.EvaluationFailed, options);
        }
    }

    internal static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    internal static void ValidatePolicyDocuments(
        ImmutableArray<string> documents,
        bool requireDefaultDeny)
    {
        if (documents.IsDefaultOrEmpty || documents.Length > MaximumPolicyDocumentCount)
            throw new ArgumentException("The AGT Policy document set is empty or exceeds the supported count.");

        var totalLength = 0;
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document) || document.Length > MaximumPolicyDocumentLength)
                throw new ArgumentException("An AGT Policy document is empty or exceeds the supported size.");
            totalLength += document.Length;
            if (totalLength > MaximumPolicySetLength)
                throw new ArgumentException("The AGT Policy document set exceeds the supported size.");

            var policy = Policy.FromYaml(document);
            if (requireDefaultDeny && policy.DefaultAction != PolicyAction.Deny)
                throw new ArgumentException("Every platform AGT Policy document must default to deny.");
            if (policy.DefaultAction is not (PolicyAction.Allow or PolicyAction.Deny) ||
                policy.Rules.Any(rule => rule.Action is not (PolicyAction.Allow or PolicyAction.Deny)))
                throw new ArgumentException("AGT Policy documents may use only allow and deny actions.");
        }
    }

    private static void ValidateRequest(AgtPolicyEvaluationRequest request)
    {
        if (!IsIdentifier(request.ActorId) || !IsIdentifier(request.ActionId) ||
            (request.Context?.Count ?? 0) > MaximumContextEntries)
            throw new ArgumentException("The AGT Policy evaluation request is invalid.");

        if (request.Context is null)
            return;

        foreach (var (key, value) in request.Context)
        {
            if (!IsContextKey(key) || key is "actor_id" or "action_id" ||
                !IsContextValue(value))
                throw new ArgumentException("The AGT Policy evaluation context is invalid.");
        }
    }

    private static bool IsContextKey(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsContextValue(object value) => value switch
    {
        string text => text.Length <= MaximumContextStringLength,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => true,
        float number => float.IsFinite(number),
        double number => double.IsFinite(number),
        _ => false
    };

    private static Dictionary<string, object> CreateContext(AgtPolicyEvaluationRequest request)
    {
        var context = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["actor_id"] = request.ActorId,
            ["action_id"] = request.ActionId
        };
        if (request.Context is not null)
            foreach (var (key, value) in request.Context)
                context.Add(key, value);
        return context;
    }

    private static ProviderResult<DocumentDecision> EvaluateDocuments(
        ImmutableArray<string> documents,
        Dictionary<string, object> context)
    {
        if (documents.IsDefaultOrEmpty)
            return ProviderResult<DocumentDecision>.Failure(
                ProviderErrorCode.InvalidConfiguration,
                "An AGT Policy decision requires at least one policy document.");

        foreach (var document in documents)
        {
            var engine = new PolicyEngine { ConflictStrategy = ConflictResolutionStrategy.DenyOverrides };
            engine.LoadYaml(document);

            var decision = engine.Evaluate(AgentGovernancePrincipalId, context);
            if (decision.Action == "deny")
                return ProviderResult<DocumentDecision>.Success(new DocumentDecision(false));
            if (decision.Action != "allow")
                return ProviderResult<DocumentDecision>.Failure(
                    ProviderErrorCode.InvalidConfiguration,
                    "AGT returned an unsupported policy action.");
        }

        return ProviderResult<DocumentDecision>.Success(new DocumentDecision(true));
    }

    private static AgtPolicyEvaluationResult Result(
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reason,
        AgtPolicyProviderOptions options) =>
        new(
            outcome,
            reason,
            ProviderId,
            AdapterVersion,
            options.OptionsSchemaVersion == OptionsSchemaVersion
                ? options.OptionsSchemaVersion
                : OptionsSchemaVersion,
            IsIdentifier(options.OptionsRevision) ? options.OptionsRevision : "unavailable");

    private static void RecordPinnedBinding(PinnedProviderBinding binding)
    {
        using var activity = Activities.StartActivity("policy.provider.binding.pinned", ActivityKind.Internal);
        if (activity is null)
            return;

        activity.SetTag("provider.seam", ProviderSeam.Policy.ToString());
        activity.SetTag("provider.resolved.id", binding.ProviderId);
        activity.SetTag("provider.adapter.version", binding.AdapterVersion.ToString());
        activity.SetTag("provider.options.schema_version", binding.OptionsSchemaVersion);
        activity.SetTag("provider.options.revision", binding.OptionsRevision);
        activity.SetTag("provider.negotiated_capabilities",
            binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        activity.SetTag("provider.resource.id_hash",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Resource.ResourceId)))[..24]);
        activity.SetTag("provider.resource.generation", binding.Resource.Generation);
    }
}
