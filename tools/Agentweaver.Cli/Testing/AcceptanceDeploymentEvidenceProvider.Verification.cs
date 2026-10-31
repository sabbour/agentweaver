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
    private static readonly HashSet<string> IdentityServices = new(StringComparer.Ordinal)
    {
        "identity-broker", "events-and-sessions", "environment", "knowledge", "orchestrator", "projects-config",
    };

    private static string ComponentImageRepository(string componentId) =>
        componentId switch
        {
            "Agentweaver.FoundationProbe" => "agentweaver-foundation-probe",
            "Agentweaver.Web" => "agentweaver-web",
            "Agentweaver.AgentHost" => "agentweaver-agenthost",
            _ => componentId.ToLowerInvariant(),
        };

    internal static Dictionary<string, string> ValidateImageManifest(JsonObject manifest, string sourceSha)
    {
        if (manifest.Count != 1 || manifest["images"] is not JsonObject images || images.Count == 0)
            throw new InvalidDataException("The canonical image manifest must contain a nonempty images object.");

        HashSet<string> allowed = ServiceComponents.Select(component => ComponentImageRepository(component.Id))
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> selected = new(StringComparer.Ordinal);
        foreach ((string repository, JsonNode? node) in images)
        {
            string? reference = GetString(node);
            Match tag = Regex.Match(reference ?? "", "^sha-([a-f0-9]{40})$", RegexOptions.CultureInvariant);
            if (!allowed.Contains(repository)
                || !selected.TryAdd(repository, reference ?? "")
                || !(tag.Success && tag.Groups[1].Value == sourceSha || Matches(Digest, reference)))
                throw new InvalidDataException($"The canonical image manifest has an unsupported repository or source reference for {repository}.");
        }
        return selected;
    }

    private async Task<string> ResolveImageReferenceAsync(
        string repository,
        string? selectedReference,
        string currentReference,
        string sourceSha,
        CancellationToken ct)
    {
        string imageRepository = $"ghcr.io/sabbour/{repository}";
        string reference = selectedReference is null ? currentReference : "";
        if (selectedReference is not null)
        {
            if (Matches(Digest, selectedReference))
            {
                reference = $"{imageRepository}@{selectedReference}";
            }
            else
            {
                Match tag = Regex.Match(selectedReference, "^sha-([a-f0-9]{40})$", RegexOptions.CultureInvariant);
                if (!tag.Success || tag.Groups[1].Value != sourceSha)
                    throw new InvalidDataException($"The selected GHCR tag for {repository} is not bound to the exact source commit.");
                string path = $"sabbour/{repository}";
                string token = await GetRegistryPullTokenAsync(path, ct).ConfigureAwait(false);
                Uri tagUri = new($"{RegistryOrigin}/v2/{path}/manifests/{selectedReference}");
                var tagManifest = await ReadRegistryJsonAsync(tagUri, null, token, RegistryAccept, ct)
                    .ConfigureAwait(false);
                reference = $"{imageRepository}@{tagManifest.Digest}";
            }

            if (!string.Equals(currentReference, reference, StringComparison.Ordinal))
                throw new InvalidDataException($"The live Kubernetes image for {repository} does not match the selected immutable GHCR reference.");
        }

        if (!TryImageReference(reference, out string actualRepository, out _)
            || actualRepository != imageRepository)
            throw new InvalidDataException($"The live Kubernetes image for {repository} is not the exact immutable GHCR image.");
        return reference;
    }

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

    private static string ImageIdDigest(string imageId)
    {
        int separator = imageId.LastIndexOf('@');
        if (separator >= 0)
            return imageId[(separator + 1)..];
        foreach (string scheme in new[] { "containerd://", "docker://", "cri-o://" })
        {
            if (imageId.StartsWith(scheme, StringComparison.Ordinal))
                return imageId[scheme.Length..];
        }
        return imageId;
    }

    internal async Task<JsonObject> ObserveServicesEquivalentAsync(
        AcceptanceManifest manifest,
        string sourceSha,
        string sourceTree,
        IReadOnlyDictionary<string, string> selectedImages,
        CancellationToken ct)
    {
        JsonObject pins = manifest.Pins;
        string context = ApiAcceptanceSupport.GetString(pins, "context")!;
        string namespaceName = ApiAcceptanceSupport.GetString(pins, "namespace")!;
        if (!IsValidContext(context) || namespaceName != "agentweaver")
            throw new InvalidDataException("The source-bound P1 runtime target is invalid.");

        JsonObject providerPin = (JsonObject)pins["providerReceipt"]!;
        if (!Matches(Sha, ApiAcceptanceSupport.GetString(providerPin, "sourceCommit")))
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
        JsonArray serviceImages = [];
        foreach ((string componentId, string name) in ServiceComponents.Where(item =>
                     item.Id != "Agentweaver.AgentHost"))
        {
            JsonObject[] deploymentMatches = deployments.Where(item =>
                ApiAcceptanceSupport.GetString(item["metadata"] as JsonObject ?? [], "name") == name)
                .ToArray();
            if (deploymentMatches.Length != 1)
                throw new InvalidDataException($"Expected one live Deployment for {componentId}.");
            JsonObject deployment = deploymentMatches[0];
            JsonObject template = deployment["spec"]?["template"] as JsonObject ?? [];
            JsonArray containers = template["spec"]?["containers"] as JsonArray ?? [];
            if (containers.Count != 1 || containers[0] is not JsonObject container
                || ApiAcceptanceSupport.GetString(container, "name") != name
                || string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(container, "image")))
                throw new InvalidDataException($"Deployment {name} does not have the single application image.");
            string currentImage = ApiAcceptanceSupport.GetString(container, "image")!;
            string repository = ComponentImageRepository(componentId);
            string imageReference = await ResolveImageReferenceAsync(
                repository, selectedImages.GetValueOrDefault(repository), currentImage, sourceSha, ct)
                .ConfigureAwait(false);
            serviceImages.Add(new JsonObject
            {
                ["id"] = componentId,
                ["name"] = name,
                ["image"] = imageReference,
                ["buildSource"] = new JsonObject { ["sourceSha"] = sourceSha, ["sourceTree"] = sourceTree },
            });
        }

        JsonObject[] environmentMatches = deployments.Where(item =>
            ApiAcceptanceSupport.GetString(item["metadata"] as JsonObject ?? [], "name") == "environment")
            .ToArray();
        if (environmentMatches.Length != 1)
            throw new InvalidDataException("Expected one live Environment Deployment for AgentHost image settings.");
        JsonObject environmentDeployment = environmentMatches[0];
        JsonArray environmentContainers = environmentDeployment["spec"]?["template"]?["spec"]?["containers"] as JsonArray ?? [];
        if (environmentContainers.Count != 1 || environmentContainers[0] is not JsonObject environmentContainer
            || ApiAcceptanceSupport.GetString(environmentContainer, "name") != "environment")
            throw new InvalidDataException("The Environment Deployment does not have its single application container.");
        JsonArray environmentVariables = environmentContainer["env"] as JsonArray ?? [];
        string hostCurrentImage = RequireEnvironmentSetting(
            environmentVariables, "Environment__Sandbox__AgentSandbox__ContainerImage");
        string hostImageReference = await ResolveImageReferenceAsync(
            ComponentImageRepository("Agentweaver.AgentHost"),
            selectedImages.GetValueOrDefault(ComponentImageRepository("Agentweaver.AgentHost")),
            hostCurrentImage, sourceSha, ct).ConfigureAwait(false);
        if (RequireEnvironmentSetting(
                environmentVariables, "Environment__Sandbox__AgentSandbox__ContainerImagePlatform") != "linux/amd64"
            || !long.TryParse(RequireEnvironmentSetting(
                environmentVariables, "Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes"),
                NumberStyles.None, CultureInfo.InvariantCulture, out long configuredHostPullBytes)
            || configuredHostPullBytes < 1
            || configuredHostPullBytes > 9_007_199_254_740_991L)
            throw new InvalidDataException("The live Environment configuration must contain the exact linux/amd64 AgentHost image and positive compressed-byte measurement.");

        JsonObject hostRegistry = await VerifyRegistryImageAsync(
            hostImageReference, hostImageReference, sourceSha, sourceTree, ct).ConfigureAwait(false);
        long hostPullBytes = hostRegistry["compressedPullBytes"]?.GetValue<long>()
            ?? throw new InvalidDataException("GHCR did not provide AgentHost OCI descriptor sizes.");
        if (hostPullBytes != configuredHostPullBytes)
            throw new InvalidDataException("The deployed AgentHost compressed-byte setting does not match the digest-verified GHCR OCI metadata.");

        JsonArray observedServices = [];
        foreach (JsonObject image in serviceImages.OfType<JsonObject>())
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
                if (name == "environment"
                    && (RequireEnvironmentSetting(
                            applicationContainer["env"] as JsonArray ?? [],
                            "Environment__Sandbox__AgentSandbox__ContainerImage") != hostImageReference
                        || RequireEnvironmentSetting(
                            applicationContainer["env"] as JsonArray ?? [],
                            "Environment__Sandbox__AgentSandbox__ContainerImagePlatform") != "linux/amd64"
                        || !long.TryParse(RequireEnvironmentSetting(
                                applicationContainer["env"] as JsonArray ?? [],
                                "Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes"),
                            NumberStyles.None, CultureInfo.InvariantCulture, out long podHostPullBytes)
                        || podHostPullBytes != hostPullBytes))
                    throw new InvalidDataException("The running Environment Pod does not use the digest-verified AgentHost image and compressed-byte measurement.");
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
            ["agentHostImageMeasurement"] = new JsonObject
            {
                ["status"] = "passed",
                ["image"] = hostImageReference,
                ["digest"] = hostRegistry["platformDigest"]?.DeepClone(),
                ["manifestDigest"] = hostRegistry["manifestDigest"]?.DeepClone(),
                ["configDigest"] = hostRegistry["configDigest"]?.DeepClone(),
                ["platform"] = "linux/amd64",
                ["compressedPullBytes"] = hostPullBytes,
                ["measurement"] = "digest-verified platform manifest plus unique config and layer descriptor sizes",
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
        var rootManifest = await ReadRegistryJsonAsync(
            manifestUri, requestedDigest, token, RegistryAccept, ct).ConfigureAwait(false);
        byte[] manifestBytes = rootManifest.Bytes;
        JsonObject manifest = rootManifest.Value;
        string manifestDigest = requestedDigest;
        string platformDigest = requestedDigest;
        string mediaType = ApiAcceptanceSupport.GetString(manifest, "mediaType") ?? "";
        if (!ApiAcceptanceSupport.TryLong(manifest["schemaVersion"], out long schemaVersion) || schemaVersion != 2)
            throw new InvalidDataException("The immutable GHCR image descriptor is not a schema-version 2 image manifest.");
        if (mediaType is "application/vnd.oci.image.index.v1+json"
            or "application/vnd.docker.distribution.manifest.list.v2+json")
        {
            JsonObject[] matches = (manifest["manifests"] as JsonArray)?.OfType<JsonObject>()
                .Where(item => ApiAcceptanceSupport.GetString(item["platform"] as JsonObject ?? [], "os") == "linux"
                    && ApiAcceptanceSupport.GetString(item["platform"] as JsonObject ?? [], "architecture") == "amd64")
                .ToArray() ?? [];
            if (matches.Length != 1
                || !Matches(Digest, ApiAcceptanceSupport.GetString(matches[0], "digest"))
                || !ApiAcceptanceSupport.TryLong(matches[0]["size"], out long platformManifestSize)
                || platformManifestSize < 1)
                throw new InvalidDataException("The immutable image index does not contain one linux/amd64 manifest.");
            platformDigest = ApiAcceptanceSupport.GetString(matches[0], "digest")!;
            Uri platformUri = new($"{RegistryOrigin}/v2/{repository["ghcr.io/".Length..]}/manifests/{platformDigest}");
            var platformManifest = await ReadRegistryJsonAsync(platformUri, platformDigest, token, RegistryAccept, ct)
                .ConfigureAwait(false);
            manifestBytes = platformManifest.Bytes;
            manifest = platformManifest.Value;
            if (manifestBytes.LongLength != platformManifestSize)
                throw new InvalidDataException("The linux/amd64 manifest size does not match its immutable image-index descriptor.");
            mediaType = ApiAcceptanceSupport.GetString(manifest, "mediaType") ?? "";
        }
        if (mediaType is not ("application/vnd.oci.image.manifest.v1+json"
                or "application/vnd.docker.distribution.manifest.v2+json")
            || !ApiAcceptanceSupport.TryLong(manifest["schemaVersion"], out schemaVersion)
            || schemaVersion != 2)
            throw new InvalidDataException("The digest-verified linux/amd64 descriptor is not a supported image manifest.");
        string imageIdDigest = ImageIdDigest(imageId);
        if (imageIdDigest != manifestDigest && imageIdDigest != platformDigest)
            throw new InvalidDataException("The running container imageID does not match the reviewed registry manifest.");

        JsonObject configDescriptor = manifest["config"] as JsonObject ?? [];
        string configDigest = ApiAcceptanceSupport.GetString(configDescriptor, "digest") ?? "";
        if (!Matches(Digest, configDigest)
            || ApiAcceptanceSupport.GetString(configDescriptor, "mediaType") is string configMediaType
                && configMediaType is not ("application/vnd.oci.image.config.v1+json"
                    or "application/vnd.docker.container.image.v1+json")
            || !ApiAcceptanceSupport.TryLong(configDescriptor["size"], out long configSize)
            || configSize < 1)
            throw new InvalidDataException("The registry manifest has no valid immutable config descriptor.");
        Uri configUri = new($"{RegistryOrigin}/v2/{repository["ghcr.io/".Length..]}/blobs/{configDigest}");
        var configResponse = await ReadRegistryJsonAsync(configUri, configDigest, token, "application/json", ct)
            .ConfigureAwait(false);
        byte[] configBytes = configResponse.Bytes;
        JsonObject imageConfig = configResponse.Value;
        if (configBytes.LongLength != configSize)
            throw new InvalidDataException("The image config size does not match its immutable manifest descriptor.");
        JsonObject config = imageConfig["config"] as JsonObject ?? [];
        JsonObject labels = config["Labels"] as JsonObject ?? config["labels"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(imageConfig, "os") != "linux"
            || ApiAcceptanceSupport.GetString(imageConfig, "architecture") != "amd64"
            || ApiAcceptanceSupport.GetString(labels, "org.opencontainers.image.revision") != sourceSha
            || ApiAcceptanceSupport.GetString(labels, "io.agentweaver.source-tree") is string imageSourceTree
                && imageSourceTree != sourceTree)
            throw new InvalidDataException("The running image configuration is not bound to the reviewed source commit and tree.");

        JsonArray layers = manifest["layers"] as JsonArray ?? [];
        if (layers.Count == 0 || layers.Count != layers.OfType<JsonObject>().Count())
            throw new InvalidDataException("The OCI platform manifest contains no layer descriptors for a compressed-byte measurement.");
        Dictionary<string, long> blobs = new(StringComparer.Ordinal)
        {
            [configDigest] = configSize,
        };
        if (configSize > 9_007_199_254_740_991L - manifestBytes.LongLength)
            throw new InvalidDataException("The OCI compressed-byte measurement exceeds the exact integer range.");
        long compressedPullBytes = manifestBytes.LongLength + configSize;
        foreach (JsonObject descriptor in layers.OfType<JsonObject>())
        {
            string digest = ApiAcceptanceSupport.GetString(descriptor, "digest") ?? "";
            if (!Matches(Digest, digest)
                || !ApiAcceptanceSupport.TryLong(descriptor["size"], out long size)
                || size < 1
                || blobs.TryGetValue(digest, out long existingSize) && existingSize != size)
                throw new InvalidDataException("The OCI platform manifest contains an invalid or conflicting blob descriptor.");
            if (blobs.ContainsKey(digest))
                continue;
            if (size > 9_007_199_254_740_991L - compressedPullBytes)
                throw new InvalidDataException("The OCI compressed-byte measurement exceeds the exact integer range.");
            blobs.Add(digest, size);
            compressedPullBytes += size;
        }

        if (repository.EndsWith("/agentweaver-agenthost", StringComparison.Ordinal)
            && (ApiAcceptanceSupport.GetString(config, "User") != "1654:1654"
                || config["Entrypoint"] is not JsonArray { Count: 2 } entrypoint
                || GetString(entrypoint[0]) != "dotnet"
                || GetString(entrypoint[1]) != "Agentweaver.AgentHost.dll"))
            throw new InvalidDataException("The AgentHost image must use its reviewed non-root owner and executable.");
        return new JsonObject
        {
            ["manifestDigest"] = manifestDigest,
            ["platformDigest"] = platformDigest,
            ["configDigest"] = configDigest,
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["platform"] = "linux/amd64",
            ["compressedPullBytes"] = compressedPullBytes,
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

    private async Task<(byte[] Bytes, JsonObject Value, string Digest)> ReadRegistryJsonAsync(
        Uri uri,
        string? expectedDigest,
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
            bool hasResponseDigest = response.Headers.TryGetValues(
                "Docker-Content-Digest", out IEnumerable<string>? values);
            string[] responseDigests = hasResponseDigest ? values!.ToArray() : [];
            if (expectedDigest is not null && digest != expectedDigest
                || hasResponseDigest && (responseDigests.Length != 1 || responseDigests[0] != digest))
                throw new InvalidDataException("Registry response bytes do not match the immutable image descriptor.");
            JsonObject value = JsonNode.Parse(bytes) as JsonObject
                ?? throw new InvalidDataException("Registry returned malformed image metadata.");
            return (bytes, value, digest);
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

    private static string RequireEnvironmentSetting(JsonArray environment, string name)
    {
        JsonObject[] matches = environment.OfType<JsonObject>()
            .Where(variable => ApiAcceptanceSupport.GetString(variable, "name") == name)
            .ToArray();
        string? value = matches.Length == 1 ? ApiAcceptanceSupport.GetString(matches[0], "value") : null;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"The live Environment Deployment must contain one nonempty {name} setting.");
        return value;
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

}
