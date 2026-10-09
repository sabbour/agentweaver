using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class AgentApplicationManifestValidatorTests
{
    private const string ContentDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DependencyDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Accepts_valid_manifest_and_returns_raw_identity()
    {
        var configuration = Serialize(CreateManifest());
        var result = Validate(configuration);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(configuration)).ToLowerInvariant(),
            result.ConfigurationSha256);
        Assert.Empty(result.DependencyLock);
    }

    [Fact]
    public void Raw_identity_changes_when_configuration_bytes_change()
    {
        var compact = Serialize(CreateManifest());
        var indented = JsonSerializer.SerializeToUtf8Bytes(
            CreateManifest(), new JsonSerializerOptions { WriteIndented = true });

        Assert.True(Validate(compact).IsValid);
        Assert.True(Validate(indented).IsValid);
        Assert.NotEqual(Validate(compact).ConfigurationSha256, Validate(indented).ConfigurationSha256);
    }

    [Fact]
    public void Rejects_duplicate_json_properties_and_invalid_utf8()
    {
        var duplicate = Encoding.UTF8.GetString(Serialize(CreateManifest())).Replace(
            "\"schemaVersion\":\"agentweaver.application/1\"",
            "\"schemaVersion\":\"agentweaver.application/1\",\"schemaVersion\":\"agentweaver.application/1\"",
            StringComparison.Ordinal);

        AssertIssue(Validate(Encoding.UTF8.GetBytes(duplicate)), "duplicate_json_property");
        AssertIssue(Validate([0xff]), "invalid_json");
    }

    [Fact]
    public void Enforces_configuration_and_content_limits()
    {
        AssertIssue(
            Validate(new byte[1_048_577]),
            "configuration_too_large");

        var oversizedFile = CreateManifest();
        ((JsonObject)((JsonArray)oversizedFile["files"]!)[0]!)["size"] = 1_048_577;
        AssertIssue(Validate(Serialize(oversizedFile)), "file_too_large");

        var result = Validate(Serialize(CreateManifest()), compressedLayerBytes: 16_777_217);
        AssertIssue(result, "compressed_layer_too_large");
    }

    [Fact]
    public void Requires_exact_inventory_and_rejects_unsafe_or_colliding_paths()
    {
        AssertIssue(
            Validate(Serialize(CreateManifest()), [new("agents/analyst.json", 7, DependencyDigest)]),
            "inventory_digest_mismatch");

        var traversal = CreateManifest();
        ((JsonObject)((JsonArray)traversal["files"]!)[0]!)["path"] = "../agents/analyst.json";
        AssertIssue(Validate(Serialize(traversal)), "invalid_package_path");

        var collision = CreateManifest();
        ((JsonArray)collision["files"]!).Add(new JsonObject
        {
            ["kind"] = "agent",
            ["id"] = "analyst",
            ["schemaVersion"] = 1,
            ["path"] = "agents/ANALYST.json",
            ["mediaType"] = "application/json",
            ["size"] = 7,
            ["digest"] = ContentDigest,
        });
        AssertIssue(
            Validate(Serialize(collision),
                [
                    new("agents/analyst.json", 7, ContentDigest),
                    new("agents/ANALYST.json", 7, ContentDigest),
                ]),
            "colliding_package_path");
    }

    [Fact]
    public void Rejects_inventory_over_documented_file_count()
    {
        var manifest = CreateManifest();
        var files = (JsonArray)manifest["files"]!;
        var inventory = ImmutableArray.CreateBuilder<AgentApplicationInventoryEntry>();
        inventory.Add(new("agents/analyst.json", 7, ContentDigest));
        for (var index = 0; index < 256; index++)
        {
            var path = $"agents/support-{index}.json";
            files.Add(new JsonObject
            {
                ["kind"] = "agent",
                ["id"] = "analyst",
                ["schemaVersion"] = 1,
                ["path"] = path,
                ["mediaType"] = "application/json",
                ["size"] = 1,
                ["digest"] = ContentDigest,
            });
            inventory.Add(new AgentApplicationInventoryEntry(path, 1, ContentDigest));
        }

        AssertIssue(Validate(Serialize(manifest), inventory.ToImmutable()), "inventory_too_large");
    }

    [Fact]
    public void Rejects_unsupported_host_schema_and_application_version()
    {
        var unsupportedSchema = CreateManifest();
        ((JsonObject)((JsonObject)unsupportedSchema["host"]!)["componentSchemas"]!)["agentCharter"] = 2;
        AssertIssue(Validate(Serialize(unsupportedSchema)), "unsupported_component_schema");

        var invalidVersion = CreateManifest();
        invalidVersion["version"] = "01.0.0";
        AssertIssue(Validate(Serialize(invalidVersion)), "invalid_app_version");
    }

    [Fact]
    public void Resolves_existing_provider_cardinalities_without_pinning()
    {
        var manifest = CreateManifest();
        var requirements = (JsonArray)manifest["providerRequirements"]!;
        requirements.Add(ProviderRequirement(
            "Guardrails", ["guardrails.audit"], requiredL3L4: [], requiredL7: []));
        requirements.Add(ProviderRequirement(
            "NetworkPolicy", [], requiredL3L4: ["networkpolicy.l3l4"], requiredL7: []));
        requirements.Add(ProviderRequirement(
            "Cost", ["cost.usage.price"], requiredL3L4: [], requiredL7: [],
            meterSource: "copilot.tokens"));

        var result = Validate(Serialize(manifest));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        Assert.Empty(result.DependencyLock);
    }

    [Fact]
    public void Rejects_unsatisfied_provider_capability_and_unsupported_application_hosting()
    {
        var unsupportedCapability = CreateManifest();
        ((JsonArray)unsupportedCapability["providerRequirements"]!)[0] =
            ProviderRequirement("Sandbox", ["container.execute"]);
        AssertIssue(Validate(Serialize(unsupportedCapability)), "provider_incompatible");

        var applicationHosting = CreateManifest();
        ((JsonArray)applicationHosting["providerRequirements"]!)[0] =
            ProviderRequirement("ApplicationHosting", []);
        AssertIssue(Validate(Serialize(applicationHosting)), "unsupported_provider_cardinality");
    }

    [Fact]
    public void Rejects_canvas_requirements_without_claiming_a_canvas_runtime()
    {
        var manifest = CreateManifest();
        manifest["canvasRequirements"] = new JsonArray(new JsonObject
        {
            ["profile"] = "native",
            ["contractVersion"] = 1,
            ["requiredAdapterVersion"] = "1.0.0",
            ["requiredOptionsSchemaVersion"] = 1,
            ["requiredCapabilities"] = Strings("actions.typed"),
        });

        AssertIssue(Validate(Serialize(manifest)), "unsupported_canvas_requirement");
    }

    [Fact]
    public void Resolves_dependency_lock_and_rejects_out_of_range_versions_or_digests()
    {
        var manifest = CreateManifest();
        ((JsonArray)manifest["dependencies"]!).Add(Dependency(
            "org.example.library", "1.0.0", "2.0.0", DependencyDigest));
        var graph = ImmutableArray.Create(
            new AgentApplicationDependencyNode(
                "org.example.library", "1.5.0", DependencyDigest, []));

        var valid = Validate(Serialize(manifest), dependencyGraph: graph);
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Issues));
        Assert.Collection(valid.DependencyLock, item =>
        {
            Assert.Equal("org.example.library", item.AppId);
            Assert.Equal("1.5.0", item.Version);
            Assert.Equal(DependencyDigest, item.ManifestDigest);
            Assert.Equal("reusableContent", item.Kind);
        });

        var wrongVersion = Validate(
            Serialize(manifest),
            dependencyGraph:
            [
                new("org.example.library", "2.0.0", DependencyDigest, []),
            ]);
        AssertIssue(wrongVersion, "dependency_version_conflict");

        var wrongDigest = Validate(
            Serialize(manifest),
            dependencyGraph:
            [
                new("org.example.library", "1.5.0", ContentDigest, []),
            ]);
        AssertIssue(wrongDigest, "dependency_digest_conflict");
    }

    [Fact]
    public void Rejects_dependency_cycles_and_conflicting_constraints()
    {
        var manifest = CreateManifest();
        ((JsonArray)manifest["dependencies"]!).Add(Dependency(
            "org.example.library", "1.0.0", "2.0.0"));
        var cycle = Validate(
            Serialize(manifest),
            dependencyGraph:
            [
                new(
                    "org.example.library",
                    "1.5.0",
                    DependencyDigest,
                    [new("org.example.assistant", "1.0.0", "2.0.0", null, "reusableContent")]),
            ]);
        AssertIssue(cycle, "dependency_cycle");

        var conflict = CreateManifest();
        ((JsonArray)conflict["dependencies"]!).Add(Dependency(
            "org.example.first", "1.0.0", "2.0.0"));
        ((JsonArray)conflict["dependencies"]!).Add(Dependency(
            "org.example.second", "1.0.0", "2.0.0"));
        var graph = ImmutableArray.Create(
            new AgentApplicationDependencyNode(
                "org.example.first", "1.5.0", ContentDigest,
                [new("org.example.shared", "1.0.0", "2.0.0", null, "reusableContent")]),
            new AgentApplicationDependencyNode(
                "org.example.second", "1.5.0", DependencyDigest,
                [new("org.example.shared", "2.0.0", "3.0.0", null, "reusableContent")]),
            new AgentApplicationDependencyNode("org.example.shared", "2.5.0", ContentDigest, []));

        AssertIssue(Validate(Serialize(conflict), dependencyGraph: graph), "dependency_version_conflict");
    }

    [Fact]
    public void Traverses_deep_dependency_graph_iteratively_without_a_graph_depth_cap()
    {
        const int nodeCount = 4_096;
        var manifest = CreateManifest();
        ((JsonArray)manifest["dependencies"]!).Add(Dependency(
            "org.example.node-00000", "0.0.0", "2.0.0"));
        var graph = ImmutableArray.CreateBuilder<AgentApplicationDependencyNode>(nodeCount);
        for (var index = 0; index < nodeCount; index++)
        {
            var appId = $"org.example.node-{index:D5}";
            var child = index + 1 < nodeCount
                ? ImmutableArray.Create(DependencyRequirement(
                    $"org.example.node-{index + 1:D5}", "0.0.0", "2.0.0"))
                : ImmutableArray<AgentApplicationDependencyRequirement>.Empty;
            graph.Add(new AgentApplicationDependencyNode(appId, "1.0.0", DependencyDigest, child));
        }

        var result = Validate(Serialize(manifest), dependencyGraph: graph.ToImmutable());

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(nodeCount, result.DependencyLock.Length);
    }

    private static AgentApplicationManifestValidationResult Validate(
        byte[]? configuration = null,
        ImmutableArray<AgentApplicationInventoryEntry>? inventory = null,
        long compressedLayerBytes = 128,
        ImmutableArray<AgentApplicationDependencyNode>? dependencyGraph = null,
        AgentApplicationHostCompatibility? hostCompatibility = null,
        ProviderCatalog? providerCatalog = null) =>
        AgentApplicationManifestValidator.Validate(
            configuration ?? Serialize(CreateManifest()),
            inventory ?? [new("agents/analyst.json", 7, ContentDigest)],
            compressedLayerBytes,
            dependencyGraph ?? [],
            providerCatalog ?? CreateProviderCatalog(),
            hostCompatibility ?? CreateHostCompatibility());

    private static void AssertIssue(AgentApplicationManifestValidationResult result, string code) =>
        Assert.Contains(result.Issues, issue => issue.Code == code);

    private static JsonObject CreateManifest() => new()
    {
        ["schemaVersion"] = "agentweaver.application/1",
        ["appId"] = "org.example.assistant",
        ["version"] = "1.0.0",
        ["host"] = new JsonObject
        {
            ["contract"] = "agentweaver.agent-app-host/1",
            ["minimumVersion"] = "1.0.0",
            ["maximumVersionExclusive"] = "2.0.0",
            ["componentSchemas"] = new JsonObject { ["agentCharter"] = 1 },
        },
        ["entryPoints"] = new JsonArray(new JsonObject
        {
            ["id"] = "start",
            ["kind"] = "agent",
            ["componentId"] = "analyst",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
            },
        }),
        ["components"] = new JsonObject
        {
            ["agents"] = Strings("analyst"),
            ["tools"] = Strings(),
            ["skills"] = Strings(),
            ["policies"] = Strings(),
            ["canvases"] = Strings(),
            ["workflows"] = Strings(),
            ["assets"] = Strings(),
        },
        ["files"] = new JsonArray(new JsonObject
        {
            ["kind"] = "agent",
            ["id"] = "analyst",
            ["schemaVersion"] = 1,
            ["path"] = "agents/analyst.json",
            ["mediaType"] = "application/json",
            ["size"] = 7,
            ["digest"] = ContentDigest,
        }),
        ["dependencies"] = new JsonArray(),
        ["providerRequirements"] = new JsonArray(
            ProviderRequirement("Sandbox", ["container.create"])),
    };

    private static JsonObject ProviderRequirement(
        string seam,
        string[] capabilities,
        string[]? requiredL3L4 = null,
        string[]? requiredL7 = null,
        string? meterSource = null)
    {
        var requirement = new JsonObject
        {
            ["seam"] = seam,
            ["requiredAdapterVersion"] = "1.0.0",
            ["requiredOptionsSchemaVersion"] = 1,
            ["requiredCapabilities"] = Strings(capabilities),
        };
        if (requiredL3L4 is not null)
            requirement["requiredL3L4Capabilities"] = Strings(requiredL3L4);
        if (requiredL7 is not null)
            requirement["requiredL7Capabilities"] = Strings(requiredL7);
        if (meterSource is not null)
            requirement["meterSource"] = meterSource;
        return requirement;
    }

    private static JsonObject Dependency(
        string appId,
        string minimum,
        string maximumExclusive,
        string? manifestDigest = null,
        string kind = "reusableContent")
    {
        var result = new JsonObject
        {
            ["appId"] = appId,
            ["minimumVersion"] = minimum,
            ["maximumVersionExclusive"] = maximumExclusive,
            ["kind"] = kind,
        };
        if (manifestDigest is not null)
            result["manifestDigest"] = manifestDigest;
        return result;
    }

    private static AgentApplicationDependencyRequirement DependencyRequirement(
        string appId,
        string minimum,
        string maximumExclusive,
        string? manifestDigest = null,
        string kind = "reusableContent") =>
        new(appId, minimum, maximumExclusive, manifestDigest, kind);

    private static JsonArray Strings(params string[] values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static byte[] Serialize(JsonObject manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest);

    private static AgentApplicationHostCompatibility CreateHostCompatibility() =>
        new(
            "agentweaver.agent-app-host/1",
            "1.2.0",
            ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("agentCharter", 1),
            AllowPrerelease: false);

    private static ProviderCatalog CreateProviderCatalog()
    {
        static ProviderRegistration Registration(
            ProviderSeam seam,
            string id,
            string[] capabilities) =>
            new(
                new ProviderDescriptor(
                    seam,
                    id,
                    new Version(1, 0, 0),
                    1,
                    ProviderHostingPattern.InProcess,
                    capabilities.ToImmutableHashSet(StringComparer.Ordinal)),
                Enabled: true,
                OptionsRevision: "options-v1",
                OptionsSchemaVersion: 1);

        var catalog = ProviderCatalog.Create(
            [
                Registration(ProviderSeam.Sandbox, "sandbox", ["container.create"]),
                Registration(ProviderSeam.NetworkPolicy, "network-policy", ["networkpolicy.l3l4"]),
                Registration(ProviderSeam.Guardrails, "guardrails", ["guardrails.audit"]),
                Registration(ProviderSeam.Cost, "cost", ["cost.usage.price"]),
            ],
            [new ProviderSelection(ProviderSeam.Sandbox, "sandbox")],
            [],
            orderedSelections:
            [
                new ProviderOrderedSelection(ProviderSeam.Guardrails, ["guardrails"]),
            ],
            layerSelections:
            [
                new ProviderLayerSelection(NetworkPolicyLayer.L3L4, "network-policy"),
            ],
            meterSourceSelections:
            [
                new ProviderMeterSourceSelection("copilot.tokens", "cost"),
            ]);
        return Assert.IsType<ProviderCatalog>(catalog.Value);
    }
}
