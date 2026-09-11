using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using k8s;

namespace Agentweaver.Api.Diagnostics;

/// <summary>Discovers a bounded, safe Kubernetes relationship graph for cluster diagnostics.</summary>
public sealed class KubernetesTopologyService
{
    public const int MaxNodes = 250;
    public const int MaxEdges = 500;
    internal const int ListLimit = 100;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(8);
    private static readonly string[] SupportedLayers =
        ["runtime", "networking", "workloads", "storage", "autoscaling", "availability"];

    private readonly IKubernetes? _k8s;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _discoveryGate = new(6, 6);

    public KubernetesTopologyService(IConfiguration configuration, IKubernetes? k8s = null)
    {
        _configuration = configuration;
        _k8s = k8s;
    }

    public async Task<KubernetesTopologyDto> DiscoverAsync(
        IEnumerable<string>? requestedLayers,
        CancellationToken ct = default)
    {
        var layers = NormalizeLayers(requestedLayers);
        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        if (_k8s is null)
            return Empty(ns, layers, "Kubernetes client is not configured.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DiscoveryTimeout);
        var state = new GraphState(ns);

        var tasks = layers.Select(layer => DiscoverLayerAsync(layer, state, timeout.Token)).ToArray();
        LayerResult[] results;
        try
        {
            results = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            results = layers.Select(layer => new LayerResult(layer, "partial", "Discovery budget reached.")).ToArray();
        }

        state.BuildEdges();
        var nodes = state.Nodes.Values
            .OrderBy(n => n.Layer, StringComparer.Ordinal)
            .ThenBy(n => n.Type, StringComparer.Ordinal)
            .ThenBy(n => n.Namespace, StringComparer.Ordinal)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .Take(MaxNodes)
            .Select(n => n.ToDto())
            .ToList();
        var nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = state.Edges.Values
            .Where(e => nodeIds.Contains(e.Source) && nodeIds.Contains(e.Target))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Take(MaxEdges)
            .ToList();
        var truncated = state.Truncated || state.Nodes.Count > MaxNodes || state.Edges.Count > MaxEdges;

        return new KubernetesTopologyDto
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            Namespace = ns,
            RequestedLayers = layers,
            Layers = SupportedLayers.Select(layer =>
            {
                var result = results.FirstOrDefault(r => r.Name == layer);
                return new KubernetesTopologyLayerDto
                {
                    Name = layer,
                    Status = result?.Status ?? "not_requested",
                    ResourceCount = nodes.Count(n => n.Layer == layer),
                    Message = result?.Message ?? "Layer was not requested.",
                };
            }).ToList(),
            Nodes = nodes,
            Edges = edges,
            Truncated = truncated,
        };
    }

