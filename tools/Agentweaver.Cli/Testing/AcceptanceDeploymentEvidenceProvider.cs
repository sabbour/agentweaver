using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli;

internal sealed partial class AcceptanceDeploymentEvidenceProvider : IAcceptanceDeploymentEvidenceProvider
{
    private const string Repository = "sabbour/agentweaver";
    private const string FoundationPath = "releases/foundation.json";
    private static readonly string[] ServiceNames =
    [
        "identity-broker", "events-and-sessions", "gateway", "mcp", "web",
        "orchestrator", "environment", "knowledge", "projects-config",
    ];
    private static readonly (string Id, string Name)[] ServiceComponents =
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
        ("Agentweaver.AgentHost", "agenthost"),
    ];
    private static readonly Regex Sha = new("^[a-f0-9]{40}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256 = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Digest = new("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SemanticVersion = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ProjectReference = new(
        "<ProjectReference\\b[^>]*\\bInclude\\s*=\\s*([\"'])(.*?)\\1[^>]*\\/?>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient httpClient;
    private readonly Func<string, string, CancellationToken, Task<JsonObject>> readKubernetesObjects;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<(string SourceSha, string SourceTree)> readBuildIdentity;

    public AcceptanceDeploymentEvidenceProvider(HttpClient httpClient)
        : this(httpClient, ReadKubernetesObjectsAsync)
    {
    }

    internal AcceptanceDeploymentEvidenceProvider(
        HttpClient httpClient,
        Func<string, string, CancellationToken, Task<JsonObject>> readKubernetesObjects,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<(string SourceSha, string SourceTree)>? readBuildIdentity = null)
    {
        this.httpClient = httpClient;
        this.readKubernetesObjects = readKubernetesObjects;
        this.delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        this.readBuildIdentity = readBuildIdentity ?? ReadCliBuildIdentity;
    }

    public async Task<JsonObject> PrepareAsync(AcceptanceManifest manifest, CancellationToken ct)
    {
        JsonObject pins = manifest.Pins;
        JsonObject deployment = (JsonObject)manifest.Root["deploymentEvidence"]!;
        JsonObject sourcePin = (JsonObject)pins["sourceReceipt"]!;
        string sourceSha = ApiAcceptanceSupport.GetString(sourcePin, "sourceSha")!;
        string sourceTree = ApiAcceptanceSupport.GetString(sourcePin, "sourceTree")!;
        if (!Sha.IsMatch(sourceSha) || !Sha.IsMatch(sourceTree))
            throw new InvalidDataException("A full GitHub source commit and tree are required.");

        string manifestFile = ResolveProofPath(manifest, deployment, "manifestPath");
        string publicationFile = ResolveProofPath(manifest, deployment, "publicationPath");
        string hostArchive = ResolveProofPath(manifest, deployment, "agentHostArchivePath");
        string? integrationFile = deployment.ContainsKey("integrationDeploymentReceiptPath")
            ? ResolveProofPath(manifest, deployment, "integrationDeploymentReceiptPath")
            : null;
        JsonObject localManifest = await ReadObjectAsync(manifestFile, "The exact source manifest is missing or malformed.", ct)
            .ConfigureAwait(false);
        JsonObject publication = await ReadObjectAsync(publicationFile,
            "The exact source-bound publication, required deployment receipt, or manifest is missing or malformed.", ct)
            .ConfigureAwait(false);
        bool integration = ApiAcceptanceSupport.GetString(publication, "mode") == "images-only";
        string? releaseTag = ApiAcceptanceSupport.GetString(deployment, "releaseTag");
        if (integration && (releaseTag is not null || integrationFile is null)
            || !integration && (string.IsNullOrWhiteSpace(releaseTag) || integrationFile is not null))
            throw new InvalidDataException("Release and integration publication evidence must use their exact supported receipt mode.");

        if (integration)
        {
            (string buildSha, string buildTree) = readBuildIdentity();
            if (!Sha.IsMatch(buildSha) || !Sha.IsMatch(buildTree)
                || sourceSha != buildSha || sourceTree != buildTree)
                throw new InvalidDataException("The selected source commit and tree must exactly match the CLI embedded build identity.");
        }

        // The pinned Git commit and tree are the trust root for all local publication and runtime receipts below.
        JsonObject commit = await GetGitHubObjectAsync($"/commits/{sourceSha}", ct).ConfigureAwait(false);
        JsonObject commitTree = (commit["commit"] as JsonObject)?["tree"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(commit, "sha") != sourceSha
            || ApiAcceptanceSupport.GetString(commitTree, "sha") != sourceTree)
        {
            throw new InvalidDataException("GitHub does not report the exact pinned source commit and tree.");
        }

        byte[] remoteManifestBytes = await GetGitHubFileAsync(sourceSha, FoundationPath, ct).ConfigureAwait(false);
        string remoteManifestHash = GitBlobSha(remoteManifestBytes);
        JsonObject remoteManifest = JsonNode.Parse(remoteManifestBytes) as JsonObject
            ?? throw new InvalidDataException("The pinned GitHub foundation manifest is malformed.");
        if (JsonText(remoteManifest) != JsonText(localManifest)
            || !ValidateFoundationManifest(remoteManifest, sourceSha))
        {
            throw new InvalidDataException("The supplied foundation manifest does not match the fixed GitHub source identity.");
        }
        await ValidateProjectMetadataAsync(remoteManifest, sourceSha, ct).ConfigureAwait(false);

        if (!integration)
        {
            bool bootstrapDraft = ApiAcceptanceSupport.GetString(remoteManifest, "stage") == "draft";
            await VerifyReleasePublicationAsync(
                releaseTag!, sourceSha, publication, bootstrapDraft, publicationFile, ct)
                .ConfigureAwait(false);
            if (bootstrapDraft)
                ValidateBootstrapProductComponents(remoteManifest, releaseTag!);
        }
        JsonArray images = ValidatePublicationEquivalent(
            publication, remoteManifest, sourceSha, sourceTree, integration, out JsonObject deploymentManifest);

        JsonObject imagePin = (JsonObject)pins["imageReceipt"]!;
        JsonObject hostPublished = FindPublished(publication, "Agentweaver.AgentHost");
        string hostImage = ApiAcceptanceSupport.GetString(hostPublished, "image")!;
        if (ApiAcceptanceSupport.GetString(imagePin, "registryImage") != hostImage
            || ApiAcceptanceSupport.GetString(imagePin, "registryDigest") != DigestFromImage(hostImage)
            || ApiAcceptanceSupport.GetString(hostPublished, "ociArchiveSha256") != ApiAcceptanceSupport.GetString(imagePin, "archiveSha256")
            || ApiAcceptanceSupport.GetString(hostPublished, "manifestDigest") is string manifestDigest
                && manifestDigest != ApiAcceptanceSupport.GetString(imagePin, "manifestDigest")
            || ApiAcceptanceSupport.GetString(hostPublished, "imageConfigDigest") != ApiAcceptanceSupport.GetString(imagePin, "configDigest")
            || !ApiAcceptanceSupport.TryLong(hostPublished["preparedOciMeasurement"]?["image"]?["compressedPullBytes"], out long pullBytes)
            || ApiAcceptanceSupport.TryLong(imagePin["compressedPullBytes"], out long pinnedBytes) && pinnedBytes != pullBytes)
        {
            throw new InvalidDataException("The AgentHost image receipt does not match the exact source publication.");
        }

        string archiveSha256;
        try
        {
            archiveSha256 = await OciArchiveMeasurement.HashFileAsync(hostArchive, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("The exact AgentHost OCI archive could not be read for its pinned SHA-256.");
        }
        if (archiveSha256 != ApiAcceptanceSupport.GetString(imagePin, "archiveSha256"))
            throw new InvalidDataException("The measured AgentHost OCI archive does not match the exact source-bound publication receipt.");

        JsonObject hostImageRow = FindPublishedImage(images, "Agentweaver.AgentHost");
        JsonObject buildSource = (JsonObject)hostImageRow["buildSource"]!;
        JsonObject measuredHost = await OciArchiveMeasurement.MeasureAsync(
            hostArchive,
            ApiAcceptanceSupport.GetString(buildSource, "sourceSha")!,
            ApiAcceptanceSupport.GetString(buildSource, "sourceTree")!,
            ct).ConfigureAwait(false);
        JsonObject publishedMeasurement = FindPublishedImage(images, "Agentweaver.AgentHost")["preparedOciMeasurement"] as JsonObject
            ?? throw new InvalidDataException("The published AgentHost image receipt has no source-bound OCI measurement.");
        JsonObject measuredImage = measuredHost["image"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(measuredHost, "kind") != "agenthost-image"
            || ApiAcceptanceSupport.GetString(measuredHost, "sourceSha") != ApiAcceptanceSupport.GetString(publishedMeasurement, "sourceSha")
            || ApiAcceptanceSupport.GetString(measuredHost, "sourceTree") != ApiAcceptanceSupport.GetString(publishedMeasurement, "sourceTree")
            || ApiAcceptanceSupport.GetString(measuredImage, "digest") != ApiAcceptanceSupport.GetString(imagePin, "manifestDigest")
            || ApiAcceptanceSupport.GetString(measuredImage, "digest")
                != ApiAcceptanceSupport.GetString(publishedMeasurement["image"] as JsonObject ?? [], "digest")
            || ApiAcceptanceSupport.GetString(measuredImage, "configDigest") != ApiAcceptanceSupport.GetString(imagePin, "configDigest")
            || ApiAcceptanceSupport.GetString(hostPublished, "imageConfigDigest") != ApiAcceptanceSupport.GetString(measuredImage, "configDigest")
            || !ApiAcceptanceSupport.TryLong(measuredImage["compressedPullBytes"], out long measuredPullBytes)
            || measuredPullBytes != pullBytes
            || !ApiAcceptanceSupport.TryLong(imagePin["compressedPullBytes"], out long pinnedPullBytes)
            || measuredPullBytes != pinnedPullBytes
            || !JsonNode.DeepEquals(measuredImage, publishedMeasurement["image"])
            || archiveSha256 != ApiAcceptanceSupport.GetString(hostPublished, "ociArchiveSha256"))
            throw new InvalidDataException("The measured AgentHost OCI archive does not match the exact source-bound publication receipt.");

        JsonObject services = await ObserveServicesEquivalentAsync(
            manifest, sourceSha, sourceTree, images, ct).ConfigureAwait(false);
        JsonObject? integrationReceipt = integrationFile is null
            ? null
            : await ReadObjectAsync(integrationFile, "The integration deployment receipt is missing or malformed.", ct)
                .ConfigureAwait(false);
        JsonObject deploymentEvidence = integration
            ? ValidateIntegrationReceiptEquivalent(integrationReceipt!, sourceSha, sourceTree, remoteManifest, publication, manifest, images)
            : CreateReleaseDeploymentEvidenceEquivalent(
                sourceSha, sourceTree, remoteManifest, deploymentManifest, publication, pins, images, services);

        return new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["repository"] = Repository,
                ["path"] = FoundationPath,
                ["sourceSha"] = sourceSha,
                ["sourceTree"] = sourceTree,
                ["manifestBlobSha"] = remoteManifestHash,
            },
            [integration ? "integrationDeployment" : "releaseDeployment"] = deploymentEvidence,
            ["providerCatalog"] = services["providerCatalog"]?.DeepClone(),
            ["services"] = services,
            ["agentHostImageMeasurement"] = new JsonObject
            {
                ["status"] = "passed",
                ["kind"] = measuredHost["kind"]!.DeepClone(),
                ["schemaVersion"] = measuredHost["schemaVersion"]!.DeepClone(),
                ["sourceSha"] = measuredHost["sourceSha"]!.DeepClone(),
                ["sourceTree"] = measuredHost["sourceTree"]!.DeepClone(),
                ["image"] = measuredHost["image"]!.DeepClone(),
                ["measurement"] = measuredHost["measurement"]!.DeepClone(),
                ["registryImage"] = hostImage,
                ["registryDigest"] = ApiAcceptanceSupport.GetString(imagePin, "registryDigest"),
                ["archiveSha256"] = archiveSha256,
                ["compressedPullBytes"] = measuredHost["image"]?["compressedPullBytes"]?.DeepClone(),
                ["hardByteCeiling"] = null,
            },
            ["agentHostRuntime"] = services["agentHostRuntime"]?.DeepClone(),
        };
    }

    private async Task<JsonObject> GetGitHubObjectAsync(string route, CancellationToken ct)
    {
        using HttpRequestMessage request = GitHubRequest(route);
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("GitHub could not provide the exact pinned source evidence.");
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("GitHub returned malformed source evidence.");
        }
    }

    private async Task<byte[]> GetGitHubFileAsync(string sourceSha, string path, CancellationToken ct)
    {
        string escapedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using HttpRequestMessage request = GitHubRequest($"/contents/{escapedPath}?ref={Uri.EscapeDataString(sourceSha)}");
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("The fixed foundation manifest was not found at the exact GitHub source commit.");
        try
        {
            JsonObject envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException();
            if (ApiAcceptanceSupport.GetString(envelope, "path") != path
                || ApiAcceptanceSupport.GetString(envelope, "encoding") != "base64")
                throw new InvalidDataException();
            return Convert.FromBase64String(ApiAcceptanceSupport.GetString(envelope, "content")!.Replace("\n", "", StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("GitHub returned malformed foundation manifest content.");
        }
    }

    private static HttpRequestMessage GitHubRequest(string route)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri($"https://api.github.com/repos/{Repository}{route}"));
        request.Headers.UserAgent.ParseAdd("Agentweaver-CLI/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    internal async Task VerifyReleasePublicationAsync(
        string tag,
        string sourceSha,
        JsonObject publication,
        bool bootstrapDraft,
        string publicationFile,
        CancellationToken ct)
    {
        BootstrapRelease? bootstrap = null;
        if (bootstrapDraft)
        {
            if (!BootstrapReleases.TryGetValue(tag, out bootstrap) || bootstrap.SourceSha != sourceSha)
                throw new InvalidDataException($"The pre-acceptance bootstrap requires the exact published {tag} source.");
            bool hasContinuation = publication.ContainsKey("continuation");
            if (ApiAcceptanceSupport.GetString(publication, "sourceSha") != sourceSha
                || ApiAcceptanceSupport.GetString(publication, "status") != "published"
                || bootstrap.Continuation && publication["continuation"] is not JsonObject
                || !bootstrap.Continuation && hasContinuation)
                throw new InvalidDataException("The bootstrap publication receipt does not match the exact release mode and source.");
            if (bootstrap.PublicationSha256 is string expectedHash
                && await OciArchiveMeasurement.HashFileAsync(publicationFile, ct).ConfigureAwait(false) != expectedHash)
                throw new InvalidDataException($"{tag} publication.json does not match its pinned SHA-256.");
        }
        else if (publication.ContainsKey("continuation"))
        {
            throw new InvalidDataException("A continuation publication receipt is only valid for its pinned bootstrap source.");
        }

        JsonObject release = await GetGitHubObjectAsync($"/releases/tags/{Uri.EscapeDataString(tag)}", ct).ConfigureAwait(false);
        if (ApiAcceptanceSupport.GetString(release, "tag_name") != tag
            || release["draft"]?.GetValue<bool>() != false
            || release["prerelease"]?.GetValue<bool>() != false)
            throw new InvalidDataException("The selected source tag must identify a published, non-prerelease GitHub Release.");
        JsonObject reference = await GetGitHubObjectAsync($"/git/ref/tags/{Uri.EscapeDataString(tag)}", ct).ConfigureAwait(false);
        JsonObject tagObject = reference["object"] as JsonObject ?? [];
        string? tagType = ApiAcceptanceSupport.GetString(tagObject, "type");
        string? tagSha = ApiAcceptanceSupport.GetString(tagObject, "sha");
        if (tagType == "commit" && bootstrap?.AllowLightweightTag == true)
        {
            if (tagSha != sourceSha)
                throw new InvalidDataException($"The published {tag} tag does not resolve to the exact source commit.");
        }
        else if (tagType != "tag" || !Matches(Sha, tagSha))
            throw new InvalidDataException("The published source release must use an annotated Git tag.");
        if (tagType == "tag")
        {
            JsonObject annotated = await GetGitHubObjectAsync(
                $"/git/tags/{Uri.EscapeDataString(tagSha!)}", ct).ConfigureAwait(false);
            if (ApiAcceptanceSupport.GetString(annotated, "tag") != tag
                || ApiAcceptanceSupport.GetString(annotated["object"] as JsonObject ?? [], "type") != "commit"
                || ApiAcceptanceSupport.GetString(annotated["object"] as JsonObject ?? [], "sha") != sourceSha)
                throw new InvalidDataException("The published release tag does not resolve to the exact source commit.");
        }

        string resultName = publication.ContainsKey("continuation")
            ? PartialPublicationResultRef
            : $"agentweaver-publication/{sourceSha}/result";
        if (publication.ContainsKey("continuation") && sourceSha != PartialPublicationSourceSha)
            throw new InvalidDataException("A continuation publication receipt is only valid for the approved partial source.");
        JsonObject resultRef = await GetGitHubObjectAsync($"/git/ref/tags/{Uri.EscapeDataString(resultName)}", ct).ConfigureAwait(false);
        JsonObject resultTagRef = resultRef["object"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(resultTagRef, "type") != "tag"
            || ApiAcceptanceSupport.GetString(resultTagRef, "sha") is not string resultTagSha
            || !Matches(Sha, resultTagSha))
            throw new InvalidDataException("The immutable publication result for the exact source SHA is missing.");
        JsonObject resultTag = await GetGitHubObjectAsync($"/git/tags/{Uri.EscapeDataString(resultTagSha)}", ct).ConfigureAwait(false);
        JsonObject published;
        try
        {
            published = JsonNode.Parse(ApiAcceptanceSupport.GetString(resultTag, "message") ?? "") as JsonObject
                ?? throw new JsonException();
        }
        catch
        {
            throw new InvalidDataException("The immutable GitHub publication result is malformed.");
        }
        if (ApiAcceptanceSupport.GetString(resultTag, "tag") != resultName
            || ApiAcceptanceSupport.GetString(resultTag["object"] as JsonObject ?? [], "type") != "commit"
            || ApiAcceptanceSupport.GetString(resultTag["object"] as JsonObject ?? [], "sha") != sourceSha
            || JsonText(published) != JsonText(publication))
            throw new InvalidDataException("The supplied publication receipt differs from the immutable GitHub publication result.");
    }

    private static void ValidateBootstrapProductComponents(JsonObject manifest, string tag)
    {
        BootstrapRelease release = BootstrapReleases[tag];
        string[] expected = BaseProductComponents.Concat(release.AdditionalProductComponents ?? [])
            .Order(StringComparer.Ordinal).ToArray();
        string[] actual = (manifest["components"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(component => ApiAcceptanceSupport.GetString(component, "id") ?? "")
            .Where(id => id != "Agentweaver.FoundationProbe")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException($"{tag} draft manifest must contain exactly the expected product components.");
    }

    private static bool ValidateFoundationManifest(JsonObject manifest, string sourceSha)
    {
        string? stage = ApiAcceptanceSupport.GetString(manifest, "stage");
        if (stage is not ("draft" or "release")
            || stage == "draft" && manifest.ContainsKey("evidence")
            || stage == "release" && ApiAcceptanceSupport.GetString(manifest["evidence"] as JsonObject ?? [], "sourceSha") != sourceSha
            || manifest["components"] is not JsonArray components
            || manifest["compatibility"] is not JsonArray)
            return false;
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (JsonObject component in components.OfType<JsonObject>())
        {
            string? id = ApiAcceptanceSupport.GetString(component, "id");
            string? kind = ApiAcceptanceSupport.GetString(component, "kind");
            string? version = ApiAcceptanceSupport.GetString(component, "version");
            string? path = ApiAcceptanceSupport.GetString(component, "project");
            if (id is null || !Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9.-]*$", RegexOptions.CultureInvariant)
                || !ids.Add(id)
                || kind is not ("contract" or "library" or "service")
                || !IsValidSemanticVersion(version)
                || !IsSafeProjectPath(path, kind, id)
                || !paths.Add(path!))
                return false;
        }
        if (ids.Count != components.Count)
            return false;
        foreach ((string id, _) in ServiceComponents)
        {
            if (components.OfType<JsonObject>().Count(component =>
                    ApiAcceptanceSupport.GetString(component, "id") == id
                    && ApiAcceptanceSupport.GetString(component, "kind") == "service"
                    && !string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(component, "version"))
                    && !string.IsNullOrWhiteSpace(ApiAcceptanceSupport.GetString(component, "project"))) != 1)
                return false;
        }
        return true;
    }

    private async Task ValidateProjectMetadataAsync(JsonObject manifest, string sourceSha, CancellationToken ct)
    {
        JsonArray rows = (JsonArray)manifest["components"]!;
        Dictionary<string, JsonObject> components = rows.OfType<JsonObject>()
            .ToDictionary(item => ApiAcceptanceSupport.GetString(item, "id")!, StringComparer.Ordinal);
        Dictionary<string, string> projectPaths = components.Values.ToDictionary(
            item => ApiAcceptanceSupport.GetString(item, "project")!,
            item => ApiAcceptanceSupport.GetString(item, "id")!,
            StringComparer.Ordinal);
        Dictionary<string, string> projectContents = new(StringComparer.Ordinal);

        foreach (JsonObject component in components.Values)
        {
            string projectPath = ApiAcceptanceSupport.GetString(component, "project")!;
            byte[] content = await GetGitHubFileAsync(sourceSha, projectPath, ct).ConfigureAwait(false);
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(content);
            }
            catch
            {
                throw new InvalidDataException($"The exact pinned GitHub project metadata is not valid UTF-8: {projectPath}.");
            }

            if (projectPath == "apps/web/package.json")
            {
                JsonObject package;
                try
                {
                    package = JsonNode.Parse(text) as JsonObject
                        ?? throw new InvalidDataException();
                }
                catch
                {
                    throw new InvalidDataException("The pinned GitHub web package metadata is malformed.");
                }
                if (ApiAcceptanceSupport.GetString(package, "version") != ApiAcceptanceSupport.GetString(component, "version"))
                    throw new InvalidDataException($"The pinned GitHub {projectPath} version does not match the foundation manifest.");

                JsonObject lockFile;
                try
                {
                    lockFile = JsonNode.Parse(Encoding.UTF8.GetString(
                        await GetGitHubFileAsync(sourceSha, "apps/web/package-lock.json", ct).ConfigureAwait(false))) as JsonObject
                        ?? throw new InvalidDataException();
                }
                catch
                {
                    throw new InvalidDataException("The pinned GitHub web package lockfile is malformed or missing.");
                }
                string? version = ApiAcceptanceSupport.GetString(component, "version");
                if (ApiAcceptanceSupport.GetString(lockFile, "version") != version
                    || ApiAcceptanceSupport.GetString(lockFile["packages"]?[""] as JsonObject ?? [], "version") != version)
                    throw new InvalidDataException("The pinned GitHub web package lock versions do not match the foundation manifest.");
                projectContents[projectPath] = "";
                continue;
            }

            string xml = Regex.Replace(text, "<!--[\\s\\S]*?-->", string.Empty, RegexOptions.CultureInvariant);
            MatchCollection versions = Regex.Matches(xml, "<Version>([^<]*)</Version>", RegexOptions.CultureInvariant);
            if (versions.Count != 1
                || versions[0].Groups[1].Value.Trim() != ApiAcceptanceSupport.GetString(component, "version"))
                throw new InvalidDataException($"The pinned GitHub {projectPath} version does not match the foundation manifest.");
            projectContents[projectPath] = xml;
        }

        HashSet<(string Consumer, string Dependency)> compatibility = [];
        foreach (JsonNode? node in (JsonArray)manifest["compatibility"]!)
        {
            JsonObject rule = ApiAcceptanceSupport.RequireObject(node, "The pinned foundation compatibility rule is malformed.");
            ApiAcceptanceSupport.RequireExactKeys(rule, ["consumer", "dependency", "versions"], "foundation compatibility rule");
            string consumer = ApiAcceptanceSupport.RequireString(rule, "consumer", "compatibility consumer");
            string dependency = ApiAcceptanceSupport.RequireString(rule, "dependency", "compatibility dependency");
            if (consumer == dependency || !components.TryGetValue(consumer, out _)
                || !components.TryGetValue(dependency, out JsonObject? dependencyComponent)
                || rule["versions"] is not JsonArray versions || versions.Count == 0
                || !compatibility.Add((consumer, dependency)))
                throw new InvalidDataException("The pinned foundation compatibility relationship is invalid or duplicated.");

            HashSet<string> supported = new(StringComparer.Ordinal);
            foreach (JsonNode? versionNode in versions)
            {
                string? version = versionNode is JsonValue json && json.TryGetValue<string>(out string? text) ? text : null;
                if (!IsValidSemanticVersion(version) || !supported.Add(version!))
                    throw new InvalidDataException("The pinned foundation compatibility versions are invalid or duplicated.");
            }
            if (!supported.Contains(ApiAcceptanceSupport.GetString(dependencyComponent, "version")!))
                throw new InvalidDataException("The pinned foundation compatibility rule omits the selected dependency version.");
        }

        HashSet<(string Consumer, string Dependency)> references = [];
        foreach ((string projectPath, string xml) in projectContents)
        {
            if (xml.Length == 0)
                continue;
            string consumer = projectPaths[projectPath];
            foreach (Match reference in ProjectReference.Matches(xml))
            {
                string targetPath = ResolveProjectReference(projectPath, reference.Groups[2].Value);
                if (!projectPaths.TryGetValue(targetPath, out string? dependency))
                    throw new InvalidDataException($"The pinned GitHub {projectPath} references an unpinned project.");
                if (!references.Add((consumer, dependency)))
                    throw new InvalidDataException($"The pinned GitHub {projectPath} repeats a ProjectReference.");
                if (!compatibility.Contains((consumer, dependency)))
                    throw new InvalidDataException($"The pinned foundation manifest omits compatibility for {consumer} -> {dependency}.");
            }
        }
        if (compatibility.Any(pair => !references.Contains(pair)))
            throw new InvalidDataException("The pinned foundation compatibility relationship has no matching ProjectReference.");
    }

    private static bool IsSafeProjectPath(string? value, string kind, string id)
    {
        if (id == "Agentweaver.Web")
            return value == "apps/web/package.json" && kind == "service";
        if (value == "apps/web/package.json")
            return false;
        if (value == "tools/Agentweaver.FoundationProbe/Agentweaver.FoundationProbe.csproj")
            return id == "Agentweaver.FoundationProbe" && kind == "service";
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains(':')
            || !value.EndsWith(".csproj", StringComparison.Ordinal)
            || value.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or ".."))
            return false;
        string[] prefixes = kind switch
        {
            "service" => ["services"],
            "library" => ["packages", "services"],
            _ => ["packages"],
        };
        return prefixes.Any(prefix => value.StartsWith(prefix + "/", StringComparison.Ordinal))
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '-' or '_');
    }

    private static bool IsValidSemanticVersion(string? value)
    {
        Match match = SemanticVersion.Match(value ?? "");
        return match.Success && (!match.Groups[4].Success
            || !match.Groups[4].Value.Split('.').Any(part =>
                part.Length > 1 && part.All(char.IsAsciiDigit) && part[0] == '0'));
    }

    private static string ResolveProjectReference(string projectPath, string include)
    {
        string normalized = include.Replace('\\', '/');
        if (normalized.Length > 0 && normalized[0] == '/'
            || Regex.IsMatch(normalized, "^[A-Za-z]:", RegexOptions.CultureInvariant)
            || normalized.Contains(':'))
            throw new InvalidDataException("The pinned GitHub ProjectReference path is rooted or unsafe.");
        List<string> segments = projectPath[..projectPath.LastIndexOf('/')].Split('/').ToList();
        foreach (string segment in normalized.Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new InvalidDataException("The pinned GitHub ProjectReference escapes the repository root.");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (!segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
                throw new InvalidDataException("The pinned GitHub ProjectReference path contains an unsafe segment.");
            segments.Add(segment);
        }
        string result = string.Join('/', segments);
        if (!result.EndsWith(".csproj", StringComparison.Ordinal))
            throw new InvalidDataException("The pinned GitHub ProjectReference does not identify a project file.");
        return result;
    }

    private static (string SourceSha, string SourceTree) ReadCliBuildIdentity()
    {
        Assembly assembly = typeof(AcceptanceDeploymentEvidenceProvider).Assembly;
        AssemblyMetadataAttribute[] metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        string[] sourceShas = metadata.Where(item => item.Key == "Agentweaver.SourceSha")
            .Select(item => item.Value ?? "").ToArray();
        string[] sourceTrees = metadata.Where(item => item.Key == "Agentweaver.SourceTree")
            .Select(item => item.Value ?? "").ToArray();
        return (sourceShas.Length == 1 ? sourceShas[0] : "",
            sourceTrees.Length == 1 ? sourceTrees[0] : "");
    }

    private static void ValidatePublication(JsonObject publication, JsonObject manifest, string sourceSha, string sourceTree, bool integration)
    {
        if (ApiAcceptanceSupport.TryLong(publication["schemaVersion"], out long schema) is false || schema != 1
            || ApiAcceptanceSupport.GetString(publication, "status") != "published"
            || ApiAcceptanceSupport.GetString(publication, "sourceSha") != sourceSha
            || ApiAcceptanceSupport.GetString(publication, "sourceTree") != sourceTree
            || !Sha256.IsMatch(ApiAcceptanceSupport.GetString(publication, "provenanceSha256") ?? "")
            || ApiAcceptanceSupport.GetString(publication, "mode") != (integration ? "images-only" : null)
            || publication["published"] is not JsonArray published
            || published.Count != ServiceComponents.Length
            || publication["planned"] is not JsonArray planned
            || planned.Count != ServiceComponents.Length)
            throw new InvalidDataException("The source-bound publication receipt is not a complete result for the exact source.");

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JsonObject entry in published.OfType<JsonObject>())
        {
            string? id = ApiAcceptanceSupport.GetString(entry, "id");
            string? image = ApiAcceptanceSupport.GetString(entry, "image");
            if (id is null || !ids.Add(id) || !ServiceComponents.Any(component => component.Id == id)
                || image is null || !image.StartsWith("ghcr.io/", StringComparison.Ordinal)
                || !image.Contains("@sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("The source-bound publication receipt contains an invalid or duplicate product image.");
        }
        if (ServiceComponents.Any(component => !ids.Contains(component.Id)))
            throw new InvalidDataException("The source-bound publication receipt does not contain the exact service and AgentHost image set.");

        JsonArray manifestComponents = (JsonArray)manifest["components"]!;
        foreach (JsonObject publishedItem in published.OfType<JsonObject>())
        {
            string id = ApiAcceptanceSupport.GetString(publishedItem, "id")!;
            JsonObject component = manifestComponents.OfType<JsonObject>().Single(item => ApiAcceptanceSupport.GetString(item, "id") == id);
            JsonObject[] plans = planned.OfType<JsonObject>().Where(item => ApiAcceptanceSupport.GetString(item, "id") == id).ToArray();
            if (plans.Length != 1
                || ApiAcceptanceSupport.GetString(plans[0], "version") != ApiAcceptanceSupport.GetString(component, "version"))
                throw new InvalidDataException($"The exact source manifest is missing service component {id}.");
        }
    }

    private static string DigestFromImage(string image)
    {
        int separator = image.LastIndexOf('@');
        return separator >= 0 ? image[(separator + 1)..] : "";
    }

    private static JsonObject FindPublished(JsonObject publication, string id) =>
        (publication["published"] as JsonArray)?.OfType<JsonObject>()
            .SingleOrDefault(item => ApiAcceptanceSupport.GetString(item, "id") == id)
        ?? throw new InvalidDataException($"The source-bound publication is missing {id}.");

    private static async Task<JsonObject> ReadKubernetesObjectsAsync(string context, string namespaceName, CancellationToken ct)
    {
        ProcessStartInfo start = new("kubectl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[]
        {
            "--context", context, "--namespace", namespaceName, "--request-timeout=15s",
            "get", "configmaps,services,deployments,replicasets,pods", "--output=json",
        })
            start.ArgumentList.Add(arg);

        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || Encoding.UTF8.GetByteCount(output) > 8 * 1024 * 1024)
                throw new InvalidDataException("Read-only Kubernetes observation failed.");
            return JsonNode.Parse(output) as JsonObject
                ?? throw new InvalidDataException("Kubernetes returned malformed workload evidence.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("Read-only Kubernetes observation failed.");
        }
    }

    private static JsonObject ValidateIntegrationReceipt(
        JsonObject receipt,
        string sourceSha,
        string sourceTree,
        JsonObject foundation,
        JsonObject publication,
        AcceptanceManifest manifest)
    {
        JsonObject readbacks = receipt["readbacks"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(receipt, "status") != "deployed"
            || ApiAcceptanceSupport.GetString(receipt, "sourceSha") != sourceSha
            || ApiAcceptanceSupport.GetString(receipt, "sourceTree") != sourceTree
            || ApiAcceptanceSupport.GetString(receipt, "manifestSha256") != HashJson(foundation)
            || ApiAcceptanceSupport.GetString(receipt, "publicationSha256") != HashJson(publication)
            || ApiAcceptanceSupport.GetString(readbacks, "sha256") is not string readbackHash
            || readbacks["resources"] is not JsonArray resources
            || !Sha256.IsMatch(readbackHash)
            || ApiAcceptanceSupport.GetString(receipt["target"] as JsonObject ?? [], "namespace")
                != ApiAcceptanceSupport.GetString(manifest.Pins, "namespace"))
        {
            throw new InvalidDataException("The integration deployment receipt is not bound to the exact source, manifest, and publication.");
        }
        return new JsonObject
        {
            ["status"] = "blocked",
            ["reason"] = "The supplied integration receipt is source-bound, but the .NET runner does not validate its complete image and resource readback set.",
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["manifestSha256"] = ApiAcceptanceSupport.GetString(receipt, "manifestSha256"),
            ["publicationSha256"] = ApiAcceptanceSupport.GetString(receipt, "publicationSha256"),
            ["readbackSha256"] = readbackHash,
            ["completedAt"] = ApiAcceptanceSupport.GetString(receipt, "completedAt"),
            ["target"] = new JsonObject
            {
                ["clusterName"] = ApiAcceptanceSupport.GetString(receipt["target"] as JsonObject ?? [], "clusterName"),
                ["namespace"] = ApiAcceptanceSupport.GetString(receipt["target"] as JsonObject ?? [], "namespace"),
            },
        };
    }

    private static string ResolveProofPath(AcceptanceManifest manifest, JsonObject evidence, string property)
    {
        string path = ApiAcceptanceSupport.RequireString(evidence, property, $"deployment evidence {property}");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(manifest.ManifestDirectory, path));
    }

    private static async Task<JsonObject> ReadObjectAsync(string path, string error, CancellationToken ct)
    {
        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException(error);
        }
    }

    internal static string HashJson(JsonNode value) =>
        ApiAcceptanceSupport.Sha256Hex(Encoding.UTF8.GetBytes(JsonText(value)));

    internal static bool IsSha(string? value) => Sha.IsMatch(value ?? "");

    internal static bool IsDigest(string? value) => Digest.IsMatch(value ?? "");

    private static string GitBlobSha(byte[] content)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        byte[] blob = new byte[prefix.Length + content.Length];
        prefix.CopyTo(blob, 0);
        content.CopyTo(blob, prefix.Length);
        return Convert.ToHexStringLower(SHA1.HashData(blob));
    }
}
