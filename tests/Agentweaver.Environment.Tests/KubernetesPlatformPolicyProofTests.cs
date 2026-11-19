using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment.Tests;

public sealed class KubernetesPlatformPolicyProofTests
{
    [Fact]
    public async Task InventoryCapturesAllThreeCollectionsAndStableHashes()
    {
        var api = new FakePolicyApi(SafePolicies());
        using var client = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var reader = new KubernetesPlatformPolicyInventoryReader(client);
        var capture = Capture();

        var first = await reader.CaptureAsync("cluster-a", capture, CancellationToken.None);
        var second = await reader.CaptureAsync("cluster-a", capture, CancellationToken.None);

        Assert.Equal(3, first.Policies.Length);
        Assert.Contains(first.Policies, policy => policy.Kind == "NetworkPolicy");
        Assert.Contains(first.Policies, policy => policy.Kind == "CiliumNetworkPolicy");
        Assert.Contains(first.Policies, policy => policy.Kind == "CiliumClusterwideNetworkPolicy");
        Assert.Equal(first.SnapshotId, second.SnapshotId);

        api.NetworkPolicies = api.NetworkPolicies.SetItem(0, api.NetworkPolicies[0] with
        {
            ResourceVersion = "2",
        });
        var changed = await reader.CaptureAsync("cluster-a", capture, CancellationToken.None);
        Assert.NotEqual(first.SnapshotId, changed.SnapshotId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task InventoryRejectsUnavailablePolicyKinds(HttpStatusCode status)
    {
        var api = new FakePolicyApi(SafePolicies()) { CiliumStatus = status };
        using var client = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            new KubernetesPlatformPolicyInventoryReader(client).CaptureAsync(
                "cluster-a", Capture(), CancellationToken.None));
        Assert.Equal("policy_inventory_unavailable", error.Code);
    }

    [Fact]
    public async Task InventoryRejectsIncompleteMalformedAndDeletingLists()
    {
        var api = new FakePolicyApi(SafePolicies()) { MalformedCiliumList = true };
        using var client = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var reader = new KubernetesPlatformPolicyInventoryReader(client);
        var malformed = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            reader.CaptureAsync("cluster-a", Capture(), CancellationToken.None));
        Assert.Equal("policy_inventory_unavailable", malformed.Code);

        api.MalformedCiliumList = false;
        api.Deleting = true;
        var deleting = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            reader.CaptureAsync("cluster-a", Capture(), CancellationToken.None));
        Assert.Equal("policy_inventory_unavailable", deleting.Code);
    }

    [Fact]
    public void EvaluatorAcceptsSystemOnlySetAcrossNamespaceAndClusterPolicies()
    {
        var inventory = Inventory(SafePolicies());
        var result = SystemOnlyPolicySetEvaluator.Evaluate(inventory, Ceiling());

        Assert.True(result.IsSafe);
        Assert.Equal(3, result.SelectedPolicyCount);
    }

    [Fact]
    public void EvaluatorRejectsEmptyInventoryAndCombinedWiderGrant()
    {
        var empty = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory([]), Ceiling()));
        Assert.Equal("policy_set_exceeds_system", empty.Code);

        var wide = SafePolicies().Add(Policy(
            "CiliumNetworkPolicy",
            "agenthost-model-egress",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toFQDNs":[{"matchName":"extra.example.com"}],
                "toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}]
            }
            """));
        var denied = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(wide), Ceiling()));
        Assert.Equal("policy_set_exceeds_system", denied.Code);
    }

    [Fact]
    public void EvaluatorIgnoresDisjointSelectorsBeforeInspectingUnsupportedRules()
    {
        var unrelated = SafePolicies().Add(Policy(
            "CiliumClusterwideNetworkPolicy",
            "worker-policy",
            "",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"build-worker"}},
              "egress":[{"toEntities":["world"]}]
            }
            """));

        Assert.True(SystemOnlyPolicySetEvaluator.Evaluate(Inventory(unrelated), Ceiling()).IsSafe);
    }

    [Fact]
    public void EvaluatorAppliesExactCiliumDenyAcrossPolicyKinds()
    {
        var extra = Policy(
            "CiliumNetworkPolicy",
            "agenthost-model-egress",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toFQDNs":[{"matchName":"extra.example.com"}],
                "toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}]
            }
            """);
        var deny = Policy(
            "CiliumClusterwideNetworkPolicy",
            "deny-extra",
            "",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egressDeny":[{"toFQDNs":[{"matchName":"extra.example.com"}],
                "toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}]
            }
            """);

        Assert.True(SystemOnlyPolicySetEvaluator.Evaluate(
            Inventory(SafePolicies().Add(extra).Add(deny)), Ceiling()).IsSafe);
    }

    [Fact]
    public void EvaluatorRejectsUnsupportedSelectedSemantics()
    {
        var malformed = SafePolicies().Add(Policy(
            "CiliumNetworkPolicy",
            "unsupported",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEntities":["world"]}]
            }
            """));
        var error = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(malformed), Ceiling()));
        Assert.Equal("policy_semantics_unsupported", error.Code);
    }

    private static ImmutableArray<KubernetesPlatformPolicyRecord> SafePolicies() =>
    [
        Policy(
            "NetworkPolicy",
            "allow-dns",
            "agentweaver",
            """
            {
              "podSelector":{},
              "policyTypes":["Egress"],
              "egress":[{
                "to":[{"namespaceSelector":{"matchLabels":{"kubernetes.io/metadata.name":"kube-system"}},
                  "podSelector":{"matchLabels":{"k8s-app":"kube-dns"}}}],
                "ports":[{"protocol":"UDP","port":53},{"protocol":"TCP","port":53}]
              }]
            }
            """),
        Policy(
            "CiliumNetworkPolicy",
            "agenthost-platform-egress",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toFQDNs":[{"matchName":"api.example.com"}],
                "toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}],
              "egressDeny":[{"toCIDRSet":[{"cidr":"0.0.0.0/8"}]}]
            }
            """),
        Policy(
            "CiliumClusterwideNetworkPolicy",
            "agenthost-owner-egress",
            "",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver",
                "agentweaver.io/workload":"environment"}}],
                "toPorts":[{"ports":[{"port":"8443","protocol":"TCP"}]}]}]
            }
            """),
    ];

    private static SystemOnlyPolicyCeiling Ceiling() =>
        SystemOnlyPolicyCeiling.Create([new PlatformEgressDestination("api.example.com", 443)]);

    private static KubernetesPlatformPolicyCapture Capture() =>
        new(
            "agentweaver",
            "namespace-uid-1",
            1,
            Sha("guard"),
            Sha("profile"),
            SystemOnlyPolicySetEvaluation.Version,
            SystemOnlyPolicyCeiling.Version,
            Ceiling().Hash());

    private static KubernetesPlatformPolicyInventory Inventory(
        IEnumerable<KubernetesPlatformPolicyRecord> policies) =>
        new(
            "cluster-a",
            "agentweaver",
            "namespace-uid-1",
            "10",
            "11",
            "12",
            policies.ToImmutableArray(),
            Sha("snapshot"));

    private static KubernetesPlatformPolicyRecord Policy(
        string kind,
        string name,
        string @namespace,
        string spec)
    {
        var apiVersion = kind == "NetworkPolicy" ? "networking.k8s.io/v1" : "cilium.io/v2";
        var json = $$"""
        {
          "apiVersion":"{{apiVersion}}",
          "kind":"{{kind}}",
          "metadata":{
            "name":"{{name}}",
            {{(@namespace.Length == 0 ? "" : $"\"namespace\":\"{@namespace}\",")}}
            "uid":"uid-{{name}}",
            "resourceVersion":"1"
          },
          "spec":{{spec}}
        }
        """;
        using var document = JsonDocument.Parse(json);
        return new(
            apiVersion,
            kind,
            @namespace,
            name,
            "uid-" + name,
            "1",
            json,
            document.RootElement.Clone());
    }

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FakePolicyApi(ImmutableArray<KubernetesPlatformPolicyRecord> policies)
        : HttpMessageHandler
    {
        public ImmutableArray<KubernetesPlatformPolicyRecord> NetworkPolicies { get; set; } =
            policies.Where(policy => policy.Kind == "NetworkPolicy").ToImmutableArray();
        public ImmutableArray<KubernetesPlatformPolicyRecord> CiliumPolicies { get; } =
            policies.Where(policy => policy.Kind == "CiliumNetworkPolicy").ToImmutableArray();
        public ImmutableArray<KubernetesPlatformPolicyRecord> ClusterwidePolicies { get; } =
            policies.Where(policy => policy.Kind == "CiliumClusterwideNetworkPolicy").ToImmutableArray();
        public HttpStatusCode CiliumStatus { get; set; } = HttpStatusCode.OK;
        public bool MalformedCiliumList { get; set; }
        public bool Deleting { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/api/v1/namespaces/agentweaver", StringComparison.Ordinal) &&
                !path.Contains("networkpolicies", StringComparison.Ordinal))
                return Json(new { apiVersion = "v1", kind = "Namespace",
                    metadata = new { name = "agentweaver", uid = "namespace-uid-1", resourceVersion = "9" } });
            if (path.Contains("networkpolicies", StringComparison.Ordinal) &&
                path.Contains("networking.k8s.io", StringComparison.Ordinal))
                return List("NetworkPolicyList", NetworkPolicies, "10");
            if (path.Contains("ciliumnetworkpolicies", StringComparison.Ordinal) &&
                path.Contains("namespaces", StringComparison.Ordinal))
            {
                if (CiliumStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(CiliumStatus));
                return MalformedCiliumList
                    ? Json(new { apiVersion = "cilium.io/v2", kind = "CiliumNetworkPolicyList", items = new object[0] })
                    : List("CiliumNetworkPolicyList", CiliumPolicies, "11");
            }
            if (path.Contains("ciliumclusterwidenetworkpolicies", StringComparison.Ordinal))
                return List("CiliumClusterwideNetworkPolicyList", ClusterwidePolicies, "12");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private Task<HttpResponseMessage> List(
            string kind,
            ImmutableArray<KubernetesPlatformPolicyRecord> policies,
            string resourceVersion)
        {
            var items = policies.Select(policy =>
            {
                var json = JsonDocument.Parse(Deleting
                    ? policy.Content.GetRawText().Replace(
                        $"\"resourceVersion\":\"{policy.ResourceVersion}\"",
                        $"\"resourceVersion\":\"{policy.ResourceVersion}\",\"deletionTimestamp\":\"2026-11-18T00:00:00Z\"")
                    : policy.Content.GetRawText().Replace(
                        "\"resourceVersion\":\"1\"",
                        $"\"resourceVersion\":\"{policy.ResourceVersion}\""));
                return json.RootElement.Clone();
            }).ToArray();
            return Json(new
            {
                apiVersion = kind.StartsWith("NetworkPolicy", StringComparison.Ordinal)
                    ? "networking.k8s.io/v1"
                    : "cilium.io/v2",
                kind,
                metadata = new { resourceVersion, @continue = "" },
                items,
            });
        }

        private static Task<HttpResponseMessage> Json(object value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value),
            });
    }
}