    private async Task<LayerResult> DiscoverLayerAsync(string layer, GraphState state, CancellationToken ct)
    {
        var resources = layer switch
        {
            "runtime" => new[]
            {
                NamespacedCore("Pod", "pods", async token => (object)await _k8s!.CoreV1.ListNamespacedPodAsync(state.Namespace, limit: ListLimit, cancellationToken: token)),
                Custom("SandboxClaim", "extensions.agents.x-k8s.io/v1beta1", "sandboxclaims"),
                Custom("SandboxWarmPool", "extensions.agents.x-k8s.io/v1beta1", "sandboxwarmpools"),
                Custom("SandboxTemplate", "extensions.agents.x-k8s.io/v1beta1", "sandboxtemplates"),
            },
            "networking" => new[]
            {
                NamespacedCore("Service", "services", async token => (object)await _k8s!.CoreV1.ListNamespacedServiceAsync(state.Namespace, limit: ListLimit, cancellationToken: token)),
                Custom("NetworkPolicy", "networking.k8s.io/v1", "networkpolicies"),
                Custom("Gateway", "gateway.networking.k8s.io/v1", "gateways"),
                Custom("HTTPRoute", "gateway.networking.k8s.io/v1", "httproutes"),
            },
            "workloads" => new[]
            {
                Namespaced("Deployment", "apps/v1", "deployments", async token => (object)await _k8s!.AppsV1.ListNamespacedDeploymentAsync(state.Namespace, limit: ListLimit, cancellationToken: token)),
                Namespaced("ReplicaSet", "apps/v1", "replicasets", async token => (object)await _k8s!.AppsV1.ListNamespacedReplicaSetAsync(state.Namespace, limit: ListLimit, cancellationToken: token)),
                NamespacedCore("ServiceAccount", "serviceaccounts", async token => (object)await _k8s!.CoreV1.ListNamespacedServiceAccountAsync(state.Namespace, limit: ListLimit, cancellationToken: token)),
            },
            "storage" => new[]
            {
                NamespacedCore("PersistentVolumeClaim", "persistentvolumeclaims", async token => (object)await _k8s!.CoreV1.ListNamespacedPersistentVolumeClaimAsync(state.Namespace, limit: ListLimit, cancellationToken: token)),
                ClusterCore("PersistentVolume", "persistentvolumes", async token => (object)await _k8s!.CoreV1.ListPersistentVolumeAsync(limit: ListLimit, cancellationToken: token)),
                Cluster("StorageClass", "storage.k8s.io/v1", "storageclasses", async token => (object)await _k8s!.StorageV1.ListStorageClassAsync(limit: ListLimit, cancellationToken: token)),
            },
            "autoscaling" => new[]
            {
                Namespaced("HorizontalPodAutoscaler", "autoscaling/v2", "horizontalpodautoscalers", async token => (object)await _k8s!.AutoscalingV2.ListNamespacedHorizontalPodAutoscalerAsync(state.Namespace, limit: ListLimit, cancellationToken: token)),
                Custom("VerticalPodAutoscaler", "autoscaling.k8s.io/v1", "verticalpodautoscalers"),
                Custom("ScaledObject", "keda.sh/v1alpha1", "scaledobjects"),
            },
            "availability" => new[]
            {
                Custom("PodDisruptionBudget", "policy/v1", "poddisruptionbudgets"),
            },
            _ => Array.Empty<ResourceQuery>(),
        };

        var outcomes = await Task.WhenAll(resources.Select(r => DiscoverResourceAsync(layer, r, state, ct))).ConfigureAwait(false);
        var available = outcomes.Count(o => o == QueryOutcome.Available);
        var unavailable = outcomes.Count(o => o == QueryOutcome.Unavailable);
        if (available == resources.Length)
            return new LayerResult(layer, "available", $"{available} resource types discovered.");
        if (available > 0)
            return new LayerResult(layer, "partial", $"{available} resource types available; {unavailable} unavailable.");
        return new LayerResult(layer, "unavailable", "This layer is unavailable or not installed.");
    }

