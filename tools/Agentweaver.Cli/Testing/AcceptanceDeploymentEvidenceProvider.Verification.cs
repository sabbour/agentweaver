using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli;

internal sealed partial class AcceptanceDeploymentEvidenceProvider
{
    private const string RegistryOrigin = "https://ghcr.io";
    private const string RegistryBlobOrigin = "https://pkg-containers.githubusercontent.com";
    private const string RegistryAccept =
        "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, " +
        "application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json";
    private const string ProviderCatalogPrefix = "ProjectsConfig__ProviderCatalog__";
    private const string ProviderCatalogSourceAnnotation = "agentweaver.io/provider-catalog-source";
    private const int MaxRegistryRedirects = 3;
    private const string PartialPublicationSourceSha = "c24d0000fe5fc6163e4f38594c39dff782d3a46d";
    private const string PartialPublicationResultRef =
        "agentweaver-publication-continuation/1.0.0/c24d0000fe5fc6163e4f38594c39dff782d3a46d/result";
    private static readonly IReadOnlyDictionary<string, BootstrapRelease> BootstrapReleases =
        new Dictionary<string, BootstrapRelease>(StringComparer.Ordinal)
        {
            ["v1.0.0"] = new(PartialPublicationSourceSha, Continuation: true),
            ["v1.0.1"] = new("56cf5a8f841dc22f69cd087d45f4e2882702dfb4", AllowLightweightTag: true),
            ["v1.0.2"] = new("3c834c12cf1e66bf385b3833a7f0a5cdd5782f8a", AllowLightweightTag: true),
            ["v1.0.3"] = new("acfa2732dba0728adfeaf5754c8e9af9118f2482", AllowLightweightTag: true),
            ["v1.0.4"] = new("7bb3ceb282c9377e49da2bc7b6d336baa4eb8209", AllowLightweightTag: true),
            ["v1.0.5"] = new("edae14b469548b8409b0eef651504691e0216a1e", AllowLightweightTag: true),
            ["v1.0.6"] = new("472601710cf1cf22d98d9cbe7492893768ce1218", AllowLightweightTag: true),
            ["v1.0.7"] = new("e9f463b345a8ec1bc879a32dd80fd509ba836867", AllowLightweightTag: true, AdditionalProductComponents: ["Agentweaver.Providers.Sessions.AgentSessions"]),
            ["v1.0.9"] = new("f21632d48f606da215cf37e70921c85c2d604f1d", AdditionalProductComponents: ["Agentweaver.Providers.Sessions.AgentSessions"]),
            ["v1.0.11"] = new("e2929bca622b5397804cdb05abed49befc86aa3d", AllowLightweightTag: true, AdditionalProductComponents: ["Agentweaver.Providers.Sessions.AgentSessions"]),
            ["v1.0.14"] = new("4105e2f2520659e7c9c636cf34972661fa26342f", AllowLightweightTag: true,
                PublicationSha256: "21dc5b63e3bab229e8e8df07cc083ce94d48c031c045af3f7ba438a93fbe5538",
                AdditionalProductComponents: ["Agentweaver.Providers.Sessions.AgentSessions"]),
            ["v1.0.15"] = new("d35c4851f591aca0f623559e1a03e3bed722f20f", AllowLightweightTag: true,
                PublicationSha256: "3be9dfd2a28616f0208a052e58c72069c102eafdef0961dc69765466e1d198f0",
                AdditionalProductComponents: ["Agentweaver.Providers.Sessions.AgentSessions"]),
        };
    private static readonly string[] BaseProductComponents =
    [
        "Agentweaver.Abstractions", "Agentweaver.Providers", "Agentweaver.SourceControl",
        "Agentweaver.Orchestrator.Core", "Agentweaver.Persistence.Postgres",
        "Agentweaver.Secrets.AzureKeyVault", "Agentweaver.Identity", "Agentweaver.AgentRuntime",
        "Agentweaver.AgentHost", "Agentweaver.Telemetry", "Agentweaver.Telemetry.AzureMonitor",
        "Agentweaver.ObjectStore.AzureBlob", "Agentweaver.Identity.Broker",
        "Agentweaver.EventsAndSessions", "Agentweaver.Gateway", "Agentweaver.Mcp",
        "Agentweaver.Web", "Agentweaver.Orchestrator", "Agentweaver.Environment",
        "Agentweaver.Providers.Storage.AzureFiles", "Agentweaver.Providers.Sandbox.AgentSandbox",
        "Agentweaver.Knowledge", "Agentweaver.Projects.Config",
    ];
    private static readonly HashSet<string> IdentityServices = new(StringComparer.Ordinal)
    {
        "identity-broker", "events-and-sessions", "environment", "knowledge", "orchestrator", "projects-config",
    };

