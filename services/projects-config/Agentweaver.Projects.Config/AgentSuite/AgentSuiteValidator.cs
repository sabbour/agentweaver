using System.Collections.Immutable;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// Offline AgentSuite validator: the native upstream rules (pinned Go module parity) plus the stricter
/// Agentweaver import profile. It never executes, fetches, or contacts anything.
/// </summary>
internal static class AgentSuiteValidator
{
    /// <summary>Agentweaver upload cap for the archive bytes.</summary>
    public const int MaxArchiveBytes = 16 * 1024 * 1024;

    // Mirrors AgentApplicationManifestValidator package limits.
    public const int MaxContentFiles = 256;
    public const long MaxExpandedContentBytes = 16 * 1024 * 1024;
    public const long MaxContentFileBytes = 1024 * 1024;
    public const int MaxContentPathBytes = 240;

    public const string ArchiveInvalidCode = "agentsuite_archive_invalid";
    public const string ArchiveTooLargeCode = "agentsuite_archive_too_large";
    public const string ContentLimitCode = "agentsuite_content_limit";
    public const string SecretReferenceCode = "agentsuite_secret_reference";

    private const string OctetStream = "application/octet-stream";

    public static AgentSuiteValidationResult Validate(AgentSuiteArchiveFormat format, ReadOnlyMemory<byte> archive)
    {
        AgentSuiteSchemaBundle.EnsureVerified();
        var bytes = archive.ToArray();
        var sourceDigest = Go.DigestBytes(bytes);
        if (bytes.Length > MaxArchiveBytes)
            return Refused(format, sourceDigest, null, null, ArchiveTooLargeCode, $"The archive exceeds {MaxArchiveBytes} bytes.");
        if (format == AgentSuiteArchiveFormat.ContentTarGzip)
            return ValidateContent(format, sourceDigest, bytes);
        if (AgentSuiteOci.ReadLayoutTar(bytes, out var layout) is { } archiveError)
            return Refused(format, sourceDigest, null, null, ArchiveInvalidCode, archiveError);
        return ValidateLayout(format, sourceDigest, layout!);
    }

    /// <summary>Validates an OCI layout given as relative path to bytes.</summary>
    public static AgentSuiteValidationResult ValidateOciLayout(IReadOnlyDictionary<string, byte[]> layoutFiles)
    {
        ArgumentNullException.ThrowIfNull(layoutFiles);
        AgentSuiteSchemaBundle.EnsureVerified();
        return ValidateLayout(AgentSuiteArchiveFormat.OciLayoutTar, null, new LayoutFs(layoutFiles));
    }

    private static AgentSuiteValidationResult ValidateLayout(AgentSuiteArchiveFormat format, string? sourceDigest, LayoutFs layout)
    {
        var load = new OciLoad();
        string? error;
        try
        {
            error = AgentSuiteOci.LoadLayout(layout, load);
        }
        catch (AgentSuiteLimitException exception)
        {
            var digest = load.Manifest?.Digest ?? load.IndexedManifestDigest;
            return Refused(format, sourceDigest ?? FallbackDigest(layout, digest), digest, load.Layer?.Digest, ContentLimitCode, exception.Message);
        }
        var manifestDigest = load.Manifest?.Digest ?? load.IndexedManifestDigest;
        sourceDigest ??= FallbackDigest(layout, manifestDigest);
        var blobs = new List<AgentSuiteBlob>();
        foreach (var blob in new[] { load.Manifest, load.Config, load.Layer })
        {
            if (blob is not null)
                blobs.Add(new AgentSuiteBlob(blob.Digest, blob.MediaType, blob.Data));
        }
        if (error is not null)
            return Rejected(format, sourceDigest, manifestDigest, load.Layer?.Digest, error);
        return Finish(format, sourceDigest, manifestDigest, load.Layer!.Digest, load.Content!, blobs);
    }