    private async Task<QueryOutcome> DiscoverResourceAsync(
        string layer, ResourceQuery query, GraphState state, CancellationToken ct)
    {
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var raw = await query.Fetch(ct).ConfigureAwait(false);
            var typedItems = raw.GetType().GetProperty("Items")?.GetValue(raw) as System.Collections.IEnumerable;
            if (typedItems is not null)
            {
                var count = 0;
                lock (state.Gate)
                {
                    foreach (var item in typedItems)
                    {
                        if (item is null || count++ >= ListLimit) break;
                        using var itemDoc = JsonDocument.Parse(KubernetesJson.Serialize(item));
                        state.AddNode(layer, query, itemDoc.RootElement.Clone());
                    }
                    if (count >= ListLimit) state.Truncated = true;
                }
                return QueryOutcome.Available;
            }

            using var doc = JsonDocument.Parse(KubernetesJson.Serialize(raw));
            var items = Property(doc.RootElement, "items");
            if (items.ValueKind != JsonValueKind.Array)
                return QueryOutcome.Available;
            lock (state.Gate)
            {
                foreach (var item in items.EnumerateArray().Take(ListLimit))
                    state.AddNode(layer, query, item.Clone());
                if (items.GetArrayLength() >= ListLimit ||
                    Property(doc.RootElement, "metadata") is var metadata &&
                    metadata.ValueKind == JsonValueKind.Object &&
                    Property(metadata, "continue") is var continuation &&
                    continuation.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(continuation.GetString()))
                    state.Truncated = true;
            }
            return QueryOutcome.Available;
        }
        catch (OperationCanceledException) { throw; }
        catch (k8s.Autorest.HttpOperationException ex) when (
            ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return QueryOutcome.Unavailable;
        }
        catch
        {
            return QueryOutcome.Unavailable;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private ResourceQuery Custom(string kind, string apiVersion, string plural)
    {
        var split = apiVersion.Split('/', 2);
        return Namespaced(kind, apiVersion, plural, token => _k8s!.CustomObjects.ListNamespacedCustomObjectAsync(
            split[0], split[1], _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver",
            plural, limit: ListLimit, cancellationToken: token));
    }

    private static ResourceQuery NamespacedCore(string kind, string plural, Func<CancellationToken, Task<object>> fetch) =>
        new(kind, "v1", plural, true, fetch);
    private static ResourceQuery ClusterCore(string kind, string plural, Func<CancellationToken, Task<object>> fetch) =>
        new(kind, "v1", plural, false, fetch);
    private static ResourceQuery Namespaced(string kind, string apiVersion, string plural, Func<CancellationToken, Task<object>> fetch) =>
        new(kind, apiVersion, plural, true, fetch);
    private static ResourceQuery Cluster(string kind, string apiVersion, string plural, Func<CancellationToken, Task<object>> fetch) =>
        new(kind, apiVersion, plural, false, fetch);

    private static IReadOnlyList<string> NormalizeLayers(IEnumerable<string>? requested)
    {
        var values = requested?
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(v => v.ToLowerInvariant())
            .Where(v => SupportedLayers.Contains(v, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return values is { Count: > 0 } ? values : ["runtime"];
    }

    private static KubernetesTopologyDto Empty(string ns, IReadOnlyList<string> requested, string message) => new()
    {
        GeneratedUtc = DateTimeOffset.UtcNow,
        Namespace = ns,
        RequestedLayers = requested,
        Layers = SupportedLayers.Select(layer => new KubernetesTopologyLayerDto
        {
            Name = layer,
            Status = requested.Contains(layer) ? "unavailable" : "not_requested",
            ResourceCount = 0,
            Message = requested.Contains(layer) ? message : "Layer was not requested.",
        }).ToList(),
        Nodes = [],
        Edges = [],
        Truncated = false,
    };

    private sealed record ResourceQuery(
        string Kind, string ApiVersion, string Plural, bool Namespaced,
        Func<CancellationToken, Task<object>> Fetch);
    private sealed record LayerResult(string Name, string Status, string Message);
    private enum QueryOutcome { Available, Unavailable }

    private sealed class GraphState(string ns)
    {
        public object Gate { get; } = new();
        public string Namespace { get; } = ns;
        public Dictionary<string, ResourceNode> Nodes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, KubernetesTopologyEdgeDto> Edges { get; } = new(StringComparer.Ordinal);
        public bool Truncated { get; set; }

        public void AddNode(string layer, ResourceQuery query, JsonElement resource)
        {
            var metadata = Property(resource, "metadata");
            var name = String(metadata, "name");
            if (string.IsNullOrWhiteSpace(name)) return;
            var resourceNs = query.Namespaced ? String(metadata, "namespace") ?? Namespace : null;
            var id = StableId(query.ApiVersion, query.Kind, resourceNs, name);
            Nodes[id] = new ResourceNode(
                id, layer, query.Kind, query.ApiVersion, name, resourceNs,
                Labels(metadata), resource, Health(query.Kind, resource),
                Summary(query.Kind, resource), Details(query.Kind, resource));
        }

        public void BuildEdges()
        {
            foreach (var node in Nodes.Values.ToList())
            {
                AddOwnerEdges(node);
                switch (node.Type)
                {
                    case "Service": AddSelectorEdges(node, "selects", PodNodes(node.Namespace)); break;
                    case "Deployment": AddSelectorEdges(node, "selects", ReplicaSetAndPodNodes(node.Namespace)); break;
                    case "NetworkPolicy": AddNetworkPolicyEdges(node); break;
                    case "PodDisruptionBudget": AddSelectorEdges(node, "protects", PodNodes(node.Namespace)); break;
                    case "HTTPRoute": AddHttpRouteEdges(node); break;
                    case "Pod": AddPodEdges(node); break;
                    case "PersistentVolumeClaim": AddPvcEdges(node); break;
                    case "PersistentVolume": AddPvEdges(node); break;
                    case "HorizontalPodAutoscaler":
                    case "VerticalPodAutoscaler":
                    case "ScaledObject": AddScaleTargetEdge(node); break;
                    case "SandboxClaim":
                        AddNamedEdge(node, "spec", "warmPoolRef", "name", "SandboxWarmPool", "uses", false);
                        AddSandboxClaimPodEdge(node);
                        break;
                    case "SandboxWarmPool":
                        AddNamedEdge(node, "spec", "sandboxTemplateRef", "name", "SandboxTemplate", "uses-template", false);
                        AddNamedEdge(node, "spec", "templateRef", "name", "SandboxTemplate", "uses-template", false);
                        break;
                }
            }
        }

        private void AddOwnerEdges(ResourceNode child)
        {
            var metadata = Property(child.Resource, "metadata");
            var refs = Property(metadata, "ownerReferences");
            if (refs.ValueKind != JsonValueKind.Array) return;
            foreach (var owner in refs.EnumerateArray())
            {
                var kind = String(owner, "kind");
                var name = String(owner, "name");
                if (kind is null || name is null) continue;
                var parent = Find(kind, child.Namespace, name);
                if (parent is not null) AddEdge(parent, child, "owns", false, "Kubernetes owner reference");
            }
        }

        private void AddSelectorEdges(ResourceNode source, string type, IEnumerable<ResourceNode> candidates)
        {
            var selector = source.Type switch
            {
                "Service" => Property(Property(source.Resource, "spec"), "selector"),
                "NetworkPolicy" => Property(Property(source.Resource, "spec"), "podSelector"),
                _ => Property(Property(source.Resource, "spec"), "selector"),
            };
            if (selector.ValueKind != JsonValueKind.Object) return;
            var matches = source.Type == "Service"
                ? candidates.Where(c => Matches(c.Labels, ObjectStrings(selector)))
                : candidates.Where(c => MatchesSelector(c.Labels, selector));
            foreach (var target in matches)
                AddEdge(source, target, type, true, "Selector match");
        }

        private void AddHttpRouteEdges(ResourceNode route)
        {
            var spec = Property(route.Resource, "spec");
            var parents = Property(spec, "parentRefs");
            if (parents.ValueKind == JsonValueKind.Array)
            {
                foreach (var parent in parents.EnumerateArray())
                {
                    if (String(parent, "kind") is { } kind && kind != "Gateway") continue;
                    var name = String(parent, "name");
                    var ns = String(parent, "namespace") ?? route.Namespace;
                    var gateway = name is null ? null : Find("Gateway", ns, name);
                    if (gateway is not null) AddEdge(route, gateway, "attaches-to", false, "HTTPRoute parentRef");
                }
            }
            var rules = Property(spec, "rules");
            if (rules.ValueKind != JsonValueKind.Array) return;
            foreach (var backend in rules.EnumerateArray().SelectMany(rule =>
                         Property(rule, "backendRefs").ValueKind == JsonValueKind.Array
                             ? Property(rule, "backendRefs").EnumerateArray().ToArray()
                             : []))
            {
                if (String(backend, "kind") is { } kind && kind != "Service") continue;
                var name = String(backend, "name");
                var ns = String(backend, "namespace") ?? route.Namespace;
                var service = name is null ? null : Find("Service", ns, name);
                if (service is not null) AddEdge(route, service, "routes-to", false, "HTTPRoute backendRef");
            }
        }

        private void AddPodEdges(ResourceNode pod)
        {
            var spec = Property(pod.Resource, "spec");
            var serviceAccount = String(spec, "serviceAccountName") ?? "default";
            var account = Find("ServiceAccount", pod.Namespace, serviceAccount);
            if (account is not null) AddEdge(pod, account, "uses-identity", false, "Pod serviceAccountName");

            var volumes = Property(spec, "volumes");
            if (volumes.ValueKind != JsonValueKind.Array) return;
            foreach (var volume in volumes.EnumerateArray())
            {
                var claimName = String(Property(volume, "persistentVolumeClaim"), "claimName");
                var claim = claimName is null ? null : Find("PersistentVolumeClaim", pod.Namespace, claimName);
                if (claim is not null) AddEdge(pod, claim, "mounts", false, "Pod volume claim");
            }
        }

        private void AddPvcEdges(ResourceNode claim)
        {
            var spec = Property(claim.Resource, "spec");
            var volume = Find("PersistentVolume", null, String(spec, "volumeName"));
            if (volume is not null) AddEdge(claim, volume, "bound-to", false, "PVC volumeName");
            var storageClass = Find("StorageClass", null, String(spec, "storageClassName"));
            if (storageClass is not null) AddEdge(claim, storageClass, "uses-storage-class", false, "PVC storageClassName");
        }

        private void AddPvEdges(ResourceNode volume)
        {
            var spec = Property(volume.Resource, "spec");
            var claimRef = Property(spec, "claimRef");
            var claim = Find("PersistentVolumeClaim", String(claimRef, "namespace"), String(claimRef, "name"));
            if (claim is not null) AddEdge(volume, claim, "claims", false, "PV claimRef");
            var storageClass = Find("StorageClass", null, String(spec, "storageClassName"));
            if (storageClass is not null) AddEdge(volume, storageClass, "uses-storage-class", false, "PV storageClassName");
        }

        private void AddScaleTargetEdge(ResourceNode scaler)
        {
            var target = Property(Property(scaler.Resource, "spec"), "scaleTargetRef");
            var kind = String(target, "kind");
            var name = String(target, "name");
            var resource = kind is null || name is null ? null : Find(kind, scaler.Namespace, name);
            if (resource is not null) AddEdge(scaler, resource, "scales", false, "scaleTargetRef");
        }

        private void AddNamedEdge(ResourceNode source, string root, string refName, string nameField, string targetKind, string edgeType, bool inferred)
        {
            var name = String(Property(Property(source.Resource, root), refName), nameField);
            var target = Find(targetKind, source.Namespace, name);
            if (target is not null) AddEdge(source, target, edgeType, inferred, $"{refName}.{nameField}");
        }

        private void AddNetworkPolicyEdges(ResourceNode policy)
        {
            AddSelectorEdges(policy, "selects", PodNodes(policy.Namespace));
            var spec = Property(policy.Resource, "spec");
            foreach (var direction in new[] { "ingress", "egress" })
            {
                var rules = Property(spec, direction);
                if (rules.ValueKind != JsonValueKind.Array) continue;
                foreach (var rule in rules.EnumerateArray())
                {
                    var peers = Property(rule, direction == "ingress" ? "from" : "to");
                    if (peers.ValueKind != JsonValueKind.Array) continue;
                    foreach (var peer in peers.EnumerateArray())
                    {
                        var podSelector = Property(peer, "podSelector");
                        if (podSelector.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var pod in PodNodes(policy.Namespace).Where(p => MatchesSelector(p.Labels, podSelector)))
                                AddEdge(policy, pod, direction == "ingress" ? "allows-from" : "allows-to", true, "NetworkPolicy podSelector peer");
                        }

                        var namespaceSelector = Property(peer, "namespaceSelector");
                        if (namespaceSelector.ValueKind == JsonValueKind.Object)
                        {
                            var selectorNode = AddSelectorNode(policy, namespaceSelector, "NamespaceSelector");
                            AddEdge(policy, selectorNode, direction == "ingress" ? "allows-from" : "allows-to", true, "NetworkPolicy namespaceSelector peer");
                        }
                    }
                }
            }
        }

        private ResourceNode AddSelectorNode(ResourceNode policy, JsonElement selector, string type)
        {
            var canonical = selector.GetRawText();
            var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))[..12].ToLowerInvariant();
            var name = $"{policy.Name}-{digest}";
            var id = StableId("internal.agentweaver.io/v1", type, policy.Namespace, name);
            if (!Nodes.TryGetValue(id, out var node))
            {
                node = new ResourceNode(
                    id, policy.Layer, type, "internal.agentweaver.io/v1", name, policy.Namespace,
                    new Dictionary<string, string>(), default, "unknown",
                    "Namespace selector · inferred peer",
                    new Dictionary<string, string> { ["selectorTerms"] = SelectorTermCount(selector).ToString() });
                Nodes[id] = node;
            }
            return node;
        }

        private void AddSandboxClaimPodEdge(ResourceNode claim)
        {
            var podName = String(Property(Property(claim.Resource, "status"), "sandbox"), "name");
            var pod = Find("Pod", claim.Namespace, podName);
            if (pod is not null) AddEdge(claim, pod, "binds", false, "SandboxClaim status.sandbox.name");
        }

        private IEnumerable<ResourceNode> PodNodes(string? ns) =>
            Nodes.Values.Where(n => n.Type == "Pod" && n.Namespace == ns);
        private IEnumerable<ResourceNode> ReplicaSetAndPodNodes(string? ns) =>
            Nodes.Values.Where(n => (n.Type == "ReplicaSet" || n.Type == "Pod") && n.Namespace == ns);

        private ResourceNode? Find(string kind, string? ns, string? name) =>
            name is null ? null : Nodes.Values.FirstOrDefault(n =>
                n.Type.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
                n.Namespace == ns && n.Name == name);

        private void AddEdge(ResourceNode source, ResourceNode target, string type, bool inferred, string summary)
        {
            var id = $"{source.Id}|{type}|{target.Id}";
            Edges[id] = new KubernetesTopologyEdgeDto
            {
                Id = id, Source = source.Id, Target = target.Id, Type = type,
                Inferred = inferred, Summary = summary,
            };
        }

        private static bool Matches(IReadOnlyDictionary<string, string> labels, IReadOnlyDictionary<string, string> selector) =>
            selector.Count > 0 && selector.All(pair => labels.TryGetValue(pair.Key, out var value) && value == pair.Value);

        private static bool MatchesSelector(IReadOnlyDictionary<string, string> labels, JsonElement selector)
        {
            var matchLabels = ObjectStrings(Property(selector, "matchLabels"));
            if (matchLabels.Count == 0)
                matchLabels = ObjectStrings(selector);
            if (matchLabels.Any(pair => !labels.TryGetValue(pair.Key, out var value) || value != pair.Value))
                return false;
            var expressions = Property(selector, "matchExpressions");
            // Kubernetes LabelSelector {} selects every resource in scope.
            if (expressions.ValueKind != JsonValueKind.Array) return true;
            foreach (var expression in expressions.EnumerateArray())
            {
                var key = String(expression, "key");
                var operation = String(expression, "operator");
                var values = Property(expression, "values");
                if (key is null || operation is null) return false;
                var allowed = values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray().Select(v => v.GetString()).Where(v => v is not null).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string?>();
                var hasValue = labels.TryGetValue(key, out var actual);
                if (operation == "In" && (!hasValue || !allowed.Contains(actual)) ||
                    operation == "NotIn" && hasValue && allowed.Contains(actual) ||
                    operation == "Exists" && !hasValue ||
                    operation == "DoesNotExist" && hasValue)
                    return false;
            }
            return true;
        }
    }

