using System.Text.Json;
using Agentweaver.Api.Diagnostics;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests;

public sealed class KubernetesTopologyServiceTests
{
    [Fact]
    public async Task DiscoverAsync_DefaultsToRuntimeAndUsesStableIds()
    {
        var handler = EmptyHandler("/api/v1/namespaces/agentweaver/pods");
        handler.OnGet("/api/v1/namespaces/agentweaver/pods",
            List("PodList", """
              {
                "apiVersion":"v1","kind":"Pod",
                "metadata":{"name":"api-1","namespace":"agentweaver"},
                "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}
              }
              """));
        var graph = await Service(handler).DiscoverAsync(null);

        graph.RequestedLayers.Should().Equal("runtime");
        graph.Nodes.Should().ContainSingle(n =>
            n.Id == "v1:Pod:agentweaver:api-1" &&
            n.Health == "healthy",
            string.Join("; ", graph.Layers.Select(l => $"{l.Name}={l.Status}:{l.Message}")) +
            " requests=" + string.Join(",", handler.Requests.Select(r => r.Path)));
        handler.Requests.Should().OnlyContain(r =>
            r.Path.Contains("/pods") ||
            r.Path.Contains("/sandboxclaims") ||
            r.Path.Contains("/sandboxwarmpools") ||
            r.Path.Contains("/sandboxtemplates"));
    }

