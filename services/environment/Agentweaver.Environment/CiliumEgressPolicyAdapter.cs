using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

public static class CiliumEgressCapabilities
{
    public const string L3L4 = "networkpolicy.l3l4";
    public const string Fqdn = "networkpolicy.fqdn";
    public const string Cidr = "networkpolicy.cidr";
    public const string Dns = "networkpolicy.dns";
}

public sealed record CiliumEgressProviderOptions(
    string Namespace,
    Version AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    ImmutableDictionary<string, ImmutableDictionary<string, string>> EndpointSelectors)
{
    public CiliumEgressProviderOptions Validate()
    {
        if (!IsDnsLabel(Namespace) || AdapterVersion is null ||
            OptionsSchemaVersion != 1 || string.IsNullOrWhiteSpace(OptionsRevision) ||
            EndpointSelectors is null)
            throw new CiliumPolicyException(
                "invalid_provider_options",
                "Cilium adapter options require a valid namespace, versioned schema, revision, and endpoint selectors.");

        foreach (var (service, labels) in EndpointSelectors)
        {
            var split = service.Split('/');
            if (split.Length != 2 || !IsDnsLabel(split[0]) || !IsDnsLabel(split[1]) ||
                labels is null || labels.Count == 0 ||
                labels.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                    string.IsNullOrWhiteSpace(pair.Value) || pair.Value == "*"))
                throw new CiliumPolicyException(
                    "invalid_provider_options",
                    "Cilium service selectors must use exact namespace/name identities and nonempty match labels.");
            var namespaceLabel = labels.FirstOrDefault(pair =>
                pair.Key == "k8s:io.kubernetes.pod.namespace");
            if (namespaceLabel.Value != split[0])
                throw new CiliumPolicyException(
                    "invalid_provider_options",
                    "Each Cilium service selector must pin the target namespace.");
        }

        return this;
    }

    public ProviderRegistration CreateRegistration()
    {
        Validate();
        return new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.NetworkPolicy,
                CiliumEgressPolicyAdapter.ProviderId,
                AdapterVersion,
                OptionsSchemaVersion,
                ProviderHostingPattern.KubernetesController,
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    CiliumEgressCapabilities.L3L4,
                    CiliumEgressCapabilities.Fqdn,
                    CiliumEgressCapabilities.Cidr,
                    CiliumEgressCapabilities.Dns)),
            true,
            OptionsRevision,
            OptionsSchemaVersion);
    }

    private static bool IsDnsLabel(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 63 &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

public sealed class EnvironmentEgressSelector
{
    private EnvironmentEgressSelector(
        string @namespace,
        string environmentIdHash,
        string ownerHash,
        string policyName)
    {
        Namespace = @namespace;
        EnvironmentLabel = environmentIdHash;
        OwnerLabel = ownerHash;
        PolicyName = policyName;
    }

    public string Namespace { get; }
    public string EnvironmentLabel { get; }
    public string OwnerLabel { get; }
    public string PolicyName { get; }
    public ImmutableDictionary<string, string> MatchLabels =>
        ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>("agentweaver.dev/managed-environment", "true"),
            new KeyValuePair<string, string>("agentweaver.dev/environment-id", EnvironmentLabel),
            new KeyValuePair<string, string>("agentweaver.dev/environment-owner", OwnerLabel),
        ]);

    public static EnvironmentEgressSelector Create(
        string environmentId,
        string tenantId,
        string projectId,
        string runId,
        string @namespace)
    {
        ValidateIdentifier(environmentId, nameof(environmentId));
        ValidateIdentifier(tenantId, nameof(tenantId));
        ValidateIdentifier(projectId, nameof(projectId));
        ValidateIdentifier(runId, nameof(runId));
        if (!IsNamespace(@namespace))
            throw new CiliumPolicyException("invalid_environment_selector", "Environment namespace is invalid.");

        var environmentHash = HashLabel(environmentId);
        var ownerHash = HashLabel(string.Join('\0', tenantId, projectId, runId));
        return new EnvironmentEgressSelector(
            @namespace,
            environmentHash,
            ownerHash,
            $"aw-egress-{environmentHash[..20]}");
    }

    private static string HashLabel(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsNamespace(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 63 &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new CiliumPolicyException(
                "invalid_environment_selector",
                $"Environment {name} is invalid.");
    }
}