    internal static JsonArray ValidatePublicationEquivalent(
        JsonObject publication,
        JsonObject manifest,
        string sourceSha,
        string sourceTree,
        bool integration,
        out JsonObject deploymentManifest)
    {
        JsonArray planned = publication["planned"] as JsonArray ?? [];
        JsonArray published = publication["published"] as JsonArray ?? [];
        string? stage = ApiAcceptanceSupport.GetString(manifest, "stage");
        if (!ApiAcceptanceSupport.TryLong(publication["schemaVersion"], out long schema) || schema != 1
            || ApiAcceptanceSupport.GetString(publication, "status") != "published"
            || ApiAcceptanceSupport.GetString(publication, "sourceSha") != sourceSha
            || !Matches(Sha256, ApiAcceptanceSupport.GetString(publication, "provenanceSha256"))
            || integration && (ApiAcceptanceSupport.GetString(publication, "mode") != "images-only"
                || ApiAcceptanceSupport.GetString(publication, "sourceTree") != sourceTree
                || stage != "draft" || manifest.ContainsKey("evidence"))
            || !integration && publication.ContainsKey("mode")
            || !integration && publication["requiredPackageIds"] is not JsonArray
            || !integration && publication.ContainsKey("sourceTree")
                && ApiAcceptanceSupport.GetString(publication, "sourceTree") != sourceTree
            || !Matches(Sha, sourceTree)
            || manifest["components"] is not JsonArray manifestComponents
            || planned.Count == 0 || published.Count == 0)
        {
            throw new InvalidDataException("The source-bound publication receipt is not a complete result for the exact source.");
        }

        deploymentManifest = (JsonObject)manifest.DeepClone();
        bool bootstrapDraft = !integration && stage == "draft" && !manifest.ContainsKey("evidence");
        if (!integration && !bootstrapDraft
            && (stage != "release"
                || ApiAcceptanceSupport.GetString(manifest["evidence"] as JsonObject ?? [], "sourceSha") != sourceSha))
            throw new InvalidDataException("The release publication is not bound to the exact reviewed source manifest.");

        Dictionary<string, JsonObject> components = new(StringComparer.Ordinal);
        foreach (JsonObject component in manifestComponents.OfType<JsonObject>())
        {
            string? id = ApiAcceptanceSupport.GetString(component, "id");
            if (id is null || !components.TryAdd(id, component))
                throw new InvalidDataException("The source manifest contains a missing or duplicate component identity.");
        }
        Dictionary<string, JsonObject> deploymentComponents = new(StringComparer.Ordinal);
        foreach (JsonObject component in (deploymentManifest["components"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string? id = ApiAcceptanceSupport.GetString(component, "id");
            if (id is null || !deploymentComponents.TryAdd(id, component))
                throw new InvalidDataException("The deployment manifest contains a missing or duplicate component identity.");
        }
        foreach ((string id, string _) in ServiceComponents)
        {
            if (!components.TryGetValue(id, out JsonObject? component)
                || ApiAcceptanceSupport.GetString(component, "kind") != "service"
                || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(component, "version"))
                || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(component, "project")))
                throw new InvalidDataException($"The exact source manifest is missing service component {id}.");
        }

        Dictionary<string, JsonObject> plans = UniqueRows(planned, "publication plan");
        Dictionary<string, JsonObject> results = UniqueRows(published, "published receipt");
        if (plans.Count != results.Count || plans.Keys.Any(id => !results.ContainsKey(id)))
            throw new InvalidDataException("The publication plan and result selections differ.");
        if (plans.ContainsKey("Agentweaver.FoundationProbe") || results.ContainsKey("Agentweaver.FoundationProbe"))
            throw new InvalidDataException("The acceptance-only FoundationProbe must not be part of product publication or deployment.");

        HashSet<string> productIds = components.Keys.Where(id => id != "Agentweaver.FoundationProbe")
            .ToHashSet(StringComparer.Ordinal);
        if (plans.Keys.Any(id => !productIds.Contains(id)))
            throw new InvalidDataException("The publication receipt contains a non-product component.");

        HashSet<string> packageIds = components.Values
            .Where(component => ApiAcceptanceSupport.GetString(component, "kind") != "service")
            .Select(component => ApiAcceptanceSupport.GetString(component, "id")!)
            .ToHashSet(StringComparer.Ordinal);
        JsonArray? requiredPackages = publication["requiredPackageIds"] as JsonArray;
        HashSet<string> selectedPackages = requiredPackages is null
            ? new(packageIds, StringComparer.Ordinal)
            : requiredPackages.Select(node => node?.GetValue<string>() ?? "")
                .ToHashSet(StringComparer.Ordinal);
        if (requiredPackages is not null
            && (selectedPackages.Count != requiredPackages.Count || selectedPackages.Any(id => !packageIds.Contains(id))))
            throw new InvalidDataException("The publication receipt has an invalid required package selection.");

        HashSet<string> plannedPackages = plans.Values
            .Where(plan => ApiAcceptanceSupport.GetString(plan, "kind") == "package")
            .Select(plan => ApiAcceptanceSupport.GetString(plan, "id")!)
            .ToHashSet(StringComparer.Ordinal);
        if (!integration && !plannedPackages.SetEquals(selectedPackages))
            throw new InvalidDataException("The publication plan does not match its required package selection.");

        foreach (string id in plans.Keys)
        {
            if (!components.TryGetValue(id, out JsonObject? component)
                || !results.TryGetValue(id, out JsonObject? result))
                throw new InvalidDataException($"The publication receipt does not prove component {id}.");
            JsonObject plan = plans[id];
            bool service = ApiAcceptanceSupport.GetString(component, "kind") == "service";
            string kind = service ? "image" : "package";
            string version = ApiAcceptanceSupport.GetString(component, "version") ?? "";
            if (!string.Equals(ApiAcceptanceSupport.GetString(plan, "version"), version, StringComparison.Ordinal)
                || !string.Equals(ApiAcceptanceSupport.GetString(result, "version"), version, StringComparison.Ordinal)
                || ApiAcceptanceSupport.GetString(plan, "kind") != kind
                || ApiAcceptanceSupport.GetString(result, "kind") != kind
                || !Matches(Sha256, ApiAcceptanceSupport.GetString(plan, "sha256")))
                throw new InvalidDataException($"The publication receipt does not prove the exact {id} {version} artifact.");

            if (kind == "package")
            {
                if (ApiAcceptanceSupport.GetString(result, "sha256") != ApiAcceptanceSupport.GetString(plan, "sha256")
                    || ApiAcceptanceSupport.GetString(result, "feed") != ApiAcceptanceSupport.GetString(plan, "destination"))
                    throw new InvalidDataException($"The publication receipt package hash does not match its plan for {id}.");
                continue;
            }

            bool hasPlanReuse = plan.ContainsKey("reused");
            bool hasResultReuse = result.ContainsKey("reused");
            JsonObject? reuse = plan["reused"] as JsonObject;
            if (hasPlanReuse || hasResultReuse)
            {
                if (!service || requiredPackages is null || reuse is null || result["reused"] is not JsonObject resultReuse
                    || HashJson(reuse) != HashJson(resultReuse)
                    || !Matches(Sha, ApiAcceptanceSupport.GetString(reuse, "sourceSha"))
                    || ApiAcceptanceSupport.GetString(reuse, "sourceSha") == sourceSha
                    || !Matches(Sha, ApiAcceptanceSupport.GetString(reuse, "sourceTree"))
                    || !Matches(Digest, ApiAcceptanceSupport.GetString(reuse, "inputFingerprint"))
                    || !Matches(Sha, ApiAcceptanceSupport.GetString(reuse, "receiptSourceSha")))
                    throw new InvalidDataException($"The publication receipt has invalid reused-image provenance for {id}.");
            }

            string buildSha = reuse is null ? sourceSha : ApiAcceptanceSupport.GetString(reuse, "sourceSha")!;
            string buildTree = reuse is null ? sourceTree : ApiAcceptanceSupport.GetString(reuse, "sourceTree")!;
            if (integration)
            {
                ValidateIntegrationImage(plan, result, component, id, ServiceComponents.Single(item => item.Id == id).Name,
                    version, sourceSha, sourceTree);
            }
            else
            {
                if (id == "Agentweaver.FoundationProbe")
                    throw new InvalidDataException("The acceptance-only FoundationProbe must not be part of product publication or deployment.");
                if (bootstrapDraft)
                {
                    string imageDigest = DigestFromImage(ApiAcceptanceSupport.GetString(result, "image") ?? "");
                    if (!Matches(Digest, imageDigest))
                        throw new InvalidDataException($"The release image for {id} has no immutable digest.");
                    JsonObject deploymentComponent = deploymentComponents[id];
                    deploymentComponent["imageDigest"] = imageDigest;
                }
                ValidateReleaseImage(
                    plan, result, deploymentComponents[id], id, version, buildSha, buildTree,
                    reuse is not null, requiredPackages is not null);
            }
        }

        if (integration)
        {
            if (plans.Count != ServiceComponents.Length
                || ServiceComponents.Any(service => !plans.ContainsKey(service.Id)))
                throw new InvalidDataException("The images-only publication must contain the complete P1 product image set.");
        }
        else
        {
            foreach ((string id, string _) in ServiceComponents)
            {
                if (!plans.ContainsKey(id))
                    throw new InvalidDataException($"The publication receipt must contain every product service image, including {id}.");
            }
        }

        JsonArray images = [];
        foreach ((string id, string name) in ServiceComponents)
        {
            JsonObject component = components[id];
            JsonObject result = results[id];
            JsonObject plan = plans[id];
            JsonObject? reuse = plan["reused"] as JsonObject;
            JsonObject row = new()
            {
                ["id"] = id,
                ["name"] = name,
                ["image"] = ApiAcceptanceSupport.GetString(result, "image"),
                ["component"] = component.DeepClone(),
                ["deploymentComponent"] = deploymentComponents[id].DeepClone(),
                ["result"] = result.DeepClone(),
                ["buildSource"] = new JsonObject
                {
                    ["sourceSha"] = reuse is null ? sourceSha : ApiAcceptanceSupport.GetString(reuse, "sourceSha"),
                    ["sourceTree"] = reuse is null ? sourceTree : ApiAcceptanceSupport.GetString(reuse, "sourceTree"),
                },
            };
            if (reuse is not null)
                row["reusedFrom"] = reuse.DeepClone();
            if (id == "Agentweaver.AgentHost")
            {
                string image = ApiAcceptanceSupport.GetString(result, "image")!;
                JsonObject measurement = (JsonObject)result["preparedOciMeasurement"]!.DeepClone();
                string digest = ApiAcceptanceSupport.GetString(measurement["image"] as JsonObject ?? [], "digest")!;
                row["sandboxImage"] = $"{image[..image.LastIndexOf('@')]}@{digest}";
                row["sandboxImageDigest"] = digest;
                row["compressedPullBytes"] = measurement["image"]?["compressedPullBytes"]?.DeepClone();
                row["ociArchiveSha256"] = ApiAcceptanceSupport.GetString(result, "ociArchiveSha256");
                row["preparedOciMeasurement"] = measurement;
            }
            images.Add(row);
        }
        return images;
    }

    private static Dictionary<string, JsonObject> UniqueRows(JsonArray rows, string description)
    {
        Dictionary<string, JsonObject> result = new(StringComparer.Ordinal);
        foreach (JsonObject row in rows.OfType<JsonObject>())
        {
            string? id = ApiAcceptanceSupport.GetString(row, "id");
            if (id is null || !result.TryAdd(id, row))
                throw new InvalidDataException($"The {description} has malformed or duplicate entries.");
        }
        if (result.Count != rows.Count)
            throw new InvalidDataException($"The {description} contains a non-object entry.");
        return result;
    }

