using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>
/// One digest-pinned OCI image for one platform. The digest comes from <see cref="ImageRef"/>;
/// <see cref="Size"/> is the byte size of that image manifest.
/// </summary>
public sealed record AgentSuiteImageInput(string Architecture, string ImageRef, long Size);

/// <summary>An exact model requirement written to each agent as <c>{protocol, model}</c>.</summary>
public sealed record AgentSuiteModelInput(string Protocol, string Model);

/// <summary>
/// Build inputs that a 0.x blueprint doesn't have but the AgentSuite spec requires. Every value must be real:
/// the converter never fills in images, digests or models.
/// </summary>
public sealed record AgentSuiteBuildInputs
{
    /// <summary>Build profile ID referenced by every composition.</summary>
    public required string BuildProfileId { get; init; }
    /// <summary>Reproducible build time, in Unix seconds. Must be at least 1.</summary>
    public required long SourceEpoch { get; init; }
    /// <summary>Runtime base images, one per architecture.</summary>
    public required ImmutableArray<AgentSuiteImageInput> RuntimeBase { get; init; }
    /// <summary>Harness images, one per architecture. Must cover the same architectures as <see cref="RuntimeBase"/>.</summary>
    public required ImmutableArray<AgentSuiteImageInput> Harness { get; init; }
    /// <summary>Model for agents without a role-specific model.</summary>
    public required AgentSuiteModelInput Model { get; init; }
    /// <summary>Role-specific models, keyed by role ID.</summary>
    public ImmutableDictionary<string, AgentSuiteModelInput> RoleModels { get; init; } =
        ImmutableDictionary<string, AgentSuiteModelInput>.Empty;
}

/// <summary>One file in a suite content layer.</summary>
public sealed record AgentSuiteFile(string Path, ImmutableArray<byte> Content);

/// <summary>
/// A converted blueprint: native AgentSuite content files, the deterministic <c>content-tar-gzip</c> archive that
/// the AgentSuite import accepts, and the blueprint projection that travels with the import.
/// </summary>
public sealed record AgentSuiteBlueprintPackage(
    string SuiteName,
    ImmutableArray<AgentSuiteFile> Files,
    ImmutableArray<byte> ContentArchive,
    string ContentDigest,
    ProjectAgentSuiteBlueprint Blueprint);

/// <summary>Thrown when a blueprint or its build inputs can't produce a conformant suite. Carries every error.</summary>
public sealed class AgentSuiteConversionException(ImmutableArray<string> errors)
    : Exception($"The blueprint can't be converted to AgentSuite: {string.Join(" ", errors)}")
{
    /// <summary>Every conversion error.</summary>
    public ImmutableArray<string> Errors { get; } = errors;
}

/// <summary>
/// Converts 0.x blueprints to native AgentSuite content (pinned spec <c>1.0.0-draft</c>) plus an Agentweaver blueprint
/// projection. Each roster role becomes one agent: the role ID is the agent ID, the charter is its instructions and
/// the role title is its description. Workflows, review policy, sandbox profile and skill bindings have no native
/// field, so they go in the projection. The output declares no invokes, tool providers or extensions.
/// </summary>
public static class AgentSuiteBlueprintConverter
{
    /// <summary>The pinned AgentSuite schema version.</summary>
    public const string SpecVersion = "1.0.0-draft";

    /// <summary>Media type of the suite manifest.</summary>
    public const string SuiteMediaType = "application/vnd.agentsuite.manifest.v1+json";

    /// <summary>Media type of an agent manifest.</summary>
    public const string AgentMediaType = "application/vnd.agentsuite.agent.v1+json";

    /// <summary>Media type of agent instructions.</summary>
    public const string InstructionsMediaType = "text/markdown; charset=utf-8";

    /// <summary>Media type of the tool provider catalog.</summary>
    public const string ToolProviderCatalogMediaType = "application/vnd.agentsuite.tool.provider.catalog.v1+json";

    /// <summary>Media type of a composition.</summary>
    public const string CompositionMediaType = "application/vnd.agentsuite.composition.v1+json";

    /// <summary>Media type of a build profile.</summary>
    public const string BuildProfileMediaType = "application/vnd.agentsuite.build.profile.v1+json";

    /// <summary>Media type of an OCI image manifest.</summary>
    public const string ImageManifestMediaType = "application/vnd.oci.image.manifest.v1+json";

