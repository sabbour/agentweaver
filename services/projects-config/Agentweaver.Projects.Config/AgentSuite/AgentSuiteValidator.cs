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
        // Fail closed if the embedded schemas were changed; validation must use the pinned bundle only.
        AgentSuiteSchemaBundle.EnsureVerified();
        // Copy the caller's bytes so later changes to their buffer cannot affect what we validate and retain.
        var bytes = archive.ToArray();
        // The digest of the exact uploaded bytes identifies this source, even when validation fails.
        var sourceDigest = Go.DigestBytes(bytes);
        // Size cap is checked before any parsing so a huge upload never reaches the tar/gzip readers.
        if (bytes.Length > MaxArchiveBytes)
            return Refused(format, sourceDigest, null, null, ArchiveTooLargeCode, $"The archive exceeds {MaxArchiveBytes} bytes.");
        // A raw content layer skips the OCI layout step; the whole upload is the content layer.
        if (format == AgentSuiteArchiveFormat.ContentTarGzip)
            return ValidateContent(format, sourceDigest, bytes);
        // An unreadable outer tar is an Agentweaver refusal, not a native suite error, because no suite was found.
        if (AgentSuiteOci.ReadLayoutTar(bytes, out var layout) is { } archiveError)
            return Refused(format, sourceDigest, null, null, ArchiveInvalidCode, archiveError);
        return ValidateLayout(format, sourceDigest, layout!);
    }

    /// <summary>Validates an OCI layout given as relative path to bytes.</summary>
    public static AgentSuiteValidationResult ValidateOciLayout(IReadOnlyDictionary<string, byte[]> layoutFiles)
    {
        ArgumentNullException.ThrowIfNull(layoutFiles);
        AgentSuiteSchemaBundle.EnsureVerified();
        // No outer archive exists here, so the source digest is derived later from the manifest or index.json.
        return ValidateLayout(AgentSuiteArchiveFormat.OciLayoutTar, null, new LayoutFs(layoutFiles));
    }

    private static AgentSuiteValidationResult ValidateLayout(AgentSuiteArchiveFormat format, string? sourceDigest, LayoutFs layout)
    {
        // OciLoad collects whatever was read so far, so even a failed load can report the digests it reached.
        var load = new OciLoad();
        string? error;
        try
        {
            error = AgentSuiteOci.LoadLayout(layout, load);
        }
        catch (AgentSuiteLimitException exception)
        {
            // A size/count limit stops reading early; report it as an Agentweaver refusal with the partial digests.
            var digest = load.Manifest?.Digest ?? load.IndexedManifestDigest;
            return Refused(format, sourceDigest ?? FallbackDigest(layout, digest), digest, load.Layer?.Digest, ContentLimitCode, exception.Message);
        }
        // Prefer the parsed manifest digest; fall back to the digest index.json pointed at if the manifest did not load.
        var manifestDigest = load.Manifest?.Digest ?? load.IndexedManifestDigest;
        sourceDigest ??= FallbackDigest(layout, manifestDigest);
        // Keep the original OCI blobs (manifest, config, content layer) byte-for-byte for later retention.
        var blobs = new List<AgentSuiteBlob>();
        foreach (var blob in new[] { load.Manifest, load.Config, load.Layer })
        {
            if (blob is not null)
                blobs.Add(new AgentSuiteBlob(blob.Digest, blob.MediaType, blob.Data));
        }
        // Native layout errors use the upstream Go error text; Rejected carries no blobs, so nothing is retained.
        if (error is not null)
            return Rejected(format, sourceDigest, manifestDigest, load.Layer?.Digest, error);
        // Success means LoadLayout set both the layer and the expanded content.
        return Finish(format, sourceDigest, manifestDigest, load.Layer!.Digest, load.Content!, blobs);
    }

    // Used when there is no uploaded archive to hash: the manifest digest, else a digest of index.json (or of empty bytes).
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
            // For a raw content layer the upload itself is the layer, so the source digest doubles as the content digest.
            return Refused(format, sourceDigest, null, sourceDigest, ContentLimitCode, exception.Message);
        }
        if (error is not null)
            return Rejected(format, sourceDigest, null, sourceDigest, error);
        // There is no OCI manifest here; the only original blob to retain is the content layer itself.
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
        // Agentweaver's stricter limits are checked alongside native rules so the caller sees both kinds of error.
        var profileErrors = ContentLimits(content);
        var error = AgentSuiteNativeValidator.Validate(content, out var validator);
        if (error is not null)
        {
            // Native failure: no graph is built, so the result can never be Accepted.
            return new AgentSuiteValidationResult(format, sourceDigest, manifestDigest, contentDigest, Lines(error),
                [.. profileErrors], null, [.. blobs]);
        }
        var graph = BuildGraph(validator!, content);
        // Secret references are only visible after the graph is built, so this profile check runs last.
        profileErrors.AddRange(SecretReferences(graph));
        // Also retain each content file as its own blob, de-duplicated by digest (identical files are stored once).
        var seen = new HashSet<string>(blobs.Select(blob => blob.Digest), StringComparer.Ordinal);
        foreach (var entry in content.Entries)
        {
            // Directories and other non-file entries have no bytes to retain.
            if (entry.Type != "file" || entry.Bytes is null)
                continue;
            var digest = Go.DigestBytes(entry.Bytes);
            // Media type comes from the descriptor that referenced the file; unreferenced files are generic bytes.
            if (seen.Add(digest))
                blobs.Add(new AgentSuiteBlob(digest, validator!.MediaTypes.GetValueOrDefault(entry.Path, OctetStream), entry.Bytes));
        }
        return new AgentSuiteValidationResult(format, sourceDigest, manifestDigest, contentDigest, [],
            [.. profileErrors], graph, [.. blobs]);
    }

    private static List<AgentSuiteProfileError> ContentLimits(ContentSet content)
    {
        // These caps match Agentweaver's own package limits; they are stricter than the upstream spec.
        var errors = new List<AgentSuiteProfileError>();
        // Count and size limits apply to files only; the path-length limit below also covers directories.
        var files = content.Entries.Where(entry => entry.Type == "file").ToList();
        if (files.Count > MaxContentFiles)
            errors.Add(new(ContentLimitCode, $"The suite content has more than {MaxContentFiles} files."));
        if (files.Sum(entry => entry.Size) > MaxExpandedContentBytes)
            errors.Add(new(ContentLimitCode, $"The suite content exceeds {MaxExpandedContentBytes} expanded bytes."));
        foreach (var entry in files.Where(entry => entry.Size > MaxContentFileBytes))
            errors.Add(new(ContentLimitCode, $"File '{entry.Path}' exceeds {MaxContentFileBytes} bytes."));
        // Path length is measured in UTF-8 bytes (like Go's len), not in .NET UTF-16 characters.
        foreach (var entry in content.Entries.Where(entry => Go.ByteLength(entry.Path) > MaxContentPathBytes))
            errors.Add(new(ContentLimitCode, $"Path '{entry.Path}' exceeds {MaxContentPathBytes} bytes."));
        return errors;
    }

    // The spec allows endpointEnv/secretRefs, but Agentweaver refuses them: credentials must come from the deployment, not the package.
    private static IEnumerable<AgentSuiteProfileError> SecretReferences(AgentSuiteGraph graph) =>
        graph.Agents
            .Where(agent => agent.Model.EndpointEnv is not null || !agent.Model.SecretRefs.IsEmpty)
            .Select(agent => new AgentSuiteProfileError(SecretReferenceCode,
                $"Agent '{agent.Id}' model declares a secret reference; packages must not carry secrets."));

    private static AgentSuiteGraph BuildGraph(AgentSuiteNativeValidator validator, ContentSet content)
    {
        // Only called after native validation passed, so every lookup below is known to succeed.
        var suite = validator.Suite;
        var agents = ImmutableArray.CreateBuilder<AgentSuiteAgent>();
        // Walk agents in suite order (not dictionary order) so the graph is stable for the same archive.
        foreach (var reference in suite["agents"].A)
        {
            var id = reference["id"].S;
            var agent = validator.Agents[id];
            var instructions = agent["instructions"];
            var instructionsPath = instructions["path"].S;
            // Instruction bytes were already matched to their descriptor digest during native validation.
            var instructionBytes = content.Get(instructionsPath)?.Bytes ?? [];
            var model = agent["model"];
            // Missing JSON fields read as empty strings (Go zero values); NullIfEmpty turns them into null.
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
                    // Capability-based means the agent asks for model capabilities instead of naming a model.
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

        // Bundled variants are packaged stdio MCP servers; a remote block is an endpoint that is recorded for review but never contacted.
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
            // Rebuild the same agent@platform[@runtimeProfile] key the native validator used to store the composition.
            var key = AgentSuiteNativeValidator.CompositionKey(reference["agent"].S, reference["platform"], runtimeProfile);
            // Defensive skip; after a successful native pass every reference should have a stored composition.
            if (!validator.Compositions.TryGetValue(key, out var composition))
                continue;
            compositions.Add(new AgentSuiteComposition(
                composition["agent"].S,
                composition["platform"].Platform,
                composition["buildProfile"].S,
                NullIfEmpty(runtimeProfile),
                reference["descriptor"]["digest"].S));
        }

        // Image references from both runtime base and harness images are listed together for review.
        var buildProfiles = validator.Builds.Select(pair => new AgentSuiteBuildProfile(
            pair.Key,
            validator.BuildDigests[pair.Key],
            pair.Value["sourceEpoch"].I,
            !pair.Value["execution"].IsNull,
            [.. pair.Value["runtimeBase"].A.Concat(pair.Value["harness"].A).Select(image => image["imageRef"].S)]));

        // The suite manifest digest is taken over the exact original agentsuite.json bytes.
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

    // The joined Go error has one message per line; sorting makes the output independent of map iteration order.
    private static ImmutableArray<string> Lines(string error) =>
        [.. error.Split('\n').Order(StringComparer.Ordinal)];

    // Rejected = the upstream rules failed. Refused = an Agentweaver profile limit failed. Neither keeps any blobs.
    private static AgentSuiteValidationResult Rejected(
        AgentSuiteArchiveFormat format, string sourceDigest, string? manifestDigest, string? contentDigest, string error) =>
        new(format, sourceDigest, manifestDigest, contentDigest, Lines(error), [], null, []);

    private static AgentSuiteValidationResult Refused(
        AgentSuiteArchiveFormat format, string sourceDigest, string? manifestDigest, string? contentDigest, string code, string message) =>
        new(format, sourceDigest, manifestDigest, contentDigest, [], [new AgentSuiteProfileError(code, message)], null, []);
}