    private sealed record ResourceNode(
        string Id, string Layer, string Type, string ApiVersion, string Name, string? Namespace,
        IReadOnlyDictionary<string, string> Labels, JsonElement Resource, string Health,
        string Summary, IReadOnlyDictionary<string, string> Details)
    {
        public KubernetesTopologyNodeDto ToDto() => new()
        {
            Id = Id, Layer = Layer, Type = Type, ApiVersion = ApiVersion, Name = Name,
            Namespace = Namespace, Health = Health, Summary = Summary, Details = Details,
        };
    }

    internal static string StableId(string apiVersion, string kind, string? ns, string name) =>
        $"{apiVersion}:{kind}:{ns ?? "_cluster"}:{name}";

    private static string Health(string kind, JsonElement item)
    {
        var status = Property(item, "status");
        return kind switch
        {
            "Pod" => String(status, "phase") switch
            {
                "Running" or "Succeeded" => ConditionsHealthy(status) ? "healthy" : "attention",
                "Failed" => "critical",
                "Pending" => "attention",
                _ => "unknown",
            },
            "Deployment" => Int(status, "availableReplicas") >= Math.Max(1, Int(Property(item, "spec"), "replicas"))
                ? "healthy" : Int(Property(item, "spec"), "replicas") == 0
                    ? "healthy" : Int(status, "availableReplicas") > 0 ? "attention" : "critical",
            "ReplicaSet" => Int(status, "readyReplicas") >= Int(Property(item, "spec"), "replicas")
                ? "healthy" : Int(status, "readyReplicas") > 0 ? "attention" : "critical",
            "PersistentVolumeClaim" or "PersistentVolume" => String(status, "phase") switch
            {
                "Bound" or "Available" => "healthy",
                "Lost" or "Failed" => "critical",
                "Pending" or "Released" => "attention",
                _ => "unknown",
            },
            "Gateway" or "HTTPRoute" => ConditionsHealthy(status) ? "healthy" :
                Property(status, "conditions").ValueKind == JsonValueKind.Array ? "attention" : "unknown",
            "PodDisruptionBudget" => Int(status, "currentHealthy") >= Int(status, "desiredHealthy") ? "healthy" : "attention",
            "HorizontalPodAutoscaler" or "VerticalPodAutoscaler" or "ScaledObject" =>
                ConditionsHealthy(status) ? "healthy" : "unknown",
            "SandboxClaim" => ConditionsHealthy(status) ? "healthy" : "attention",
            "SandboxWarmPool" => Int(status, "readyReplicas") >= Math.Max(1, Int(Property(item, "spec"), "replicas"))
                ? "healthy" : Int(status, "readyReplicas") > 0 ? "attention" : "critical",
            _ => "unknown",
        };
    }

