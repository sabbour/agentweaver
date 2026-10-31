using System.Diagnostics;
using System.Net;
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

    public AcceptanceDeploymentEvidenceProvider(HttpClient httpClient)
        : this(httpClient, ReadKubernetesObjectsAsync)
    {
    }

    internal AcceptanceDeploymentEvidenceProvider(
        HttpClient httpClient,
        Func<string, string, CancellationToken, Task<JsonObject>> readKubernetesObjects,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.httpClient = httpClient;
        this.readKubernetesObjects = readKubernetesObjects;
        this.delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
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
        string imageManifestFile = ResolveProofPath(manifest, deployment, "imageManifestPath");
        JsonObject localManifest = await ReadObjectAsync(manifestFile, "The exact source manifest is missing or malformed.", ct)
            .ConfigureAwait(false);
        (JsonObject imageManifest, byte[] imageManifestBytes) = await ReadObjectBytesAsync(
            imageManifestFile, "The canonical GHCR image-selection manifest is missing or malformed.", ct)
            .ConfigureAwait(false);
        Dictionary<string, string> selectedImages = ValidateImageManifest(imageManifest, sourceSha);

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

        JsonObject services = await ObserveServicesEquivalentAsync(
            manifest, sourceSha, sourceTree, selectedImages, ct).ConfigureAwait(false);

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
            ["imageManifest"] = new JsonObject
            {
                ["sha256"] = ApiAcceptanceSupport.Sha256Hex(imageManifestBytes),
                ["selectedRepositories"] = new JsonArray(selectedImages.Keys.Order(StringComparer.Ordinal)
                    .Select(repository => (JsonNode?)JsonValue.Create(repository)).ToArray()),
            },
            ["providerCatalog"] = services["providerCatalog"]?.DeepClone(),
            ["services"] = services,
            ["agentHostImageMeasurement"] = services["agentHostImageMeasurement"]?.DeepClone(),
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

    private static async Task<(JsonObject Value, byte[] Bytes)> ReadObjectBytesAsync(
        string path,
        string error,
        CancellationToken ct)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return (JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException(), bytes);
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