public sealed record CiliumPolicyMetadata(
    string Name,
    string Namespace,
    ImmutableDictionary<string, string> Labels,
    ImmutableDictionary<string, string> Annotations,
    string? ResourceVersion = null,
    long? Generation = null);

public sealed record CiliumEndpointSelector(
    ImmutableDictionary<string, string> MatchLabels);

public sealed record CiliumFqdnSelector(
    string? MatchName = null,
    string? MatchPattern = null);

public sealed record CiliumCidrSelector(string Cidr);

public sealed record CiliumPort(string Port, string Protocol);

public sealed record CiliumDnsRule(string MatchPattern);

public sealed record CiliumPortRules(
    ImmutableArray<CiliumPort> Ports,
    CiliumDnsRuleCollection? Rules = null);

public sealed record CiliumDnsRuleCollection(
    ImmutableArray<CiliumDnsRule> Dns);

public sealed record CiliumEgressRuleDocument(
    ImmutableArray<CiliumEndpointSelector>? ToEndpoints = null,
    [property: JsonPropertyName("toFQDNs")] ImmutableArray<CiliumFqdnSelector>? ToFqDns = null,
    [property: JsonPropertyName("toCIDRSet")] ImmutableArray<CiliumCidrSelector>? ToCidrSet = null,
    ImmutableArray<CiliumPortRules>? ToPorts = null);

public sealed record CiliumPolicySpec(
    CiliumEndpointSelector EndpointSelector,
    ImmutableArray<CiliumEgressRuleDocument> Egress);

public sealed record CiliumNetworkPolicyDocument(
    string ApiVersion,
    string Kind,
    CiliumPolicyMetadata Metadata,
    CiliumPolicySpec Spec);

public sealed class CiliumPolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record CiliumPolicyObservation(
    bool Exists,
    bool ObjectVerified,
    long? AppliedIntentGeneration,
    long? ResourceGeneration,
    string? ResourceVersion,
    string? IntentHash,
    bool DatapathEnforcementVerified,
    bool Revoked = false);

public interface ICiliumPolicyResourceStore
{
    Task<CiliumNetworkPolicyDocument?> GetAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken);

    Task<CiliumNetworkPolicyDocument> CreateAsync(
        CiliumNetworkPolicyDocument policy,
        CancellationToken cancellationToken);

    Task<CiliumNetworkPolicyDocument> ReplaceAsync(
        CiliumNetworkPolicyDocument policy,
        string expectedResourceVersion,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string @namespace,
        string name,
        string expectedResourceVersion,
        CancellationToken cancellationToken);
}