    private static string FallbackDigest(LayoutFs layout, string? manifestDigest) =>
        manifestDigest ?? Go.DigestBytes(layout.Files.GetValueOrDefault("index.json") ?? []);

    private static AgentSuiteValidationResult ValidateContent(AgentSuiteArchiveFormat format, string sourceDigest, byte[] layer)
    {
        string? error;
        ContentSet? content;
        try
        {
            error = AgentSuiteContent.LoadContentLayer(layer, out content);
        }
        catch (AgentSuiteLimitException exception)
        {
            return Refused(format, sourceDigest, null, sourceDigest, ContentLimitCode, exception.Message);
        }
        if (error is not null)
            return Rejected(format, sourceDigest, null, sourceDigest, error);
        var blobs = new List<AgentSuiteBlob> { new(sourceDigest, AgentSuiteTypes.MediaTypeContent, layer) };
        return Finish(format, sourceDigest, null, sourceDigest, content!, blobs);
    }

    private static AgentSuiteValidationResult Finish(
        AgentSuiteArchiveFormat format,
        string sourceDigest,
        string? manifestDigest,
        string contentDigest,
        ContentSet content,
        List<AgentSuiteBlob> blobs)
    {
        var profileErrors = ContentLimits(content);
        var error = AgentSuiteNativeValidator.Validate(content, out var validator);
        if (error is not null)
        {
            return new AgentSuiteValidationResult(format, sourceDigest, manifestDigest, contentDigest, Lines(error),
                [.. profileErrors], null, [.. blobs]);
        }
        var graph = BuildGraph(validator!, content);
        profileErrors.AddRange(SecretReferences(graph));
        var seen = new HashSet<string>(blobs.Select(blob => blob.Digest), StringComparer.Ordinal);
        foreach (var entry in content.Entries)
        {
            if (entry.Type != "file" || entry.Bytes is null)
                continue;
            var digest = Go.DigestBytes(entry.Bytes);
            if (seen.Add(digest))
                blobs.Add(new AgentSuiteBlob(digest, validator!.MediaTypes.GetValueOrDefault(entry.Path, OctetStream), entry.Bytes));
        }
        return new AgentSuiteValidationResult(format, sourceDigest, manifestDigest, contentDigest, [],
            [.. profileErrors], graph, [.. blobs]);
    }

    private static List<AgentSuiteProfileError> ContentLimits(ContentSet content)
    {
        var errors = new List<AgentSuiteProfileError>();
        var files = content.Entries.Where(entry => entry.Type == "file").ToList();
        if (files.Count > MaxContentFiles)
            errors.Add(new(ContentLimitCode, $"The suite content has more than {MaxContentFiles} files."));
        if (files.Sum(entry => entry.Size) > MaxExpandedContentBytes)
            errors.Add(new(ContentLimitCode, $"The suite content exceeds {MaxExpandedContentBytes} expanded bytes."));
        foreach (var entry in files.Where(entry => entry.Size > MaxContentFileBytes))
            errors.Add(new(ContentLimitCode, $"File '{entry.Path}' exceeds {MaxContentFileBytes} bytes."));
        foreach (var entry in content.Entries.Where(entry => Go.ByteLength(entry.Path) > MaxContentPathBytes))
            errors.Add(new(ContentLimitCode, $"Path '{entry.Path}' exceeds {MaxContentPathBytes} bytes."));
        return errors;
    }

    private static IEnumerable<AgentSuiteProfileError> SecretReferences(AgentSuiteGraph graph) =>
        graph.Agents
            .Where(agent => agent.Model.EndpointEnv is not null || !agent.Model.SecretRefs.IsEmpty)
            .Select(agent => new AgentSuiteProfileError(SecretReferenceCode,
                $"Agent '{agent.Id}' model declares a secret reference; packages must not carry secrets."));

