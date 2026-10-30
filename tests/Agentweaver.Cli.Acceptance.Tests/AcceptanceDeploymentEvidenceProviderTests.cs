using System.Formats.Tar;
using System.IO.Compression;
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
    public void ReceiptHashMatchesJavaScriptJsonStringifyPropertyOrderAndEscaping()
    {
        JsonObject receipt = new()
        {
            ["text"] = "x<y",
            ["count"] = 1.0,
            ["separator"] = "\u2028",
        };
        string expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("{\"text\":\"x<y\",\"count\":1,\"separator\":\"\u2028\"}")));

        Assert.Equal(expected, AcceptanceDeploymentEvidenceProvider.HashJson(receipt));
    }

    [Fact]
    public async Task OciArchiveMeasurementVerifiesUniqueCompressedBlobsAndExactSourceLabels()
    {
        string workspace = NewWorkspace();
        try
        {
            string archive = Path.Combine(workspace, "agenthost.oci.tar");
            string expectedConfigDigest = await WriteOciArchiveAsync(archive, SourceSha, SourceTree);

            JsonObject measured = await OciArchiveMeasurement.MeasureAsync(archive, SourceSha, SourceTree, default);

            Assert.Equal("agenthost-image", measured["kind"]?.GetValue<string>());
            Assert.Equal(SourceSha, measured["sourceSha"]?.GetValue<string>());
            Assert.Equal(SourceTree, measured["sourceTree"]?.GetValue<string>());
            Assert.Equal(expectedConfigDigest, measured["image"]?["configDigest"]?.GetValue<string>());
            Assert.Equal("linux/amd64", measured["image"]?["platform"]?.GetValue<string>());
            Assert.True(measured["image"]?["compressedPullBytes"]?.GetValue<long>() > 0);

            JsonObject mutatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(archive + ".config.json"))!.AsObject();
            mutatedConfig["config"]!["Labels"]!["io.agentweaver.source-tree"] = new string('d', 40);
            await WriteOciArchiveAsync(archive, SourceSha, SourceTree, mutatedConfig);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                OciArchiveMeasurement.MeasureAsync(archive, SourceSha, SourceTree, default));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task OciArchiveMeasurementRejectsChangedLayerDescriptorsAndBlobContents()
    {
        string workspace = NewWorkspace();
        try
        {
            string archive = Path.Combine(workspace, "agenthost.oci.tar");

            await WriteOciArchiveAsync(
                archive, SourceSha, SourceTree,
                layerDescriptorDigest: $"sha256:{new string('f', 64)}");
            InvalidDataException digestFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                OciArchiveMeasurement.MeasureAsync(archive, SourceSha, SourceTree, default));
            Assert.Contains("compressed size and digest", digestFailure.Message, StringComparison.Ordinal);

            await WriteOciArchiveAsync(archive, SourceSha, SourceTree, layerDescriptorSize: 1);
            InvalidDataException sizeFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                OciArchiveMeasurement.MeasureAsync(archive, SourceSha, SourceTree, default));
            Assert.Contains("compressed size and digest", sizeFailure.Message, StringComparison.Ordinal);

            await WriteOciArchiveAsync(
                archive, SourceSha, SourceTree,
                layerArchiveContent: [0x1f, 0x8b, 0x08]);
            InvalidDataException blobFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                OciArchiveMeasurement.MeasureAsync(archive, SourceSha, SourceTree, default));
            Assert.Contains("compressed size and digest", blobFailure.Message, StringComparison.Ordinal);

            await WriteOciArchiveAsync(archive, SourceSha, SourceTree, layerContent: [1, 2, 3]);
            InvalidDataException compressionFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                OciArchiveMeasurement.MeasureAsync(archive, SourceSha, SourceTree, default));
            Assert.Contains("not a gzip blob", compressionFailure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryReadsRetryPullTokenAndNeverForwardBearerToBlobRedirect()
    {
        RegistryFixtureSet fixtures = new();
        RegistryFixture registry = fixtures.Add("Agentweaver.Gateway", "gateway");
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
            registry.Image, registry.Image, SourceSha, SourceTree, default);

        Assert.Equal(registry.Digest, result["manifestDigest"]?.GetValue<string>());
        Assert.Equal(registry.ConfigDigest, result["configDigest"]?.GetValue<string>());
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath == "/token"));
        Assert.All(handler.Requests.Where(request => request.Uri.Host == "pkg-containers.githubusercontent.com"),
            request => Assert.Null(request.Authorization));
        Assert.All(handler.Requests.Where(request => request.Uri.Host == "ghcr.io" && request.Uri.AbsolutePath != "/token"),
            request => Assert.Equal("Bearer controlled-pull-token", request.Authorization));
        Assert.Contains(handler.Requests, request => request.Uri.Query.Contains("repository%3Asabbour%2Fagentweaver%2Fgateway%3Apull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegistryRejectsUnallowlistedRedirectBeforeSendingCredentials()
    {
        RegistryFixtureSet fixtures = new();
        RegistryFixture registry = fixtures.Add("Agentweaver.Gateway", "gateway");
        var handler = new RegistryHandler(fixtures, redirectToUntrustedHost: true);
        using var http = new HttpClient(handler);
        var provider = new AcceptanceDeploymentEvidenceProvider(
            http,
            (_, _, _) => throw new InvalidOperationException("No cluster read is expected."),
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.VerifyRegistryImageAsync(
            registry.Image, registry.Image, SourceSha, SourceTree, default));
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "attacker.example");
    }

    [Fact]
    public async Task PrepareAsyncRejectsMissingEmbeddedBuildIdentityBeforeGitHubReads()
    {
        string workspace = NewWorkspace();
        try
        {
            var handler = new RegistryHandler(new RegistryFixtureSet());
            using var http = new HttpClient(handler);
            JsonObject deployment = new()
            {
                ["manifestPath"] = "manifest.json",
                ["publicationPath"] = "publication.json",
                ["agentHostArchivePath"] = "agenthost.oci.tar",
                ["integrationDeploymentReceiptPath"] = "integration.json",
            };
            AcceptanceManifest manifest = new(
                new JsonObject { ["deploymentEvidence"] = deployment },
                new JsonObject(),
                new JsonObject
                {
                    ["sourceReceipt"] = new JsonObject { ["sourceSha"] = SourceSha, ["sourceTree"] = SourceTree },
                },
                new JsonObject(),
                "message",
                Path.Combine(workspace, "input.json"),
                workspace);
            await File.WriteAllTextAsync(Path.Combine(workspace, "manifest.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(workspace, "publication.json"),
                new JsonObject { ["mode"] = "images-only" }.ToJsonString());

            foreach ((string buildSha, string buildTree) in new[]
            {
                ("", ""),
                (new string('d', 40), new string('e', 40)),
            })
            {
                var provider = new AcceptanceDeploymentEvidenceProvider(
                    http,
                    (_, _, _) => throw new InvalidOperationException("No cluster read is expected."),
                    readBuildIdentity: () => (buildSha, buildTree));
                InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    provider.PrepareAsync(manifest, default));

                Assert.Contains("CLI embedded build identity", failure.Message, StringComparison.Ordinal);
            }
            Assert.Empty(handler.Requests);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapReleaseSelectsPinnedSourceAndAllowsItsConfiguredLightweightTag()
    {
        const string releaseTag = "v1.0.1";
        const string sourceSha = "56cf5a8f841dc22f69cd087d45f4e2882702dfb4";
        JsonObject publication = new()
        {
            ["sourceSha"] = sourceSha,
            ["status"] = "published",
            ["planned"] = new JsonArray(),
        };
        var handler = new GitHubHandler(releaseTag, sourceSha, publication, lightweight: true);
        using var http = new HttpClient(handler);
        var provider = new AcceptanceDeploymentEvidenceProvider(
            http,
            (_, _, _) => throw new InvalidOperationException(),
            (_, _) => Task.CompletedTask);
        string workspace = NewWorkspace();
        string publicationFile = Path.Combine(workspace, "publication.json");
        await File.WriteAllTextAsync(publicationFile, publication.ToJsonString());
        try
        {
            await provider.VerifyReleasePublicationAsync(
                releaseTag, sourceSha, publication, bootstrapDraft: true, publicationFile, default);

            Assert.Contains(handler.Requests, uri => Uri.UnescapeDataString(uri.AbsolutePath)
                .EndsWith($"/git/ref/tags/agentweaver-publication/{sourceSha}/result", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, uri => Uri.UnescapeDataString(uri.AbsolutePath)
                .EndsWith($"/git/tags/cccccccccccccccccccccccccccccccccccccccc", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ContinuationReceiptUsesItsPinnedResultRefAndPublicationHashPinIsEnforced()
    {
        const string releaseTag = "v1.0.0";
        const string sourceSha = "c24d0000fe5fc6163e4f38594c39dff782d3a46d";
        JsonObject continuation = new()
        {
            ["sourceSha"] = sourceSha,
            ["status"] = "published",
            ["continuation"] = new JsonObject { ["resultSha"] = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" },
        };
        var handler = new GitHubHandler(releaseTag, sourceSha, continuation);
        using var http = new HttpClient(handler);
        var provider = new AcceptanceDeploymentEvidenceProvider(http, (_, _, _) => throw new InvalidOperationException());
        string workspace = NewWorkspace();
        string publicationFile = Path.Combine(workspace, "publication.json");
        await File.WriteAllTextAsync(publicationFile, continuation.ToJsonString());
        try
        {
            await provider.VerifyReleasePublicationAsync(
                releaseTag, sourceSha, continuation, bootstrapDraft: true, publicationFile, default);
            Assert.Contains(handler.Requests, uri => Uri.UnescapeDataString(uri.AbsolutePath)
                .EndsWith("/git/ref/tags/agentweaver-publication-continuation/1.0.0/c24d0000fe5fc6163e4f38594c39dff782d3a46d/result",
                    StringComparison.Ordinal));

            var pinnedHandler = new GitHubHandler("v1.0.14",
                "4105e2f2520659e7c9c636cf34972661fa26342f", new JsonObject());
            using var pinnedHttp = new HttpClient(pinnedHandler);
            var pinnedProvider = new AcceptanceDeploymentEvidenceProvider(
                pinnedHttp, (_, _, _) => throw new InvalidOperationException());
            JsonObject pinnedPublication = new()
            {
                ["sourceSha"] = "4105e2f2520659e7c9c636cf34972661fa26342f",
                ["status"] = "published",
                ["planned"] = new JsonArray(),
            };
            string pinnedFile = Path.Combine(workspace, "pinned-publication.json");
            await File.WriteAllTextAsync(pinnedFile, pinnedPublication.ToJsonString());
            InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => pinnedProvider.VerifyReleasePublicationAsync(
                "v1.0.14", "4105e2f2520659e7c9c636cf34972661fa26342f",
                pinnedPublication, bootstrapDraft: true, pinnedFile, default));
            Assert.Contains("pinned SHA-256", failure.Message, StringComparison.Ordinal);
            Assert.Empty(pinnedHandler.Requests);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task KubernetesReadbackRequiresCurrentReadyRolloutAndValidatesEveryServiceImage()
    {
        RegistryFixtureSet registry = CreateRegistryFixtureSet();
        JsonObject cluster = CreateCluster(registry);
        using var http = new HttpClient(new RegistryHandler(registry));
        var provider = new AcceptanceDeploymentEvidenceProvider(
            http,
            (_, _, _) => Task.FromResult((JsonObject)cluster.DeepClone()),
            (_, _) => Task.CompletedTask);

        JsonObject evidence = await provider.ObserveServicesEquivalentAsync(
            CreateManifest(), SourceSha, SourceTree, registry.Images, default);

        Assert.Equal("passed", evidence["status"]?.GetValue<string>());
        Assert.Equal(Services.Length, evidence["services"]?.AsArray().Count);
        Assert.Equal("blocked", evidence["agentHostRuntime"]?["status"]?.GetValue<string>());
        JsonObject deployment = cluster["items"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["kind"]?.GetValue<string>() == "Deployment"
                && item["metadata"]?["name"]?.GetValue<string>() == "gateway");
        deployment["status"]!["observedGeneration"] = 1;

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ObserveServicesEquivalentAsync(
            CreateManifest(), SourceSha, SourceTree, registry.Images, default));
    }

    [Fact]
    public async Task KubernetesReadbackRejectsUnreadyPodsAndBrokenDeploymentOwnership()
    {
        var mutations = new (Action<JsonObject> Apply, string Expected)[]
        {
            (cluster =>
            {
                JsonObject pod = FindResource(cluster, "Pod", "identity-broker-pod");
                pod["status"]!["conditions"]![0]!["status"] = "False";
            }, "not a ready owner"),
            (cluster =>
            {
                JsonObject pod = FindResource(cluster, "Pod", "identity-broker-pod");
                pod["metadata"]!["ownerReferences"]![0]!["uid"] = "stale-replicaset-uid";
            }, "unexpected number of current owner Pods"),
            (cluster =>
            {
                JsonObject replicaSet = FindResource(cluster, "ReplicaSet", "identity-broker-replicaset");
                replicaSet["metadata"]!["ownerReferences"]![0]!["uid"] = "stale-deployment-uid";
            }, "one current owner ReplicaSet"),
        };

        foreach ((Action<JsonObject> apply, string expected) in mutations)
        {
            RegistryFixtureSet registry = CreateRegistryFixtureSet();
            JsonObject cluster = CreateCluster(registry);
            apply(cluster);
            using var http = new HttpClient(new RegistryHandler(registry));
            var provider = new AcceptanceDeploymentEvidenceProvider(
                http,
                (_, _, _) => Task.FromResult((JsonObject)cluster.DeepClone()),
                (_, _) => Task.CompletedTask);

            InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.ObserveServicesEquivalentAsync(
                    CreateManifest(), SourceSha, SourceTree, registry.Images, default));
            Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task KubernetesReadbackRejectsRegistryImageWithMismatchedSourceTreeLabel()
    {
        RegistryFixtureSet registry = new();
        foreach ((string id, string name) in Services)
            registry.Add(id, name, name == "identity-broker" ? new string('d', 40) : SourceTree);
        registry.Add("Agentweaver.AgentHost", "agenthost");
        JsonObject cluster = CreateCluster(registry);
        using var http = new HttpClient(new RegistryHandler(registry));
        var provider = new AcceptanceDeploymentEvidenceProvider(
            http,
            (_, _, _) => Task.FromResult((JsonObject)cluster.DeepClone()),
            (_, _) => Task.CompletedTask);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ObserveServicesEquivalentAsync(
                CreateManifest(), SourceSha, SourceTree, registry.Images, default));

        Assert.Contains("running image configuration", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsyncValidatesPinnedSourceProjectMetadataAndRejectsUnsafePaths()
    {
        string workspace = NewWorkspace();
        try
        {
            RegistryFixtureSet registry = CreateRegistryFixtureSet();
            JsonObject foundation = CreatePrepareFoundation(out Dictionary<string, byte[]> sourceFiles, out string projectPath);
            sourceFiles["releases/foundation.json"] = Encoding.UTF8.GetBytes(foundation.ToJsonString());

            string archivePath = Path.Combine(workspace, "agenthost.oci.tar");
            await WriteOciArchiveAsync(archivePath, SourceSha, SourceTree);
            string archiveSha = await OciArchiveMeasurement.HashFileAsync(archivePath, default);
            JsonObject measuredHost = await OciArchiveMeasurement.MeasureAsync(
                archivePath, SourceSha, SourceTree, default);
            JsonObject publication = CreatePreparePublication(foundation, registry, archiveSha, measuredHost);
            JsonArray images = AcceptanceDeploymentEvidenceProvider.ValidatePublicationEquivalent(
                publication, foundation, SourceSha, SourceTree, integration: true, out _);
            JsonObject integrationReceipt = CreatePrepareIntegrationReceipt(foundation, publication, images);

            string foundationPath = Path.Combine(workspace, "foundation.json");
            string publicationPath = Path.Combine(workspace, "publication.json");
            string integrationPath = Path.Combine(workspace, "integration.json");
            await File.WriteAllTextAsync(foundationPath, foundation.ToJsonString());
            await File.WriteAllTextAsync(publicationPath, publication.ToJsonString());
            await File.WriteAllTextAsync(integrationPath, integrationReceipt.ToJsonString());

            JsonObject pins = CreateManifest().Pins;
            pins["sourceReceipt"] = new JsonObject { ["sourceSha"] = SourceSha, ["sourceTree"] = SourceTree };
            JsonObject hostResult = publication["published"]!.AsArray().OfType<JsonObject>()
                .Single(result => result["id"]?.GetValue<string>() == "Agentweaver.AgentHost");
            string hostImage = hostResult["image"]!.GetValue<string>();
            string hostDigest = hostImage[(hostImage.LastIndexOf('@') + 1)..];
            pins["imageReceipt"] = new JsonObject
            {
                ["registryImage"] = hostImage,
                ["registryDigest"] = hostDigest,
                ["manifestDigest"] = measuredHost["image"]!["digest"]!.DeepClone(),
                ["configDigest"] = hostResult["configDigest"]!.DeepClone(),
                ["archiveSha256"] = archiveSha,
                ["compressedPullBytes"] = measuredHost["image"]!["compressedPullBytes"]!.DeepClone(),
            };
            JsonObject deployment = new()
            {
                ["manifestPath"] = "foundation.json",
                ["publicationPath"] = "publication.json",
                ["agentHostArchivePath"] = "agenthost.oci.tar",
                ["integrationDeploymentReceiptPath"] = "integration.json",
            };
            AcceptanceManifest input = new(
                new JsonObject { ["deploymentEvidence"] = deployment },
                new JsonObject(),
                pins,
                new JsonObject(),
                "message",
                Path.Combine(workspace, "input.json"),
                workspace);
            JsonObject cluster = CreateCluster(registry);
            var sourceHandler = new PinnedSourceHandler(new RegistryHandler(registry), sourceFiles);
            using var http = new HttpClient(sourceHandler);
            var provider = new AcceptanceDeploymentEvidenceProvider(
                http,
                (_, _, _) => Task.FromResult((JsonObject)cluster.DeepClone()),
                (_, _) => Task.CompletedTask,
                () => (SourceSha, SourceTree));

            Assert.NotEqual(hostDigest, pins["imageReceipt"]?["manifestDigest"]?.GetValue<string>());
            var mismatchedProvider = new AcceptanceDeploymentEvidenceProvider(
                http,
                (_, _, _) => Task.FromResult((JsonObject)cluster.DeepClone()),
                (_, _) => Task.CompletedTask,
                () => (new string('d', 40), new string('e', 40)));
            InvalidDataException identityMismatch = await Assert.ThrowsAsync<InvalidDataException>(() =>
                mismatchedProvider.PrepareAsync(input, default));
            Assert.Contains("CLI embedded build identity", identityMismatch.Message, StringComparison.Ordinal);

            JsonObject evidence = await provider.PrepareAsync(input, default);

            Assert.Equal("deployed", evidence["integrationDeployment"]?["status"]?.GetValue<string>());
            Assert.Equal(SourceSha, evidence["source"]?["sourceSha"]?.GetValue<string>());
            Assert.Equal("passed", evidence["services"]?["status"]?.GetValue<string>());
            Assert.Equal(pins["imageReceipt"]?["manifestDigest"]?.GetValue<string>(),
                evidence["agentHostImageMeasurement"]?["image"]?["digest"]?.GetValue<string>());
            Assert.NotEqual(evidence["agentHostImageMeasurement"]?["registryDigest"]?.GetValue<string>(),
                evidence["agentHostImageMeasurement"]?["image"]?["digest"]?.GetValue<string>());
            Assert.Equal($"ghcr.io/sabbour/agentweaver/agenthost@{pins["imageReceipt"]?["manifestDigest"]?.GetValue<string>()}",
                evidence["services"]?["agentHostImageReceipt"]?["image"]?.GetValue<string>());
            Assert.Contains("apps/web/package-lock.json", sourceHandler.ContentPaths);

            sourceFiles[projectPath] = Encoding.UTF8.GetBytes("<Project><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>");
            InvalidDataException versionFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.PrepareAsync(input, default));
            Assert.Contains(projectPath, versionFailure.Message, StringComparison.Ordinal);

            JsonObject unsafeFoundation = (JsonObject)foundation.DeepClone();
            unsafeFoundation["components"]!.AsArray()[0]!["project"] = "../escape.csproj";
            sourceFiles["releases/foundation.json"] = Encoding.UTF8.GetBytes(unsafeFoundation.ToJsonString());
            await File.WriteAllTextAsync(foundationPath, unsafeFoundation.ToJsonString());
            int previousContentCount = sourceHandler.ContentPaths.Count;
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.PrepareAsync(input, default));
            Assert.Single(sourceHandler.ContentPaths.Skip(previousContentCount));
            Assert.Equal("releases/foundation.json", sourceHandler.ContentPaths.Last());
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsyncAcceptsHistoricalReleaseSourceWithNewerHelperBuild()
    {
        const string releaseTag = "v1.99.0";
        string workspace = NewWorkspace();
        try
        {
            RegistryFixtureSet registry = CreateReleaseRegistryFixtureSet();
            JsonObject foundation = CreatePrepareFoundation(out Dictionary<string, byte[]> sourceFiles, out _);
            foundation["stage"] = "release";
            foundation["evidence"] = new JsonObject { ["sourceSha"] = SourceSha };
            sourceFiles["releases/foundation.json"] = Encoding.UTF8.GetBytes(foundation.ToJsonString());

            string archivePath = Path.Combine(workspace, "agenthost.oci.tar");
            await WriteOciArchiveAsync(archivePath, SourceSha, SourceTree);
            string archiveSha = await OciArchiveMeasurement.HashFileAsync(archivePath, default);
            JsonObject measuredHost = await OciArchiveMeasurement.MeasureAsync(archivePath, SourceSha, SourceTree, default);
            JsonObject publication = CreatePreparePublication(foundation, registry, archiveSha, measuredHost);
            ConfigureReleasePublication(foundation, publication);
            sourceFiles["releases/foundation.json"] = Encoding.UTF8.GetBytes(foundation.ToJsonString());

            string foundationPath = Path.Combine(workspace, "foundation.json");
            string publicationPath = Path.Combine(workspace, "publication.json");
            await File.WriteAllTextAsync(foundationPath, foundation.ToJsonString());
            await File.WriteAllTextAsync(publicationPath, publication.ToJsonString());

            JsonObject pins = CreateManifest().Pins;
            pins["sourceReceipt"] = new JsonObject { ["sourceSha"] = SourceSha, ["sourceTree"] = SourceTree };
            JsonObject hostResult = publication["published"]!.AsArray().OfType<JsonObject>()
                .Single(result => result["id"]?.GetValue<string>() == "Agentweaver.AgentHost");
            string hostImage = hostResult["image"]!.GetValue<string>();
            string hostDigest = hostImage[(hostImage.LastIndexOf('@') + 1)..];
            pins["imageReceipt"] = new JsonObject
            {
                ["registryImage"] = hostImage,
                ["registryDigest"] = hostDigest,
                ["manifestDigest"] = measuredHost["image"]!["digest"]!.DeepClone(),
                ["configDigest"] = hostResult["configDigest"]!.DeepClone(),
                ["archiveSha256"] = archiveSha,
                ["compressedPullBytes"] = measuredHost["image"]!["compressedPullBytes"]!.DeepClone(),
            };
            AcceptanceManifest input = new(
                new JsonObject
                {
                    ["deploymentEvidence"] = new JsonObject
                    {
                        ["manifestPath"] = "foundation.json",
                        ["publicationPath"] = "publication.json",
                        ["agentHostArchivePath"] = "agenthost.oci.tar",
                        ["releaseTag"] = releaseTag,
                    },
                },
                new JsonObject(),
                pins,
                new JsonObject(),
                "message",
                Path.Combine(workspace, "input.json"),
                workspace);

            var sourceHandler = new PinnedSourceHandler(
                new GitHubHandler(releaseTag, SourceSha, publication), sourceFiles);
            using var http = new HttpClient(new RegistryHandler(registry, fallbackHandler: sourceHandler));
            JsonObject cluster = CreateCluster(registry);
            var provider = new AcceptanceDeploymentEvidenceProvider(
                http,
                (_, _, _) => Task.FromResult((JsonObject)cluster.DeepClone()),
                (_, _) => Task.CompletedTask,
                () => (new string('f', 40), new string('1', 40)));

            JsonObject evidence = await provider.PrepareAsync(input, default);

            Assert.Equal("deployed", evidence["releaseDeployment"]?["status"]?.GetValue<string>());
            Assert.Equal(SourceSha, evidence["source"]?["sourceSha"]?.GetValue<string>());
            Assert.NotEqual(hostDigest, measuredHost["image"]?["digest"]?.GetValue<string>());
            Assert.Equal(measuredHost["image"]?["digest"]?.GetValue<string>(),
                evidence["agentHostImageMeasurement"]?["image"]?["digest"]?.GetValue<string>());
            Assert.Equal($"ghcr.io/sabbour/{ReleaseRepositoryName("Agentweaver.AgentHost")}@{measuredHost["image"]?["digest"]?.GetValue<string>()}",
                evidence["services"]?["agentHostImageReceipt"]?["image"]?.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void IntegrationReceiptBindsEveryServiceReadbackAndItsActualHash()
    {
        (JsonObject receipt, JsonObject manifest, JsonObject publication, AcceptanceManifest input, JsonArray images) =
            CreateIntegrationReceipt();

        Assert.Equal(AcceptanceDeploymentEvidenceProvider.HashJson(manifest), receipt["manifestSha256"]?.GetValue<string>());
        Assert.Equal(AcceptanceDeploymentEvidenceProvider.HashJson(publication), receipt["publicationSha256"]?.GetValue<string>());
        Assert.Equal(publication["provenanceSha256"]?.GetValue<string>(), receipt["provenanceSha256"]?.GetValue<string>());
        Assert.True(ApiAcceptanceSupport.TryLong(receipt["schemaVersion"], out long schema) && schema == 1);
        Assert.True(DateTimeOffset.TryParse(receipt["completedAt"]!.GetValue<string>(), out _));

        JsonObject result = AcceptanceDeploymentEvidenceProvider.ValidateIntegrationReceiptEquivalent(
            receipt, SourceSha, SourceTree, manifest, publication, input, images);

        Assert.Equal("deployed", result["status"]?.GetValue<string>());
        Assert.Equal(AcceptanceDeploymentEvidenceProvider.HashJson(receipt["readbacks"]!["resources"]!),
            result["readbackSha256"]?.GetValue<string>());
        receipt["readbacks"]!["resources"]![0]!["sha256"] = new string('f', 64);
        Assert.Throws<InvalidDataException>(() =>
            AcceptanceDeploymentEvidenceProvider.ValidateIntegrationReceiptEquivalent(
                receipt, SourceSha, SourceTree, manifest, publication, input, images));
    }

    [Fact]
    public void ReleaseReceiptIsBuiltFromExactServiceAndRegistryReadbacks()
    {
        (JsonObject manifest, JsonObject pins, JsonArray images, JsonObject services) = CreateReleaseReadbacks();

        JsonObject receipt = AcceptanceDeploymentEvidenceProvider.CreateReleaseDeploymentEvidenceEquivalent(
            SourceSha, SourceTree, manifest, (JsonObject)manifest.DeepClone(),
            new JsonObject { ["sourceSha"] = SourceSha }, pins, images, services);

        Assert.Equal("deployed", receipt["status"]?.GetValue<string>());
        Assert.Equal("release", receipt["resultType"]?.GetValue<string>());
        Assert.Equal(AcceptanceDeploymentEvidenceProvider.HashJson(receipt["readbacks"]!),
            receipt["readbackSha256"]?.GetValue<string>());
        services["services"]!.AsArray()[0]!["pods"]![0]!["ready"] = false;
        Assert.Throws<InvalidDataException>(() =>
            AcceptanceDeploymentEvidenceProvider.CreateReleaseDeploymentEvidenceEquivalent(
                SourceSha, SourceTree, manifest, (JsonObject)manifest.DeepClone(),
                new JsonObject { ["sourceSha"] = SourceSha }, pins, images, services));
    }

    [Fact]
    public void ReleasePublicationAcceptsAgentHostReuseOnlyWithItsOriginalBuildEvidence()
    {
        const string reusedSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string reusedTree = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        JsonArray components = [];
        JsonArray planned = [];
        JsonArray published = [];
        foreach ((string id, string name) in Services.Append(("Agentweaver.AgentHost", "agenthost")))
        {
            string digest = Digest(Encoding.UTF8.GetBytes(id));
            string configDigest = Digest(Encoding.UTF8.GetBytes(name));
            string repositoryName = id switch
            {
                "Agentweaver.Web" => "agentweaver-web",
                "Agentweaver.AgentHost" => "agentweaver-agenthost",
                _ => id.ToLowerInvariant(),
            };
            JsonObject component = new()
            {
                ["id"] = id,
                ["kind"] = "service",
                ["version"] = "1.0.0",
                ["project"] = $"services/{name}/{id}.csproj",
                ["imageDigest"] = digest,
            };
            components.Add(component);
            JsonObject plan = new()
            {
                ["id"] = id,
                ["kind"] = "image",
                ["version"] = "1.0.0",
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"archive:{id}"))),
                ["destination"] = $"ghcr.io/sabbour/{repositoryName}:1.0.0-{(id == "Agentweaver.AgentHost" ? reusedSha : SourceSha)}",
            };
            JsonObject result = new()
            {
                ["id"] = id,
                ["kind"] = "image",
                ["version"] = "1.0.0",
                ["archiveSha256"] = plan["sha256"]!.DeepClone(),
                ["image"] = $"ghcr.io/sabbour/{repositoryName}@{digest}",
            };
            if (id == "Agentweaver.AgentHost")
            {
                JsonObject reuse = new()
                {
                    ["sourceSha"] = reusedSha,
                    ["sourceTree"] = reusedTree,
                    ["inputFingerprint"] = $"sha256:{new string('4', 64)}",
                    ["receiptSourceSha"] = SourceSha,
                };
                plan["reused"] = (JsonObject)reuse.DeepClone();
                result["reused"] = (JsonObject)reuse.DeepClone();
                result["imageConfigDigest"] = configDigest;
                result["ociArchiveSha256"] = new string('5', 64);
                result["preparedOciMeasurement"] = new JsonObject
                {
                    ["kind"] = "agenthost-image",
                    ["schemaVersion"] = 1L,
                    ["sourceSha"] = reusedSha,
                    ["sourceTree"] = reusedTree,
                    ["image"] = new JsonObject
                    {
                        ["version"] = "1.0.0",
                        ["platform"] = "linux/amd64",
                        ["digest"] = digest,
                        ["configDigest"] = configDigest,
                        ["compressedPullBytes"] = 1234L,
                    },
                };
            }
            planned.Add(plan);
            published.Add(result);
        }
        JsonObject manifest = new()
        {
            ["stage"] = "release",
            ["evidence"] = new JsonObject { ["sourceSha"] = SourceSha },
            ["components"] = components,
        };
        JsonObject publication = new()
        {
            ["schemaVersion"] = 1L,
            ["status"] = "published",
            ["sourceSha"] = SourceSha,
            ["sourceTree"] = SourceTree,
            ["provenanceSha256"] = new string('1', 64),
            ["requiredPackageIds"] = new JsonArray(),
            ["planned"] = planned,
            ["published"] = published,
        };

        JsonArray images = AcceptanceDeploymentEvidenceProvider.ValidatePublicationEquivalent(
            publication, manifest, SourceSha, SourceTree, integration: false, out _);
        JsonObject host = images.OfType<JsonObject>()
            .Single(image => image["id"]?.GetValue<string>() == "Agentweaver.AgentHost");

        Assert.Equal(reusedSha, host["buildSource"]?["sourceSha"]?.GetValue<string>());
        Assert.Equal(reusedTree, host["reusedFrom"]?["sourceTree"]?.GetValue<string>());
        published.OfType<JsonObject>().Single(result =>
            result["id"]?.GetValue<string>() == "Agentweaver.AgentHost")
            ["preparedOciMeasurement"]!["sourceSha"] = SourceSha;
        Assert.Throws<InvalidDataException>(() =>
            AcceptanceDeploymentEvidenceProvider.ValidatePublicationEquivalent(
                publication, manifest, SourceSha, SourceTree, integration: false, out _));
    }

    private static string NewWorkspace()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

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
        foreach (var (id, name) in Services)
            fixture.Add(id, name);
        fixture.Add("Agentweaver.AgentHost", "agenthost");
        return fixture;
    }

    private static RegistryFixtureSet CreateReleaseRegistryFixtureSet()
    {
        RegistryFixtureSet fixture = new();
        foreach ((string id, string name) in Services.Append(("Agentweaver.AgentHost", "agenthost")))
            fixture.Add(id, name, repositoryName: ReleaseRepositoryName(id));
        return fixture;
    }

    private static string ReleaseRepositoryName(string id) =>
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
        foreach (var (id, name) in Services)
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

    private static JsonObject FindResource(JsonObject cluster, string kind, string name) =>
        cluster["items"]!.AsArray().OfType<JsonObject>().Single(item =>
            item["kind"]?.GetValue<string>() == kind
            && item["metadata"]?["name"]?.GetValue<string>() == name);

    private static (JsonObject Receipt, JsonObject Manifest, JsonObject Publication, AcceptanceManifest Input, JsonArray Images)
        CreateIntegrationReceipt()
    {
        JsonObject manifest = new() { ["stage"] = "draft" };
        JsonObject publication = new()
        {
            ["mode"] = "images-only",
            ["sourceTree"] = SourceTree,
            ["provenanceSha256"] = new string('1', 64),
        };
        JsonArray images = CreateIntegrationImages();
        JsonArray registryImages = new();
        foreach (JsonObject image in images.OfType<JsonObject>())
        {
            JsonObject component = (JsonObject)image["component"]!;
            JsonObject result = (JsonObject)image["result"]!;
            registryImages.Add(new JsonObject
            {
                ["id"] = image["id"]!.DeepClone(),
                ["image"] = image["id"]!.GetValue<string>() == "Agentweaver.AgentHost"
                    ? image["sandboxImage"]!.DeepClone()
                    : image["image"]!.DeepClone(),
                ["configDigest"] = result["configDigest"]!.DeepClone(),
                ["sourceSha"] = SourceSha,
                ["sourceTree"] = SourceTree,
                ["version"] = component["version"]!.DeepClone(),
            });
        }
        JsonArray resources = new();
        foreach (string name in Services.Select(item => item.Name))
        foreach (string kind in new[] { "deployment", "service", "pods" })
            resources.Add(new JsonObject { ["kind"] = kind, ["name"] = name, ["sha256"] = new string('2', 64) });
        JsonObject readbacks = new()
        {
            ["resources"] = resources,
            ["sha256"] = AcceptanceDeploymentEvidenceProvider.HashJson(resources),
        };
        JsonObject receipt = new()
        {
            ["schemaVersion"] = 1L,
            ["status"] = "deployed",
            ["sourceSha"] = SourceSha,
            ["sourceTree"] = SourceTree,
            ["manifestSha256"] = AcceptanceDeploymentEvidenceProvider.HashJson(manifest),
            ["provenanceSha256"] = publication["provenanceSha256"]!.DeepClone(),
            ["publicationSha256"] = AcceptanceDeploymentEvidenceProvider.HashJson(publication),
            ["runtimeManifestSha256"] = new string('3', 64),
            ["completedAt"] = "2026-10-29T12:00:00.000Z",
            ["target"] = new JsonObject
            {
                ["subscriptionId"] = "subscription", ["tenantId"] = "tenant", ["resourceGroup"] = "agentweaver",
                ["clusterName"] = "agentweaver-aks", ["namespace"] = Namespace, ["nodeMin"] = 2L, ["nodeMax"] = 3L,
            },
            ["registryImages"] = registryImages,
            ["readbacks"] = readbacks,
        };
        return (receipt, manifest, publication, CreateManifest(), images);
    }

    private static JsonObject CreatePrepareFoundation(
        out Dictionary<string, byte[]> sourceFiles,
        out string projectToMutate)
    {
        sourceFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        projectToMutate = "";
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
            if (id == "Agentweaver.Web")
            {
                sourceFiles[project] = Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\"}");
                sourceFiles["apps/web/package-lock.json"] = Encoding.UTF8.GetBytes(
                    "{\"version\":\"1.0.0\",\"packages\":{\"\":{\"version\":\"1.0.0\"}}}");
            }
            else
            {
                sourceFiles[project] = Encoding.UTF8.GetBytes(
                    "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");
                if (projectToMutate.Length == 0)
                    projectToMutate = project;
            }
        }

        return new JsonObject
        {
            ["stage"] = "draft",
            ["components"] = components,
            ["compatibility"] = new JsonArray(),
        };
    }

    private static JsonObject CreatePreparePublication(
        JsonObject foundation,
        RegistryFixtureSet registry,
        string archiveSha,
        JsonObject measuredHost)
    {
        JsonArray planned = [];
        JsonArray published = [];
        foreach ((string id, string name) in Services.Append(("Agentweaver.AgentHost", "agenthost")))
        {
            RegistryFixture? fixture = id == "Agentweaver.AgentHost" ? null : registry.ByName[name];
            JsonObject component = foundation["components"]!.AsArray().OfType<JsonObject>()
                .Single(row => row["id"]?.GetValue<string>() == id);
            string imageDigest = id == "Agentweaver.AgentHost"
                ? Digest(Encoding.UTF8.GetBytes("agenthost-docker-publication"))
                : fixture!.Digest;
            string configDigest = id == "Agentweaver.AgentHost"
                ? measuredHost["image"]!["configDigest"]!.GetValue<string>()
                : fixture!.ConfigDigest;
            string image = $"ghcr.io/sabbour/agentweaver/{name}@{imageDigest}";
            string artifactSha = id == "Agentweaver.AgentHost"
                ? archiveSha
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
            planned.Add(new JsonObject
            {
                ["id"] = id,
                ["kind"] = "image",
                ["version"] = "1.0.0",
                ["sha256"] = artifactSha,
                ["destination"] = $"ghcr.io/sabbour/agentweaver/{name}:{SourceSha}",
            });
            JsonObject result = new()
            {
                ["id"] = id,
                ["kind"] = "image",
                ["version"] = "1.0.0",
                ["archiveSha256"] = artifactSha,
                ["image"] = image,
                ["sourceSha"] = SourceSha,
                ["sourceTree"] = SourceTree,
                ["tag"] = SourceSha,
                ["configDigest"] = configDigest,
            };
            if (id == "Agentweaver.AgentHost")
            {
                result["imageConfigDigest"] = configDigest;
                result["ociArchiveSha256"] = archiveSha;
                result["preparedOciMeasurement"] = measuredHost.DeepClone();
            }
            published.Add(result);
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1L,
            ["status"] = "published",
            ["sourceSha"] = SourceSha,
            ["sourceTree"] = SourceTree,
            ["provenanceSha256"] = new string('1', 64),
            ["mode"] = "images-only",
            ["planned"] = planned,
            ["published"] = published,
        };
    }

    private static void ConfigureReleasePublication(JsonObject foundation, JsonObject publication)
    {
        publication.Remove("mode");
        publication["requiredPackageIds"] = new JsonArray();
        foreach (JsonObject plan in publication["planned"]!.AsArray().OfType<JsonObject>())
        {
            string id = plan["id"]!.GetValue<string>();
            string repositoryName = ReleaseRepositoryName(id);
            JsonObject result = publication["published"]!.AsArray().OfType<JsonObject>()
                .Single(item => item["id"]?.GetValue<string>() == id);
            string image = result["image"]!.GetValue<string>();
            string digest = image[(image.LastIndexOf('@') + 1)..];
            plan["destination"] = $"ghcr.io/sabbour/{repositoryName}:1.0.0-{SourceSha}";
            result["image"] = $"ghcr.io/sabbour/{repositoryName}@{digest}";
            foundation["components"]!.AsArray().OfType<JsonObject>()
                .Single(item => item["id"]?.GetValue<string>() == id)["imageDigest"] = digest;
        }
    }

    private static JsonObject CreatePrepareIntegrationReceipt(
        JsonObject foundation,
        JsonObject publication,
        JsonArray images)
    {
        JsonArray registryImages = [];
        foreach (JsonObject image in images.OfType<JsonObject>())
        {
            JsonObject component = (JsonObject)image["component"]!;
            JsonObject result = (JsonObject)image["result"]!;
            registryImages.Add(new JsonObject
            {
                ["id"] = image["id"]!.DeepClone(),
                ["image"] = (image["sandboxImage"] ?? image["image"])!.DeepClone(),
                ["configDigest"] = result["configDigest"]!.DeepClone(),
                ["sourceSha"] = SourceSha,
                ["sourceTree"] = SourceTree,
                ["version"] = component["version"]!.DeepClone(),
            });
        }
        JsonArray resources = [];
        foreach (string name in Services.Select(service => service.Name))
        foreach (string kind in new[] { "deployment", "service", "pods" })
            resources.Add(new JsonObject
            {
                ["kind"] = kind,
                ["name"] = name,
                ["sha256"] = new string('2', 64),
            });
        return new JsonObject
        {
            ["schemaVersion"] = 1L,
            ["status"] = "deployed",
            ["sourceSha"] = SourceSha,
            ["sourceTree"] = SourceTree,
            ["manifestSha256"] = AcceptanceDeploymentEvidenceProvider.HashJson(foundation),
            ["provenanceSha256"] = publication["provenanceSha256"]!.DeepClone(),
            ["publicationSha256"] = AcceptanceDeploymentEvidenceProvider.HashJson(publication),
            ["runtimeManifestSha256"] = new string('3', 64),
            ["completedAt"] = "2026-10-29T12:00:00.000Z",
            ["target"] = new JsonObject
            {
                ["subscriptionId"] = "subscription",
                ["tenantId"] = "tenant",
                ["resourceGroup"] = "agentweaver",
                ["clusterName"] = "agentweaver-aks",
                ["namespace"] = Namespace,
                ["nodeMin"] = 2L,
                ["nodeMax"] = 3L,
            },
            ["registryImages"] = registryImages,
            ["readbacks"] = new JsonObject
            {
                ["resources"] = resources,
                ["sha256"] = AcceptanceDeploymentEvidenceProvider.HashJson(resources),
            },
        };
    }

    private static (JsonObject Manifest, JsonObject Pins, JsonArray Images, JsonObject Services) CreateReleaseReadbacks()
    {
        JsonObject manifest = new()
        {
            ["stage"] = "release",
            ["evidence"] = new JsonObject { ["sourceSha"] = SourceSha },
        };
        JsonObject pins = CreateManifest().Pins;
        JsonArray images = CreateIntegrationImages();
        JsonArray serviceRows = new();
        foreach (JsonObject image in images.OfType<JsonObject>()
            .Where(item => item["id"]?.GetValue<string>() != "Agentweaver.AgentHost"))
        {
            string name = image["name"]!.GetValue<string>();
            string id = image["id"]!.GetValue<string>();
            string reference = image["image"]!.GetValue<string>();
            string digest = reference[(reference.LastIndexOf('@') + 1)..];
            string configDigest = new string('4', 64);
            serviceRows.Add(new JsonObject
            {
                ["componentId"] = id,
                ["service"] = new JsonObject
                {
                    ["name"] = name, ["uid"] = $"service-{name}", ["resourceVersion"] = "1",
                    ["sha256"] = new string('5', 64),
                },
                ["deployment"] = new JsonObject { ["name"] = name, ["uid"] = $"deployment-{name}", ["sha256"] = new string('6', 64) },
                ["replicaSet"] = new JsonObject { ["uid"] = $"replicaset-{name}", ["sha256"] = new string('7', 64) },
                ["pods"] = new JsonArray(new JsonObject
                {
                    ["ready"] = true, ["uid"] = $"pod-{name}", ["sha256"] = new string('8', 64),
                    ["image"] = reference, ["imageID"] = reference,
                    ["registry"] = new JsonObject
                    {
                        ["manifestDigest"] = digest, ["platformDigest"] = digest,
                        ["sourceSha"] = SourceSha, ["sourceTree"] = SourceTree, ["configDigest"] = $"sha256:{configDigest}",
                    },
                }),
            });
        }
        JsonObject services = new()
        {
            ["status"] = "passed",
            ["observedAt"] = "2026-10-29T12:00:00.000Z",
            ["context"] = "approved-aks-context",
            ["namespace"] = Namespace,
            ["sourceSha"] = SourceSha,
            ["sourceTree"] = SourceTree,
            ["providerCatalog"] = new JsonObject
            {
                ["name"] = "provider-catalog-v1", ["uid"] = "catalog-uid", ["resourceVersion"] = "12",
                ["sourceCommit"] = ProviderSource, ["sha256"] = new string('9', 64),
            },
            ["services"] = serviceRows,
        };
        return (manifest, pins, images, services);
    }

    private static JsonArray CreateIntegrationImages()
    {
        JsonArray images = new();
        foreach ((string id, string name) in Services.Append(("Agentweaver.AgentHost", "agenthost")))
        {
            string digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))}";
            string configDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))}";
            string image = $"ghcr.io/sabbour/agentweaver/{name}@{digest}";
            JsonObject row = new()
            {
                ["id"] = id,
                ["name"] = name,
                ["image"] = image,
                ["component"] = new JsonObject { ["version"] = "1.0.0" },
                ["deploymentComponent"] = new JsonObject { ["imageDigest"] = digest },
                ["buildSource"] = new JsonObject { ["sourceSha"] = SourceSha, ["sourceTree"] = SourceTree },
                ["result"] = new JsonObject { ["configDigest"] = configDigest },
            };
            if (id == "Agentweaver.AgentHost")
            {
                row["sandboxImage"] = image;
                row["sandboxImageDigest"] = digest;
                row                ["compressedPullBytes"] = 1234L;
                row["result"]!["configDigest"] = configDigest;
            }
            images.Add(row);
        }
        return images;
    }

    private static async Task<string> WriteOciArchiveAsync(
        string archivePath,
        string sourceSha,
        string sourceTree,
        JsonObject? suppliedConfig = null,
        byte[]? layerContent = null,
        byte[]? layerArchiveContent = null,
        long? layerDescriptorSize = null,
        string? layerDescriptorDigest = null)
    {
        byte[] layerBytes;
        if (layerContent is not null)
        {
            layerBytes = layerContent;
        }
        else
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
                await gzip.WriteAsync(new byte[] { 1, 2, 3 });
            layerBytes = buffer.ToArray();
        }
        JsonObject config = suppliedConfig ?? new JsonObject
        {
            ["os"] = "linux",
            ["architecture"] = "amd64",
            ["config"] = new JsonObject
            {
                ["User"] = "1654:1654",
                ["Entrypoint"] = new JsonArray("dotnet", "Agentweaver.AgentHost.dll"),
                ["Labels"] = new JsonObject
                {
                    ["org.opencontainers.image.revision"] = sourceSha,
                    ["io.agentweaver.source-tree"] = sourceTree,
                    ["org.opencontainers.image.version"] = "1.0.0",
                },
            },
        };
        byte[] configBytes = Encoding.UTF8.GetBytes(config.ToJsonString());
        await File.WriteAllTextAsync(archivePath + ".config.json", config.ToJsonString());
        byte[] archivedLayer = layerArchiveContent ?? layerBytes;
        string configDigest = Digest(configBytes);
        string layerDigest = layerDescriptorDigest ?? Digest(layerBytes);
        JsonObject manifest = new()
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
            ["config"] = new JsonObject
            {
                ["mediaType"] = "application/vnd.oci.image.config.v1+json",
                ["digest"] = configDigest,
                ["size"] = configBytes.Length,
            },
            ["layers"] = new JsonArray(new JsonObject
            {
                ["mediaType"] = "application/vnd.oci.image.layer.v1.tar+gzip",
                ["digest"] = layerDigest,
                ["size"] = layerDescriptorSize ?? layerBytes.Length,
            }),
        };
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        string manifestDigest = Digest(manifestBytes);
        JsonObject index = new()
        {
            ["schemaVersion"] = 2,
            ["manifests"] = new JsonArray(new JsonObject
            {
                ["digest"] = manifestDigest,
                ["size"] = manifestBytes.Length,
            }),
        };
        using FileStream file = File.Create(archivePath);
        using TarWriter writer = new(file, TarEntryFormat.Pax, leaveOpen: false);
        await WriteTarEntryAsync(writer, "index.json", Encoding.UTF8.GetBytes(index.ToJsonString()));
        await WriteTarEntryAsync(writer, $"blobs/sha256/{manifestDigest[7..]}", manifestBytes);
        await WriteTarEntryAsync(writer, $"blobs/sha256/{configDigest[7..]}", configBytes);
        await WriteTarEntryAsync(writer, $"blobs/sha256/{layerDigest[7..]}", archivedLayer);
        return configDigest;
    }

    private static async Task WriteTarEntryAsync(TarWriter writer, string name, byte[] bytes)
    {
        PaxTarEntry entry = new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) };
        await writer.WriteEntryAsync(entry);
    }

    private static string Digest(byte[] bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private sealed record RegistryFixture(string Image, string Digest, string ConfigDigest, byte[] ManifestBytes, byte[] ConfigBytes);

    private sealed class RegistryFixtureSet
    {
        public Dictionary<string, RegistryFixture> ByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);
        public JsonArray Images { get; } = [];

        public RegistryFixture Add(
            string id,
            string name,
            string? sourceTree = null,
            string? repositoryName = null)
        {
            JsonObject config = new()
            {
                ["os"] = "linux",
                ["architecture"] = "amd64",
                ["config"] = new JsonObject
                {
                    ["Labels"] = new JsonObject
                    {
                        ["org.opencontainers.image.revision"] = SourceSha,
                        ["io.agentweaver.source-tree"] = sourceTree ?? SourceTree,
                    },
                },
            };
            byte[] configBytes = Encoding.UTF8.GetBytes(config.ToJsonString());
            string configDigest = Digest(configBytes);
            JsonObject manifest = new()
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
                ["config"] = new JsonObject
                {
                    ["mediaType"] = "application/vnd.oci.image.config.v1+json",
                    ["digest"] = configDigest,
                    ["size"] = configBytes.Length,
                },
                ["layers"] = new JsonArray(),
            };
            byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            string digest = Digest(manifestBytes);
            string repositoryPath = repositoryName ?? $"agentweaver/{name}";
            string repository = $"sabbour/{repositoryPath}";
            string image = $"ghcr.io/{repository}@{digest}";
            RegistryFixture fixture = new(image, digest, configDigest, manifestBytes, configBytes);
            ByName.Add(name, fixture);
            Bodies.Add($"/v2/{repository}/manifests/{digest}", manifestBytes);
            Bodies.Add($"/v2/{repository}/blobs/{configDigest}", configBytes);
            if (name != "agenthost")
                Images.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = name, ["image"] = image,
                    ["buildSource"] = new JsonObject { ["sourceSha"] = SourceSha, ["sourceTree"] = SourceTree },
                });
            else
                Images.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = name, ["sandboxImage"] = image,
                    ["sandboxImageDigest"] = digest, ["compressedPullBytes"] = 1234L,
                });
            return fixture;
        }
    }

    private sealed class RegistryHandler(
        RegistryFixtureSet fixtures,
        int tokenFailures = 0,
        bool redirectManifests = false,
        bool redirectToUntrustedHost = false,
        HttpMessageHandler? fallbackHandler = null) : HttpMessageHandler
    {
        private int tokenAttempts;
        private readonly HttpMessageInvoker? fallback = fallbackHandler is null
            ? null
            : new HttpMessageInvoker(fallbackHandler);
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            string? authorization = request.Headers.Authorization?.ToString();
            Requests.Add(new RecordedRequest(uri, authorization));
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
            if (uri.Host == "pkg-containers.githubusercontent.com")
            {
                string path = uri.AbsolutePath.Replace("/v2/", "/v2/", StringComparison.Ordinal);
                return Task.FromResult(fixtures.Bodies.TryGetValue(path, out byte[]? body)
                    ? JsonResponse(body)
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (fixtures.Bodies.TryGetValue(uri.AbsolutePath, out byte[]? bytes))
                return Task.FromResult(JsonResponse(bytes));
            return fallback?.SendAsync(request, cancellationToken)
                ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                fallback?.Dispose();
            base.Dispose(disposing);
        }

        private static HttpResponseMessage JsonResponse(string value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage JsonResponse(byte[] value) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    }

    private sealed record RecordedRequest(Uri Uri, string? Authorization);

    private sealed class PinnedSourceHandler(
        HttpMessageHandler innerHandler,
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
            if (path.EndsWith($"/commits/{SourceSha}", StringComparison.Ordinal))
                return Json(new JsonObject
                {
                    ["sha"] = SourceSha,
                    ["commit"] = new JsonObject
                    {
                        ["tree"] = new JsonObject { ["sha"] = SourceTree },
                    },
                });
            if (!path.StartsWith(RepositoryContents, StringComparison.Ordinal))
                return base.SendAsync(request, cancellationToken);

            string sourcePath = path[RepositoryContents.Length..];
            ContentPaths.Add(sourcePath);
            if (uri.Query != $"?ref={SourceSha}"
                || !sourceFiles.TryGetValue(sourcePath, out byte[]? content))
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

    private sealed class GitHubHandler(
        string releaseTag,
        string sourceSha,
        JsonObject publication,
        bool lightweight = false) : HttpMessageHandler
    {
        private const string SourceTagSha = "cccccccccccccccccccccccccccccccccccccccc";
        private const string ResultTagSha = "dddddddddddddddddddddddddddddddddddddddd";
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Requests.Add(uri);
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (path.EndsWith($"/releases/tags/{releaseTag}", StringComparison.Ordinal))
                return Json(new JsonObject { ["tag_name"] = releaseTag, ["draft"] = false, ["prerelease"] = false });
            if (path.EndsWith($"/git/ref/tags/{releaseTag}", StringComparison.Ordinal))
                return Json(new JsonObject
                {
                    ["object"] = new JsonObject
                    {
                        ["type"] = lightweight ? "commit" : "tag",
                        ["sha"] = lightweight ? sourceSha : SourceTagSha,
                    },
                });
            if (!lightweight && path.EndsWith($"/git/tags/{SourceTagSha}", StringComparison.Ordinal))
                return Json(new JsonObject
                {
                    ["tag"] = releaseTag,
                    ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = sourceSha },
                });
            if (path.Contains("/git/ref/tags/agentweaver-publication", StringComparison.Ordinal))
                return Json(new JsonObject { ["object"] = new JsonObject { ["type"] = "tag", ["sha"] = ResultTagSha } });
            if (path.EndsWith($"/git/tags/{ResultTagSha}", StringComparison.Ordinal))
                return Json(new JsonObject
                {
                    ["tag"] = publication.ContainsKey("continuation")
                        ? "agentweaver-publication-continuation/1.0.0/c24d0000fe5fc6163e4f38594c39dff782d3a46d/result"
                        : $"agentweaver-publication/{sourceSha}/result",
                    ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = sourceSha },
                    ["message"] = AcceptanceDeploymentEvidenceProvider.JsonText(publication),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(JsonObject value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json"),
            });
    }
}
