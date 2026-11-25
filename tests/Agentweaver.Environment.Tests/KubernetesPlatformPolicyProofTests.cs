using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Npgsql;

namespace Agentweaver.Environment.Tests;

public sealed class KubernetesPlatformPolicyProofTests
{
    [Fact]
    public void SnapshotLimitCountsTheSerializedUtf8Representation()
    {
        var original = Inventory(SafePolicies());
        var record = original.Policies[0] with { CanonicalContent = "é\"\\\n" };
        var inventory = original with { Policies = [record] };
        var baseline = PlatformPolicyProofSnapshot.Serialize(inventory);
        Assert.Contains("\\u00E9", baseline);
        Assert.Contains("\\u0022", baseline);
        var remaining = PlatformPolicyProofSnapshot.MaxBytes - Encoding.UTF8.GetByteCount(baseline);
        var exact = inventory with
        {
            Policies = [record with { CanonicalContent = record.CanonicalContent + new string('x', remaining) }],
        };
        var json = PlatformPolicyProofSnapshot.Serialize(exact);
        Assert.Equal(PlatformPolicyProofSnapshot.MaxBytes, Encoding.UTF8.GetByteCount(json));
        var over = exact with
        {
            Policies = [exact.Policies[0] with { CanonicalContent = exact.Policies[0].CanonicalContent + "x" }],
        };
        var error = Assert.Throws<PlatformPolicyProofException>(() => PlatformPolicyProofSnapshot.Serialize(over));
        Assert.Equal("policy_semantics_unsupported", error.Code);
    }

    [Fact]
    public void ProofCompositionDoesNotAcceptCallerOwnedTransports()
    {
        Assert.Empty(typeof(PlatformPolicyProofCoordinator).GetConstructors());
        var create = Assert.Single(typeof(PlatformPolicyProofFactory).GetMethods(),
            method => method.Name == nameof(PlatformPolicyProofFactory.Create));
        Assert.DoesNotContain(create.GetParameters(), parameter =>
            parameter.ParameterType == typeof(HttpClient) ||
            typeof(HttpMessageHandler).IsAssignableFrom(parameter.ParameterType) ||
            parameter.ParameterType == typeof(KubernetesPlatformPolicyInventoryReader) ||
            parameter.ParameterType == typeof(ICiliumPolicyResourceStore));
    }