    private static void ValidateReleaseImage(
        JsonObject plan,
        JsonObject result,
        JsonObject component,
        string id,
        string version,
        string buildSha,
        string buildTree,
        bool reused,
        bool currentReceipt)
    {
        string repository = $"ghcr.io/sabbour/{ComponentImageRepository(id)}";
        string digest = ApiAcceptanceSupport.GetString(component, "imageDigest") ?? "";
        string expectedImage = $"{repository}@{digest}";
        string tag = currentReceipt
            ? $"{version.Replace("+", "_", StringComparison.Ordinal)}-{buildSha}"
            : version;
        if (!Matches(Digest, digest)
            || ApiAcceptanceSupport.GetString(plan, "destination") != $"{repository}:{tag}"
            || ApiAcceptanceSupport.GetString(result, "image") != expectedImage
            || ApiAcceptanceSupport.GetString(result, "archiveSha256") != ApiAcceptanceSupport.GetString(plan, "sha256"))
            throw new InvalidDataException($"The publication receipt and release manifest disagree on the published image for {id}.");

        if (id == "Agentweaver.AgentHost")
        {
            JsonObject measurement = result["preparedOciMeasurement"] as JsonObject ?? [];
            JsonObject image = measurement["image"] as JsonObject ?? [];
            string? measuredTree = ApiAcceptanceSupport.GetString(measurement, "sourceTree");
            if (ApiAcceptanceSupport.GetString(measurement, "kind") != "agenthost-image"
                || !ApiAcceptanceSupport.TryLong(measurement["schemaVersion"], out long schema) || schema != 1
                || ApiAcceptanceSupport.GetString(measurement, "sourceSha") != buildSha
                || !Matches(Sha, measuredTree)
                || reused && measuredTree != buildTree
                || ApiAcceptanceSupport.GetString(image, "version") != version
                || ApiAcceptanceSupport.GetString(image, "platform") != "linux/amd64"
                || !Matches(Digest, ApiAcceptanceSupport.GetString(image, "digest"))
                || !Matches(Digest, ApiAcceptanceSupport.GetString(image, "configDigest"))
                || !ApiAcceptanceSupport.TryLong(image["compressedPullBytes"], out long bytes) || bytes < 1
                || ApiAcceptanceSupport.GetString(result, "imageConfigDigest") != ApiAcceptanceSupport.GetString(image, "configDigest")
                || !Matches(Sha256, ApiAcceptanceSupport.GetString(result, "ociArchiveSha256")))
                throw new InvalidDataException("The AgentHost publication receipt lacks source-bound linux/amd64 image and compressed-byte evidence.");
        }
        else if (result.ContainsKey("preparedOciMeasurement")
            || result.ContainsKey("imageConfigDigest")
            || result.ContainsKey("ociArchiveSha256"))
        {
            throw new InvalidDataException($"{id} publication receipt contains unsupported AgentHost image evidence.");
        }
    }

    private static void ValidateIntegrationImage(
        JsonObject plan,
        JsonObject result,
        JsonObject component,
        string id,
        string name,
        string version,
        string sourceSha,
        string sourceTree)
    {
        string expectedRepository = $"ghcr.io/sabbour/agentweaver/{name}";
        string plannedDigest = ApiAcceptanceSupport.GetString(plan, "sha256") ?? "";
        string image = ApiAcceptanceSupport.GetString(result, "image") ?? "";
        string imageDigest = DigestFromImage(image);
        string destination = ApiAcceptanceSupport.GetString(plan, "destination") ?? "";
        if (ApiAcceptanceSupport.GetString(plan, "kind") != "image"
            || ApiAcceptanceSupport.GetString(result, "kind") != "image"
            || ApiAcceptanceSupport.GetString(result, "archiveSha256") != plannedDigest
            || !Matches(Sha256, plannedDigest)
            || !destination.StartsWith("ghcr.io/", StringComparison.Ordinal)
            || !destination.EndsWith($":{sourceSha}", StringComparison.Ordinal)
            || destination.Contains('@')
            || !TryImageReference(image, out string repository, out _)
            || repository != expectedRepository
            || ApiAcceptanceSupport.GetString(result, "sourceSha") != sourceSha
            || ApiAcceptanceSupport.GetString(result, "sourceTree") != sourceTree
            || ApiAcceptanceSupport.GetString(result, "tag") != sourceSha
            || component.ContainsKey("imageDigest") && ApiAcceptanceSupport.GetString(component, "imageDigest") != imageDigest)
            throw new InvalidDataException($"The published immutable image for {id} does not match its planned destination or manifest pin.");

        if (id != "Agentweaver.AgentHost")
        {
            if (!Matches(Digest, ApiAcceptanceSupport.GetString(result, "configDigest")))
                throw new InvalidDataException($"The published image config digest for {id} is missing.");
            return;
        }

        JsonObject measurement = result["preparedOciMeasurement"] as JsonObject ?? [];
        JsonObject measuredImage = measurement["image"] as JsonObject ?? [];
        if (!Matches(Sha256, ApiAcceptanceSupport.GetString(result, "ociArchiveSha256"))
            || !Matches(Digest, ApiAcceptanceSupport.GetString(result, "imageConfigDigest"))
            || ApiAcceptanceSupport.GetString(result, "configDigest") != ApiAcceptanceSupport.GetString(result, "imageConfigDigest")
            || ApiAcceptanceSupport.GetString(measurement, "kind") != "agenthost-image"
            || !ApiAcceptanceSupport.TryLong(measurement["schemaVersion"], out long schema) || schema != 1
            || ApiAcceptanceSupport.GetString(measurement, "sourceSha") != sourceSha
            || ApiAcceptanceSupport.GetString(measurement, "sourceTree") != sourceTree
            || ApiAcceptanceSupport.GetString(measuredImage, "platform") != "linux/amd64"
            || ApiAcceptanceSupport.GetString(measuredImage, "version") != version
            || !Matches(Digest, ApiAcceptanceSupport.GetString(measuredImage, "digest"))
            || ApiAcceptanceSupport.GetString(measuredImage, "configDigest") != ApiAcceptanceSupport.GetString(result, "imageConfigDigest")
            || !ApiAcceptanceSupport.TryLong(measuredImage["compressedPullBytes"], out long pullBytes) || pullBytes < 1)
            throw new InvalidDataException("The published AgentHost receipt has no source-bound OCI archive measurement.");
    }

    private static string ComponentImageRepository(string componentId) =>
        componentId switch
        {
            "Agentweaver.FoundationProbe" => "agentweaver-foundation-probe",
            "Agentweaver.Web" => "agentweaver-web",
            "Agentweaver.AgentHost" => "agentweaver-agenthost",
            _ => componentId.ToLowerInvariant(),
        };

    private static bool TryImageReference(string value, out string repository, out string digest)
    {
        repository = "";
        digest = "";
        int separator = value.LastIndexOf('@');
        if (separator <= 0)
            return false;
        repository = value[..separator];
        digest = value[(separator + 1)..];
        return repository.StartsWith("ghcr.io/", StringComparison.Ordinal)
            && repository["ghcr.io/".Length..].Split('/').Length >= 2
            && Matches(Digest, digest);
    }

    private static JsonObject FindPublishedImage(JsonArray images, string id) =>
        images.OfType<JsonObject>().SingleOrDefault(image => ApiAcceptanceSupport.GetString(image, "id") == id)
        ?? throw new InvalidDataException($"The validated publication is missing {id}.");