    private static string Summary(string kind, JsonElement item)
    {
        var spec = Property(item, "spec");
        var status = Property(item, "status");
        return kind switch
        {
            "Pod" => $"Pod · {String(status, "phase") ?? "unknown"}",
            "Deployment" => $"Deployment · {Int(status, "availableReplicas")}/{Int(spec, "replicas")} available",
            "ReplicaSet" => $"ReplicaSet · {Int(status, "readyReplicas")}/{Int(spec, "replicas")} ready",
            "Service" => $"Service · {String(spec, "type") ?? "ClusterIP"} · {ArrayCount(spec, "ports")} ports",
            "NetworkPolicy" => $"NetworkPolicy · {ArrayCount(spec, "policyTypes")} policy types",
            "Gateway" => $"Gateway · {ArrayCount(spec, "listeners")} listeners",
            "HTTPRoute" => $"HTTPRoute · {ArrayCount(spec, "rules")} rules",
            "ServiceAccount" => "Service account",
            "PersistentVolumeClaim" => $"PVC · {String(status, "phase") ?? "unknown"}",
            "PersistentVolume" => $"PV · {String(status, "phase") ?? "unknown"}",
            "StorageClass" => $"StorageClass · {String(item, "provisioner") ?? "unknown provisioner"}",
            "HorizontalPodAutoscaler" => $"HPA · {Int(status, "currentReplicas")}/{Int(status, "desiredReplicas")} current/desired",
            "VerticalPodAutoscaler" => $"VPA · {String(Property(spec, "updatePolicy"), "updateMode") ?? "Auto"}",
            "ScaledObject" => $"KEDA ScaledObject · {ArrayCount(spec, "triggers")} triggers",
            "PodDisruptionBudget" => $"PDB · {Int(status, "disruptionsAllowed")} disruptions allowed",
            "SandboxClaim" => $"SandboxClaim · {(ConditionsHealthy(status) ? "ready" : "pending")}",
            "SandboxWarmPool" => $"SandboxWarmPool · {Int(status, "readyReplicas")}/{Int(spec, "replicas")} ready",
            "SandboxTemplate" => "SandboxTemplate",
            _ => kind,
        };
    }