    [Fact]
    public async Task DiscoverAsync_ConnectsAgentweaverRuntimeCrdsToTheirPod()
    {
        var handler = EmptyHandler(
            "/api/v1/namespaces/agentweaver/pods",
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxclaims",
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxwarmpools",
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxtemplates");
        handler.OnGet("/api/v1/namespaces/agentweaver/pods", List("PodList", """
          {"apiVersion":"v1","kind":"Pod","metadata":{"name":"sandbox-1","namespace":"agentweaver"},"status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}}
          """));
        handler.OnGet("/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxclaims", List("SandboxClaimList", """
          {"apiVersion":"extensions.agents.x-k8s.io/v1beta1","kind":"SandboxClaim","metadata":{"name":"claim-1","namespace":"agentweaver"},"spec":{"warmPoolRef":{"name":"pool-1"}},"status":{"sandbox":{"name":"sandbox-1"},"conditions":[{"type":"Ready","status":"True"}]}}
          """));
        handler.OnGet("/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxwarmpools", List("SandboxWarmPoolList", """
          {"apiVersion":"extensions.agents.x-k8s.io/v1beta1","kind":"SandboxWarmPool","metadata":{"name":"pool-1","namespace":"agentweaver"},"spec":{"sandboxTemplateRef":{"name":"template-1"},"replicas":1},"status":{"readyReplicas":1}}
          """));
        handler.OnGet("/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxtemplates", List("SandboxTemplateList", """
          {"apiVersion":"extensions.agents.x-k8s.io/v1beta1","kind":"SandboxTemplate","metadata":{"name":"template-1","namespace":"agentweaver"},"spec":{"podTemplate":{"spec":{"containers":[{"name":"agent","env":[{"name":"TOKEN","value":"hidden"}]}]}}}}
          """));

        var graph = await Service(handler).DiscoverAsync(null);

        graph.Edges.Should().Contain(e => e.Type == "uses-template" && !e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "uses" && !e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "binds" && !e.Inferred);
        JsonSerializer.Serialize(graph).Should().NotContain("hidden");
    }

    [Fact]
    public async Task DiscoverAsync_BuildsAuthoritativeAndInferredRelationships()
    {
        var handler = EmptyHandler(
            "/api/v1/namespaces/agentweaver/pods",
            "/api/v1/namespaces/agentweaver/services",
            "/api/v1/namespaces/agentweaver/serviceaccounts",
            "/apis/apps/v1/namespaces/agentweaver/replicasets",
            "/apis/apps/v1/namespaces/agentweaver/deployments");
        handler.OnGet("/api/v1/namespaces/agentweaver/pods", List("PodList", """
          {
            "apiVersion":"v1","kind":"Pod",
            "metadata":{
              "name":"web-abc","namespace":"agentweaver",
              "labels":{"app":"web"},
              "ownerReferences":[{"apiVersion":"apps/v1","kind":"ReplicaSet","name":"web-rs","uid":"1"}]
            },
            "spec":{
              "serviceAccountName":"web-sa",
              "containers":[{"name":"web","env":[{"name":"TOKEN","value":"do-not-leak"}]}]
            },
            "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}
          }
          """));
        handler.OnGet("/api/v1/namespaces/agentweaver/services", List("ServiceList", """
          {
            "apiVersion":"v1","kind":"Service",
            "metadata":{"name":"web","namespace":"agentweaver"},
            "spec":{"selector":{"app":"web"},"clusterIP":"10.0.0.42","ports":[{"port":80}]}
          }
          """));
        handler.OnGet("/api/v1/namespaces/agentweaver/serviceaccounts", List("ServiceAccountList", """
          {"apiVersion":"v1","kind":"ServiceAccount","metadata":{"name":"web-sa","namespace":"agentweaver"},"secrets":[{"name":"sa-token"}]}
          """));
        handler.OnGet("/apis/apps/v1/namespaces/agentweaver/replicasets", List("ReplicaSetList", """
          {"apiVersion":"apps/v1","kind":"ReplicaSet","metadata":{"name":"web-rs","namespace":"agentweaver","labels":{"app":"web"}},"spec":{"replicas":1},"status":{"readyReplicas":1}}
          """));
        handler.OnGet("/apis/apps/v1/namespaces/agentweaver/deployments", """{"apiVersion":"v1","kind":"DeploymentList","items":[]}""");

        var graph = await Service(handler).DiscoverAsync(["runtime", "networking", "workloads"]);

        graph.Edges.Should().Contain(e => e.Type == "owns" && !e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "selects" && e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "uses-identity" && !e.Inferred);
        var serialized = JsonSerializer.Serialize(graph);
        serialized.Should().NotContain("do-not-leak").And.NotContain("10.0.0.42").And.NotContain("sa-token");
        graph.Nodes.Single(n => n.Type == "Service").Details.Should().ContainKey("type");
    }

    [Fact]
    public async Task DiscoverAsync_UsesGatewayStorageScalingAndPolicyReferences()
    {
        var handler = EmptyHandler(
            "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/gateways",
            "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            "/api/v1/namespaces/agentweaver/services",
            "/api/v1/namespaces/agentweaver/persistentvolumeclaims",
            "/api/v1/persistentvolumes",
            "/apis/storage.k8s.io/v1/storageclasses",
            "/apis/autoscaling/v2/namespaces/agentweaver/horizontalpodautoscalers",
            "/apis/apps/v1/namespaces/agentweaver/deployments");
        handler.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/gateways", List("GatewayList", """
          {"apiVersion":"gateway.networking.k8s.io/v1","kind":"Gateway","metadata":{"name":"public","namespace":"agentweaver"},"spec":{"listeners":[{"name":"https"}]},"status":{"conditions":[{"type":"Programmed","status":"True"}]}}
          """));
        handler.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes", List("HTTPRouteList", """
          {"apiVersion":"gateway.networking.k8s.io/v1","kind":"HTTPRoute","metadata":{"name":"web","namespace":"agentweaver"},"spec":{"parentRefs":[{"name":"public"}],"rules":[{"backendRefs":[{"name":"web"}]}]}}
          """));
        handler.OnGet("/api/v1/namespaces/agentweaver/services", List("ServiceList", """
          {"apiVersion":"v1","kind":"Service","metadata":{"name":"web","namespace":"agentweaver"},"spec":{"ports":[{"port":80}]}}
          """));
        handler.OnGet("/api/v1/namespaces/agentweaver/persistentvolumeclaims", List("PersistentVolumeClaimList", """
          {"apiVersion":"v1","kind":"PersistentVolumeClaim","metadata":{"name":"data","namespace":"agentweaver"},"spec":{"volumeName":"pv-data","storageClassName":"fast"},"status":{"phase":"Bound"}}
          """));
        handler.OnGet("/api/v1/persistentvolumes", List("PersistentVolumeList", """
          {"apiVersion":"v1","kind":"PersistentVolume","metadata":{"name":"pv-data"},"spec":{"storageClassName":"fast","claimRef":{"namespace":"agentweaver","name":"data"}},"status":{"phase":"Bound"}}
          """));
        handler.OnGet("/apis/storage.k8s.io/v1/storageclasses", List("StorageClassList", """
          {"apiVersion":"storage.k8s.io/v1","kind":"StorageClass","metadata":{"name":"fast"},"provisioner":"disk.csi.azure.com"}
          """));
        handler.OnGet("/apis/autoscaling/v2/namespaces/agentweaver/horizontalpodautoscalers", List("HorizontalPodAutoscalerList", """
          {"apiVersion":"autoscaling/v2","kind":"HorizontalPodAutoscaler","metadata":{"name":"web","namespace":"agentweaver"},"spec":{"scaleTargetRef":{"apiVersion":"apps/v1","kind":"Deployment","name":"web"}},"status":{"currentReplicas":2,"desiredReplicas":3}}
          """));
        handler.OnGet("/apis/apps/v1/namespaces/agentweaver/deployments", List("DeploymentList", """
          {"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"web","namespace":"agentweaver"},"spec":{"replicas":3},"status":{"availableReplicas":2}}
          """));

        var graph = await Service(handler).DiscoverAsync(["networking", "storage", "autoscaling", "workloads"]);

        graph.Edges.Should().Contain(e => e.Type == "attaches-to" && !e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "routes-to" && !e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "bound-to" && !e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "uses-storage-class" && !e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "scales" && !e.Inferred);
    }

    [Fact]
    public async Task DiscoverAsync_MarksPolicyAndPdbSelectorEdgesAsInferred()
    {
        var handler = EmptyHandler(
            "/api/v1/namespaces/agentweaver/pods",
            "/apis/networking.k8s.io/v1/namespaces/agentweaver/networkpolicies",
            "/apis/policy/v1/namespaces/agentweaver/poddisruptionbudgets");
        handler.OnGet("/api/v1/namespaces/agentweaver/pods", List("PodList", """
          {"apiVersion":"v1","kind":"Pod","metadata":{"name":"web-1","namespace":"agentweaver","labels":{"app":"web","tier":"frontend"}},"status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}}
          """));
        handler.OnGet("/apis/networking.k8s.io/v1/namespaces/agentweaver/networkpolicies", List("NetworkPolicyList", """
          {
            "apiVersion":"networking.k8s.io/v1","kind":"NetworkPolicy",
            "metadata":{"name":"web-policy","namespace":"agentweaver"},
            "spec":{
              "podSelector":{"matchLabels":{"app":"web"}},
              "ingress":[{"from":[
                {"podSelector":{"matchExpressions":[{"key":"tier","operator":"In","values":["frontend"]}]}},
                {"namespaceSelector":{"matchLabels":{"environment":"trusted"}}}
              ]}]
            }
          }
          """));
        handler.OnGet("/apis/policy/v1/namespaces/agentweaver/poddisruptionbudgets", List("PodDisruptionBudgetList", """
          {"apiVersion":"policy/v1","kind":"PodDisruptionBudget","metadata":{"name":"web-pdb","namespace":"agentweaver"},"spec":{"selector":{"matchLabels":{"app":"web"}}},"status":{"currentHealthy":1,"desiredHealthy":1,"disruptionsAllowed":0}}
          """));

        var graph = await Service(handler).DiscoverAsync(["runtime", "networking", "availability"]);

        graph.Edges.Should().Contain(e => e.Type == "selects" && e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "allows-from" && e.Inferred);
        graph.Edges.Should().Contain(e => e.Type == "protects" && e.Inferred);
        graph.Nodes.Should().Contain(n => n.Type == "NamespaceSelector");
    }

    [Fact]
    public async Task DiscoverAsync_BoundsLargeListsAndMarksTruncation()
    {
        var handler = EmptyHandler("/api/v1/namespaces/agentweaver/pods");
        var pods = Enumerable.Range(0, 300).Select(i => new
        {
            apiVersion = "v1",
            kind = "Pod",
            metadata = new { name = $"pod-{i}", @namespace = "agentweaver" },
            status = new { phase = "Pending" },
        });
        handler.OnGet("/api/v1/namespaces/agentweaver/pods", JsonSerializer.Serialize(new
        {
            apiVersion = "v1",
            kind = "PodList",
            items = pods,
        }));

        var graph = await Service(handler).DiscoverAsync(["runtime"]);

        graph.Nodes.Should().HaveCount(KubernetesTopologyService.ListLimit);
        graph.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverAsync_ReportsUnavailableKindsWithoutFailingGraph()
    {
        var handler = new FakeKubeHandler();
        handler.OnGet("/api/v1/namespaces/agentweaver/pods", """{"apiVersion":"v1","kind":"PodList","items":[]}""");

        var graph = await Service(handler).DiscoverAsync(["runtime", "autoscaling"]);

        graph.Layers.Single(l => l.Name == "runtime").Status.Should().Be("partial");
        graph.Layers.Single(l => l.Name == "autoscaling").Status.Should().Be("unavailable");
        graph.Nodes.Should().BeEmpty();
    }

    private static KubernetesTopologyService Service(FakeKubeHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sandbox:Kubernetes:Namespace"] = "agentweaver",
            })
            .Build();
        var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler);
        return new KubernetesTopologyService(configuration, client);
    }

    private static FakeKubeHandler EmptyHandler(params string[] excluded)
    {
        var handler = new FakeKubeHandler();
        var excludedPaths = excluded.ToHashSet(StringComparer.Ordinal);
        foreach (var path in new[]
        {
            "/api/v1/namespaces/agentweaver/pods",
            "/api/v1/namespaces/agentweaver/services",
            "/api/v1/namespaces/agentweaver/serviceaccounts",
            "/api/v1/namespaces/agentweaver/persistentvolumeclaims",
            "/api/v1/persistentvolumes",
            "/apis/apps/v1/namespaces/agentweaver/deployments",
            "/apis/apps/v1/namespaces/agentweaver/replicasets",
            "/apis/networking.k8s.io/v1/namespaces/agentweaver/networkpolicies",
            "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/gateways",
            "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            "/apis/storage.k8s.io/v1/storageclasses",
            "/apis/autoscaling/v2/namespaces/agentweaver/horizontalpodautoscalers",
            "/apis/autoscaling.k8s.io/v1/namespaces/agentweaver/verticalpodautoscalers",
            "/apis/keda.sh/v1alpha1/namespaces/agentweaver/scaledobjects",
            "/apis/policy/v1/namespaces/agentweaver/poddisruptionbudgets",
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxclaims",
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxwarmpools",
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxtemplates",
        }.Where(path => !excludedPaths.Contains(path)))
            handler.OnGet(path, """{"apiVersion":"v1","kind":"List","items":[]}""");
        return handler;
    }

    private static string List(string kind, string item) =>
        $$"""{"apiVersion":"v1","kind":"{{kind}}","items":[{{item}}]}""";
}