    private static readonly Regex IdPattern = new(
        @"\A[a-z0-9][a-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex ImageRefPattern = new(
        @"\A[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?::[0-9]+)?(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)+@sha256:[a-f0-9]{64}\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Converts a 0.x <c>blueprint-package-v1</c> package. Package roles become agents with their 0.x charter text.
    /// A role's <c>default_model</c> becomes its model with the protocol of <see cref="AgentSuiteBuildInputs.Model"/>;
    /// an entry in <see cref="AgentSuiteBuildInputs.RoleModels"/> takes precedence.
    /// </summary>
    /// <exception cref="AgentSuiteConversionException">The package, the blueprint, or the build inputs are invalid.</exception>
    public static AgentSuiteBlueprintPackage ConvertPackage(
        ReadOnlySpan<byte> manifest,
        IReadOnlyDictionary<string, byte[]> payloads,
        AgentSuiteBuildInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        // Validate the package with the 0.x rules and turn its role payloads into bespoke roles.
        var content = BlueprintPackageV1.Read(manifest, payloads);
        // Add each role's default model from the package. Role models in the build inputs win, so only fill gaps.
        var roleModels = inputs.RoleModels;
        foreach (var (roleId, model) in content.RoleDefaultModels)
        {
            if (!roleModels.ContainsKey(roleId))
                roleModels = roleModels.SetItem(roleId, new AgentSuiteModelInput(inputs.Model.Protocol, model));
        }
        // Convert with the merged role models; all other build inputs stay as given.
        return Convert(content.Blueprint, inputs with { RoleModels = roleModels });
    }

    /// <summary>Converts a 0.x blueprint. Roles resolve from bespoke roles first, then the role catalog.</summary>
    /// <exception cref="AgentSuiteConversionException">The blueprint or the build inputs are invalid.</exception>
    public static AgentSuiteBlueprintPackage Convert(BlueprintDefinition blueprint, AgentSuiteBuildInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(inputs);
        // Collect every error first so the caller sees all problems in one exception.
        var errors = new List<string>();
        // Workflow availability is checked when the blueprint is applied, so packages keep every 0.x workflow.
        errors.AddRange(ProjectBlueprintCatalog.Validate(blueprint, requireAvailableWorkflows: false).Errors);
        // The blueprint ID becomes the suite name, so it must also match the AgentSuite ID grammar.
        // Skip this check when the blueprint is already invalid, to avoid noisy follow-on errors.
        if (errors.Count == 0 && !IdPattern.IsMatch(blueprint.Id))
            errors.Add($"Blueprint id '{blueprint.Id}' isn't a valid suite name.");
        // A default (uninitialized) roster counts as empty so validation can't throw.
        // The result is the sorted list of architectures to build compositions for.
        var platforms = ValidateInputs(inputs, blueprint.Roster.IsDefault ? [] : blueprint.Roster, errors);
        if (errors.Count > 0)
            throw new AgentSuiteConversionException(errors.ToImmutableArray());

        // Files keyed by suite-relative path. Ordinal sorting keeps the output deterministic.
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        // Suite manifest references, collected while each agent and composition file is written.
        var agentRefs = new JsonArray();
        var compositionRefs = new JsonArray();

        // Build profile: the digest-pinned runtime base and harness images that every composition uses.
        var profile = Canonical(new JsonObject
        {
            ["schemaVersion"] = SpecVersion,
            ["mediaType"] = BuildProfileMediaType,
            ["id"] = inputs.BuildProfileId,
            ["sourceEpoch"] = inputs.SourceEpoch,
            ["runtimeBase"] = Images(inputs.RuntimeBase),
            ["harness"] = Images(inputs.Harness),
        });
        var profilePath = $"build-profiles/{inputs.BuildProfileId}.json";
        files[profilePath] = profile;

        // The spec requires a tool provider catalog. It is empty because 0.x blueprints declare no tool providers.
        var catalog = Canonical(new JsonObject
        {
            ["schemaVersion"] = SpecVersion,
            ["mediaType"] = ToolProviderCatalogMediaType,
            ["toolProviders"] = new JsonArray(),
        });
        files["tool-providers/catalog.json"] = catalog;

        // One agent per roster role: an instructions file, an agent manifest, and one composition per platform.
        foreach (var roleId in blueprint.Roster)
        {
            // Bespoke roles win over the shared role catalog.
            var (title, charter) = ProjectBlueprintCatalog.ResolveRole(blueprint, roleId);
            // Strict UTF-8 fails on invalid text instead of silently writing replacement characters.
            var instructions = StrictUtf8.GetBytes(charter);
            var instructionsPath = $"instructions/{roleId}.md";
            files[instructionsPath] = instructions;

            // Use the role-specific model when there is one; otherwise use the default model.
            var model = inputs.RoleModels.TryGetValue(roleId, out var roleModel) ? roleModel : inputs.Model;
            var agent = Canonical(new JsonObject
            {
                ["schemaVersion"] = SpecVersion,
                ["mediaType"] = AgentMediaType,
                ["id"] = roleId,
                ["description"] = title,
                ["instructions"] = new JsonObject
                {
                    ["path"] = instructionsPath,
                    ["descriptor"] = Descriptor(InstructionsMediaType, instructions),
                },
                ["model"] = new JsonObject { ["protocol"] = model.Protocol, ["model"] = model.Model },
            });
            var agentPath = $"agents/{roleId}.json";
            files[agentPath] = agent;
            // The suite manifest points to the agent by path and by the digest of its exact bytes.
            agentRefs.Add(new JsonObject
            {
                ["id"] = roleId,
                ["path"] = agentPath,
                ["descriptor"] = Descriptor(AgentMediaType, agent),
            });

            // One composition per architecture binds the agent to the build profile for that platform.
            foreach (var architecture in platforms)
            {
                var composition = Canonical(new JsonObject
                {
                    ["schemaVersion"] = SpecVersion,
                    ["mediaType"] = CompositionMediaType,
                    ["agent"] = roleId,
                    ["platform"] = Platform(architecture),
                    ["buildProfile"] = inputs.BuildProfileId,
                    ["toolProviders"] = new JsonArray(),
                });
                var compositionPath = $"compositions/{roleId}-linux-{architecture}.json";
                files[compositionPath] = composition;
                compositionRefs.Add(new JsonObject
                {
                    ["agent"] = roleId,
                    ["platform"] = Platform(architecture),
                    ["path"] = compositionPath,
                    ["descriptor"] = Descriptor(CompositionMediaType, composition),
                });
            }
        }

        // Write the suite manifest last, because it needs the digests of every other file.
        files["agentsuite.json"] = Canonical(new JsonObject
        {
            ["schemaVersion"] = SpecVersion,
            ["mediaType"] = SuiteMediaType,
            ["name"] = blueprint.Id,
            ["agents"] = agentRefs,
            ["toolProviderCatalog"] = new JsonObject
            {
                ["id"] = "catalog",
                ["path"] = "tool-providers/catalog.json",
                ["descriptor"] = Descriptor(ToolProviderCatalogMediaType, catalog),
            },
            ["compositions"] = compositionRefs,
            ["buildProfiles"] = new JsonArray(new JsonObject
            {
                ["id"] = inputs.BuildProfileId,
                ["path"] = profilePath,
                ["descriptor"] = Descriptor(BuildProfileMediaType, profile),
            }),
        });

        // Pack the files into the deterministic content archive and record the archive digest.
        var fileList = files
            .Select(pair => new AgentSuiteFile(pair.Key, ImmutableArray.Create(pair.Value)))
            .ToImmutableArray();
        var archive = WriteContentArchive(fileList);
        // The projection carries the team data that has no AgentSuite field: workflows, default workflow,
        // review policy, sandbox profile and skill bindings.
        return new(
            blueprint.Id,
            fileList,
            ImmutableArray.Create(archive),
            Digest(archive),
            ProjectBlueprintCatalog.ToAgentSuiteBlueprint(blueprint));
    }

    /// <summary>
    /// Writes a deterministic AgentSuite content layer: a ustar archive with sorted entries, parent directories,
    /// Unix-epoch modification times, no access or change times, owner 0, mode 0644 for files and 0755 for
    /// directories, compressed with gzip with no modification time.
    /// </summary>
    public static byte[] WriteContentArchive(IEnumerable<AgentSuiteFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        // A null value marks a directory entry. Ordinal sorting fixes the entry order, so equal input gives equal bytes.
        var entries = new SortedDictionary<string, AgentSuiteFile?>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            entries[file.Path] = file;
            // Add an entry for every parent directory. Directory names end in '/', as tar expects.
            for (var slash = file.Path.LastIndexOf('/'); slash > 0; slash = file.Path.LastIndexOf('/', slash - 1))
                entries.TryAdd(file.Path[..slash] + "/", null);
        }

        // Write an uncompressed ustar archive first, then gzip it.
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (path, file) in entries)
            {
                // Fixed time, owner and mode keep machine-specific metadata out of the archive.
                var entry = new UstarTarEntry(file is null ? TarEntryType.Directory : TarEntryType.RegularFile, path)
                {
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    Mode = file is null
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                          UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    Uid = 0,
                    Gid = 0,
                };
                // Only regular files have content; directory entries carry no data.
                if (file is not null)
                    entry.DataStream = new MemoryStream(file.Content.ToArray(), writable: false);
                writer.WriteEntry(entry);
            }
        }

        // GZipStream writes no file name and leaves the header modification time at 0.
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(tar.GetBuffer(), 0, (int)tar.Length);
        var bytes = compressed.ToArray();
        // RFC 1952 header: bytes 4-7 are the modification time (left at 0); byte 9 is the OS, which zlib sets per
        // build platform. Use 255 ("unknown") so the same input gives the same archive on every platform.
        bytes[9] = 0xff;
        return bytes;
    }

    /// <summary>Returns <c>sha256:</c> and the lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Digest(ReadOnlySpan<byte> bytes) =>
        "sha256:" + System.Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImmutableArray<string> ValidateInputs(
        AgentSuiteBuildInputs inputs,
        ImmutableArray<string> roster,
        List<string> errors)
    {
        // The build profile ID is used in file names and references, so it must match the AgentSuite ID grammar.
        if (string.IsNullOrEmpty(inputs.BuildProfileId) || !IdPattern.IsMatch(inputs.BuildProfileId))
            errors.Add("BuildProfileId must match ^[a-z0-9][a-z0-9._-]{0,127}$.");
        // Reproducible builds need a real, positive build time.
        if (inputs.SourceEpoch < 1)
            errors.Add("SourceEpoch must be at least 1.");
        ValidateModel(inputs.Model, "Model", errors);
        // A role model for a role that isn't in the roster is almost always a mistake, so reject it.
        foreach (var (roleId, model) in inputs.RoleModels)
        {
            if (!roster.Contains(roleId, StringComparer.Ordinal))
                errors.Add($"RoleModels role '{roleId}' isn't in the roster.");
            ValidateModel(model, $"RoleModels['{roleId}']", errors);
        }

        // Every composition needs both a runtime base image and a harness image for its architecture.
        var runtime = ValidateImages(inputs.RuntimeBase, "RuntimeBase", errors);
        var harness = ValidateImages(inputs.Harness, "Harness", errors);
        if (!runtime.SetEquals(harness))
            errors.Add("RuntimeBase and Harness must list images for the same architectures.");
        // Sorted so compositions are generated in a stable order.
        return runtime.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static void ValidateModel(AgentSuiteModelInput? model, string name, List<string> errors)
    {
        if (model is null || string.IsNullOrWhiteSpace(model.Protocol) || string.IsNullOrWhiteSpace(model.Model))
            errors.Add($"{name} must have a protocol and a model.");
    }

    private static HashSet<string> ValidateImages(
        ImmutableArray<AgentSuiteImageInput> images,
        string name,
        List<string> errors)
    {
        var architectures = new HashSet<string>(StringComparer.Ordinal);
        // The converter never invents images, so at least one real image is required.
        if (images.IsDefaultOrEmpty)
        {
            errors.Add($"{name} must list at least one digest-pinned image.");
            return architectures;
        }
        foreach (var image in images)
        {
            if (image is null)
            {
                errors.Add($"{name} must not contain empty entries.");
                continue;
            }
            // Only the two Linux architectures the AgentSuite spec supports are allowed, each at most once.
            if (image.Architecture is not ("amd64" or "arm64"))
                errors.Add($"{name} architecture '{image.Architecture}' must be amd64 or arm64.");
            else if (!architectures.Add(image.Architecture))
                errors.Add($"{name} lists architecture '{image.Architecture}' more than once.");
            // Images must be pinned by digest. Tags are rejected because a tag can move to a different image.
            if (string.IsNullOrEmpty(image.ImageRef) || !ImageRefPattern.IsMatch(image.ImageRef))
                errors.Add($"{name} image '{image.ImageRef}' must be registry/path@sha256:<64 hex> with no tag.");
            // The descriptor must carry the real manifest size so consumers can verify what they fetch.
            if (image.Size < 1)
                errors.Add($"{name} image '{image.ImageRef}' must have the image manifest size.");
        }
        return architectures;
    }

    private static JsonArray Images(ImmutableArray<AgentSuiteImageInput> images) =>
        new(images
            // Sort by architecture so the build profile bytes are deterministic.
            .OrderBy(image => image.Architecture, StringComparer.Ordinal)
            .Select(image => (JsonNode)new JsonObject
            {
                ["platform"] = Platform(image.Architecture),
                ["imageRef"] = image.ImageRef,
                ["image"] = new JsonObject
                {
                    ["mediaType"] = ImageManifestMediaType,
                    // The digest is the part of the image reference after '@'.
                    ["digest"] = image.ImageRef[(image.ImageRef.LastIndexOf('@') + 1)..],
                    ["size"] = image.Size,
                },
            })
            .ToArray());

    private static JsonObject Platform(string architecture) =>
        new() { ["os"] = "linux", ["architecture"] = architecture };

    private static JsonObject Descriptor(string mediaType, byte[] bytes) =>
        new() { ["mediaType"] = mediaType, ["digest"] = Digest(bytes), ["size"] = bytes.LongLength };

    // Canonical JSON bytes, so file digests are the same on every run and platform.
    private static byte[] Canonical(JsonObject value) =>
        CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(value.ToJsonString()));
}
