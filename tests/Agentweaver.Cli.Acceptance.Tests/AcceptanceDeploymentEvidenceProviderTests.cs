using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class AcceptanceDeploymentEvidenceProviderTests
{
    private const string SourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceTree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ProviderSource = "cccccccccccccccccccccccccccccccccccccccc";
    private const string Namespace = "agentweaver";
    private const string HostImageSetting = "Environment__Sandbox__AgentSandbox__ContainerImage";
    private const string HostPlatformSetting = "Environment__Sandbox__AgentSandbox__ContainerImagePlatform";
    private const string HostBytesSetting = "Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes";
    private static readonly (string Id, string Name)[] Services =
    [
        ("Agentweaver.Identity.Broker", "identity-broker"),
        ("Agentweaver.EventsAndSessions", "events-and-sessions"),
        ("Agentweaver.Gateway", "gateway"),
        ("Agentweaver.Mcp", "mcp"),
        ("Agentweaver.Web", "web"),
        ("Agentweaver.Orchestrator", "orchestrator"),
        ("Agentweaver.Environment", "environment"),
        ("Agentweaver.Knowledge", "knowledge"),
        ("Agentweaver.Projects.Config", "projects-config"),
    ];

    [Fact]
    public void ImageManifestAcceptsCanonicalDigestAndSourceCommitTagOnly()
    {
        RegistryFixtureSet registry = CreateRegistryFixtureSet();
        string hostRepository = Repository("Agentweaver.AgentHost");
        JsonObject valid = new()
        {
            ["images"] = new JsonObject
            {
                [hostRepository] = registry.ByName["agenthost"].Digest,
                [Repository("Agentweaver.Gateway")] = $"sha-{SourceSha}",
            },
        };

        Dictionary<string, string> selected =
            AcceptanceDeploymentEvidenceProvider.ValidateImageManifest(valid, SourceSha);

        Assert.Equal(2, selected.Count);
        Assert.Equal(registry.ByName["agenthost"].Digest, selected[hostRepository]);
        Assert.Throws<InvalidDataException>(() =>
            AcceptanceDeploymentEvidenceProvider.ValidateImageManifest(new JsonObject { ["images"] = new JsonObject() }, SourceSha));
        Assert.Throws<InvalidDataException>(() =>
            AcceptanceDeploymentEvidenceProvider.ValidateImageManifest(
                new JsonObject { ["images"] = new JsonObject { ["unknown"] = registry.ByName["agenthost"].Digest } },
                SourceSha));
        Assert.Throws<InvalidDataException>(() =>
            AcceptanceDeploymentEvidenceProvider.ValidateImageManifest(
                new JsonObject { ["images"] = new JsonObject { [hostRepository] = $"sha-{new string('d', 40)}" } },
                SourceSha));
        Assert.Throws<InvalidDataException>(() =>
            AcceptanceDeploymentEvidenceProvider.ValidateImageManifest(
                new JsonObject { ["images"] = new JsonObject { [hostRepository] = "ghcr.io/sabbour/agentweaver-agenthost:latest" } },
                SourceSha));
    }

    [Fact]
    public async Task KubernetesReadbackAcceptsSparseSourceTagSelectionAndMeasuresAgentHostFromGhcr()
    {
        RegistryFixtureSet registry = CreateRegistryFixtureSet();
        JsonObject cluster = CreateCluster(registry);
        using var http = new HttpClient(new RegistryHandler(registry));
        var provider = CreateProvider(http, cluster);
        Dictionary<string, string> sparseSelection = new(StringComparer.Ordinal)
        {
            [Repository("Agentweaver.AgentHost")] = $"sha-{SourceSha}",
        };

        JsonObject evidence = await provider.ObserveServicesEquivalentAsync(
            CreateManifest(), SourceSha, SourceTree, sparseSelection, default);

        Assert.Equal("passed", evidence["status"]?.GetValue<string>());
        Assert.Equal(Services.Length, evidence["services"]?.AsArray().Count);
        Assert.Equal("passed", evidence["agentHostImageMeasurement"]?["status"]?.GetValue<string>());
        Assert.Equal(registry.ByName["agenthost"].CompressedPullBytes,
            evidence["agentHostImageMeasurement"]?["compressedPullBytes"]?.GetValue<long>());
        Assert.Equal("linux/amd64", evidence["agentHostImageMeasurement"]?["platform"]?.GetValue<string>());
        Assert.Equal("blocked", evidence["agentHostRuntime"]?["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task PrepareAsyncConsumesPinnedSourceAndCanonicalImageManifestThroughProductionProvider()
    {
        string workspace = NewWorkspace();
        try
        {
            RegistryFixtureSet registry = CreateRegistryFixtureSet();
            JsonObject foundation = CreateFoundation(out Dictionary<string, byte[]> sourceFiles);
            sourceFiles["releases/foundation.json"] = Encoding.UTF8.GetBytes(foundation.ToJsonString());
            byte[] imageManifestBytes = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["images"] = new JsonObject
                {
                    [Repository("Agentweaver.AgentHost")] = $"sha-{SourceSha}",
                },
            }.ToJsonString());
            string foundationPath = Path.Combine(workspace, "foundation.json");
            string imageManifestPath = Path.Combine(workspace, "images.json");
            await File.WriteAllTextAsync(foundationPath, foundation.ToJsonString());
            await File.WriteAllBytesAsync(imageManifestPath, imageManifestBytes);

            JsonObject pins = CreateManifest().Pins;
            pins["sourceReceipt"] = new JsonObject { ["sourceSha"] = SourceSha, ["sourceTree"] = SourceTree };
            JsonObject root = new()
            {
                ["deploymentEvidence"] = new JsonObject
                {
                    ["manifestPath"] = "foundation.json",
                    ["imageManifestPath"] = "images.json",
                },
            };
            AcceptanceManifest input = new(
                root, new JsonObject(), pins, new JsonObject(), "message",
                Path.Combine(workspace, "input.json"), workspace);
            JsonObject cluster = CreateCluster(registry);
            var handler = new PinnedSourceHandler(new RegistryHandler(registry), foundation, sourceFiles);
            using var http = new HttpClient(handler);
            var provider = CreateProvider(http, cluster);

            JsonObject evidence = await provider.PrepareAsync(input, default);

            Assert.Equal(SourceSha, evidence["source"]?["sourceSha"]?.GetValue<string>());
            Assert.Equal(SourceTree, evidence["source"]?["sourceTree"]?.GetValue<string>());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(imageManifestBytes)),
                evidence["imageManifest"]?["sha256"]?.GetValue<string>());
            Assert.Equal("passed", evidence["services"]?["status"]?.GetValue<string>());
            Assert.Equal("passed", evidence["agentHostImageMeasurement"]?["status"]?.GetValue<string>());
            Assert.Contains("apps/web/package-lock.json", handler.ContentPaths);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task KubernetesReadbackRejectsSelectedDigestMismatchAndAbsentHostMeasurement()
    {
        RegistryFixtureSet registry = CreateRegistryFixtureSet();
        JsonObject cluster = CreateCluster(registry);
        using var http = new HttpClient(new RegistryHandler(registry));
        var provider = CreateProvider(http, cluster);
        Dictionary<string, string> mismatchedSelection = new(StringComparer.Ordinal)
        {
            [Repository("Agentweaver.Gateway")] = $"sha256:{new string('f', 64)}",
        };

        InvalidDataException mismatch = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ObserveServicesEquivalentAsync(
                CreateManifest(), SourceSha, SourceTree, mismatchedSelection, default));
        Assert.Contains("does not match the selected immutable GHCR reference", mismatch.Message, StringComparison.Ordinal);

        JsonObject container = FindResource(cluster, "Deployment", "environment")
            ["spec"]!["template"]!["spec"]!["containers"]![0]!.AsObject();
        JsonArray environment = container["env"]!.AsArray();
        int measurementIndex = environment.IndexOf(environment.OfType<JsonObject>()
            .Single(variable => variable["name"]?.GetValue<string>() == HostBytesSetting));
        environment.RemoveAt(measurementIndex);
        InvalidDataException missingMeasurement = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ObserveServicesEquivalentAsync(
                CreateManifest(), SourceSha, SourceTree, registry.SelectedImages, default));
        Assert.Contains(HostBytesSetting, missingMeasurement.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KubernetesReadbackRejectsAgentHostMeasurementThatDiffersFromVerifiedOciDescriptors()
    {
        RegistryFixtureSet registry = CreateRegistryFixtureSet();
        JsonObject cluster = CreateCluster(registry);
        JsonArray environment = FindResource(cluster, "Deployment", "environment")
            ["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]!.AsArray();
        JsonObject bytes = environment.OfType<JsonObject>()
            .Single(variable => variable["name"]?.GetValue<string>() == HostBytesSetting);
        bytes["value"] = "0";
        using var http = new HttpClient(new RegistryHandler(registry));
        var provider = CreateProvider(http, cluster);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ObserveServicesEquivalentAsync(
                CreateManifest(), SourceSha, SourceTree, registry.SelectedImages, default));

        Assert.Contains("positive compressed-byte measurement", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryReadsRetryPullTokenAndNeverForwardBearerToBlobRedirect()
    {
        RegistryFixtureSet fixtures = CreateRegistryFixtureSet();
        RegistryFixture gateway = fixtures.ByName["gateway"];
        var delays = new List<TimeSpan>();
        var handler = new RegistryHandler(fixtures, tokenFailures: 1, redirectManifests: true);
        using var http = new HttpClient(handler);
        var provider = new AcceptanceDeploymentEvidenceProvider(
            http,
            (_, _, _) => throw new InvalidOperationException("No cluster read is expected."),
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        JsonObject result = await provider.VerifyRegistryImageAsync(
            gateway.Image, gateway.Image, SourceSha, SourceTree, default);

        Assert.Equal(gateway.Digest, result["manifestDigest"]?.GetValue<string>());
        Assert.Equal(gateway.ConfigDigest, result["configDigest"]?.GetValue<string>());
        Assert.Equal(gateway.CompressedPullBytes, result["compressedPullBytes"]?.GetValue<long>());
        JsonObject containerdResult = await provider.VerifyRegistryImageAsync(
            gateway.Image, $"containerd://{gateway.Digest}", SourceSha, SourceTree, default);
        Assert.Equal(gateway.Digest, containerdResult["platformDigest"]?.GetValue<string>());
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.Equal(3, handler.Requests.Count(request => request.Uri.AbsolutePath == "/token"));
        Assert.All(handler.Requests.Where(request => request.Uri.Host == "pkg-containers.githubusercontent.com"),
            request => Assert.Null(request.Authorization));
        Assert.All(handler.Requests.Where(request => request.Uri.Host == "ghcr.io" && request.Uri.AbsolutePath != "/token"),
            request => Assert.Equal("Bearer controlled-pull-token", request.Authorization));
        Assert.Contains(handler.Requests, request =>
            request.Uri.Query.Contains("repository%3Asabbour%2Fagentweaver.gateway%3Apull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegistryRejectsUnallowlistedRedirectBeforeSendingCredentials()
    {
        RegistryFixtureSet fixtures = CreateRegistryFixtureSet();
        RegistryFixture gateway = fixtures.ByName["gateway"];
        var handler = new RegistryHandler(fixtures, redirectToUntrustedHost: true);
        using var http = new HttpClient(handler);
        var provider = new AcceptanceDeploymentEvidenceProvider(
            http,
            (_, _, _) => throw new InvalidOperationException("No cluster read is expected."),
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.VerifyRegistryImageAsync(
            gateway.Image, gateway.Image, SourceSha, SourceTree, default));
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "attacker.example");
    }

    [Fact]
    public async Task RegistryRejectsManifestBytesThatDoNotMatchTheSelectedDigest()
    {
        RegistryFixtureSet fixtures = CreateRegistryFixtureSet();
        RegistryFixture gateway = fixtures.ByName["gateway"];
        string manifestPath = $"/v2/sabbour/{Repository("Agentweaver.Gateway")}/manifests/{gateway.Digest}";
        fixtures.Bodies[manifestPath] = Encoding.UTF8.GetBytes("{}");
        using var http = new HttpClient(new RegistryHandler(fixtures));
        var provider = new AcceptanceDeploymentEvidenceProvider(
            http,
            (_, _, _) => throw new InvalidOperationException("No cluster read is expected."),
            (_, _) => Task.CompletedTask);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.VerifyRegistryImageAsync(gateway.Image, gateway.Image, SourceSha, SourceTree, default));

        Assert.Contains("immutable image descriptor", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryRequiresExactSourceAndConfigAndRejectsBadDescriptorSizes()
    {
        RegistryFixtureSet wrongSource = new();
        wrongSource.Add("Agentweaver.Gateway", "gateway", sourceSha: new string('d', 40));
        RegistryFixture gateway = wrongSource.ByName["gateway"];
        using (var http = new HttpClient(new RegistryHandler(wrongSource)))
        {
            var provider = new AcceptanceDeploymentEvidenceProvider(
                http,
                (_, _, _) => throw new InvalidOperationException(),
                (_, _) => Task.CompletedTask);
            InvalidDataException sourceFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.VerifyRegistryImageAsync(gateway.Image, gateway.Image, SourceSha, SourceTree, default));
            Assert.Contains("source commit and tree", sourceFailure.Message, StringComparison.Ordinal);
        }

        RegistryFixtureSet badConfigSize = new();
        badConfigSize.Add("Agentweaver.Gateway", "gateway", configDescriptorSizeDelta: 1);
        gateway = badConfigSize.ByName["gateway"];
        using (var http = new HttpClient(new RegistryHandler(badConfigSize)))
        {
            var provider = new AcceptanceDeploymentEvidenceProvider(
                http,
                (_, _, _) => throw new InvalidOperationException(),
                (_, _) => Task.CompletedTask);
            InvalidDataException configFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.VerifyRegistryImageAsync(gateway.Image, gateway.Image, SourceSha, SourceTree, default));
            Assert.Contains("config size", configFailure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task KubernetesReadbackRequiresCurrentReadyRolloutAndRejectsSourceTreeClaimMismatch()
    {
        RegistryFixtureSet registry = CreateRegistryFixtureSet();
        JsonObject cluster = CreateCluster(registry);
        FindResource(cluster, "Deployment", "gateway")["status"]!["observedGeneration"] = 1;
        using var http = new HttpClient(new RegistryHandler(registry));
        var provider = CreateProvider(http, cluster);
        InvalidDataException rolloutFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ObserveServicesEquivalentAsync(
                CreateManifest(), SourceSha, SourceTree, registry.SelectedImages, default));
        Assert.Contains("fully available rollout", rolloutFailure.Message, StringComparison.Ordinal);

        RegistryFixtureSet badTree = new();
        foreach ((string id, string name) in Services)
            badTree.Add(id, name, sourceTree: name == "gateway" ? new string('d', 40) : SourceTree);
        badTree.Add("Agentweaver.AgentHost", "agenthost");
        JsonObject badTreeCluster = CreateCluster(badTree);
        using var badHttp = new HttpClient(new RegistryHandler(badTree));
        var badTreeProvider = CreateProvider(badHttp, badTreeCluster);
        InvalidDataException sourceFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            badTreeProvider.ObserveServicesEquivalentAsync(
                CreateManifest(), SourceSha, SourceTree, badTree.SelectedImages, default));
        Assert.Contains("source commit and tree", sourceFailure.Message, StringComparison.Ordinal);
    }

    private static AcceptanceDeploymentEvidenceProvider CreateProvider(HttpClient http, JsonObject cluster) =>
        new(http, (_, _, _) => Task.FromResult((JsonObject)cluster.DeepClone()), (_, _) => Task.CompletedTask);

    private static AcceptanceManifest CreateManifest() =>
        new(
            new JsonObject(),
            new JsonObject(),
            new JsonObject
            {
                ["context"] = "approved-aks-context",
                ["namespace"] = Namespace,
                ["providerReceipt"] = new JsonObject
                {
                    ["configMapName"] = "provider-catalog-v1",
                    ["sourceCommit"] = ProviderSource,
                    ["uid"] = "catalog-uid",
                    ["resourceVersion"] = "12",
                },
            },
            new JsonObject(),
            "message",
            "manifest.json",
            AppContext.BaseDirectory);

    private static RegistryFixtureSet CreateRegistryFixtureSet()
    {
        RegistryFixtureSet fixture = new();
        foreach ((string id, string name) in Services)
            fixture.Add(id, name);
        fixture.Add("Agentweaver.AgentHost", "agenthost");
        return fixture;
    }

    private static string NewWorkspace()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static JsonObject CreateFoundation(out Dictionary<string, byte[]> sourceFiles)
    {
        sourceFiles = new(StringComparer.Ordinal);
        JsonArray components = [];
        foreach ((string id, string name) in Services.Append(("Agentweaver.AgentHost", "agenthost")))
        {
            string project = id == "Agentweaver.Web"
                ? "apps/web/package.json"
                : $"services/{name}/{id}.csproj";
            components.Add(new JsonObject
            {
                ["id"] = id,
                ["kind"] = "service",
                ["version"] = "1.0.0",
                ["project"] = project,
            });
            sourceFiles[project] = id == "Agentweaver.Web"
                ? Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\"}")
                : Encoding.UTF8.GetBytes("<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");
        }
        sourceFiles["apps/web/package-lock.json"] = Encoding.UTF8.GetBytes(
            "{\"version\":\"1.0.0\",\"packages\":{\"\":{\"version\":\"1.0.0\"}}}");
        return new JsonObject
        {
            ["stage"] = "draft",
            ["components"] = components,
            ["compatibility"] = new JsonArray(),
        };
    }

    private static string Repository(string id) =>
        id switch
        {
            "Agentweaver.Web" => "agentweaver-web",
            "Agentweaver.AgentHost" => "agentweaver-agenthost",
            _ => id.ToLowerInvariant(),
        };

    private static JsonObject CreateCluster(RegistryFixtureSet registry)
    {
        JsonArray items =
        [
            new JsonObject
            {
                ["kind"] = "ConfigMap",
                ["metadata"] = new JsonObject
                {
                    ["name"] = "provider-catalog-v1",
                    ["namespace"] = Namespace,
                    ["uid"] = "catalog-uid",
                    ["resourceVersion"] = "12",
                },
            },
        ];
        foreach ((string _, string name) in Services)
        {
            RegistryFixture image = registry.ByName[name];
            string deploymentUid = $"deployment-{name}-uid";
            string replicaSetUid = $"replicaset-{name}-uid";
            JsonObject labels = new()
            {
                ["app.kubernetes.io/name"] = name,
                ["agentweaver.io/service"] = name,
                ["agentweaver.io/workload"] = name,
            };
            if (new[] { "identity-broker", "events-and-sessions", "environment", "knowledge", "orchestrator", "projects-config" }.Contains(name))
                labels["azure.workload.identity/use"] = "true";
            JsonObject templateMetadata = new() { ["labels"] = labels.DeepClone() };
            JsonObject container = new()
            {
                ["name"] = name,
                ["image"] = image.Image,
            };
            if (name == "environment")
            {
                RegistryFixture host = registry.ByName["agenthost"];
                container["env"] = new JsonArray(
                    EnvironmentVariable(HostImageSetting, host.Image),
                    EnvironmentVariable(HostPlatformSetting, "linux/amd64"),
                    EnvironmentVariable(HostBytesSetting, host.CompressedPullBytes.ToString(CultureInfo.InvariantCulture)));
            }
            JsonObject podContainer = (JsonObject)container.DeepClone();
            if (name == "projects-config")
            {
                templateMetadata["annotations"] = new JsonObject { ["agentweaver.io/provider-catalog-source"] = ProviderSource };
                JsonObject binding = new()
                {
                    ["prefix"] = "ProjectsConfig__ProviderCatalog__",
                    ["configMapRef"] = new JsonObject { ["name"] = "provider-catalog-v1", ["optional"] = false },
                };
                container["envFrom"] = new JsonArray(binding.DeepClone());
                podContainer["envFrom"] = new JsonArray(binding.DeepClone());
            }
            items.Add(new JsonObject
            {
                ["kind"] = "Service",
                ["metadata"] = new JsonObject
                {
                    ["name"] = name, ["namespace"] = Namespace, ["uid"] = $"service-{name}-uid",
                    ["resourceVersion"] = "8",
                    ["labels"] = new JsonObject
                    {
                        ["app.kubernetes.io/name"] = name,
                        ["agentweaver.io/service"] = name,
                    },
                },
            });
            items.Add(new JsonObject
            {
                ["kind"] = "Deployment",
                ["metadata"] = new JsonObject
                {
                    ["name"] = name, ["namespace"] = Namespace, ["uid"] = deploymentUid,
                    ["generation"] = 2L,
                    ["labels"] = new JsonObject
                    {
                        ["app.kubernetes.io/name"] = name,
                        ["agentweaver.io/service"] = name,
                    },
                },
                ["spec"] = new JsonObject
                {
                    ["replicas"] = 1L,
                    ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["app.kubernetes.io/name"] = name } },
                    ["template"] = new JsonObject
                    {
                        ["metadata"] = templateMetadata,
                        ["spec"] = new JsonObject { ["containers"] = new JsonArray(container) },
                    },
                },
                ["status"] = new JsonObject
                {
                    ["observedGeneration"] = 2L, ["updatedReplicas"] = 1L, ["readyReplicas"] = 1L, ["availableReplicas"] = 1L,
                },
            });
            items.Add(new JsonObject
            {
                ["kind"] = "ReplicaSet",
                ["metadata"] = new JsonObject
                {
                    ["name"] = $"{name}-replicaset", ["namespace"] = Namespace, ["uid"] = replicaSetUid,
                    ["labels"] = labels.DeepClone(),
                    ["ownerReferences"] = new JsonArray(new JsonObject
                    {
                        ["kind"] = "Deployment", ["uid"] = deploymentUid, ["controller"] = true,
                    }),
                },
                ["spec"] = new JsonObject { ["replicas"] = 1L },
            });
            JsonObject podMetadata = new()
            {
                ["name"] = $"{name}-pod", ["namespace"] = Namespace, ["uid"] = $"pod-{name}-uid",
                ["labels"] = labels.DeepClone(),
                ["ownerReferences"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "ReplicaSet", ["uid"] = replicaSetUid, ["controller"] = true,
                }),
            };
            if (name == "projects-config")
                podMetadata["annotations"] = new JsonObject { ["agentweaver.io/provider-catalog-source"] = ProviderSource };
            items.Add(new JsonObject
            {
                ["kind"] = "Pod",
                ["metadata"] = podMetadata,
                ["spec"] = new JsonObject { ["containers"] = new JsonArray(podContainer), ["nodeName"] = "node-a" },
                ["status"] = new JsonObject
                {
                    ["phase"] = "Running",
                    ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = "True" }),
                    ["containerStatuses"] = new JsonArray(new JsonObject
                    {
                        ["name"] = name, ["ready"] = true, ["imageID"] = image.Image,
                    }),
                },
            });
        }
        return new JsonObject { ["items"] = items };
    }

    private static JsonObject EnvironmentVariable(string name, string value) =>
        new() { ["name"] = name, ["value"] = value };

    private static JsonObject FindResource(JsonObject cluster, string kind, string name) =>
        cluster["items"]!.AsArray().OfType<JsonObject>().Single(item =>
            item["kind"]?.GetValue<string>() == kind
            && item["metadata"]?["name"]?.GetValue<string>() == name);

    private static string Digest(byte[] bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private sealed record RegistryFixture(
        string Image,
        string Digest,
        string ConfigDigest,
        long CompressedPullBytes);

    private sealed class RegistryFixtureSet
    {
        public Dictionary<string, RegistryFixture> ByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SelectedImages { get; } = new(StringComparer.Ordinal);

        public RegistryFixture Add(
            string id,
            string name,
            string? sourceTree = null,
            string? sourceSha = null,
            int configDescriptorSizeDelta = 0)
        {
            JsonObject imageConfig = new()
            {
                ["os"] = "linux",
                ["architecture"] = "amd64",
                ["config"] = new JsonObject
                {
                    ["Labels"] = new JsonObject
                    {
                        ["org.opencontainers.image.revision"] = sourceSha ?? SourceSha,
                    },
                },
            };
            if (sourceTree is not null)
                imageConfig["config"]!["Labels"]!["io.agentweaver.source-tree"] = sourceTree;
            if (name == "agenthost")
            {
                imageConfig["config"]!["User"] = "1654:1654";
                imageConfig["config"]!["Entrypoint"] = new JsonArray("dotnet", "Agentweaver.AgentHost.dll");
            }
            byte[] configBytes = Encoding.UTF8.GetBytes(imageConfig.ToJsonString());
            string configDigest = Digest(configBytes);
            byte[] layerBytes = [0x1f, 0x8b, 0x08, 0x00];
            string layerDigest = Digest(layerBytes);
            JsonObject manifest = new()
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
                ["config"] = new JsonObject
                {
                    ["mediaType"] = "application/vnd.oci.image.config.v1+json",
                    ["digest"] = configDigest,
                    ["size"] = configBytes.Length + configDescriptorSizeDelta,
                },
                ["layers"] = new JsonArray(new JsonObject
                {
                    ["mediaType"] = "application/vnd.oci.image.layer.v1.tar+gzip",
                    ["digest"] = layerDigest,
                    ["size"] = layerBytes.Length,
                }),
            };
            byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            string digest = Digest(manifestBytes);
            string repository = $"sabbour/{Repository(id)}";
            string image = $"ghcr.io/{repository}@{digest}";
            long compressedBytes = manifestBytes.Length + configBytes.Length + layerBytes.Length;
            RegistryFixture fixture = new(image, digest, configDigest, compressedBytes);
            ByName.Add(name, fixture);
            SelectedImages.Add(Repository(id), digest);
            Bodies.Add($"/v2/{repository}/manifests/{digest}", manifestBytes);
            Bodies.Add($"/v2/{repository}/manifests/sha-{SourceSha}", manifestBytes);
            Bodies.Add($"/v2/{repository}/blobs/{configDigest}", configBytes);
            return fixture;
        }
    }

    private sealed record RecordedRequest(Uri Uri, string? Authorization);

    private sealed class RegistryHandler(
        RegistryFixtureSet fixtures,
        int tokenFailures = 0,
        bool redirectManifests = false,
        bool redirectToUntrustedHost = false) : HttpMessageHandler
    {
        private int tokenAttempts;
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Requests.Add(new RecordedRequest(uri, request.Headers.Authorization?.ToString()));
            if (uri.AbsolutePath == "/token")
            {
                tokenAttempts++;
                if (tokenAttempts <= tokenFailures)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(JsonResponse("{\"token\":\"controlled-pull-token\"}"));
            }
            if (redirectToUntrustedHost && uri.Host == "ghcr.io")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("https://attacker.example/metadata") },
                });
            if (redirectManifests && uri.Host == "ghcr.io" && uri.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri($"https://pkg-containers.githubusercontent.com{uri.AbsolutePath}") },
                });
            if (fixtures.Bodies.TryGetValue(uri.AbsolutePath, out byte[]? body))
                return Task.FromResult(JsonResponse(body));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage JsonResponse(byte[] value) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    }

    private sealed class PinnedSourceHandler(
        HttpMessageHandler innerHandler,
        JsonObject foundation,
        Dictionary<string, byte[]> sourceFiles) : DelegatingHandler(innerHandler)
    {
        private const string RepositoryContents = "/repos/sabbour/agentweaver/contents/";
        public List<string> ContentPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            if (uri.Host != "api.github.com")
                return base.SendAsync(request, cancellationToken);

            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (path == $"/repos/sabbour/agentweaver/commits/{SourceSha}")
                return Json(new JsonObject
                {
                    ["sha"] = SourceSha,
                    ["commit"] = new JsonObject { ["tree"] = new JsonObject { ["sha"] = SourceTree } },
                });
            if (!path.StartsWith(RepositoryContents, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            string sourcePath = path[RepositoryContents.Length..];
            ContentPaths.Add(sourcePath);
            byte[] content = sourcePath == "releases/foundation.json"
                ? Encoding.UTF8.GetBytes(foundation.ToJsonString())
                : sourceFiles.GetValueOrDefault(sourcePath) ?? [];
            if (uri.Query != $"?ref={SourceSha}" || content.Length == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Json(new JsonObject
            {
                ["path"] = sourcePath,
                ["encoding"] = "base64",
                ["content"] = Convert.ToBase64String(content),
            });
        }

        private static Task<HttpResponseMessage> Json(JsonObject value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json"),
            });
    }
}
