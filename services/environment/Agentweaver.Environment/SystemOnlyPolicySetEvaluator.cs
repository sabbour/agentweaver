using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

public sealed record SystemOnlyPolicyCeiling(
    ImmutableArray<PlatformEgressDestination> SystemDestinations)
{
    public const string Version = "system-only-ceiling-v1";

    public static SystemOnlyPolicyCeiling Create(
        IEnumerable<PlatformEgressDestination> systemDestinations)
    {
        var destinations = (systemDestinations ?? throw new ArgumentNullException(nameof(systemDestinations)))
            .Distinct().OrderBy(destination => destination.Host, StringComparer.Ordinal)
            .ThenBy(destination => destination.Port).ToImmutableArray();
        if (destinations.IsDefault || destinations.Any(destination =>
                destination is null ||
                PlatformEgressHostValidator.Validate(destination.Host, destination.Port) is not null))
            throw new PlatformPolicyProofException(
                "policy_semantics_unsupported", "The System destination ceiling is invalid.");
        return new(destinations);
    }

    public string Hash()
    {
        var payload = string.Join("\n", new[] { Version }
            .Concat(SystemDestinations.Select(destination =>
                $"{destination.Host}:{destination.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
            .Concat(DefaultInternalDestinations.Select(destination =>
                $"{destination.Namespace}\0{destination.LabelValue}\0{destination.Protocol}\0{destination.Port}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    internal static ImmutableArray<CeilingEndpoint> DefaultInternalDestinations { get; } =
    [
        new("kube-system", "k8s-app", "kube-dns", "UDP", 53),
        new("kube-system", "k8s-app", "kube-dns", "TCP", 53),
        new("agentweaver", "agentweaver.io/workload", "environment", "TCP", 8443),
        new("agentweaver", "agentweaver.io/workload", "events-and-sessions", "TCP", 8443),
        new("agentweaver", "agentweaver.io/workload", "identity-broker", "TCP", 8443),
        new("agentweaver", "agentweaver.io/workload", "identity-broker", "TCP", 8444),
        new("agentweaver-gateway", "gateway.networking.k8s.io/gateway-name", "identity-broker", "TCP", 443),
        new("agentweaver", "agentweaver.io/workload", "orchestrator", "TCP", 8443),
        new("agentweaver", "agentweaver.io/workload", "projects-config", "TCP", 8443),
    ];
}

internal sealed record CeilingEndpoint(
    string Namespace,
    string LabelKey,
    string LabelValue,
    string Protocol,
    int Port);

public sealed record SystemOnlyPolicySetEvaluation(
    bool IsSafe,
    int SelectedPolicyCount,
    string EvaluatorVersion)
{
    public const string Version = "policy-set-evaluator-v1";
}

public static class SystemOnlyPolicySetEvaluator
{
    public static SystemOnlyPolicySetEvaluation Evaluate(
        KubernetesPlatformPolicyInventory inventory,
        SystemOnlyPolicyCeiling ceiling)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ceiling);
        var allows = new List<AllowedTraffic>(capacity: 128);
        var denies = new List<AllowedTraffic>(capacity: 64);
        var selected = 0;
        var hasDefaultDeny = false;
        foreach (var policy in inventory.Policies)
        {
            JsonElement spec;
            switch (policy.Kind)
            {
                case "NetworkPolicy":
                {
                    if (policy.Namespace != inventory.Namespace)
                        continue;
                    EnsureRoot(policy.Content, "NetworkPolicy");
                    if (!policy.Content.TryGetProperty("spec", out spec) ||
                        spec.ValueKind != JsonValueKind.Object)
                        throw Unsupported("A NetworkPolicy has no valid spec.");
                    var selector = RequiredObject(spec, "podSelector");
                    var source = ClassifySourceSelector(
                        selector, inventory.Namespace, isKubernetesNetworkPolicy: true);
                    if (source.Coverage == SourceSelectorCoverage.None)
                        continue;
                    selected++;
                    var types = policyTypes(spec);
                    if (!types.Contains("Egress", StringComparer.Ordinal))
                        continue;
                    if (source.Coverage == SourceSelectorCoverage.FullPopulation)
                        hasDefaultDeny = true;
                    foreach (var rule in RequiredArrayOrEmpty(spec, "egress"))
                        AddKubernetesRule(rule, inventory, source, allows);
                    break;
                }
                case "CiliumNetworkPolicy":
                case "CiliumClusterwideNetworkPolicy":
                {
                    if (policy.Kind == "CiliumNetworkPolicy" && policy.Namespace != inventory.Namespace)
                        throw Unsupported("A CiliumNetworkPolicy does not match the target namespace.");
                    if (policy.Kind == "CiliumClusterwideNetworkPolicy" && !string.IsNullOrEmpty(policy.Namespace))
                        throw Unsupported("A CiliumClusterwideNetworkPolicy must not have a namespace.");
                    EnsureRoot(policy.Content, policy.Kind);
                    var specs = ReadCiliumSpecs(policy.Content);
                    foreach (var candidate in specs)
                    {
                        var selector = CiliumSourceSelector(candidate);
                        var source = ClassifySourceSelector(
                            selector, inventory.Namespace, isKubernetesNetworkPolicy: false);
                        if (source.Coverage == SourceSelectorCoverage.None)
                            continue;
                        selected++;
                        var defaults = CiliumDefaultEgressDeny(candidate);
                        if (candidate.TryGetProperty("enableDefaultDeny", out var defaultDeny))
                        {
                            EnsureOnly(defaultDeny, "Cilium enableDefaultDeny", "egress", "ingress");
                            if (defaultDeny.TryGetProperty("ingress", out var ingressDeny))
                                _ = ReadBoolean(ingressDeny, "enableDefaultDeny.ingress");
                            if (defaultDeny.TryGetProperty("egress", out var egressDeny))
                                defaults = ReadBoolean(egressDeny, "enableDefaultDeny.egress");
                        }
                        if (defaults && source.Coverage == SourceSelectorCoverage.FullPopulation)
                            hasDefaultDeny = true;
                        foreach (var rule in RequiredArrayOrEmpty(candidate, "egress"))
                            AddCiliumRule(rule, source, allows);
                        foreach (var rule in RequiredArrayOrEmpty(candidate, "egressDeny"))
                            AddCiliumRule(rule, source, denies, isDeny: true);
                    }
                    break;
                }
                default:
                    throw Unsupported("A policy has an unknown kind.");
            }
        }

        if (!hasDefaultDeny)
            throw Exceeds("No selected policy establishes default-deny egress.");
        foreach (var denied in denies)
            RemoveSameTraffic(allows, denied);
        foreach (var allow in allows)
            if (!IsWithinCeiling(allow, ceiling))
                throw Exceeds($"The combined policy set grants {allow.Describe()} outside the System-only ceiling.");
        return new(true, selected, SystemOnlyPolicySetEvaluation.Version);
    }

    private static void AddKubernetesRule(
        JsonElement rule,
        KubernetesPlatformPolicyInventory inventory,
        SourceSelector source,
        List<AllowedTraffic> allows)
    {
        EnsureOnly(rule, "Kubernetes egress rule", "to", "ports");
        var ports = RequiredArrayOrEmpty(rule, "ports", missingMeansWildcard: true);
        var peers = RequiredArrayOrEmpty(rule, "to", missingMeansWildcard: true);
        if (ports.Length == 0 || peers.Length == 0)
            throw Exceeds("A Kubernetes rule permits all ports or all destinations.");
        var normalizedPorts = ports.Select(ReadKubernetesPort).ToImmutableArray();
        foreach (var peer in peers)
        {
            EnsureOnly(peer, "Kubernetes peer", "namespaceSelector", "podSelector", "ipBlock");
            if (peer.TryGetProperty("ipBlock", out _))
                throw Exceeds("A Kubernetes IP block is outside the System-only packet ceiling.");
            var targetNamespace = inventory.Namespace;
            if (peer.TryGetProperty("namespaceSelector", out var namespaceSelector))
            {
                targetNamespace = ReadExactNamespace(RequiredObject(namespaceSelector, "matchLabels"));
            }
            else if (!peer.TryGetProperty("podSelector", out _))
                throw Exceeds("A Kubernetes peer has no constrained destination.");
            var podSelector = peer.TryGetProperty("podSelector", out var pod)
                ? pod
                : throw Exceeds("A Kubernetes namespace peer without a pod selector grants a broad destination.");
            var workload = ReadExactWorkload(podSelector);
            foreach (var port in normalizedPorts)
                allows.Add(new(
                    TrafficKind.Endpoint,
                    Protocol: port.Protocol,
                    Port: port.Port,
                    Source: source,
                    Endpoint: new(
                        targetNamespace,
                        WorkloadKeyFor(workload.Key),
                        workload.Value,
                        port.Protocol,
                        port.Port)));
        }
    }

    private static void AddCiliumRule(
        JsonElement rule,
        SourceSelector source,
        List<AllowedTraffic> traffic,
        bool isDeny = false)
    {
        EnsureOnly(rule, "Cilium egress rule",
            "toEndpoints", "toFQDNs", "toCIDR", "toCIDRSet", "toPorts",
            "description");
        var destinations = 0;
        if (rule.TryGetProperty("toEndpoints", out var endpoints))
        {
            destinations++;
            if (endpoints.ValueKind != JsonValueKind.Array || endpoints.GetArrayLength() == 0)
                throw Unsupported("A Cilium endpoint destination is empty.");
            var ports = ReadCiliumPorts(rule, isDeny);
            foreach (var endpoint in endpoints.EnumerateArray())
            {
                var labels = ReadSelectorLabels(endpoint);
                if (!labels.TryGetValue("k8s:io.kubernetes.pod.namespace", out var targetNamespace) &&
                    !labels.TryGetValue("io.kubernetes.pod.namespace", out targetNamespace))
                    throw Unsupported("A Cilium endpoint destination does not pin a namespace.");
                var workload = labels.FirstOrDefault(pair =>
                    pair.Key is "agentweaver.io/workload" or "k8s:agentweaver.io/workload" or
                        "k8s:k8s-app" or "k8s-app" or
                        "gateway.networking.k8s.io/gateway-name" or "k8s:gateway.networking.k8s.io/gateway-name");
                if (workload.Value is null)
                    throw Unsupported("A Cilium endpoint destination does not pin a trusted identity.");
                if (labels.Count != 2)
                    throw Unsupported("A Cilium endpoint destination contains unsupported labels.");
                foreach (var port in ports)
                    traffic.Add(new(TrafficKind.Endpoint, port.Protocol, port.Port, source,
                        new TrafficEndpoint(
                            targetNamespace,
                            WorkloadKeyFor(workload.Key),
                            workload.Value,
                            port.Protocol,
                            port.Port)));
            }
        }
        if (rule.TryGetProperty("toFQDNs", out var names))
        {
            destinations++;
            var ports = ReadCiliumPorts(rule, isDeny);
            foreach (var name in names.EnumerateArray())
            {
                EnsureOnly(name, "Cilium FQDN selector", "matchName");
                var host = RequiredString(name, "matchName");
                foreach (var port in ports)
                    traffic.Add(new(TrafficKind.Fqdn, port.Protocol, port.Port, source, Fqdn: host));
            }
        }
        if (rule.TryGetProperty("toCIDR", out _) || rule.TryGetProperty("toCIDRSet", out _))
        {
            destinations++;
            if (!isDeny)
            {
                var ports = ReadCiliumPorts(rule, isDeny);
                foreach (var port in ports)
                    traffic.Add(new(TrafficKind.Cidr, port.Protocol, port.Port, source, Cidr: "opaque-cidr"));
            }
        }
        if (destinations == 0 && rule.TryGetProperty("toPorts", out _))
            throw Exceeds("A Cilium port-only rule permits any destination.");
        if (destinations == 0)
            throw Unsupported("A Cilium rule has no supported destination field.");
    }

    private static SourceSelector ClassifySourceSelector(
        JsonElement selector,
        string @namespace,
        bool isKubernetesNetworkPolicy)
    {
        EnsureOnly(selector, "pod selector", "matchLabels", "matchExpressions");
        var isFull = true;
        var isDisjoint = false;
        var labelEntries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (selector.TryGetProperty("matchLabels", out var labels))
        {
            if (labels.ValueKind != JsonValueKind.Object)
                throw Unsupported("A selector matchLabels value is malformed.");
            foreach (var label in labels.EnumerateObject())
            {
                var value = RequiredStringLabel(label.Value, label.Name);
                labelEntries[label.Name] = value;
                if (IsWorkloadSelectorKey(label.Name, isKubernetesNetworkPolicy))
                {
                    if (value != "agenthost")
                        isDisjoint = true;
                }
                else if (IsNamespaceSelectorKey(label.Name, isKubernetesNetworkPolicy))
                {
                    if (value != @namespace)
                        isDisjoint = true;
                }
                else
                {
                    isFull = false;
                }
            }
        }

        var expressionEntries = new List<string>();
        if (selector.TryGetProperty("matchExpressions", out var expressions))
        {
            if (expressions.ValueKind != JsonValueKind.Array)
                throw Unsupported("A selector matchExpressions value is malformed.");
            foreach (var expression in expressions.EnumerateArray())
            {
                EnsureOnly(expression, "selector expression", "key", "operator", "values");
                var key = RequiredString(expression, "key");
                var operation = RequiredString(expression, "operator");
                var values = expression.TryGetProperty("values", out var list)
                    ? ReadStringArray(list)
                    : ImmutableArray<string>.Empty;
                if (operation is not ("In" or "NotIn" or "Exists" or "DoesNotExist"))
                    throw Unsupported($"The selector operator '{operation}' is unsupported.");

                var isWorkloadKey = IsWorkloadSelectorKey(key, isKubernetesNetworkPolicy);
                var isNamespaceKey = IsNamespaceSelectorKey(key, isKubernetesNetworkPolicy);
                if (isWorkloadKey || isNamespaceKey)
                {
                    var expected = isWorkloadKey ? "agenthost" : @namespace;
                    if ((operation == "In" && !values.Contains(expected, StringComparer.Ordinal)) ||
                        (operation == "NotIn" && values.Contains(expected, StringComparer.Ordinal)) ||
                        operation == "DoesNotExist")
                    {
                        isDisjoint = true;
                    }
                }
                else
                {
                    isFull = false;
                }

                var sortedValues = string.Join(",", values.OrderBy(value => value, StringComparer.Ordinal));
                expressionEntries.Add($"{key}:{operation}:[{sortedValues}]");
            }
        }

        if (isDisjoint)
            return new(SourceSelectorCoverage.None, string.Empty);

        if (isFull)
            return new(SourceSelectorCoverage.FullPopulation, "FULL");

        expressionEntries.Sort(StringComparer.Ordinal);
        var canonicalLabels = string.Join(";", labelEntries.Select(pair => $"{pair.Key}={pair.Value}"));
        var canonicalExpressions = string.Join(";", expressionEntries);
        return new(SourceSelectorCoverage.Subset, $"{canonicalLabels}|{canonicalExpressions}");
    }

    private static bool IsWorkloadSelectorKey(string key, bool isKubernetesNetworkPolicy) =>
        key == "agentweaver.io/workload" ||
        (!isKubernetesNetworkPolicy && key == "k8s:agentweaver.io/workload");

    private static bool IsNamespaceSelectorKey(string key, bool isKubernetesNetworkPolicy) =>
        !isKubernetesNetworkPolicy &&
        key is "k8s:io.kubernetes.pod.namespace" or "io.kubernetes.pod.namespace";

    private static JsonElement CiliumSourceSelector(JsonElement spec)
    {
        if (spec.TryGetProperty("endpointSelector", out var selector))
            return selector;
        throw Unsupported("A Cilium policy has no endpoint selector.");
    }

    private static ImmutableArray<JsonElement> ReadCiliumSpecs(JsonElement policy)
    {
        var result = ImmutableArray.CreateBuilder<JsonElement>();
        if (policy.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object)
            result.Add(ValidateCiliumSpec(spec));
        else if (!policy.TryGetProperty("specs", out _))
            throw Unsupported("A Cilium policy has no valid spec.");
        if (policy.TryGetProperty("specs", out var specs))
        {
            if (specs.ValueKind != JsonValueKind.Array)
                throw Unsupported("A Cilium specs value is malformed.");
            foreach (var item in specs.EnumerateArray())
                result.Add(ValidateCiliumSpec(item));
        }
        if (result.Count == 0)
            throw Unsupported("A Cilium policy has no valid spec entries.");
        return result.ToImmutable();
    }

    private static JsonElement ValidateCiliumSpec(JsonElement spec)
    {
        EnsureOnly(spec, "Cilium policy spec",
            "endpointSelector", "ingress", "egress", "ingressDeny", "egressDeny",
            "enableDefaultDeny", "description", "labels", "log");
        return spec;
    }

    private static ImmutableArray<TrafficPort> ReadCiliumPorts(JsonElement rule, bool isDeny)
    {
        if (!rule.TryGetProperty("toPorts", out var toPorts) ||
            toPorts.ValueKind != JsonValueKind.Array || toPorts.GetArrayLength() == 0)
            throw Exceeds("A Cilium destination rule does not constrain ports.");
        var result = ImmutableArray.CreateBuilder<TrafficPort>();
        foreach (var portRule in toPorts.EnumerateArray())
        {
            EnsureOnly(portRule, "Cilium port rule", "ports", "rules");
            ValidateCiliumRules(portRule, isDeny);
            if (!portRule.TryGetProperty("ports", out var ports) ||
                ports.ValueKind != JsonValueKind.Array || ports.GetArrayLength() == 0)
                throw Unsupported("A Cilium port rule is malformed.");
            foreach (var port in ports.EnumerateArray())
            {
                EnsureOnly(port, "Cilium port", "port", "protocol");
                var value = RequiredString(port, "port");
                if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                    number is < 1 or > 65535)
                    throw Unsupported("A named or malformed Cilium port is unsupported.");
                var protocol = RequiredString(port, "protocol").ToUpperInvariant();
                if (protocol is not ("TCP" or "UDP"))
                    throw Unsupported("A Cilium transport protocol is unsupported.");
                result.Add(new(protocol, number));
            }
        }
        return result.ToImmutable();
    }

    private static void ValidateCiliumRules(JsonElement portRule, bool isDeny)
    {
        if (!portRule.TryGetProperty("rules", out var rules))
            return;
        if (isDeny)
            throw Unsupported("Cilium Layer 7 rules are unsupported in deny policies.");
        EnsureOnly(rules, "Cilium Layer 7 rules", "dns");
        if (!rules.TryGetProperty("dns", out var dns) ||
            dns.ValueKind != JsonValueKind.Array)
            throw Unsupported("A Cilium Layer 7 rule has unsupported content.");
        foreach (var rule in dns.EnumerateArray())
        {
            EnsureOnly(rule, "Cilium DNS rule", "matchName");
            _ = RequiredString(rule, "matchName");
        }
    }

    private static TrafficPort ReadKubernetesPort(JsonElement port)
    {
        EnsureOnly(port, "Kubernetes port", "protocol", "port", "endPort");
        var protocol = port.TryGetProperty("protocol", out var protocolValue)
            ? RequiredString(protocolValue).ToUpperInvariant()
            : "TCP";
        if (protocol is not ("TCP" or "UDP") || port.TryGetProperty("endPort", out _))
            throw Unsupported("A Kubernetes port or protocol is unsupported.");
        if (!port.TryGetProperty("port", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) || number is < 1 or > 65535)
            throw Exceeds("A Kubernetes named or wildcard port is outside the supported ceiling.");
        return new(protocol, number);
    }

    private static ImmutableDictionary<string, string> ReadSelectorLabels(JsonElement endpoint)
    {
        EnsureOnly(endpoint, "Cilium selector", "matchLabels", "matchExpressions");
        if (endpoint.TryGetProperty("matchExpressions", out var expressions) &&
            expressions.ValueKind == JsonValueKind.Array && expressions.GetArrayLength() > 0)
            throw Unsupported("Cilium destination selector expressions are unsupported.");
        return endpoint.TryGetProperty("matchLabels", out var labels)
            ? ReadStringMap(labels)
            : throw Unsupported("A Cilium destination does not pin exact labels.");
    }

    private static KeyValuePair<string, string> ReadExactWorkload(JsonElement selector)
    {
        if (selector.TryGetProperty("matchExpressions", out var expressions) &&
            expressions.ValueKind == JsonValueKind.Array && expressions.GetArrayLength() > 0)
            throw Unsupported("Kubernetes destination selector expressions are unsupported.");
        var labels = ReadStringMap(RequiredObject(selector, "matchLabels"));
        var workload = labels.FirstOrDefault(pair =>
            pair.Key is "agentweaver.io/workload" or "k8s-app" or "gateway.networking.k8s.io/gateway-name");
        if (workload.Value is null || labels.Count != 1)
            throw Unsupported("A Kubernetes pod destination does not pin one trusted identity.");
        return workload;
    }

    private static string ReadExactNamespace(JsonElement labels)
    {
        var map = ReadStringMap(labels);
        if (map.Count != 1 ||
            !map.TryGetValue("kubernetes.io/metadata.name", out var name))
            throw Unsupported("A Kubernetes namespace destination is not exact.");
        return name;
    }

    private static bool IsWithinCeiling(AllowedTraffic allow, SystemOnlyPolicyCeiling ceiling)
    {
        if (allow.Kind == TrafficKind.Fqdn)
            return allow.Protocol == "TCP" && allow.Port == 443 &&
                ceiling.SystemDestinations.Any(destination => destination.Host == allow.Fqdn);
        if (allow.Kind == TrafficKind.Endpoint)
            return SystemOnlyPolicyCeiling.DefaultInternalDestinations.Any(expected =>
                expected.Namespace == allow.Endpoint!.Namespace &&
                expected.LabelKey == allow.Endpoint.LabelKey &&
                expected.LabelValue == allow.Endpoint.LabelValue &&
                expected.Protocol == allow.Endpoint.Protocol &&
                expected.Port == allow.Endpoint.Port);
        return false;
    }

    private static void RemoveSameTraffic(List<AllowedTraffic> allows, AllowedTraffic denied)
    {
        for (var index = allows.Count - 1; index >= 0; index--)
            if (SameTraffic(allows[index], denied) && SourceCovers(denied.Source, allows[index].Source))
                allows.RemoveAt(index);
    }

    private static bool SameTraffic(AllowedTraffic left, AllowedTraffic right) =>
        left.Kind == right.Kind &&
        left.Protocol == right.Protocol &&
        left.Port == right.Port &&
        left.Endpoint == right.Endpoint &&
        left.Fqdn == right.Fqdn &&
        left.Cidr == right.Cidr;

    private static bool SourceCovers(SourceSelector denied, SourceSelector allow) =>
        denied.Coverage == SourceSelectorCoverage.FullPopulation ||
        (denied.Coverage == SourceSelectorCoverage.Subset && denied.CanonicalKey == allow.CanonicalKey);

    private static bool CiliumDefaultEgressDeny(JsonElement spec) =>
        spec.TryGetProperty("egress", out var egress) &&
        egress.ValueKind == JsonValueKind.Array && egress.GetArrayLength() > 0;

    private static ImmutableArray<string> policyTypes(JsonElement spec)
    {
        if (spec.TryGetProperty("policyTypes", out var types))
            return ReadStringArray(types);
        return spec.TryGetProperty("egress", out _)
            ? ["Ingress", "Egress"]
            : ["Ingress"];
    }

    private static JsonElement[] RequiredArrayOrEmpty(
        JsonElement objectElement,
        string name,
        bool missingMeansWildcard = false)
    {
        if (!objectElement.TryGetProperty(name, out var value))
        {
            if (missingMeansWildcard)
                throw Exceeds($"A missing {name} field permits a wildcard.");
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array)
            throw Unsupported($"The {name} field is malformed.");
        return value.EnumerateArray().ToArray();
    }

    private static void EnsureRoot(JsonElement root, string kind)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            RequiredString(root, "kind") != kind)
            throw Unsupported("A policy root is malformed.");
    }

    private static void EnsureOnly(JsonElement value, string name, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Unsupported($"The {name} is malformed.");
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw Unsupported($"The {name} field '{property.Name}' is unsupported.");
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw Unsupported($"The {name} field is malformed.");
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw Unsupported($"The {name} field is malformed.");
        return value.GetString()!;
    }

    private static string RequiredString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw Unsupported("A required string is malformed.");
        return value.GetString()!;
    }

    private static string RequiredStringLabel(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw Unsupported($"The selector label '{key}' is malformed.");

    private static ImmutableDictionary<string, string> ReadStringMap(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Unsupported("A label map is malformed.");
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            result[property.Name] = RequiredStringLabel(property.Value, property.Name);
        return result.ToImmutable();
    }

    private static ImmutableArray<string> ReadStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw Unsupported("A string list is malformed.");
        return value.EnumerateArray().Select(RequiredString).ToImmutableArray();
    }

    private static bool ReadBoolean(JsonElement value, string name) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Unsupported($"The {name} field is malformed."),
        };

    private static string WorkloadKeyFor(string key) => key switch
    {
        "k8s-app" or "k8s:k8s-app" => "k8s-app",
        "gateway.networking.k8s.io/gateway-name" or "k8s:gateway.networking.k8s.io/gateway-name" =>
            "gateway.networking.k8s.io/gateway-name",
        _ => "agentweaver.io/workload",
    };

    private static PlatformPolicyProofException Unsupported(string message) =>
        new("policy_semantics_unsupported", message);

    private static PlatformPolicyProofException Exceeds(string message) =>
        new("policy_set_exceeds_system", message);

    internal readonly record struct TrafficPort(string Protocol, int Port);

    internal sealed record TrafficEndpoint(
        string Namespace,
        string LabelKey,
        string LabelValue,
        string Protocol,
        int Port);

    private enum TrafficKind { Endpoint, Fqdn, Cidr }

    private enum SourceSelectorCoverage { None, Subset, FullPopulation }

    private sealed record SourceSelector(SourceSelectorCoverage Coverage, string CanonicalKey);

    private sealed record AllowedTraffic(
        TrafficKind Kind,
        string Protocol,
        int Port,
        SourceSelector Source,
        TrafficEndpoint? Endpoint = null,
        string? Fqdn = null,
        string? Cidr = null)
    {
        public string Describe() => Kind switch
        {
            TrafficKind.Endpoint =>
                $"{Endpoint?.Namespace}/{Endpoint?.LabelValue} {Protocol}/{Port}",
            TrafficKind.Fqdn => $"{Fqdn} {Protocol}/{Port}",
            _ => $"CIDR {Cidr} {Protocol}/{Port}",
        };
    }
}