    private static IReadOnlyDictionary<string, string> Details(string kind, JsonElement item)
    {
        var spec = Property(item, "spec");
        var status = Property(item, "status");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(result, "phase", String(status, "phase"));
        Add(result, "serviceAccount", String(spec, "serviceAccountName"));
        Add(result, "storageClass", String(spec, "storageClassName"));
        Add(result, "volume", String(spec, "volumeName"));
        Add(result, "replicas", Number(spec, "replicas"));
        Add(result, "readyReplicas", Number(status, "readyReplicas"));
        Add(result, "availableReplicas", Number(status, "availableReplicas"));
        Add(result, "currentReplicas", Number(status, "currentReplicas"));
        Add(result, "desiredReplicas", Number(status, "desiredReplicas"));
        Add(result, "disruptionsAllowed", Number(status, "disruptionsAllowed"));
        if (kind == "Service") Add(result, "type", String(spec, "type") ?? "ClusterIP");
        if (kind == "StorageClass") Add(result, "provisioner", String(item, "provisioner"));
        if (kind == "NetworkPolicy")
        {
            Add(result, "ingressRules", ArrayCount(spec, "ingress").ToString());
            Add(result, "egressRules", ArrayCount(spec, "egress").ToString());
        }
        return result;
    }

    private static void Add(Dictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !key.Contains("token", StringComparison.OrdinalIgnoreCase) &&
            !key.Contains("secret", StringComparison.OrdinalIgnoreCase))
            target[key] = value.Length <= 160 ? value : value[..160];
    }

    private static bool ConditionsHealthy(JsonElement status)
    {
        var conditions = Property(status, "conditions");
        if (conditions.ValueKind != JsonValueKind.Array)
        {
            var parents = Property(status, "parents");
            if (parents.ValueKind == JsonValueKind.Array)
            {
                var parentConditions = parents.EnumerateArray()
                    .SelectMany(parent => Property(parent, "conditions").ValueKind == JsonValueKind.Array
                        ? Property(parent, "conditions").EnumerateArray().ToArray()
                        : [])
                    .ToList();
                return RelevantConditionsHealthy(parentConditions);
            }
            return false;
        }
        return RelevantConditionsHealthy(conditions.EnumerateArray());
    }

    private static bool RelevantConditionsHealthy(IEnumerable<JsonElement> conditions)
    {
        var relevant = conditions
            .Where(c => String(c, "type") is "Ready" or "Available" or "Accepted" or "Programmed" or "ResolvedRefs" or "AbleToScale")
            .ToList();
        return relevant.Count > 0 && relevant.All(c => String(c, "status") == "True");
    }

    private static IReadOnlyDictionary<string, string> Labels(JsonElement metadata) =>
        ObjectStrings(Property(metadata, "labels"));

    private static Dictionary<string, string> ObjectStrings(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString()!;
        return result;
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (element.TryGetProperty(name, out var value)) return value;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return default;
    }
    private static string? String(JsonElement element, string name) =>
        Property(element, name).ValueKind == JsonValueKind.String ? Property(element, name).GetString() : null;
    private static int Int(JsonElement element, string name) =>
        Property(element, name).ValueKind == JsonValueKind.Number && Property(element, name).TryGetInt32(out var value) ? value : 0;
    private static string? Number(JsonElement element, string name) =>
        Property(element, name).ValueKind == JsonValueKind.Number ? Property(element, name).GetRawText() : null;
    private static int ArrayCount(JsonElement element, string name) =>
        Property(element, name).ValueKind == JsonValueKind.Array ? Property(element, name).GetArrayLength() : 0;
    private static int SelectorTermCount(JsonElement selector) =>
        ObjectStrings(Property(selector, "matchLabels")).Count + ArrayCount(selector, "matchExpressions");
}