    internal async Task<JsonObject> ObserveServicesEquivalentAsync(
        AcceptanceManifest manifest,
        string sourceSha,
        string sourceTree,
        JsonArray images,
        CancellationToken ct)
    {
        JsonObject pins = manifest.Pins;
        string context = ApiAcceptanceSupport.GetString(pins, "context")!;
        string namespaceName = ApiAcceptanceSupport.GetString(pins, "namespace")!;
        if (!IsValidContext(context) || namespaceName != "agentweaver")
            throw new InvalidDataException("The source-bound P1 runtime target is invalid.");

        JsonObject[] serviceImages = images.OfType<JsonObject>()
            .Where(image => ApiAcceptanceSupport.GetString(image, "id") != "Agentweaver.AgentHost")
            .ToArray();
        JsonObject hostImage = FindPublishedImage(images, "Agentweaver.AgentHost");
        if (serviceImages.Length != ServiceNames.Length
            || serviceImages.Select(image => ApiAcceptanceSupport.GetString(image, "name")).Distinct(StringComparer.Ordinal).Count()
                != ServiceNames.Length
            || ServiceNames.Any(name => !serviceImages.Any(image => ApiAcceptanceSupport.GetString(image, "name") == name))
            || !TryImageReference(ApiAcceptanceSupport.GetString(hostImage, "sandboxImage") ?? "", out _, out string hostDigest)
            || hostDigest != ApiAcceptanceSupport.GetString(hostImage, "sandboxImageDigest")
            || !ApiAcceptanceSupport.TryLong(hostImage["compressedPullBytes"], out long hostPullBytes)
            || hostPullBytes < 1)
            throw new InvalidDataException("The publication receipt does not contain all reviewed service and AgentHost images.");

        JsonObject providerPin = (JsonObject)pins["providerReceipt"]!;
        if (!IsValidContext(context) || !Matches(Sha, ApiAcceptanceSupport.GetString(providerPin, "sourceCommit")))
            throw new InvalidDataException("The exact provider catalog ConfigMap and source commit are required.");
        JsonObject cluster = await readKubernetesObjects(context, namespaceName, ct).ConfigureAwait(false);
        JsonArray items = cluster["items"] as JsonArray
            ?? throw new InvalidDataException("Kubernetes workload evidence has no item list.");
        JsonObject[] catalogMatches = SelectKind(items, "ConfigMap", namespaceName)
            .Where(item => ApiAcceptanceSupport.GetString(item["metadata"] as JsonObject ?? [], "name")
                == ApiAcceptanceSupport.GetString(providerPin, "configMapName"))
            .ToArray();
        if (catalogMatches.Length != 1)
            throw new InvalidDataException("The exact provider catalog ConfigMap was not observed.");
        JsonObject catalog = catalogMatches[0];
        JsonObject catalogMetadata = catalog["metadata"] as JsonObject ?? [];
        if (string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(catalogMetadata, "uid"))
            || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(catalogMetadata, "resourceVersion"))
            || ApiAcceptanceSupport.GetString(catalogMetadata, "uid") != ApiAcceptanceSupport.GetString(providerPin, "uid")
            || ApiAcceptanceSupport.GetString(catalogMetadata, "resourceVersion") != ApiAcceptanceSupport.GetString(providerPin, "resourceVersion"))
            throw new InvalidDataException("The live provider catalog ConfigMap does not match the exact pinned resource identity.");
        JsonObject providerEvidence = new()
        {
            ["name"] = ApiAcceptanceSupport.GetString(catalogMetadata, "name"),
            ["uid"] = ApiAcceptanceSupport.GetString(catalogMetadata, "uid"),
            ["resourceVersion"] = ApiAcceptanceSupport.GetString(catalogMetadata, "resourceVersion"),
            ["sourceCommit"] = ApiAcceptanceSupport.GetString(providerPin, "sourceCommit"),
            ["sha256"] = HashJson(catalog),
        };

        JsonObject[] services = SelectKind(items, "Service", namespaceName);
        JsonObject[] deployments = SelectKind(items, "Deployment", namespaceName);
        JsonObject[] replicaSets = SelectKind(items, "ReplicaSet", namespaceName);
        JsonObject[] pods = SelectKind(items, "Pod", namespaceName);
        JsonArray observedServices = [];
        foreach (JsonObject image in serviceImages)
        {
            string componentId = ApiAcceptanceSupport.GetString(image, "id")!;
            string name = ApiAcceptanceSupport.GetString(image, "name")!;
            string imageReference = ApiAcceptanceSupport.GetString(image, "image")!;
            JsonObject buildSource = (JsonObject)image["buildSource"]!;
            string buildSha = ApiAcceptanceSupport.GetString(buildSource, "sourceSha")!;
            string buildTree = ApiAcceptanceSupport.GetString(buildSource, "sourceTree")!;
            JsonObject[] serviceMatches = services.Where(item =>
                ApiAcceptanceSupport.GetString(item["metadata"] as JsonObject ?? [], "name") == name).ToArray();
            if (serviceMatches.Length != 1)
                throw new InvalidDataException($"Service {name} is missing or duplicated in the selected namespace.");
            JsonObject service = serviceMatches[0];
            JsonObject serviceMetadata = service["metadata"] as JsonObject ?? [];
            JsonObject serviceLabels = serviceMetadata["labels"] as JsonObject ?? [];
            string serviceUid = ApiAcceptanceSupport.GetString(serviceMetadata, "uid") ?? "";
            string serviceResourceVersion = ApiAcceptanceSupport.GetString(serviceMetadata, "resourceVersion") ?? "";
            if (ApiAcceptanceSupport.GetString(serviceLabels, "app.kubernetes.io/name") != name
                || ApiAcceptanceSupport.GetString(serviceLabels, "agentweaver.io/service") != name
                || string.IsNullOrWhiteSpace(serviceUid)
                || string.IsNullOrWhiteSpace(serviceResourceVersion))
                throw new InvalidDataException($"Service {name} owner labels or identity do not match.");
            JsonObject serviceEvidence = new()
            {
                ["name"] = name,
                ["uid"] = serviceUid,
                ["resourceVersion"] = serviceResourceVersion,
                ["sha256"] = HashJson(service),
            };

            JsonObject[] deploymentMatches = deployments.Where(item =>
                ApiAcceptanceSupport.GetString(item["metadata"] as JsonObject ?? [], "name") == name).ToArray();
            if (deploymentMatches.Length != 1)
                throw new InvalidDataException($"Expected one live Deployment for {componentId}.");
            JsonObject deployment = deploymentMatches[0];
            JsonObject deploymentMetadata = deployment["metadata"] as JsonObject ?? [];
            JsonObject deploymentSpec = deployment["spec"] as JsonObject ?? [];
            JsonObject template = deploymentSpec["template"] as JsonObject ?? [];
            JsonObject templateMetadata = template["metadata"] as JsonObject ?? [];
            JsonObject templateLabels = templateMetadata["labels"] as JsonObject ?? [];
            JsonObject deploymentLabels = deploymentMetadata["labels"] as JsonObject ?? [];
            JsonObject selector = deploymentSpec["selector"]?["matchLabels"] as JsonObject ?? [];
            long generation = ReadPositiveLong(deploymentMetadata["generation"], $"Deployment {name} generation");
            long desired = deploymentSpec["replicas"] is null
                ? 1
                : ReadPositiveLong(deploymentSpec["replicas"], $"Deployment {name} replicas");
            JsonObject status = deployment["status"] as JsonObject ?? [];
            JsonArray containers = RequireContainers(template["spec"] as JsonObject ?? [], name, imageReference);
            if (string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(deploymentMetadata, "uid"))
                || ReadLong(status["observedGeneration"]) != generation
                || ReadLong(status["updatedReplicas"]) != desired
                || ReadLong(status["readyReplicas"]) != desired
                || ReadLong(status["availableReplicas"]) != desired
                || ApiAcceptanceSupport.GetString(deploymentLabels, "app.kubernetes.io/name") != name
                || ApiAcceptanceSupport.GetString(deploymentLabels, "agentweaver.io/service") != name
                || ApiAcceptanceSupport.GetString(selector, "app.kubernetes.io/name") != name
                || ApiAcceptanceSupport.GetString(templateLabels, "app.kubernetes.io/name") != name
                || ApiAcceptanceSupport.GetString(templateLabels, "agentweaver.io/service") != name
                || ApiAcceptanceSupport.GetString(templateLabels, "agentweaver.io/workload") != name)
                throw new InvalidDataException($"Deployment {name} has no current, fully available rollout.");
            if (IdentityServices.Contains(name)
                && ApiAcceptanceSupport.GetString(templateLabels, "azure.workload.identity/use") != "true")
                throw new InvalidDataException($"Deployment {name} is missing its admitted workload identity label.");
            if (name == "projects-config")
            {
                JsonObject annotations = templateMetadata["annotations"] as JsonObject ?? [];
                JsonObject container = containers[0]!.AsObject();
                if (ApiAcceptanceSupport.GetString(annotations, ProviderCatalogSourceAnnotation)
                        != ApiAcceptanceSupport.GetString(providerPin, "sourceCommit")
                    || !HasExactProviderCatalogBinding(container["envFrom"] as JsonArray,
                        ApiAcceptanceSupport.GetString(providerPin, "configMapName")!)
                    || HasProviderCatalogEnvironmentOverride(container["env"] as JsonArray))
                    throw new InvalidDataException("Projects does not bind the exact observed provider catalog source and ConfigMap.");
            }

            string deploymentUid = ApiAcceptanceSupport.GetString(deploymentMetadata, "uid")!;
            JsonObject[] activeReplicaSets = replicaSets.Where(replicaSet =>
                HasControllerOwner(replicaSet, "Deployment", deploymentUid)
                && ReadLong(replicaSet["spec"]?["replicas"]) > 0).ToArray();
            if (activeReplicaSets.Length != 1)
                throw new InvalidDataException($"Deployment {name} does not have one current owner ReplicaSet.");
            JsonObject replicaSet = activeReplicaSets[0];
            JsonObject replicaSetMetadata = replicaSet["metadata"] as JsonObject ?? [];
            JsonObject replicaSetLabels = replicaSetMetadata["labels"] as JsonObject ?? [];
            string replicaSetUid = ApiAcceptanceSupport.GetString(replicaSetMetadata, "uid") ?? "";
            if (string.IsNullOrWhiteSpace(replicaSetUid)
                || !MatchesSelector(replicaSetLabels, selector)
                || ApiAcceptanceSupport.GetString(replicaSetLabels, "agentweaver.io/service") != name
                || ApiAcceptanceSupport.GetString(replicaSetLabels, "agentweaver.io/workload") != name)
                throw new InvalidDataException($"ReplicaSet for {name} does not match the current Deployment selector.");

            JsonObject[] ownedPods = pods.Where(pod =>
            {
                JsonObject metadata = pod["metadata"] as JsonObject ?? [];
                return HasControllerOwner(pod, "ReplicaSet", replicaSetUid)
                    && MatchesSelector(metadata["labels"] as JsonObject ?? [], selector);
            }).ToArray();
            if (ownedPods.Length != desired)
                throw new InvalidDataException($"Deployment {name} has an unexpected number of current owner Pods.");
            JsonArray podEvidence = [];
            foreach (JsonObject pod in ownedPods)
            {
                JsonObject podMetadata = pod["metadata"] as JsonObject ?? [];
                JsonObject podLabels = podMetadata["labels"] as JsonObject ?? [];
                JsonObject podAnnotations = podMetadata["annotations"] as JsonObject ?? [];
                JsonObject podSpec = pod["spec"] as JsonObject ?? [];
                JsonObject podStatus = pod["status"] as JsonObject ?? [];
                string podName = ApiAcceptanceSupport.GetString(podMetadata, "name") ?? "[unknown]";
                string podUid = ApiAcceptanceSupport.GetString(podMetadata, "uid") ?? "";
                JsonArray podContainers = RequireContainers(podSpec, name, imageReference);
                JsonArray containerStatuses = podStatus["containerStatuses"] as JsonArray ?? [];
                bool ready = (podStatus["conditions"] as JsonArray)?.OfType<JsonObject>().Any(condition =>
                    ApiAcceptanceSupport.GetString(condition, "type") == "Ready"
                    && ApiAcceptanceSupport.GetString(condition, "status") == "True") == true;
                if (ApiAcceptanceSupport.GetString(podStatus, "phase") != "Running"
                    || !ready || string.IsNullOrWhiteSpace(podUid)
                    || containerStatuses.Count != podContainers.Count
                    || containerStatuses.OfType<JsonObject>().Any(container =>
                        container["ready"]?.GetValue<bool>() != true
                        || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(container, "imageID")))
                    || ApiAcceptanceSupport.GetString(podLabels, "agentweaver.io/service") != name
                    || ApiAcceptanceSupport.GetString(podLabels, "agentweaver.io/workload") != name)
                    throw new InvalidDataException($"Pod {podName} is not a ready owner of {name}.");
                if (IdentityServices.Contains(name)
                    && ApiAcceptanceSupport.GetString(podLabels, "azure.workload.identity/use") != "true")
                    throw new InvalidDataException($"Pod {podName} is missing its admitted workload identity label.");
                JsonObject applicationContainer = podContainers[0]!.AsObject();
                if (name == "projects-config"
                    && (!HasExactProviderCatalogBinding(applicationContainer["envFrom"] as JsonArray,
                            ApiAcceptanceSupport.GetString(providerPin, "configMapName")!)
                        || HasProviderCatalogEnvironmentOverride(applicationContainer["env"] as JsonArray)
                        || ApiAcceptanceSupport.GetString(podAnnotations, ProviderCatalogSourceAnnotation)
                            != ApiAcceptanceSupport.GetString(providerPin, "sourceCommit")))
                    throw new InvalidDataException("The running Projects Pod does not bind the validated provider catalog source commit.");

                JsonObject containerStatus = containerStatuses[0] as JsonObject ?? [];
                string imageId = ApiAcceptanceSupport.GetString(containerStatus, "imageID")!;
                JsonObject registry = await VerifyRegistryImageAsync(
                    imageReference, imageId, buildSha, buildTree, ct).ConfigureAwait(false);
                podEvidence.Add(new JsonObject
                {
                    ["name"] = podName,
                    ["uid"] = podUid,
                    ["sha256"] = HashJson(pod),
                    ["phase"] = ApiAcceptanceSupport.GetString(podStatus, "phase"),
                    ["ready"] = true,
                    ["image"] = imageReference,
                    ["imageID"] = imageId,
                    ["nodeName"] = ApiAcceptanceSupport.GetString(podSpec, "nodeName"),
                    ["registry"] = registry,
                });
            }

            observedServices.Add(new JsonObject
            {
                ["componentId"] = componentId,
                ["service"] = serviceEvidence,
                ["deployment"] = new JsonObject
                {
                    ["name"] = name,
                    ["uid"] = deploymentUid,
                    ["sha256"] = HashJson(deployment),
                    ["generation"] = generation,
                    ["observedGeneration"] = ReadLong(status["observedGeneration"]),
                    ["desiredReplicas"] = desired,
                },
                ["replicaSet"] = new JsonObject
                {
                    ["name"] = ApiAcceptanceSupport.GetString(replicaSetMetadata, "name"),
                    ["uid"] = replicaSetUid,
                    ["sha256"] = HashJson(replicaSet),
                },
                ["pods"] = podEvidence,
            });
        }

        return new JsonObject
        {
            ["status"] = "passed",
            ["observedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["context"] = context,
            ["namespace"] = namespaceName,
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["providerCatalog"] = providerEvidence,
            ["services"] = observedServices,
            ["agentHostImageReceipt"] = new JsonObject
            {
                ["status"] = "receipt-validated",
                ["image"] = ApiAcceptanceSupport.GetString(hostImage, "sandboxImage"),
                ["digest"] = ApiAcceptanceSupport.GetString(hostImage, "sandboxImageDigest"),
                ["compressedPullBytes"] = hostPullBytes,
            },
            // API readbacks have no trusted native-owner source for current Sandbox runtime proof.
            ["agentHostRuntime"] = new JsonObject
            {
                ["status"] = "blocked",
                ["reason"] = "No exact provider-owned Sandbox lease, AgentHost Pod UID, and phase receipt was observed by the service Deployment readback.",
            },
        };
    }

    internal async Task<JsonObject> VerifyRegistryImageAsync(
        string imageReference,
        string imageId,
        string sourceSha,
        string sourceTree,
        CancellationToken ct)
    {
        if (!TryImageReference(imageReference, out string repository, out string requestedDigest)
            || repository["ghcr.io/".Length..].Split('/').Length < 2)
            throw new InvalidDataException("A reviewed immutable GHCR image reference is required.");
        string token = await GetRegistryPullTokenAsync(repository["ghcr.io/".Length..], ct).ConfigureAwait(false);
        Uri manifestUri = new($"{RegistryOrigin}/v2/{repository["ghcr.io/".Length..]}/manifests/{requestedDigest}");
        (byte[] manifestBytes, JsonObject manifest) = await ReadRegistryJsonAsync(
            manifestUri, requestedDigest, token, RegistryAccept, ct).ConfigureAwait(false);
        string manifestDigest = requestedDigest;
        string platformDigest = requestedDigest;
        string mediaType = ApiAcceptanceSupport.GetString(manifest, "mediaType") ?? "";
        if (mediaType is "application/vnd.oci.image.index.v1+json"
            or "application/vnd.docker.distribution.manifest.list.v2+json")
        {
            JsonObject[] matches = (manifest["manifests"] as JsonArray)?.OfType<JsonObject>()
                .Where(item => ApiAcceptanceSupport.GetString(item["platform"] as JsonObject ?? [], "os") == "linux"
                    && ApiAcceptanceSupport.GetString(item["platform"] as JsonObject ?? [], "architecture") == "amd64")
                .ToArray() ?? [];
            if (matches.Length != 1
                || !Matches(Digest, ApiAcceptanceSupport.GetString(matches[0], "digest")))
                throw new InvalidDataException("The immutable image index does not contain one linux/amd64 manifest.");
            platformDigest = ApiAcceptanceSupport.GetString(matches[0], "digest")!;
            Uri platformUri = new($"{RegistryOrigin}/v2/{repository["ghcr.io/".Length..]}/manifests/{platformDigest}");
            (_, manifest) = await ReadRegistryJsonAsync(platformUri, platformDigest, token, RegistryAccept, ct)
                .ConfigureAwait(false);
        }
        int imageIdSeparator = imageId.LastIndexOf('@');
        string imageIdDigest = imageIdSeparator >= 0 ? imageId[(imageIdSeparator + 1)..] : "";
        if (imageIdDigest != manifestDigest && imageIdDigest != platformDigest)
            throw new InvalidDataException("The running container imageID does not match the reviewed registry manifest.");

        JsonObject configDescriptor = manifest["config"] as JsonObject ?? [];
        string configDigest = ApiAcceptanceSupport.GetString(configDescriptor, "digest") ?? "";
        if (!Matches(Digest, configDigest))
            throw new InvalidDataException("The registry manifest has no immutable config digest.");
        Uri configUri = new($"{RegistryOrigin}/v2/{repository["ghcr.io/".Length..]}/blobs/{configDigest}");
        (_, JsonObject imageConfig) = await ReadRegistryJsonAsync(configUri, configDigest, token, "application/json", ct)
            .ConfigureAwait(false);
        JsonObject config = imageConfig["config"] as JsonObject ?? [];
        JsonObject labels = config["Labels"] as JsonObject ?? config["labels"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(imageConfig, "os") != "linux"
            || ApiAcceptanceSupport.GetString(imageConfig, "architecture") != "amd64"
            || ApiAcceptanceSupport.GetString(labels, "org.opencontainers.image.revision") != sourceSha
            || ApiAcceptanceSupport.GetString(labels, "io.agentweaver.source-tree") != sourceTree)
            throw new InvalidDataException("The running image configuration is not bound to the reviewed source commit and tree.");
        return new JsonObject
        {
            ["manifestDigest"] = manifestDigest,
            ["platformDigest"] = platformDigest,
            ["configDigest"] = configDigest,
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["platform"] = "linux/amd64",
        };
    }

    private async Task<string> GetRegistryPullTokenAsync(string repository, CancellationToken ct)
    {
        string scope = Uri.EscapeDataString($"repository:{repository}:pull");
        Uri uri = new($"{RegistryOrigin}/token?service=ghcr.io&scope={scope}");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("application/json");
                using HttpResponseMessage response = await SendRegistryRequestAsync(request, timeout.Token)
                    .ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if (IsTransient(response.StatusCode) && attempt < 3)
                    {
                        await delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new InvalidDataException($"GHCR pull-token exchange returned HTTP {(int)response.StatusCode}.");
                }

                JsonObject token;
                try
                {
                    token = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))
                        as JsonObject ?? throw new InvalidDataException();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    throw new InvalidDataException("GHCR returned malformed pull-token data.");
                }
                string? value = ApiAcceptanceSupport.GetString(token, "token")
                    ?? ApiAcceptanceSupport.GetString(token, "access_token");
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidDataException($"GHCR returned no pull token for {repository}.");
                return value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when ((error is HttpRequestException
                    || error is OperationCanceledException) && attempt < 3)
            {
                await delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            {
                throw new InvalidDataException("GHCR pull-token exchange failed after bounded retries.");
            }
        }
    }

    private async Task<(byte[] Bytes, JsonObject Value)> ReadRegistryJsonAsync(
        Uri uri,
        string expectedDigest,
        string? token,
        string accept,
        CancellationToken ct)
    {
        Uri current = uri;
        string? authorization = token;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        for (int redirects = 0; ; redirects++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd(accept);
            if (authorization is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization);
            HttpResponseMessage registryResponse;
            try
            {
                registryResponse = await SendRegistryRequestAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new InvalidDataException("Authenticated public registry read failed.");
            }
            using HttpResponseMessage response = registryResponse;
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= MaxRegistryRedirects
                    || response.Headers.Location is not Uri location)
                    throw new InvalidDataException("GHCR registry redirect limit exceeded or location is missing.");
                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                string[] allowedOrigins = current.GetLeftPart(UriPartial.Authority) == RegistryOrigin
                    ? [RegistryOrigin, RegistryBlobOrigin]
                    : [RegistryBlobOrigin];
                if (next.Scheme != Uri.UriSchemeHttps || next.UserInfo.Length != 0
                    || next.Fragment.Length != 0 || !allowedOrigins.Contains(next.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))
                    throw new InvalidDataException("GHCR redirected registry metadata to a non-allowlisted HTTPS origin.");
                if (next.GetLeftPart(UriPartial.Authority) != current.GetLeftPart(UriPartial.Authority))
                    authorization = null;
                current = next;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"Authenticated public registry read returned HTTP {(int)response.StatusCode}.");
            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new InvalidDataException("Authenticated public registry read failed.");
            }
            string digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
            if (digest != expectedDigest
                || response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? values)
                    && values.SingleOrDefault() is string contentDigest && contentDigest != expectedDigest)
                throw new InvalidDataException("Registry response bytes do not match the immutable image descriptor.");
            JsonObject value = JsonNode.Parse(bytes) as JsonObject
                ?? throw new InvalidDataException("Registry returned malformed image metadata.");
            return (bytes, value);
        }
    }

    private async Task<HttpResponseMessage> SendRegistryRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsTransient(HttpStatusCode status) =>
        status == (HttpStatusCode)429 || (int)status is >= 500 and <= 599;

    private static JsonObject[] SelectKind(JsonArray items, string kind, string namespaceName) =>
        items.OfType<JsonObject>().Where(item =>
            ApiAcceptanceSupport.GetString(item, "kind") == kind
            && ApiAcceptanceSupport.GetString(item["metadata"] as JsonObject ?? [], "namespace") == namespaceName)
            .ToArray();

    private static bool HasControllerOwner(JsonObject resource, string kind, string uid) =>
        (resource["metadata"]?["ownerReferences"] as JsonArray)?.OfType<JsonObject>().Count(reference =>
            ApiAcceptanceSupport.GetString(reference, "kind") == kind
            && ApiAcceptanceSupport.GetString(reference, "uid") == uid
            && reference["controller"]?.GetValue<bool>() == true) == 1;

    private static bool MatchesSelector(JsonObject labels, JsonObject selector) =>
        selector.Count > 0 && selector.All(pair =>
            ApiAcceptanceSupport.GetString(labels, pair.Key) == GetString(pair.Value));

    private static JsonArray RequireContainers(JsonObject spec, string serviceName, string image)
    {
        if (spec["containers"] is not JsonArray { Count: 1 } containers
            || containers[0] is not JsonObject container
            || ApiAcceptanceSupport.GetString(container, "image") != image
            || ApiAcceptanceSupport.GetString(container, "name") != serviceName)
            throw new InvalidDataException($"Deployment {serviceName} does not have the single reviewed application image.");
        return containers;
    }

    private static bool HasExactProviderCatalogBinding(JsonArray? envFrom, string configMapName)
    {
        JsonObject[] bindings = envFrom?.OfType<JsonObject>().Where(source =>
            ApiAcceptanceSupport.GetString(source, "prefix") == ProviderCatalogPrefix
            || ApiAcceptanceSupport.GetString(source["configMapRef"] as JsonObject ?? [], "name") == configMapName)
            .ToArray() ?? [];
        return bindings.Length == 1
            && ApiAcceptanceSupport.GetString(bindings[0], "prefix") == ProviderCatalogPrefix
            && ApiAcceptanceSupport.GetString(bindings[0]["configMapRef"] as JsonObject ?? [], "name") == configMapName
            && bindings[0]["configMapRef"]?["optional"]?.GetValue<bool>() == false;
    }

    private static bool HasProviderCatalogEnvironmentOverride(JsonArray? environment) =>
        environment?.OfType<JsonObject>().Any(variable =>
            ApiAcceptanceSupport.GetString(variable, "name")?.StartsWith(
                ProviderCatalogPrefix, StringComparison.OrdinalIgnoreCase) == true) == true;

    internal static JsonObject ValidateIntegrationReceiptEquivalent(
        JsonObject receipt,
        string sourceSha,
        string sourceTree,
        JsonObject manifest,
        JsonObject publication,
        AcceptanceManifest input,
        JsonArray images)
    {
        JsonObject readbacks = receipt["readbacks"] as JsonObject ?? [];
        JsonObject target = receipt["target"] as JsonObject ?? [];
        if (!ApiAcceptanceSupport.TryLong(receipt["schemaVersion"], out long schema) || schema != 1
            || ApiAcceptanceSupport.GetString(receipt, "status") != "deployed"
            || !Matches(Sha, sourceSha) || !Matches(Sha, sourceTree)
            || ApiAcceptanceSupport.GetString(receipt, "sourceSha") != sourceSha
            || ApiAcceptanceSupport.GetString(receipt, "sourceTree") != sourceTree
            || ApiAcceptanceSupport.GetString(receipt, "manifestSha256") != HashJson(manifest)
            || ApiAcceptanceSupport.GetString(receipt, "provenanceSha256")
                != ApiAcceptanceSupport.GetString(publication, "provenanceSha256")
            || ApiAcceptanceSupport.GetString(receipt, "publicationSha256") != HashJson(publication)
            || !Matches(Sha256, ApiAcceptanceSupport.GetString(receipt, "runtimeManifestSha256"))
            || !TryTimestamp(ApiAcceptanceSupport.GetString(receipt, "completedAt"), out _))
            throw new InvalidDataException("The integration deployment receipt is not bound to the exact source, manifest, and publication.");
        if (ApiAcceptanceSupport.GetString(publication, "mode") != "images-only"
            || ApiAcceptanceSupport.GetString(publication, "sourceTree") != sourceTree)
            throw new InvalidDataException("The integration deployment receipt requires the source-matched images-only publication.");

        string namespaceName = ApiAcceptanceSupport.GetString(input.Pins, "namespace")!;
        if (ApiAcceptanceSupport.GetString(target, "namespace") != namespaceName
            || !HasText(target, "subscriptionId")
            || !HasText(target, "tenantId")
            || !HasText(target, "resourceGroup")
            || !HasText(target, "clusterName")
            || !ApiAcceptanceSupport.TryLong(target["nodeMin"], out long nodeMin) || nodeMin < 1
            || !ApiAcceptanceSupport.TryLong(target["nodeMax"], out long nodeMax) || nodeMax < nodeMin)
            throw new InvalidDataException("The integration deployment receipt does not name the exact P1 namespace and deployment target.");

        JsonArray registryImages = receipt["registryImages"] as JsonArray
            ?? throw new InvalidDataException("The integration deployment receipt does not contain the complete P1 image set.");
        if (images.Count != ServiceComponents.Length || registryImages.Count != ServiceComponents.Length)
            throw new InvalidDataException("The integration deployment receipt does not contain the complete P1 image set.");
        Dictionary<string, JsonObject> registryById = UniqueRows(registryImages, "integration registry image receipt");
        foreach (JsonObject expected in images.OfType<JsonObject>())
        {
            string id = ApiAcceptanceSupport.GetString(expected, "id")!;
            JsonObject component = (JsonObject)expected["component"]!;
            JsonObject result = (JsonObject)expected["result"]!;
            JsonObject? registry = registryById.GetValueOrDefault(id);
            string expectedReference = id == "Agentweaver.AgentHost"
                ? ApiAcceptanceSupport.GetString(expected, "sandboxImage")!
                : ApiAcceptanceSupport.GetString(expected, "image")!;
            if (registry is null
                || ApiAcceptanceSupport.GetString(registry, "image") != expectedReference
                || ApiAcceptanceSupport.GetString(registry, "configDigest") != ApiAcceptanceSupport.GetString(result, "configDigest")
                || ApiAcceptanceSupport.GetString(registry, "sourceSha") != sourceSha
                || ApiAcceptanceSupport.GetString(registry, "sourceTree") != sourceTree
                || ApiAcceptanceSupport.GetString(registry, "version") != ApiAcceptanceSupport.GetString(component, "version"))
                throw new InvalidDataException($"The integration deployment receipt image for {id} differs from its publication receipt.");
        }

        if (readbacks["resources"] is not JsonArray resources
            || resources.Count < ServiceNames.Length * 3
            || !Matches(Sha256, ApiAcceptanceSupport.GetString(readbacks, "sha256"))
            || readbacks.ContainsKey("brokerRunBinding")
                && readbacks["brokerRunBinding"] is not JsonObject)
            throw new InvalidDataException("The integration deployment receipt has no complete resource readback set.");
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (JsonObject resource in resources.OfType<JsonObject>())
        {
            string kind = ApiAcceptanceSupport.GetString(resource, "kind") ?? "";
            string name = ApiAcceptanceSupport.GetString(resource, "name") ?? "";
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name)
                || !Matches(Sha256, ApiAcceptanceSupport.GetString(resource, "sha256")))
                throw new InvalidDataException("The integration deployment receipt contains an invalid resource readback.");
            if (!identities.Add($"{kind}\0{name}"))
                throw new InvalidDataException("The integration deployment receipt contains duplicate resource readbacks.");
        }
        if (identities.Count != resources.Count)
            throw new InvalidDataException("The integration deployment receipt contains a non-object resource readback.");
        foreach (string name in ServiceNames)
        foreach (string kind in new[] { "deployment", "service", "pods" })
        {
            if (!identities.Contains($"{kind}\0{name}"))
                throw new InvalidDataException($"The integration deployment receipt is missing the {kind} readback for {name}.");
        }

        JsonNode hashInput = readbacks.ContainsKey("brokerRunBinding")
            ? new JsonObject
            {
                ["resources"] = resources.DeepClone(),
                ["brokerRunBinding"] = readbacks["brokerRunBinding"]!.DeepClone(),
            }
            : resources;
        if (HashJson(hashInput) != ApiAcceptanceSupport.GetString(readbacks, "sha256"))
            throw new InvalidDataException("The integration deployment receipt readback digest does not match its resource readbacks.");

        return new JsonObject
        {
            ["status"] = ApiAcceptanceSupport.GetString(receipt, "status"),
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["manifestSha256"] = ApiAcceptanceSupport.GetString(receipt, "manifestSha256"),
            ["publicationSha256"] = ApiAcceptanceSupport.GetString(receipt, "publicationSha256"),
            ["readbackSha256"] = ApiAcceptanceSupport.GetString(readbacks, "sha256"),
            ["completedAt"] = ApiAcceptanceSupport.GetString(receipt, "completedAt"),
            ["target"] = new JsonObject
            {
                ["clusterName"] = ApiAcceptanceSupport.GetString(target, "clusterName"),
                ["namespace"] = namespaceName,
            },
        };
    }

    internal static JsonObject CreateReleaseDeploymentEvidenceEquivalent(
        string sourceSha,
        string sourceTree,
        JsonObject manifest,
        JsonObject deploymentManifest,
        JsonObject publication,
        JsonObject pins,
        JsonArray images,
        JsonObject services)
    {
        string context = ApiAcceptanceSupport.GetString(pins, "context")!;
        string namespaceName = ApiAcceptanceSupport.GetString(pins, "namespace")!;
        bool bootstrapDraft = ApiAcceptanceSupport.GetString(manifest, "stage") == "draft"
            && !manifest.ContainsKey("evidence")
            && !ReferenceEquals(manifest, deploymentManifest)
            && ApiAcceptanceSupport.GetString(deploymentManifest, "stage") == "draft"
            && !deploymentManifest.ContainsKey("evidence");
        if (!IsValidContext(context) || namespaceName != "agentweaver"
            || !Matches(Sha, sourceSha) || !Matches(Sha, sourceTree)
            || ApiAcceptanceSupport.GetString(publication, "sourceSha") != sourceSha
            || publication.ContainsKey("mode")
            || !bootstrapDraft && (ApiAcceptanceSupport.GetString(manifest, "stage") != "release"
                || ApiAcceptanceSupport.GetString(manifest["evidence"] as JsonObject ?? [], "sourceSha") != sourceSha
                || !JsonNode.DeepEquals(manifest, deploymentManifest))
            || services["status"]?.GetValue<string>() != "passed"
            || ApiAcceptanceSupport.GetString(services, "context") != context
            || ApiAcceptanceSupport.GetString(services, "namespace") != namespaceName
            || ApiAcceptanceSupport.GetString(services, "sourceSha") != sourceSha
            || ApiAcceptanceSupport.GetString(services, "sourceTree") != sourceTree
            || !TryTimestamp(ApiAcceptanceSupport.GetString(services, "observedAt"), out _)
            || services["services"] is not JsonArray serviceRows
            || serviceRows.Count != ServiceNames.Length
            || images.Count != ServiceComponents.Length)
            throw new InvalidDataException("The release deployment readbacks are not bound to the exact source and target.");

        Dictionary<string, JsonObject> expectedImages = images.OfType<JsonObject>()
            .Where(image => ApiAcceptanceSupport.GetString(image, "id") != "Agentweaver.AgentHost")
            .ToDictionary(image => ApiAcceptanceSupport.GetString(image, "id")!, StringComparer.Ordinal);
        Dictionary<string, JsonObject> observed = new(StringComparer.Ordinal);
        foreach (JsonObject service in serviceRows.OfType<JsonObject>())
        {
            string? id = ApiAcceptanceSupport.GetString(service, "componentId");
            if (id is null || !observed.TryAdd(id, service))
                throw new InvalidDataException("The release deployment readbacks contain a missing or duplicate service identity.");
        }
        if (expectedImages.Count != ServiceNames.Length || observed.Count != ServiceNames.Length
            || expectedImages.Keys.Any(id => !observed.ContainsKey(id)))
            throw new InvalidDataException("The release deployment readbacks do not contain all nine published services.");

        foreach ((string id, JsonObject image) in expectedImages)
        {
            JsonObject readback = observed[id];
            JsonObject component = (JsonObject)image["component"]!;
            JsonObject deploymentComponent = (JsonObject)image["deploymentComponent"]!;
            string expectedImage = ApiAcceptanceSupport.GetString(image, "image")!;
            string expectedDigest = DigestFromImage(expectedImage);
            JsonObject? reuse = image["reusedFrom"] as JsonObject;
            string expectedSourceSha = ApiAcceptanceSupport.GetString(reuse ?? (JsonObject)image["buildSource"]!, "sourceSha")!;
            string expectedSourceTree = ApiAcceptanceSupport.GetString(reuse ?? (JsonObject)image["buildSource"]!, "sourceTree")!;
            JsonObject? serviceReadback = readback["service"] as JsonObject;
            JsonObject? deploymentReadback = readback["deployment"] as JsonObject;
            JsonObject? replicaSetReadback = readback["replicaSet"] as JsonObject;
            JsonArray? podReadbacks = readback["pods"] as JsonArray;
            if (ApiAcceptanceSupport.GetString(serviceReadback ?? [], "name") != ApiAcceptanceSupport.GetString(image, "name")
                || !HasText(serviceReadback ?? [], "uid")
                || !HasText(serviceReadback ?? [], "resourceVersion")
                || !Matches(Sha256, ApiAcceptanceSupport.GetString(serviceReadback ?? [], "sha256"))
                || !HasText(deploymentReadback ?? [], "uid")
                || ApiAcceptanceSupport.GetString(deploymentReadback ?? [], "name") != ApiAcceptanceSupport.GetString(image, "name")
                || !Matches(Sha256, ApiAcceptanceSupport.GetString(deploymentReadback ?? [], "sha256"))
                || !HasText(replicaSetReadback ?? [], "uid")
                || !Matches(Sha256, ApiAcceptanceSupport.GetString(replicaSetReadback ?? [], "sha256"))
                || podReadbacks is null || podReadbacks.Count == 0)
                throw new InvalidDataException($"The release deployment readback for {id} is incomplete.");
            if (ApiAcceptanceSupport.GetString(deploymentComponent, "imageDigest") != expectedDigest
                && !bootstrapDraft)
                throw new InvalidDataException($"The release image for {id} differs from its exact manifest pin.");
            _ = component;

            foreach (JsonObject pod in podReadbacks.OfType<JsonObject>())
            {
                string podImageId = ApiAcceptanceSupport.GetString(pod, "imageID") ?? "";
                JsonObject registry = pod["registry"] as JsonObject ?? [];
                string podDigest = DigestFromImage(podImageId);
                string manifestDigest = ApiAcceptanceSupport.GetString(registry, "manifestDigest") ?? "";
                string platformDigest = ApiAcceptanceSupport.GetString(registry, "platformDigest") ?? "";
                if (pod["ready"]?.GetValue<bool>() != true
                    || !HasText(pod, "uid")
                    || !Matches(Sha256, ApiAcceptanceSupport.GetString(pod, "sha256"))
                    || ApiAcceptanceSupport.GetString(pod, "image") != expectedImage
                    || manifestDigest != expectedDigest
                    || !Matches(Digest, platformDigest)
                    || podDigest != manifestDigest && podDigest != platformDigest
                    || ApiAcceptanceSupport.GetString(registry, "sourceSha") != expectedSourceSha
                    || ApiAcceptanceSupport.GetString(registry, "sourceTree") != expectedSourceTree
                    || !Matches(Digest, ApiAcceptanceSupport.GetString(registry, "configDigest")))
                    throw new InvalidDataException($"The release deployment image readback for {id} differs from its source publication.");
            }
        }

        JsonObject readbacks = new()
        {
            ["context"] = context,
            ["namespace"] = namespaceName,
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["providerCatalog"] = services["providerCatalog"]?.DeepClone(),
            ["services"] = serviceRows.DeepClone(),
        };
        JsonObject providerPin = (JsonObject)pins["providerReceipt"]!;
        JsonObject provider = readbacks["providerCatalog"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(provider, "name") != ApiAcceptanceSupport.GetString(providerPin, "configMapName")
            || ApiAcceptanceSupport.GetString(provider, "sourceCommit") != ApiAcceptanceSupport.GetString(providerPin, "sourceCommit")
            || !HasText(provider, "uid") || !HasText(provider, "resourceVersion")
            || !Matches(Sha256, ApiAcceptanceSupport.GetString(provider, "sha256")))
            throw new InvalidDataException("The release deployment readbacks do not bind the provider catalog resource.");

        string completedAt = ApiAcceptanceSupport.GetString(services, "observedAt")!;
        return new JsonObject
        {
            ["status"] = "deployed",
            ["resultType"] = bootstrapDraft ? "pre-acceptance" : "release",
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["manifestSha256"] = HashJson(manifest),
            ["deploymentManifestSha256"] = HashJson(deploymentManifest),
            ["publicationSha256"] = HashJson(publication),
            ["readbacks"] = readbacks,
            ["readbackSha256"] = HashJson(readbacks),
            ["completedAt"] = completedAt,
            ["target"] = new JsonObject { ["context"] = context, ["namespace"] = namespaceName },
        };
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);

    private static bool HasText(JsonObject value, string property) =>
        !string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(value, property));

    internal static string JsonText(JsonNode value) => ApiAcceptanceSupport.JavaScriptJson(value);

    private static bool Matches(Regex pattern, string? value) =>
        value is not null && pattern.IsMatch(value);

    private static string? GetString(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out string? text) ? text : null;

    private static bool IsValidContext(string context) =>
        Regex.IsMatch(context, "^[A-Za-z0-9._:/@-]{1,128}$", RegexOptions.CultureInvariant);

    private static long ReadLong(JsonNode? value) =>
        ApiAcceptanceSupport.TryLong(value, out long result) ? result : long.MinValue;

    private static long ReadPositiveLong(JsonNode? value, string description)
    {
        long result = ReadLong(value);
        if (result < 1)
            throw new InvalidDataException($"{description} is missing or invalid.");
        return result;
    }

    private sealed record BootstrapRelease(
        string SourceSha,
        bool Continuation = false,
        bool AllowLightweightTag = false,
        string? PublicationSha256 = null,
        string[]? AdditionalProductComponents = null);
}