    [Fact]
    public void ProofFactoryRejectsTheSameNormalizedTokenFile()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=test;Username=test");
        var token = Path.Combine(Directory.GetCurrentDirectory(), "proof-token");
        var equivalent = Path.Combine(Directory.GetCurrentDirectory(), "other", "..", "proof-token");
        if (OperatingSystem.IsWindows())
            equivalent = equivalent.ToUpperInvariant();
        var error = Assert.Throws<PlatformPolicyProofException>(() =>
            PlatformPolicyProofFactory.Create(
                "https://kubernetes.test/", [], token, equivalent, dataSource,
                new CiliumEgressProviderOptions("agentweaver", new Version(1, 0), 1, "test",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty),
                new CiliumPlatformEgressOptions("agentweaver", true, []), TimeProvider.System));
        Assert.Equal("policy_fence_unavailable", error.Code);
    }

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

    [Theory]
    [InlineData("NetworkPolicy", "k8s-partial", "agentweaver", """{"podSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost","agentweaver.dev/sandbox-fence":"99"}},"policyTypes":["Egress"],"egress":[]}""")]
    [InlineData("NetworkPolicy", "k8s-partial-expr", "agentweaver", """{"podSelector":{"matchExpressions":[{"key":"tier","operator":"In","values":["canary"]}]},"policyTypes":["Egress"],"egress":[]}""")]
    [InlineData("NetworkPolicy", "k8s-ns-label", "agentweaver", """{"podSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost","io.kubernetes.pod.namespace":"agentweaver"}},"policyTypes":["Egress"],"egress":[]}""")]
    [InlineData("CiliumNetworkPolicy", "cilium-partial", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost","agentweaver.dev/sandbox-fence":"99"}},"egress":[{"toEndpoints":[{"matchLabels":{"k8s:io.kubernetes.pod.namespace":"kube-system","k8s:k8s-app":"kube-dns"}}],"toPorts":[{"ports":[{"port":"53","protocol":"UDP"}]}]}]}""")]
    [InlineData("CiliumNetworkPolicy", "cilium-explicit-no-deny", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"enableDefaultDeny":{"egress":false},"egress":[{"toEndpoints":[{"matchLabels":{"k8s:io.kubernetes.pod.namespace":"kube-system","k8s:k8s-app":"kube-dns"}}],"toPorts":[{"ports":[{"port":"53","protocol":"UDP"}]}]}]}""")]
    [InlineData("CiliumClusterwideNetworkPolicy", "ccnp-partial", "", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost","agentweaver.dev/sandbox-fence":"99"}},"enableDefaultDeny":{"egress":true}}""")]
    public void EvaluatorDoesNotCountPartialSelectorsAsFullPopulationDefaultDeny(
        string kind, string name, string @namespace, string specJson)
    {
        var policy = Policy(kind, name, @namespace, specJson);
        var error = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory([policy]), Ceiling()));
        Assert.Equal("policy_set_exceeds_system", error.Code);
    }

    [Fact]
    public void EvaluatorSupportsPrefixedCiliumWorkloadKeyAndValidatesDisjointSelectors()
    {
        var prefixedFull = Policy(
            "CiliumNetworkPolicy",
            "prefixed-full",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"k8s:agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"kube-system",
                "k8s:k8s-app":"kube-dns"}}],
                "toPorts":[{"ports":[{"port":"53","protocol":"UDP"}]}]}]
            }
            """);
        var disjointPrefixed = Policy(
            "CiliumNetworkPolicy",
            "disjoint-prefixed",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"k8s:agentweaver.io/workload":"build-worker"}},
              "egress":[{"toEntities":["world"]}]
            }
            """);
        var conflictingPrefixed = Policy(
            "CiliumNetworkPolicy",
            "conflicting-prefixed",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost","k8s:agentweaver.io/workload":"other"}},
              "egress":[{"toEntities":["world"]}]
            }
            """);
        Assert.True(SystemOnlyPolicySetEvaluator.Evaluate(
            Inventory([prefixedFull, disjointPrefixed, conflictingPrefixed]), Ceiling()).IsSafe);

        var disjointWithBadExpr = SafePolicies().Add(Policy(
            "CiliumNetworkPolicy",
            "disjoint-invalid-later-expr",
            "agentweaver",
            """
            {
              "endpointSelector":{
                "matchLabels":{"agentweaver.io/workload":"build-worker"},
                "matchExpressions":[{"key":"tier","operator":"Gt","values":["1"]}]
              },
              "egress":[]
            }
            """));
        var exprError = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(disjointWithBadExpr), Ceiling()));
        Assert.Equal("policy_semantics_unsupported", exprError.Code);
    }

    [Fact]
    public void EvaluatorRequiresFullOrIdenticalSourceForDenySubtraction()
    {
        var wideAllow = Policy(
            "CiliumNetworkPolicy",
            "wide-allow",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver",
                "k8s:agentweaver.io/workload":"unapproved-backend"}}],
                "toPorts":[{"ports":[{"port":"8443","protocol":"TCP"}]}]}]
            }
            """);
        var narrowDeny = Policy(
            "CiliumNetworkPolicy",
            "narrow-deny",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost","agentweaver.dev/sandbox-fence":"99"}},
              "egressDeny":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver",
                "k8s:agentweaver.io/workload":"unapproved-backend"}}],
                "toPorts":[{"ports":[{"port":"8443","protocol":"TCP"}]}]}]
            }
            """);

        var mismatch = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(SafePolicies().Add(wideAllow).Add(narrowDeny)), Ceiling()));
        Assert.Equal("policy_set_exceeds_system", mismatch.Code);

        var narrowAllow = Policy(
            "CiliumNetworkPolicy",
            "narrow-allow",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.dev/sandbox-fence":"99","agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver",
                "k8s:agentweaver.io/workload":"unapproved-backend"}}],
                "toPorts":[{"ports":[{"port":"8443","protocol":"TCP"}]}]}]
            }
            """);
        Assert.True(SystemOnlyPolicySetEvaluator.Evaluate(
            Inventory(SafePolicies().Add(narrowAllow).Add(narrowDeny)), Ceiling()).IsSafe);

        var fullDeny = Policy(
            "CiliumClusterwideNetworkPolicy",
            "full-deny",
            "",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egressDeny":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver",
                "k8s:agentweaver.io/workload":"unapproved-backend"}}],
                "toPorts":[{"ports":[{"port":"8443","protocol":"TCP"}]}]}]
            }
            """);
        Assert.True(SystemOnlyPolicySetEvaluator.Evaluate(
            Inventory(SafePolicies().Add(wideAllow).Add(fullDeny)), Ceiling()).IsSafe);
    }

    [Fact]
    public void EvaluatorAcceptsExactSharedIdentityBrokerGatewayAndRejectsVariants()
    {
        var validGateway = Policy(
            "CiliumNetworkPolicy",
            "agenthost-gateway-egress",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver-gateway",
                "k8s:gateway.networking.k8s.io/gateway-name":"identity-broker"}}],
                "toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}]
            }
            """);
        Assert.True(SystemOnlyPolicySetEvaluator.Evaluate(
            Inventory(SafePolicies().Add(validGateway)), Ceiling()).IsSafe);

        var wrongPort = Policy(
            "CiliumNetworkPolicy",
            "agenthost-gateway-wrong-port",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver-gateway",
                "k8s:gateway.networking.k8s.io/gateway-name":"identity-broker"}}],
                "toPorts":[{"ports":[{"port":"8443","protocol":"TCP"}]}]}]
            }
            """);
        var portError = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(SafePolicies().Add(wrongPort)), Ceiling()));
        Assert.Equal("policy_set_exceeds_system", portError.Code);

        var wrongNamespace = Policy(
            "CiliumNetworkPolicy",
            "agenthost-gateway-wrong-ns",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"other-gateway",
                "k8s:gateway.networking.k8s.io/gateway-name":"identity-broker"}}],
                "toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}]
            }
            """);
        var nsError = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(SafePolicies().Add(wrongNamespace)), Ceiling()));
        Assert.Equal("policy_set_exceeds_system", nsError.Code);

        var wrongGatewayName = Policy(
            "CiliumNetworkPolicy",
            "agenthost-gateway-wrong-name",
            "agentweaver",
            """
            {
              "endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},
              "egress":[{"toEndpoints":[{"matchLabels":{
                "k8s:io.kubernetes.pod.namespace":"agentweaver-gateway",
                "k8s:gateway.networking.k8s.io/gateway-name":"other-gateway"}}],
                "toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}]
            }
            """);
        var nameError = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(SafePolicies().Add(wrongGatewayName)), Ceiling()));
        Assert.Equal("policy_set_exceeds_system", nameError.Code);
    }

    [Theory]
    [InlineData("CiliumNetworkPolicy", "bad-enable-deny-type", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"enableDefaultDeny":true}""")]
    [InlineData("CiliumNetworkPolicy", "bad-enable-deny-key", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"enableDefaultDeny":{"egress":true,"extra":false}}""")]
    [InlineData("CiliumNetworkPolicy", "bad-enable-deny-bool", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"enableDefaultDeny":{"egress":"yes"}}""")]
    [InlineData("CiliumNetworkPolicy", "extra-dest-labels", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"egress":[{"toEndpoints":[{"matchLabels":{"k8s:io.kubernetes.pod.namespace":"agentweaver-gateway","k8s:gateway.networking.k8s.io/gateway-name":"identity-broker","extra":"bad"}}],"toPorts":[{"ports":[{"port":"443","protocol":"TCP"}]}]}]}""")]
    [InlineData("CiliumNetworkPolicy", "deny-dest-match-expr", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"egressDeny":[{"toEndpoints":[{"matchExpressions":[{"key":"k8s:k8s-app","operator":"In","values":["kube-dns"]}]}],"toPorts":[{"ports":[{"port":"53","protocol":"UDP"}]}]}]}""")]
    [InlineData("CiliumNetworkPolicy", "deny-l7-rules", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"egressDeny":[{"toEndpoints":[{"matchLabels":{"k8s:io.kubernetes.pod.namespace":"kube-system","k8s:k8s-app":"kube-dns"}}],"toPorts":[{"ports":[{"port":"53","protocol":"UDP"}],"rules":{"dns":[{"matchName":"api.example.com"}]}}]}]}""")]
    [InlineData("CiliumNetworkPolicy", "bad-port", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"egress":[{"toFQDNs":[{"matchName":"api.example.com"}],"toPorts":[{"ports":[{"port":"https","protocol":"TCP"}]}]}]}""")]
    [InlineData("CiliumNetworkPolicy", "bad-match-expr", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"},"matchExpressions":[{"key":"tier","operator":"Gt","values":["1"]}]},"egress":[]}""")]
    [InlineData("CiliumNetworkPolicy", "wrong-policy-ns", "other-ns", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"egress":[]}""")]
    [InlineData("CiliumClusterwideNetworkPolicy", "ccnp-with-ns", "agentweaver", """{"endpointSelector":{"matchLabels":{"agentweaver.io/workload":"agenthost"}},"egress":[]}""")]
    public void EvaluatorFailsClosedOnMalformedAndNamespaceMismatchedInputs(
        string kind, string name, string @namespace, string specJson)
    {
        var policy = Policy(kind, name, @namespace, specJson);
        var error = Assert.Throws<PlatformPolicyProofException>(() =>
            SystemOnlyPolicySetEvaluator.Evaluate(Inventory(SafePolicies().Add(policy)), Ceiling()));
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