public sealed class CiliumEgressPolicyAdapter(
    ICiliumPolicyResourceStore store,
    CiliumEgressProviderOptions options)
{
    public const string ProviderId = "cilium";
    public const string IntentGenerationAnnotation = "agentweaver.dev/egress-generation";
    public const string IntentHashAnnotation = "agentweaver.dev/egress-intent-sha256";
    public const string IntentRevokedAnnotation = "agentweaver.dev/egress-revoked";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CiliumEgressPolicyAdapter ValidateOptions(EffectiveProviderCandidate candidate)
    {
        options.Validate();
        if (candidate.Seam != ProviderSeam.NetworkPolicy ||
            candidate.Layer != NetworkPolicyLayer.L3L4 ||
            candidate.ProviderId != ProviderId ||
            !Version.TryParse(candidate.AdapterVersion, out var version) ||
            version != options.AdapterVersion ||
            candidate.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            candidate.OptionsRevision != options.OptionsRevision ||
            candidate.Hosting != ProviderHostingPattern.KubernetesController)
            throw new CiliumPolicyException(
                "provider_options_mismatch",
                "Cilium adapter options do not match the selected provider version, layer, schema, and revision.");
        return this;
    }

    public async Task<CiliumNetworkPolicyDocument> ApplyAsync(
        EnvironmentEgressSelector selector,
        CompiledEgressIntent intent,
        long generation,
        long expectedPreviousGeneration,
        CancellationToken cancellationToken)
    {
        if (generation < 1 || expectedPreviousGeneration < 0 ||
            generation <= expectedPreviousGeneration)
            throw new CiliumPolicyException(
                "invalid_generation",
                "Policy generation must be positive and greater than the expected prior generation.");

        var desired = Render(selector, intent, generation);
        var desiredHash = desired.Metadata.Annotations[IntentHashAnnotation];
        var current = await store.GetAsync(selector.Namespace, selector.PolicyName, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            if (expectedPreviousGeneration != 0 || generation != 1)
                throw new CiliumPolicyException(
                    "stale_generation",
                    "A first policy generation must start at generation 1 with expected generation 0.");
            return await store.CreateAsync(desired, cancellationToken).ConfigureAwait(false);
        }

        EnsureSameOwner(selector, current);
        var currentGeneration = ReadIntentGeneration(current);
        var currentHash = current.Metadata.Annotations.GetValueOrDefault(IntentHashAnnotation);
        if (currentGeneration == generation)
        {
            if (expectedPreviousGeneration != currentGeneration - 1)
                throw new CiliumPolicyException(
                    "stale_generation",
                    "An idempotent retry must retain the expected predecessor of the applied generation.");
            if (currentHash == desiredHash && SameSpec(current.Spec, desired.Spec))
                return current;
            throw new CiliumPolicyException(
                "generation_conflict",
                "The requested generation already exists with different policy content.");
        }
        if (currentGeneration != expectedPreviousGeneration || generation <= currentGeneration)
            throw new CiliumPolicyException(
                "stale_generation",
                "The applied policy generation no longer matches the expected predecessor.");

        if (string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
            throw new CiliumPolicyException(
                "invalid_resource_version",
                "Kubernetes returned a policy without a resource version.");
        var replacement = desired with
        {
            Metadata = desired.Metadata with { ResourceVersion = current.Metadata.ResourceVersion }
        };
        return await store.ReplaceAsync(
            replacement,
            current.Metadata.ResourceVersion,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CiliumPolicyObservation> VerifyAsync(
        EnvironmentEgressSelector selector,
        CompiledEgressIntent intent,
        long generation,
        CancellationToken cancellationToken)
    {
        var actual = await store.GetAsync(selector.Namespace, selector.PolicyName, cancellationToken)
            .ConfigureAwait(false);
        if (actual is null)
            return new(false, false, null, null, null, null, false);

        var expected = Render(selector, intent, generation);
        var actualGeneration = ReadIntentGeneration(actual);
        var actualHash = actual.Metadata.Annotations.GetValueOrDefault(IntentHashAnnotation);
        var verified = actualGeneration == generation &&
            actualHash == expected.Metadata.Annotations[IntentHashAnnotation] &&
            !IsRevoked(actual) &&
            actual.Metadata.Namespace == selector.Namespace &&
            actual.Metadata.Name == selector.PolicyName &&
            LabelsMatch(selector, actual.Metadata.Labels) &&
            SameSpec(actual.Spec, expected.Spec);
        return new(
            true,
            verified,
            actualGeneration,
            actual.Metadata.Generation,
            actual.Metadata.ResourceVersion,
            actualHash,
            false);
    }

    public async Task<CiliumPolicyObservation> RevokeAsync(
        EnvironmentEgressSelector selector,
        long generation,
        long expectedPreviousGeneration,
        CancellationToken cancellationToken)
    {
        if (generation < 1 || expectedPreviousGeneration < 0 ||
            generation <= expectedPreviousGeneration)
            throw new CiliumPolicyException(
                "invalid_generation",
                "Revoke generation must be positive and greater than its expected predecessor.");
        var current = await store.GetAsync(selector.Namespace, selector.PolicyName, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            if (expectedPreviousGeneration != 0 || generation != 1)
                throw new CiliumPolicyException(
                    "stale_generation",
                    "A revocation fence without prior Kubernetes state must start at generation 1 with expected generation 0.");
            var firstTombstone = RenderRevocation(selector, generation);
            _ = await store.CreateAsync(firstTombstone, cancellationToken).ConfigureAwait(false);
            return ObserveRevocation(
                selector,
                generation,
                await store.GetAsync(selector.Namespace, selector.PolicyName, cancellationToken)
                    .ConfigureAwait(false));
        }
        EnsureSameOwner(selector, current);
        var currentGeneration = ReadIntentGeneration(current);
        var expectedTombstone = RenderRevocation(selector, generation);
        if (currentGeneration == generation &&
            expectedPreviousGeneration == generation - 1 &&
            IsRevoked(current))
            return ObserveRevocation(selector, generation, current);
        if (currentGeneration != expectedPreviousGeneration)
            throw new CiliumPolicyException(
                "stale_generation",
                "Revoke predecessor does not match the currently applied policy generation.");
        if (string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
            throw new CiliumPolicyException(
                "invalid_resource_version",
                "Kubernetes returned a policy without a resource version.");

        var replacement = expectedTombstone with
        {
            Metadata = expectedTombstone.Metadata with { ResourceVersion = current.Metadata.ResourceVersion }
        };
        _ = await store.ReplaceAsync(
            replacement,
            current.Metadata.ResourceVersion,
            cancellationToken).ConfigureAwait(false);
        var afterReplace = await store.GetAsync(selector.Namespace, selector.PolicyName, cancellationToken)
            .ConfigureAwait(false);
        return ObserveRevocation(selector, generation, afterReplace);
    }

    public async Task<CiliumPolicyObservation> VerifyRevocationAsync(
        EnvironmentEgressSelector selector,
        long generation,
        CancellationToken cancellationToken)
    {
        if (generation < 1)
            throw new CiliumPolicyException(
                "invalid_generation",
                "Revocation generation must be positive.");
        var actual = await store.GetAsync(selector.Namespace, selector.PolicyName, cancellationToken)
            .ConfigureAwait(false);
        return ObserveRevocation(selector, generation, actual);
    }

    public static ImmutableHashSet<string> GetVerifiedCapabilities(CompiledEgressIntent intent)
    {
        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        capabilities.Add(CiliumEgressCapabilities.L3L4);
        if (intent.Rules.Any(rule => rule.DestinationKind == NetworkEgressDestinationKind.Fqdn))
            capabilities.Add(CiliumEgressCapabilities.Fqdn);
        if (intent.Rules.Any(rule => rule.DestinationKind == NetworkEgressDestinationKind.Cidr))
            capabilities.Add(CiliumEgressCapabilities.Cidr);
        if (intent.Rules.Any(rule => rule.Purpose == NetworkEgressPurpose.DnsResolver))
            capabilities.Add(CiliumEgressCapabilities.Dns);
        return capabilities.ToImmutable();
    }

    private CiliumNetworkPolicyDocument Render(
        EnvironmentEgressSelector selector,
        CompiledEgressIntent intent,
        long generation)
    {
        options.Validate();
        if (intent.RequiresL7Mediation)
            throw new CiliumPolicyException(
                "l7_provider_required",
                "Cilium L3/L4 cannot mediate public HTTPS or remote MCP requests at Layer 7.");
        var hash = EgressIntentCompiler.Hash(intent, selector);
        var labels = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>("app.kubernetes.io/managed-by", "agentweaver-environment"),
            new KeyValuePair<string, string>("agentweaver.dev/managed-environment", "true"),
            new KeyValuePair<string, string>("agentweaver.dev/managed-egress", "true"),
            new KeyValuePair<string, string>("agentweaver.dev/environment-id", selector.EnvironmentLabel),
            new KeyValuePair<string, string>("agentweaver.dev/environment-owner", selector.OwnerLabel),
        ]);
        var annotations = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(IntentGenerationAnnotation, generation.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(IntentHashAnnotation, hash),
            new KeyValuePair<string, string>(IntentRevokedAnnotation, "false"),
            new KeyValuePair<string, string>(
                "agentweaver.dev/egress-purposes",
                string.Join(",", intent.PurposeGroups.Select(group => group.Purpose.ToString()))),
        ]);
        var egress = ImmutableArray.CreateBuilder<CiliumEgressRuleDocument>();
        foreach (var group in intent.Rules
            .GroupBy(rule => (rule.Purpose, rule.DestinationKind, rule.Port))
            .OrderBy(group => group.Key.Purpose)
            .ThenBy(group => group.Key.DestinationKind)
            .ThenBy(group => group.Key.Port))
        {
            var ports = ImmutableArray.Create(new CiliumPortRules(
                group.Select(rule => rule.Protocol)
                    .Distinct()
                    .Order()
                    .Select(protocol => new CiliumPort(
                        group.Key.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        protocol.ToString().ToUpperInvariant()))
                    .ToImmutableArray(),
                group.Key.Purpose == NetworkEgressPurpose.DnsResolver
                    ? new CiliumDnsRuleCollection([new CiliumDnsRule("*")])
                    : null));

            switch (group.Key.DestinationKind)
            {
                case NetworkEgressDestinationKind.Fqdn:
                    egress.Add(new CiliumEgressRuleDocument(
                        ToFqDns: group.Select(rule => rule.Destination)
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .Select(ToFqdnSelector)
                            .ToImmutableArray(),
                        ToPorts: ports));
                    break;
                case NetworkEgressDestinationKind.Cidr:
                    egress.Add(new CiliumEgressRuleDocument(
                        ToCidrSet: group.Select(rule => new CiliumCidrSelector(rule.Destination))
                            .Distinct()
                            .OrderBy(rule => rule.Cidr, StringComparer.Ordinal)
                            .ToImmutableArray(),
                        ToPorts: ports));
                    break;
                case NetworkEgressDestinationKind.KubernetesService:
                    var selectors = group.Select(rule => rule.Destination)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .Select(ServiceSelector)
                        .ToImmutableArray();
                    egress.Add(new CiliumEgressRuleDocument(
                        ToEndpoints: selectors,
                        ToPorts: ports));
                    break;
                default:
                    throw new CiliumPolicyException(
                        "unexpressible_destination",
                        "The selected egress destination cannot be expressed by Cilium.");
            }
        }

        return new CiliumNetworkPolicyDocument(
            "cilium.io/v2",
            "CiliumNetworkPolicy",
            new CiliumPolicyMetadata(
                selector.PolicyName,
                selector.Namespace,
                labels,
                annotations),
            new CiliumPolicySpec(
                new CiliumEndpointSelector(selector.MatchLabels),
                egress.ToImmutable()));
    }

    private static CiliumNetworkPolicyDocument RenderRevocation(
        EnvironmentEgressSelector selector,
        long generation)
    {
        var generationText = generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hashMaterial = string.Join('\n', "revoked", selector.Namespace, selector.EnvironmentLabel,
            selector.OwnerLabel, generationText);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashMaterial))).ToLowerInvariant();
        var labels = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>("app.kubernetes.io/managed-by", "agentweaver-environment"),
            new KeyValuePair<string, string>("agentweaver.dev/managed-environment", "true"),
            new KeyValuePair<string, string>("agentweaver.dev/managed-egress", "true"),
            new KeyValuePair<string, string>("agentweaver.dev/environment-id", selector.EnvironmentLabel),
            new KeyValuePair<string, string>("agentweaver.dev/environment-owner", selector.OwnerLabel),
        ]);
        var annotations = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(IntentGenerationAnnotation, generationText),
            new KeyValuePair<string, string>(IntentHashAnnotation, hash),
            new KeyValuePair<string, string>(IntentRevokedAnnotation, "true"),
            new KeyValuePair<string, string>("agentweaver.dev/egress-purposes", "revoked"),
        ]);
        return new CiliumNetworkPolicyDocument(
            "cilium.io/v2",
            "CiliumNetworkPolicy",
            new CiliumPolicyMetadata(
                selector.PolicyName,
                selector.Namespace,
                labels,
                annotations),
            new CiliumPolicySpec(
                new CiliumEndpointSelector(selector.MatchLabels),
                ImmutableArray<CiliumEgressRuleDocument>.Empty));
    }

    private static CiliumPolicyObservation ObserveRevocation(
        EnvironmentEgressSelector selector,
        long generation,
        CiliumNetworkPolicyDocument? actual)
    {
        if (actual is null)
            return new(false, false, null, null, null, null, false);
        var expected = RenderRevocation(selector, generation);
        var actualGeneration = ReadIntentGeneration(actual);
        var actualHash = actual.Metadata.Annotations.GetValueOrDefault(IntentHashAnnotation);
        var verified = actualGeneration == generation &&
            actualHash == expected.Metadata.Annotations[IntentHashAnnotation] &&
            IsRevoked(actual) &&
            actual.Metadata.Namespace == selector.Namespace &&
            actual.Metadata.Name == selector.PolicyName &&
            LabelsMatch(selector, actual.Metadata.Labels) &&
            SameSpec(actual.Spec, expected.Spec);
        return new(
            true,
            verified,
            actualGeneration,
            actual.Metadata.Generation,
            actual.Metadata.ResourceVersion,
            actualHash,
            false,
            verified);
    }

    private static bool IsRevoked(CiliumNetworkPolicyDocument policy) =>
        policy.Metadata.Annotations.GetValueOrDefault(IntentRevokedAnnotation) == "true";

    private CiliumEndpointSelector ServiceSelector(string service)
    {
        if (!options.EndpointSelectors.TryGetValue(service, out var labels))
            throw new CiliumPolicyException(
                "unconfigured_service_selector",
                $"Kubernetes egress service '{service}' has no exact platform-owned Cilium selector.");
        return new CiliumEndpointSelector(labels);
    }

    private static CiliumFqdnSelector ToFqdnSelector(string name) =>
        name.StartsWith("*.", StringComparison.Ordinal)
            ? new CiliumFqdnSelector(MatchPattern: name)
            : new CiliumFqdnSelector(MatchName: name);

    private static void EnsureSameOwner(
        EnvironmentEgressSelector selector,
        CiliumNetworkPolicyDocument current)
    {
        if (!LabelsMatch(selector, current.Metadata.Labels) ||
            current.Metadata.Namespace != selector.Namespace ||
            current.Metadata.Name != selector.PolicyName)
            throw new CiliumPolicyException(
                "policy_owner_mismatch",
                "The existing Cilium policy is not owned by this environment selector.");
    }

    private static bool LabelsMatch(
        EnvironmentEgressSelector selector,
        ImmutableDictionary<string, string> labels) =>
        selector.MatchLabels.All(pair =>
            labels.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static long ReadIntentGeneration(CiliumNetworkPolicyDocument policy)
    {
        var value = policy.Metadata.Annotations.GetValueOrDefault(IntentGenerationAnnotation);
        return long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var generation) && generation > 0
            ? generation
            : throw new CiliumPolicyException(
                "invalid_applied_generation",
                "The stored Cilium policy has no valid intent generation.");
    }

    private static bool SameSpec(CiliumPolicySpec left, CiliumPolicySpec right) =>
        string.Equals(CanonicalJson(left), CanonicalJson(right), StringComparison.Ordinal);

    private static string CanonicalJson<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, JsonOptions));
        return Canonicalize(document.RootElement);
    }

    private static string Canonicalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonicalize(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonicalize)) + "]",
        _ => element.GetRawText()
    };
}