    private static AgentSuiteGraph BuildGraph(AgentSuiteNativeValidator validator, ContentSet content)
    {
        var suite = validator.Suite;
        var agents = ImmutableArray.CreateBuilder<AgentSuiteAgent>();
        foreach (var reference in suite["agents"].A)
        {
            var id = reference["id"].S;
            var agent = validator.Agents[id];
            var instructions = agent["instructions"];
            var instructionsPath = instructions["path"].S;
            var instructionBytes = content.Get(instructionsPath)?.Bytes ?? [];
            var model = agent["model"];
            agents.Add(new AgentSuiteAgent(
                id,
                NullIfEmpty(agent["description"].S),
                validator.AgentDigests[id],
                instructionsPath,
                instructions["descriptor"]["digest"].S,
                instructionBytes,
                new AgentSuiteModelRequirement(
                    model["protocol"].S,
                    NullIfEmpty(model["model"].S),
                    model["capabilities"].Present && !model["capabilities"].IsNull,
                    NullIfEmpty(model["endpointEnv"].S),
                    [.. model["secretRefs"].A.Select(item => item.S)]),
                [.. agent["toolProviders"].A.Select(item =>
                    new AgentSuiteToolProviderRequirement(item["id"].S, item["version"].S, item["executionMode"].S))],
                [.. agent["invokes"].A.Select(item =>
                    new AgentSuiteInvoke(item["agent"].S, item["maxConcurrent"].I, item["maxDepth"].I))],
                !agent["policy"].IsNull,
                agent["evals"].Count > 0,
                [.. agent["extensions"].A.Select(item => item["name"].S)]));
        }

        var toolProviders = validator.ToolProviders.Select(pair => new AgentSuiteToolProvider(
            pair.Value["id"].S,
            pair.Value["version"].S,
            validator.ToolProviderDigests[pair.Key],
            pair.Value["variants"].Count > 0,
            !pair.Value["remote"].IsNull,
            [.. pair.Value["tools"].A.Select(tool => tool["name"].S)]));

        var compositions = ImmutableArray.CreateBuilder<AgentSuiteComposition>();
        foreach (var reference in suite["compositions"].A)
        {
            var runtimeProfile = reference["runtimeProfile"].S;
            var key = AgentSuiteNativeValidator.CompositionKey(reference["agent"].S, reference["platform"], runtimeProfile);
            if (!validator.Compositions.TryGetValue(key, out var composition))
                continue;
            compositions.Add(new AgentSuiteComposition(
                composition["agent"].S,
                composition["platform"].Platform,
                composition["buildProfile"].S,
                NullIfEmpty(runtimeProfile),
                reference["descriptor"]["digest"].S));
        }

        var buildProfiles = validator.Builds.Select(pair => new AgentSuiteBuildProfile(
            pair.Key,
            validator.BuildDigests[pair.Key],
            pair.Value["sourceEpoch"].I,
            !pair.Value["execution"].IsNull,
            [.. pair.Value["runtimeBase"].A.Concat(pair.Value["harness"].A).Select(image => image["imageRef"].S)]));

        return new AgentSuiteGraph(
            suite["name"].S,
            Go.DigestBytes(validator.SuiteBytes),
            agents.ToImmutable(),
            [.. toolProviders],
            compositions.ToImmutable(),
            [.. buildProfiles],
            [.. validator.Runtimes.Keys],
            [.. suite["capabilities"].A.Select(item => item.S)]);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static ImmutableArray<string> Lines(string error) =>
        [.. error.Split('\n').Order(StringComparer.Ordinal)];

    private static AgentSuiteValidationResult Rejected(
        AgentSuiteArchiveFormat format, string sourceDigest, string? manifestDigest, string? contentDigest, string error) =>
        new(format, sourceDigest, manifestDigest, contentDigest, Lines(error), [], null, []);

    private static AgentSuiteValidationResult Refused(
        AgentSuiteArchiveFormat format, string sourceDigest, string? manifestDigest, string? contentDigest, string code, string message) =>
        new(format, sourceDigest, manifestDigest, contentDigest, [], [new AgentSuiteProfileError(code, message)], null, []);
}