public sealed class KubernetesCiliumPolicyResourceStore(HttpClient httpClient) : ICiliumPolicyResourceStore
{
    private const string CollectionPath = "apis/cilium.io/v2/namespaces";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<CiliumNetworkPolicyDocument?> GetAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, ItemPath(@namespace, name), null, cancellationToken, allowNotFound: true);

    public async Task<CiliumNetworkPolicyDocument> CreateAsync(
        CiliumNetworkPolicyDocument policy,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, NamespacePath(policy.Metadata.Namespace), policy,
            cancellationToken).ConfigureAwait(false);
        return response ?? throw new CiliumPolicyException(
            "kubernetes_api_invalid_response",
            "Kubernetes accepted Cilium policy creation without returning the applied object.");
    }

    public async Task<CiliumNetworkPolicyDocument> ReplaceAsync(
        CiliumNetworkPolicyDocument policy,
        string expectedResourceVersion,
        CancellationToken cancellationToken)
    {
        if (policy.Metadata.ResourceVersion != expectedResourceVersion)
            throw new CiliumPolicyException(
                "stale_resource_version",
                "Cilium policy replacement must use the observed Kubernetes resource version.");
        var response = await SendAsync(HttpMethod.Put,
            ItemPath(policy.Metadata.Namespace, policy.Metadata.Name), policy, cancellationToken)
            .ConfigureAwait(false);
        return response ?? throw new CiliumPolicyException(
            "kubernetes_api_invalid_response",
            "Kubernetes accepted Cilium policy replacement without returning the applied object.");
    }

    public async Task DeleteAsync(
        string @namespace,
        string name,
        string expectedResourceVersion,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, ItemPath(@namespace, name))
        {
            Content = JsonContent.Create(new
            {
                apiVersion = "v1",
                kind = "DeleteOptions",
                preconditions = new { resourceVersion = expectedResourceVersion }
            })
        };
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.OK or
            HttpStatusCode.NoContent or HttpStatusCode.Accepted)
            return;
        ThrowForStatus(response.StatusCode);
    }

    private async Task<CiliumNetworkPolicyDocument?> SendAsync(
        HttpMethod method,
        string path,
        CiliumNetworkPolicyDocument? policy,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (policy is not null)
            request.Content = JsonContent.Create(policy, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            ThrowForStatus(response.StatusCode);
        try
        {
            return await response.Content.ReadFromJsonAsync<CiliumNetworkPolicyDocument>(
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false) ??
                throw new CiliumPolicyException(
                    "kubernetes_api_invalid_response",
                    "Kubernetes returned an empty Cilium policy response.");
        }
        catch (JsonException)
        {
            throw new CiliumPolicyException(
                "kubernetes_api_invalid_response",
                "Kubernetes returned malformed Cilium policy JSON.");
        }
    }

    private static void ThrowForStatus(HttpStatusCode statusCode) =>
        throw new CiliumPolicyException(
            statusCode == HttpStatusCode.Conflict ? "kubernetes_conflict" : "kubernetes_api_failure",
            $"Kubernetes Cilium policy operation failed with HTTP {(int)statusCode}.");

    private static string NamespacePath(string @namespace) =>
        $"{CollectionPath}/{Uri.EscapeDataString(@namespace)}/ciliumnetworkpolicies";

    private static string ItemPath(string @namespace, string name) =>
        $"{NamespacePath(@namespace)}/{Uri.EscapeDataString(name)}";
}
