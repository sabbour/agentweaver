using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.Projects.Config;

internal sealed record AgentApplicationManifestValidationIssue(
    string Code,
    string Path,
    string Message);

internal sealed record AgentApplicationInventoryEntry(
    string Path,
    long Size,
    string Sha256);

internal sealed record AgentApplicationDependencyRequirement(
    string AppId,
    string MinimumVersion,
    string MaximumVersionExclusive,
    string? ManifestDigest,
    string Kind);

internal sealed record AgentApplicationDependencyNode(
    string AppId,
    string Version,
    string ManifestDigest,
    ImmutableArray<AgentApplicationDependencyRequirement> Dependencies);

internal sealed record AgentApplicationDependencyLockEntry(
    string AppId,
    string Version,
    string ManifestDigest,
    string Kind);

internal sealed record AgentApplicationHostCompatibility(
    string Contract,
    string ProductVersion,
    ImmutableDictionary<string, int> ComponentSchemas,
    bool AllowPrerelease);

internal sealed record AgentApplicationManifestValidationResult(
    string ConfigurationSha256,
    ImmutableArray<AgentApplicationManifestValidationIssue> Issues,
    ImmutableArray<AgentApplicationDependencyLockEntry> DependencyLock)
{
    public bool IsValid => Issues.IsEmpty;
}

internal static class AgentApplicationManifestValidator
{
    private const int MaximumConfigurationBytes = 1_048_576;
    private const int MaximumContentFiles = 256;
    private const long MaximumExpandedFileBytes = 1_048_576;
    private const long MaximumExpandedContentBytes = 16_777_216;
    private const long MaximumCompressedLayerBytes = 16_777_216;
    private const int MaximumPackagePathLength = 240;
    private const int MaximumJsonDepth = 64;
    private const int MaximumJsonNumberTokenLength = 4_096;
    private const int MaximumVersionLength = 4_096;
    private const int MaximumIdentifierLength = 64;
    private const string ApplicationSchema = "agentweaver.application/1";

    private static readonly Regex DigestPattern = new(
        @"\Asha256:[0-9a-f]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Regex MediaTypePattern = new(
        @"\A[a-z0-9][a-z0-9!#$&^_.+-]*/[a-z0-9][a-z0-9!#$&^_.+-]*\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaximumJsonDepth,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static AgentApplicationManifestValidationResult Validate(
        ReadOnlyMemory<byte> configuration,
        ImmutableArray<AgentApplicationInventoryEntry> inventory,
        long compressedLayerBytes,
        ImmutableArray<AgentApplicationDependencyNode> dependencyGraph,
        ProviderCatalog providerCatalog,
        AgentApplicationHostCompatibility hostCompatibility)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(hostCompatibility);

        var configurationSha256 = Convert.ToHexString(SHA256.HashData(configuration.Span)).ToLowerInvariant();
        var issues = ImmutableArray.CreateBuilder<AgentApplicationManifestValidationIssue>();
        if (configuration.Length > MaximumConfigurationBytes)
        {
            Add(issues, "configuration_too_large", "configuration",
                $"Configuration exceeds the {MaximumConfigurationBytes}-byte limit.");
            return Result(configurationSha256, issues);
        }
        if (compressedLayerBytes < 0 || compressedLayerBytes > MaximumCompressedLayerBytes)
            Add(issues, "compressed_layer_too_large", "content",
                $"Compressed content must not exceed {MaximumCompressedLayerBytes} bytes.");
        if (inventory.IsDefault)
            Add(issues, "invalid_inventory", "inventory", "Content inventory must be initialized.");
        else if (inventory.Length > MaximumContentFiles)
            Add(issues, "inventory_too_large", "inventory",
                $"Content inventory must contain no more than {MaximumContentFiles} files.");
        if (dependencyGraph.IsDefault)
            Add(issues, "invalid_dependency_graph", "dependencies",
                "Resolved dependency graph must be initialized.");
        if (issues.Count > 0)
            return Result(configurationSha256, issues);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(configuration, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
        }
        catch (JsonException exception)
        {
            Add(issues, "invalid_json", "configuration", exception.Message);
            return Result(configurationSha256, issues);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Add(issues, "invalid_manifest", "configuration", "Application configuration must be a JSON object.");
                return Result(configurationSha256, issues);
            }
            ValidateJsonProperties(document.RootElement, "$", issues);
            if (issues.Count > 0)
                return Result(configurationSha256, issues);

            ManifestDocument? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ManifestDocument>(document.RootElement, JsonOptions);
            }
            catch (JsonException exception)
            {
                Add(issues, "invalid_manifest", exception.Path ?? "configuration", exception.Message);
                return Result(configurationSha256, issues);
            }

            if (manifest is null)
            {
                Add(issues, "invalid_manifest", "configuration", "Application manifest is required.");
                return Result(configurationSha256, issues);
            }

            var appId = ValidateManifestIdentity(manifest, issues);
            var components = ValidateComponents(manifest.Components, issues);
            ValidateHost(manifest.Host, hostCompatibility, issues);
            ValidateEntryPoints(manifest.EntryPoints, components, issues);
            ValidateInventory(manifest.Files, inventory, manifest.Host, components, issues);
            ValidateConfigurationInputs(manifest.ConfigurationInputs, issues);
            ValidateConstraints(manifest.Constraints, issues);

            if (manifest.CanvasRequirements is { } canvasRequirements &&
                (canvasRequirements.ValueKind != JsonValueKind.Array ||
                 canvasRequirements.GetArrayLength() != 0))
                Add(issues, "unsupported_canvas_requirement", "canvasRequirements",
                    "Canvas requirements are not supported by the current host contract.");
            if (components.TryGetValue("canvases", out var canvases) && canvases.Count > 0)
                Add(issues, "unsupported_canvas_component", "components.canvases",
                    "Canvas components require a supported Canvas host contract.");

            var parsedDependencies = ParseDependencies(manifest.Dependencies, issues);
            var lockEntries = ValidateDependencyGraph(
                appId,
                manifest.Version,
                parsedDependencies,
                dependencyGraph,
                issues);

            ValidateProviderRequirements(manifest.ProviderRequirements, providerCatalog, issues);

            return new AgentApplicationManifestValidationResult(
                configurationSha256,
                issues.ToImmutable(),
                issues.Count == 0 ? lockEntries : []);
        }
    }

    private static string? ValidateManifestIdentity(
        ManifestDocument manifest,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (!string.Equals(manifest.SchemaVersion, ApplicationSchema, StringComparison.Ordinal))
            Add(issues, "unsupported_schema", "schemaVersion",
                $"Only '{ApplicationSchema}' is supported.");
        if (!IsPublisherQualifiedAppId(manifest.AppId))
            Add(issues, "invalid_app_id", "appId",
                "Application ID must be a stable publisher-qualified identifier.");
        if (!ApplicationSemanticVersion.TryParse(manifest.Version, out _))
            Add(issues, "invalid_app_version", "version", "Application version must be SemVer 2.0.0.");
        return IsPublisherQualifiedAppId(manifest.AppId) ? manifest.AppId : null;
    }

    private static Dictionary<string, HashSet<string>> ValidateComponents(
        ComponentDeclarations? declarations,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (declarations is null)
        {
            Add(issues, "invalid_components", "components", "Component declarations are required.");
            return result;
        }

        AddComponentCategory("agents", declarations.Agents);
        AddComponentCategory("tools", declarations.Tools);
        AddComponentCategory("skills", declarations.Skills);
        AddComponentCategory("policies", declarations.Policies);
        AddComponentCategory("canvases", declarations.Canvases);
        AddComponentCategory("workflows", declarations.Workflows);
        AddComponentCategory("assets", declarations.Assets);
        return result;

        void AddComponentCategory(string category, string[]? identifiers)
        {
            if (identifiers is null)
            {
                Add(issues, "invalid_components", $"components.{category}",
                    "Component category must be an initialized array.");
                result[category] = new HashSet<string>(StringComparer.Ordinal);
                return;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < identifiers.Length; index++)
            {
                var id = identifiers[index];
                if (!IsStableIdentifier(id))
                    Add(issues, "invalid_component_id", $"components.{category}[{index}]",
                        "Component ID must be a stable identifier.");
                else if (!ids.Add(id))
                    Add(issues, "duplicate_component_id", $"components.{category}[{index}]",
                        $"Component ID '{id}' is duplicated in '{category}'.");
            }
            result[category] = ids;
        }
    }

    private static void ValidateHost(
        HostDocument? host,
        AgentApplicationHostCompatibility compatibility,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (host is null)
        {
            Add(issues, "invalid_host_requirement", "host", "Host requirement is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(host.Contract) ||
            !string.Equals(host.Contract, compatibility.Contract, StringComparison.Ordinal))
            Add(issues, "incompatible_host_contract", "host.contract",
                "Required host contract is not supported by the supplied host compatibility context.");
        if (!ApplicationSemanticVersion.TryParse(host.MinimumVersion, out var minimum) ||
            !ApplicationSemanticVersion.TryParse(host.MaximumVersionExclusive, out var maximum) ||
            minimum!.CompareTo(maximum) >= 0)
        {
            Add(issues, "invalid_host_version_range", "host",
                "Host versions must be a valid nonempty SemVer 2.0.0 interval.");
        }
        else if (!ApplicationSemanticVersion.TryParse(compatibility.ProductVersion, out var current))
        {
            Add(issues, "invalid_host_compatibility_context", "host",
                "Current host product version must be SemVer 2.0.0.");
        }
        else if (current!.PreRelease.Length > 0 && !compatibility.AllowPrerelease)
        {
            Add(issues, "incompatible_host_prerelease", "host",
                "Pre-release host compatibility requires an explicit host admission rule.");
        }
        else if (current.CompareTo(minimum) < 0 || current.CompareTo(maximum) >= 0)
        {
            Add(issues, "incompatible_host_version", "host",
                "Current host product version is outside the required inclusive/exclusive range.");
        }

        if (host.ComponentSchemas is null)
        {
            Add(issues, "invalid_component_schemas", "host.componentSchemas",
                "Required component schemas must be an object.");
            return;
        }
        if (compatibility.ComponentSchemas is null)
        {
            Add(issues, "invalid_host_compatibility_context", "host.componentSchemas",
                "Supported component schemas must be supplied by the host compatibility context.");
            return;
        }
        foreach (var (schema, version) in host.ComponentSchemas)
        {
            if (!IsStableIdentifier(schema) || version < 1)
            {
                Add(issues, "invalid_component_schema", $"host.componentSchemas.{schema}",
                    "Component schema names must be stable identifiers with positive versions.");
            }
            else if (!compatibility.ComponentSchemas.TryGetValue(schema, out var supported) ||
                     supported != version)
            {
                Add(issues, "unsupported_component_schema", $"host.componentSchemas.{schema}",
                    $"Host does not support required component schema '{schema}' version {version}.");
            }
        }
    }

    private static void ValidateEntryPoints(
        EntryPointDocument[]? entryPoints,
        IReadOnlyDictionary<string, HashSet<string>> components,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (entryPoints is null)
        {
            Add(issues, "invalid_entry_points", "entryPoints", "Entry points must be an initialized array.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entryPoints.Length; index++)
        {
            var entry = entryPoints[index];
            var path = $"entryPoints[{index}]";
            if (entry is null)
            {
                Add(issues, "invalid_entry_point", path, "Entry point is required.");
                continue;
            }
            if (!IsStableIdentifier(entry.Id) || !ids.Add(entry.Id ?? string.Empty))
                Add(issues, "invalid_entry_point_id", path + ".id",
                    "Entry point IDs must be stable and unique.");
            if (entry.InputSchema.ValueKind != JsonValueKind.Object)
                Add(issues, "invalid_entry_point_schema", path + ".inputSchema",
                    "Entry point input schema must be a JSON object.");

            var category = entry.Kind switch
            {
                "agent" => "agents",
                "workflow" => "workflows",
                "surface" => "canvases",
                _ => null,
            };
            if (category is null)
            {
                Add(issues, "unsupported_entry_point_kind", path + ".kind",
                    $"Entry point kind '{entry.Kind}' is not supported.");
            }
            else if (!IsStableIdentifier(entry.ComponentId) ||
                     !components.TryGetValue(category, out var idsInCategory) ||
                     !idsInCategory.Contains(entry.ComponentId!))
            {
                Add(issues, "unresolved_component_reference", path + ".componentId",
                    "Entry point component reference does not resolve to a declared component.");
            }
        }
    }

    private static void ValidateInventory(
        FileDocument[]? declaredFiles,
        ImmutableArray<AgentApplicationInventoryEntry> actualInventory,
        HostDocument? host,
        IReadOnlyDictionary<string, HashSet<string>> components,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (declaredFiles is null)
        {
            Add(issues, "invalid_inventory", "files", "Manifest file inventory is required.");
            return;
        }
        if (declaredFiles.Length > MaximumContentFiles)
            Add(issues, "inventory_too_large", "files",
                $"Manifest file inventory must contain no more than {MaximumContentFiles} files.");
        if (actualInventory.Length > MaximumContentFiles)
            Add(issues, "inventory_too_large", "inventory",
                $"Content inventory must contain no more than {MaximumContentFiles} files.");

        var schemas = host?.ComponentSchemas;
        var declaredByPath = new Dictionary<string, FileDocument>(StringComparer.Ordinal);
        var collisionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        var count = Math.Min(declaredFiles.Length, MaximumContentFiles);
        for (var index = 0; index < count; index++)
        {
            var file = declaredFiles[index];
            var path = $"files[{index}]";
            if (file is null)
            {
                Add(issues, "invalid_inventory_entry", path, "File inventory entry is required.");
                continue;
            }
            var fileKind = ComponentCategory(file.Kind);
            if (fileKind is null)
                Add(issues, "unsupported_file_kind", path + ".kind",
                    $"File kind '{file.Kind}' is not supported.");
            if (!IsStableIdentifier(file.Id))
                Add(issues, "invalid_file_component_id", path + ".id",
                    "File component ID must be a stable identifier.");
            else if (fileKind is not null &&
                     (!components.TryGetValue(fileKind, out var ids) || !ids.Contains(file.Id!)))
                Add(issues, "unresolved_file_component", path + ".id",
                    "File inventory component reference does not resolve.");
            if (file.SchemaVersion < 1)
                Add(issues, "invalid_file_schema", path + ".schemaVersion",
                    "File schema version must be positive.");
            if (!IsCanonicalPackagePath(file.Path))
                Add(issues, "invalid_package_path", path + ".path",
                    "Package paths must be canonical, relative slash-separated paths within the path limit.");
            else
            {
                var collisionKey = file.Path!.Normalize(NormalizationForm.FormC);
                if (!collisionKeys.Add(collisionKey))
                    Add(issues, "colliding_package_path", path + ".path",
                        "Package paths cannot collide by case or Unicode normalization.");
                if (fileKind is not null && !file.Path.StartsWith(fileKind + "/", StringComparison.Ordinal))
                    Add(issues, "invalid_package_path_kind", path + ".path",
                        $"Files of this kind must be under '{fileKind}/'.");
                if (!declaredByPath.TryAdd(file.Path, file))
                    Add(issues, "duplicate_package_path", path + ".path",
                        $"Package path '{file.Path}' is declared more than once.");
            }
            if (!MediaTypePattern.IsMatch(file.MediaType ?? string.Empty))
                Add(issues, "invalid_media_type", path + ".mediaType", "File media type is invalid.");
            if (file.Size < 0 || file.Size > MaximumExpandedFileBytes)
                Add(issues, "file_too_large", path + ".size",
                    $"Expanded file size must not exceed {MaximumExpandedFileBytes} bytes.");
            else if (declaredTotal > MaximumExpandedContentBytes - file.Size)
                Add(issues, "expanded_content_too_large", "files",
                    $"Expanded content must not exceed {MaximumExpandedContentBytes} bytes.");
            else
                declaredTotal += file.Size;
            if (!IsSha256(file.Digest))
                Add(issues, "invalid_file_digest", path + ".digest",
                    "File digest must be a lower-case SHA-256 digest.");

            var schema = ComponentSchema(file.Kind);
            if (schema is not null &&
                (schemas is null || !schemas.TryGetValue(schema, out var requiredSchema) ||
                 requiredSchema != file.SchemaVersion))
                Add(issues, "unsupported_file_schema", path + ".schemaVersion",
                    $"File schema version does not match the required host schema '{schema}'.");
        }

        var actualByPath = new Dictionary<string, AgentApplicationInventoryEntry>(StringComparer.Ordinal);
        var actualCollisionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long actualTotal = 0;
        var actualCount = Math.Min(actualInventory.Length, MaximumContentFiles);
        for (var index = 0; index < actualCount; index++)
        {
            var file = actualInventory[index];
            var path = $"inventory[{index}]";
            if (file is null)
            {
                Add(issues, "invalid_inventory_entry", path, "Content inventory entry is required.");
                continue;
            }
            if (!IsCanonicalPackagePath(file.Path))
            {
                Add(issues, "invalid_package_path", path + ".path",
                    "Content inventory contains a noncanonical package path.");
                continue;
            }
            var collisionKey = file.Path.Normalize(NormalizationForm.FormC);
            if (!actualCollisionKeys.Add(collisionKey))
                Add(issues, "colliding_package_path", path + ".path",
                    "Content paths cannot collide by case or Unicode normalization.");
            if (file.Size < 0 || file.Size > MaximumExpandedFileBytes)
                Add(issues, "file_too_large", path + ".size",
                    $"Expanded file size must not exceed {MaximumExpandedFileBytes} bytes.");
            else if (actualTotal > MaximumExpandedContentBytes - file.Size)
                Add(issues, "expanded_content_too_large", "inventory",
                    $"Expanded content must not exceed {MaximumExpandedContentBytes} bytes.");
            else
                actualTotal += file.Size;
            if (!IsSha256(file.Sha256))
                Add(issues, "invalid_file_digest", path + ".sha256",
                    "Content digest must be a lower-case SHA-256 digest.");
            if (!actualByPath.TryAdd(file.Path, file))
                Add(issues, "duplicate_package_path", path + ".path",
                    $"Content path '{file.Path}' appears more than once.");
        }

        foreach (var (path, file) in declaredByPath)
        {
            if (!actualByPath.TryGetValue(path, out var actual))
            {
                Add(issues, "missing_inventory_file", "inventory",
                    $"Declared content file '{path}' is missing.");
                continue;
            }
            if (actual.Size != file.Size)
                Add(issues, "inventory_size_mismatch", path,
                    "Declared and actual content sizes differ.");
            if (!string.Equals(actual.Sha256, file.Digest, StringComparison.Ordinal))
                Add(issues, "inventory_digest_mismatch", path,
                    "Declared and actual content digests differ.");
        }
        foreach (var path in actualByPath.Keys)
            if (!declaredByPath.ContainsKey(path))
                Add(issues, "unlisted_inventory_file", path,
                    "Content file is not declared by the manifest.");
    }

    private static void ValidateConfigurationInputs(
        ConfigurationInputDocument[]? inputs,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (inputs is null)
            return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            var path = $"configurationInputs[{index}]";
            if (input is null)
            {
                Add(issues, "invalid_configuration_input", path, "Configuration input is required.");
                continue;
            }
            if (!IsStableIdentifier(input.Name) || !names.Add(input.Name ?? string.Empty))
                Add(issues, "invalid_configuration_input_name", path + ".name",
                    "Configuration input names must be stable and unique.");
            if (input.Type == "modelSelectionReference")
            {
                if (input.Purpose is not null || input.Scope is not null)
                    Add(issues, "invalid_configuration_input", path,
                        "Model selection references cannot declare credential purpose or scope.");
            }
            else if (input.Type == "SecretRef")
            {
                if (string.IsNullOrWhiteSpace(input.Purpose) || input.Scope != "run")
                    Add(issues, "invalid_configuration_input", path,
                        "Protected credential slots require a purpose and run scope.");
            }
            else
            {
                Add(issues, "unsupported_configuration_input", path + ".type",
                    $"Configuration input type '{input.Type}' is not supported.");
            }
        }
    }

    private static void ValidateConstraints(
        JsonElement? constraints,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (constraints is { } value && value.ValueKind != JsonValueKind.Object)
            Add(issues, "invalid_constraints", "constraints", "Constraints must be a JSON object.");
    }

    private static ImmutableArray<AgentApplicationDependencyRequirement> ParseDependencies(
        DependencyRequirementDocument[]? dependencies,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (dependencies is null)
        {
            Add(issues, "invalid_dependencies", "dependencies", "Dependencies must be an initialized array.");
            return [];
        }

        var result = ImmutableArray.CreateBuilder<AgentApplicationDependencyRequirement>(dependencies.Length);
        for (var index = 0; index < dependencies.Length; index++)
        {
            var dependency = dependencies[index];
            if (dependency is null)
            {
                Add(issues, "invalid_dependency", $"dependencies[{index}]", "Dependency requirement is required.");
                continue;
            }
            result.Add(new AgentApplicationDependencyRequirement(
                dependency.AppId ?? string.Empty,
                dependency.MinimumVersion ?? string.Empty,
                dependency.MaximumVersionExclusive ?? string.Empty,
                dependency.ManifestDigest,
                dependency.Kind ?? string.Empty));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<AgentApplicationDependencyLockEntry> ValidateDependencyGraph(
        string? rootAppId,
        string? rootVersion,
        ImmutableArray<AgentApplicationDependencyRequirement> rootDependencies,
        ImmutableArray<AgentApplicationDependencyNode> dependencies,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (rootAppId is null || !ApplicationSemanticVersion.TryParse(rootVersion, out _))
            return [];

        var nodes = new Dictionary<string, DependencyGraphNode>(StringComparer.Ordinal)
        {
            [rootAppId] = new(rootAppId, rootVersion!, null, rootDependencies),
        };
        foreach (var dependency in dependencies)
        {
            if (dependency is null)
            {
                Add(issues, "invalid_dependency_node", "dependencies", "Resolved dependency entry is required.");
                continue;
            }
            if (!IsPublisherQualifiedAppId(dependency.AppId))
            {
                Add(issues, "invalid_dependency_node", $"dependencies.{dependency.AppId}",
                    "Resolved dependency ID must be publisher-qualified.");
                continue;
            }
            if (!ApplicationSemanticVersion.TryParse(dependency.Version, out _))
                Add(issues, "invalid_dependency_node", $"dependencies.{dependency.AppId}",
                    "Resolved dependency version must be SemVer 2.0.0.");
            if (!IsSha256(dependency.ManifestDigest))
                Add(issues, "invalid_dependency_digest", $"dependencies.{dependency.AppId}.manifestDigest",
                    "Resolved dependency manifest digest must be a lower-case SHA-256 digest.");
            if (dependency.Dependencies.IsDefault)
                Add(issues, "invalid_dependency_node", $"dependencies.{dependency.AppId}",
                    "Resolved dependency requirements must be initialized.");
            if (nodes.ContainsKey(dependency.AppId))
            {
                Add(issues, "duplicate_dependency_identity", $"dependencies.{dependency.AppId}",
                    "Dependency graph contains a duplicate or root application identity.");
                continue;
            }
            var childRequirements = ImmutableArray.CreateBuilder<AgentApplicationDependencyRequirement>();
            if (!dependency.Dependencies.IsDefault)
            {
                foreach (var requirement in dependency.Dependencies)
                {
                    if (requirement is null)
                    {
                        Add(issues, "invalid_dependency", $"dependencies.{dependency.AppId}",
                            "Dependency requirement is required.");
                        continue;
                    }
                    childRequirements.Add(requirement);
                }
            }
            nodes.Add(dependency.AppId, new DependencyGraphNode(
                dependency.AppId,
                dependency.Version,
                dependency.ManifestDigest,
                childRequirements.ToImmutable()));
        }

        var selectedKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var directIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var requirement in node.Dependencies)
            {
                if (!ValidateDependencyRequirement(requirement, node.AppId, directIds, issues))
                    continue;
                if (!nodes.TryGetValue(requirement.AppId, out var selected))
                {
                    Add(issues, "missing_dependency", $"dependencies.{node.AppId}.{requirement.AppId}",
                        "Dependency lock does not contain the required application.");
                    continue;
                }
                if (!ApplicationSemanticVersion.TryParse(selected.Version, out var selectedVersion) ||
                    !ApplicationSemanticVersion.TryParse(requirement.MinimumVersion, out var minimum) ||
                    !ApplicationSemanticVersion.TryParse(requirement.MaximumVersionExclusive, out var maximum))
                    continue;
                if (selectedVersion!.CompareTo(minimum) < 0 || selectedVersion.CompareTo(maximum) >= 0)
                    Add(issues, "dependency_version_conflict", $"dependencies.{node.AppId}.{requirement.AppId}",
                        $"Selected dependency version '{selected.Version}' is outside the required interval.");
                if (requirement.ManifestDigest is { } expectedDigest &&
                    !string.Equals(selected.ManifestDigest, expectedDigest, StringComparison.Ordinal))
                    Add(issues, "dependency_digest_conflict", $"dependencies.{node.AppId}.{requirement.AppId}",
                        "Selected dependency manifest digest does not match the exact required digest.");
                if (selectedKinds.TryGetValue(requirement.AppId, out var priorKind) &&
                    !string.Equals(priorKind, requirement.Kind, StringComparison.Ordinal))
                    Add(issues, "dependency_kind_conflict", $"dependencies.{requirement.AppId}",
                        "Dependency is required with conflicting dependency kinds.");
                else
                    selectedKinds[requirement.AppId] = requirement.Kind;
            }
        }

        var cycle = HasDependencyCycle(nodes);
        if (cycle)
            Add(issues, "dependency_cycle", "dependencies", "Resolved application dependency graph contains a cycle.");

        var reachable = ReachableDependencies(rootAppId, nodes);
        foreach (var appId in nodes.Keys)
            if (!reachable.Contains(appId))
                Add(issues, "unreachable_dependency", $"dependencies.{appId}",
                    "Dependency lock contains an application that is not reachable from the root manifest.");

        return [.. nodes.Values
            .Where(node => !string.Equals(node.AppId, rootAppId, StringComparison.Ordinal) &&
                           selectedKinds.ContainsKey(node.AppId))
            .OrderBy(node => node.AppId, StringComparer.Ordinal)
            .Select(node => new AgentApplicationDependencyLockEntry(
                node.AppId, node.Version, node.ManifestDigest!, selectedKinds[node.AppId]))];
    }

    private static bool ValidateDependencyRequirement(
        AgentApplicationDependencyRequirement requirement,
        string sourceAppId,
        HashSet<string> directIds,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        var path = $"dependencies.{sourceAppId}.{requirement.AppId}";
        if (!IsPublisherQualifiedAppId(requirement.AppId))
        {
            Add(issues, "invalid_dependency_id", path, "Dependency ID must be publisher-qualified.");
            return false;
        }
        var valid = true;
        if (!directIds.Add(requirement.AppId))
        {
            Add(issues, "duplicate_dependency", path, "A dependency may be declared only once per application.");
            valid = false;
        }
        if (!ApplicationSemanticVersion.TryParse(requirement.MinimumVersion, out var minimum) ||
            !ApplicationSemanticVersion.TryParse(requirement.MaximumVersionExclusive, out var maximum) ||
            minimum!.CompareTo(maximum) >= 0)
        {
            Add(issues, "invalid_dependency_version_range", path,
                "Dependency range must be a nonempty inclusive/exclusive SemVer 2.0.0 interval.");
            valid = false;
        }
        if (requirement.ManifestDigest is { } digest && !IsSha256(digest))
        {
            Add(issues, "invalid_dependency_digest", path,
                "Exact dependency digest must be a lower-case SHA-256 digest.");
            valid = false;
        }
        if (requirement.Kind is not ("reusableContent" or "requiredInstalledApp"))
        {
            Add(issues, "unsupported_dependency_kind", path + ".kind",
                $"Dependency kind '{requirement.Kind}' is not supported.");
            valid = false;
        }
        return valid;
    }

    private static bool HasDependencyCycle(IReadOnlyDictionary<string, DependencyGraphNode> nodes)
    {
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var start in nodes.Keys)
        {
            if (state.TryGetValue(start, out var color) && color == 2)
                continue;

            var stack = new Stack<(string AppId, int NextDependency)>();
            state[start] = 1;
            stack.Push((start, 0));
            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                var node = nodes[frame.AppId];
                if (frame.NextDependency >= node.Dependencies.Length)
                {
                    state[frame.AppId] = 2;
                    continue;
                }

                stack.Push((frame.AppId, frame.NextDependency + 1));
                var dependencyId = node.Dependencies[frame.NextDependency].AppId;
                if (!nodes.ContainsKey(dependencyId))
                    continue;
                if (state.TryGetValue(dependencyId, out var dependencyColor))
                {
                    if (dependencyColor == 1)
                        return true;
                    if (dependencyColor == 2)
                        continue;
                }
                state[dependencyId] = 1;
                stack.Push((dependencyId, 0));
            }
        }
        return false;
    }

    private static HashSet<string> ReachableDependencies(
        string rootAppId,
        IReadOnlyDictionary<string, DependencyGraphNode> nodes)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(rootAppId);
        while (pending.TryPop(out var appId))
        {
            if (!reachable.Add(appId) || !nodes.TryGetValue(appId, out var node))
                continue;
            foreach (var dependency in node.Dependencies)
                if (nodes.ContainsKey(dependency.AppId))
                    pending.Push(dependency.AppId);
        }
        return reachable;
    }

    private static void ValidateProviderRequirements(
        ProviderRequirementDocument[]? requirements,
        ProviderCatalog catalog,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        if (requirements is null)
        {
            Add(issues, "invalid_provider_requirements", "providerRequirements",
                "Provider requirements must be an initialized array.");
            return;
        }

        var resolver = new ProviderResolver(catalog);
        var seen = new HashSet<(ProviderSeam Seam, string? MeterSource)>();
        for (var index = 0; index < requirements.Length; index++)
        {
            var requirement = requirements[index];
            var path = $"providerRequirements[{index}]";
            if (requirement is null ||
                !TryParseSeam(requirement.Seam, out var seam) ||
                !Version.TryParse(requirement.RequiredAdapterVersion, out var adapterVersion) ||
                adapterVersion is null || adapterVersion.Build < 0 ||
                requirement.RequiredOptionsSchemaVersion < 1 ||
                requirement.RequiredCapabilities is null ||
                requirement.RequiredCapabilities.Any(string.IsNullOrWhiteSpace))
            {
                Add(issues, "invalid_provider_requirement", path,
                    "Provider requirement seam, exact adapter/options versions, or capabilities are invalid.");
                continue;
            }

            var meterSource = requirement.MeterSource;
            if (ProviderSeams.Cardinality(seam) == ProviderCardinality.KeyedByMeterSource)
            {
                if (!IsStableIdentifier(meterSource))
                {
                    Add(issues, "invalid_provider_requirement", path + ".meterSource",
                        "Cost requirements need a stable meter source.");
                    continue;
                }
            }
            else if (meterSource is not null)
            {
                Add(issues, "invalid_provider_requirement", path + ".meterSource",
                    "Only cost requirements may name a meter source.");
                continue;
            }

            if (!seen.Add((seam, meterSource)))
            {
                Add(issues, "duplicate_provider_requirement", path,
                    "Provider requirement duplicates an existing seam and meter-source requirement.");
                continue;
            }

            var capabilities = ToCapabilities(requirement.RequiredCapabilities, path + ".requiredCapabilities", issues);
            var l3l4 = ToCapabilities(requirement.RequiredL3L4Capabilities ?? [], path + ".requiredL3L4Capabilities", issues);
            var l7 = ToCapabilities(requirement.RequiredL7Capabilities ?? [], path + ".requiredL7Capabilities", issues);
            if (capabilities is null || l3l4 is null || l7 is null)
                continue;

            var cardinality = ProviderSeams.Cardinality(seam);
            if (seam == ProviderSeam.NetworkPolicy)
            {
                if (capabilities.Count != 0)
                {
                    Add(issues, "invalid_provider_requirement", path + ".requiredCapabilities",
                        "Network Policy requirements must use layer-specific capability fields.");
                    continue;
                }
                var result = resolver.ResolveNetworkPolicy(new NetworkPolicyResolutionRequest(
                    adapterVersion,
                    requirement.RequiredOptionsSchemaVersion,
                    l3l4,
                    l7));
                if (!result.IsSuccess)
                    Add(issues, "provider_incompatible", path,
                        $"{result.Error!.Code}: {result.Error.Message}");
            }
            else if (l3l4.Count > 0 || l7.Count > 0)
            {
                Add(issues, "invalid_provider_requirement", path,
                    "Layer-specific capability fields are valid only for Network Policy.");
            }
            else if (cardinality is ProviderCardinality.Exclusive or ProviderCardinality.PlatformSingleton)
            {
                var result = resolver.Resolve(new ProviderResolutionRequest(
                    seam, null, adapterVersion, requirement.RequiredOptionsSchemaVersion, capabilities));
                if (!result.IsSuccess)
                    Add(issues, "provider_incompatible", path,
                        $"{result.Error!.Code}: {result.Error.Message}");
            }
            else if (cardinality == ProviderCardinality.OrderedComposite)
            {
                var result = resolver.ResolveOrdered(new OrderedProviderResolutionRequest(
                    seam, null, adapterVersion, requirement.RequiredOptionsSchemaVersion, capabilities));
                if (!result.IsSuccess)
                    Add(issues, "provider_incompatible", path,
                        $"{result.Error!.Code}: {result.Error.Message}");
            }
            else if (cardinality == ProviderCardinality.KeyedByMeterSource)
            {
                var result = resolver.ResolveCost(new CostProviderResolutionRequest(
                    meterSource!, adapterVersion, requirement.RequiredOptionsSchemaVersion, capabilities));
                if (!result.IsSuccess)
                    Add(issues, "provider_incompatible", path,
                        $"{result.Error!.Code}: {result.Error.Message}");
            }
            else
            {
                Add(issues, "unsupported_provider_cardinality", path,
                    $"Provider seam '{seam}' has no bundle compatibility resolver.");
            }
        }
    }

    private static ImmutableHashSet<string>? ToCapabilities(
        string[] values,
        string path,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var capability in values)
        {
            if (!IsStableIdentifier(capability))
            {
                Add(issues, "invalid_provider_capability", path,
                    "Provider capabilities must be stable identifiers.");
                return null;
            }
            if (!capabilities.Add(capability))
            {
                Add(issues, "duplicate_provider_capability", path,
                    $"Provider capability '{capability}' is duplicated.");
                return null;
            }
        }
        return capabilities.ToImmutable();
    }

    private static bool TryParseSeam(string? value, out ProviderSeam seam)
    {
        if (Enum.TryParse(value, ignoreCase: false, out seam) &&
            Enum.IsDefined(seam) &&
            string.Equals(Enum.GetName(seam), value, StringComparison.Ordinal))
            return true;
        seam = default;
        return false;
    }

    private static string? ComponentCategory(string? kind) => kind switch
    {
        "agent" => "agents",
        "tool" => "tools",
        "skill" => "skills",
        "policy" => "policies",
        "canvas" => "canvases",
        "workflow" => "workflows",
        "asset" => "assets",
        _ => null,
    };

    private static string? ComponentSchema(string? kind) => kind switch
    {
        "agent" => "agentCharter",
        "tool" => "tool",
        "skill" => "skill",
        "policy" => "policy",
        "canvas" => "canvas",
        "workflow" => "workflowDefinition",
        "asset" => "asset",
        _ => null,
    };

    private static bool IsPublisherQualifiedAppId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256)
            return false;
        var labels = value.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is > 0 and <= 63 &&
            IsAsciiAlphaNumeric(label[0]) &&
            IsAsciiAlphaNumeric(label[^1]) &&
            label.All(character => IsAsciiAlphaNumeric(character) || character == '-') &&
            label.All(character => !char.IsAsciiLetter(character) || char.IsLower(character)));
    }

    private static bool IsStableIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= MaximumIdentifierLength &&
        IsAsciiAlphaNumeric(value[0]) &&
        value.All(character =>
            IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        char.IsAsciiLetterOrDigit(value);

    private static bool IsSha256(string? value) =>
        value is not null && DigestPattern.IsMatch(value);

    private static bool IsCanonicalPackagePath(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumPackagePathLength ||
            value[0] == '/' ||
            value.Contains('\\') ||
            value.Contains(':') ||
            value.Any(character => char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*'))
            return false;
        try
        {
            if (!value.IsNormalized(NormalizationForm.FormC))
                return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                IsReservedWindowsDeviceName(segment))
                return false;
        }
        return true;
    }

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        var basename = segment.Split('.')[0].TrimEnd(' ', '.');
        if (basename.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            basename.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;
        return basename.Length == 4 &&
               (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               basename[3] is >= '1' and <= '9';
    }

    private static void ValidateJsonProperties(
        JsonElement element,
        string path,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        Add(issues, "duplicate_json_property", path,
                            $"JSON object contains duplicate property '{property.Name}'.");
                    ValidateJsonProperties(property.Value, path + "." + property.Name, issues);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    ValidateJsonProperties(item, $"{path}[{index++}]", issues);
                break;
            }
            case JsonValueKind.Number when element.GetRawText().Length > MaximumJsonNumberTokenLength:
                Add(issues, "json_number_too_large", path,
                    $"JSON number token exceeds the {MaximumJsonNumberTokenLength}-character limit.");
                break;
        }
    }

    private static void Add(
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues,
        string code,
        string path,
        string message) =>
        issues.Add(new AgentApplicationManifestValidationIssue(code, path, message));

    private static AgentApplicationManifestValidationResult Result(
        string configurationSha256,
        ImmutableArray<AgentApplicationManifestValidationIssue>.Builder issues) =>
        new(configurationSha256, issues.ToImmutable(), []);

    private sealed record DependencyGraphNode(
        string AppId,
        string Version,
        string? ManifestDigest,
        ImmutableArray<AgentApplicationDependencyRequirement> Dependencies);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ManifestDocument
    {
        public ManifestDocument() { }

        public string? SchemaVersion { get; init; }
        public string? AppId { get; init; }
        public string? Version { get; init; }
        public HostDocument? Host { get; init; }
        public EntryPointDocument[]? EntryPoints { get; init; }
        public ComponentDeclarations? Components { get; init; }
        public FileDocument[]? Files { get; init; }
        public DependencyRequirementDocument[]? Dependencies { get; init; }
        public ProviderRequirementDocument[]? ProviderRequirements { get; init; }
        public JsonElement? CanvasRequirements { get; init; }
        public ConfigurationInputDocument[]? ConfigurationInputs { get; init; }
        public JsonElement? Constraints { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class HostDocument
    {
        public HostDocument() { }

        public string? Contract { get; init; }
        public string? MinimumVersion { get; init; }
        public string? MaximumVersionExclusive { get; init; }
        public Dictionary<string, int>? ComponentSchemas { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class EntryPointDocument
    {
        public EntryPointDocument() { }

        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? ComponentId { get; init; }
        public JsonElement InputSchema { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ComponentDeclarations
    {
        public ComponentDeclarations() { }

        public string[]? Agents { get; init; }
        public string[]? Tools { get; init; }
        public string[]? Skills { get; init; }
        public string[]? Policies { get; init; }
        public string[]? Canvases { get; init; }
        public string[]? Workflows { get; init; }
        public string[]? Assets { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FileDocument
    {
        public FileDocument() { }

        public string? Kind { get; init; }
        public string? Id { get; init; }
        public int SchemaVersion { get; init; }
        public string? Path { get; init; }
        public string? MediaType { get; init; }
        public long Size { get; init; }
        public string? Digest { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class DependencyRequirementDocument
    {
        public DependencyRequirementDocument() { }

        public string? AppId { get; init; }
        public string? MinimumVersion { get; init; }
        public string? MaximumVersionExclusive { get; init; }
        public string? ManifestDigest { get; init; }
        public string? Kind { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ProviderRequirementDocument
    {
        public ProviderRequirementDocument() { }

        public string? Seam { get; init; }
        public string? MeterSource { get; init; }
        public string? RequiredAdapterVersion { get; init; }
        public int RequiredOptionsSchemaVersion { get; init; }
        public string[]? RequiredCapabilities { get; init; }
        public string[]? RequiredL3L4Capabilities { get; init; }
        public string[]? RequiredL7Capabilities { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ConfigurationInputDocument
    {
        public ConfigurationInputDocument() { }

        public string? Name { get; init; }
        public string? Type { get; init; }
        public bool? Required { get; init; }
        public string? Purpose { get; init; }
        public string? Scope { get; init; }
    }

    private sealed class ApplicationSemanticVersion : IComparable<ApplicationSemanticVersion>
    {
        private static readonly Regex Pattern = new(
            @"\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(1));

        private ApplicationSemanticVersion(
            string major,
            string minor,
            string patch,
            ImmutableArray<string> preRelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease;
        }

        public string Major { get; }
        public string Minor { get; }
        public string Patch { get; }
        public ImmutableArray<string> PreRelease { get; }

        public static bool TryParse(string? value, out ApplicationSemanticVersion? version)
        {
            version = null;
            if (value is null || value.Length > MaximumVersionLength)
                return false;
            Match match;
            try
            {
                match = Pattern.Match(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            if (!match.Success)
                return false;

            var preRelease = match.Groups["pre"].Success
                ? match.Groups["pre"].Value.Split('.', StringSplitOptions.None)
                : [];
            if (preRelease.Any(identifier =>
                    identifier.Length > 1 &&
                    identifier[0] == '0' &&
                    identifier.All(char.IsAsciiDigit)))
                return false;

            version = new ApplicationSemanticVersion(
                match.Groups["major"].Value,
                match.Groups["minor"].Value,
                match.Groups["patch"].Value,
                [.. preRelease]);
            return true;
        }

        public int CompareTo(ApplicationSemanticVersion? other)
        {
            if (other is null)
                return 1;
            var result = CompareDecimal(Major, other.Major);
            if (result != 0)
                return result;
            result = CompareDecimal(Minor, other.Minor);
            if (result != 0)
                return result;
            result = CompareDecimal(Patch, other.Patch);
            if (result != 0)
                return result;
            if (PreRelease.Length == 0 || other.PreRelease.Length == 0)
                return PreRelease.Length == other.PreRelease.Length
                    ? 0
                    : PreRelease.Length == 0 ? 1 : -1;

            for (var index = 0; index < Math.Min(PreRelease.Length, other.PreRelease.Length); index++)
            {
                result = ComparePrerelease(PreRelease[index], other.PreRelease[index]);
                if (result != 0)
                    return result;
            }
            return PreRelease.Length.CompareTo(other.PreRelease.Length);
        }

        private static int ComparePrerelease(string left, string right)
        {
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            if (leftNumeric && rightNumeric)
                return CompareDecimal(left, right);
            if (leftNumeric != rightNumeric)
                return leftNumeric ? -1 : 1;
            return string.CompareOrdinal(left, right);
        }

        private static int CompareDecimal(string left, string right) =>
            left.Length != right.Length
                ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(left, right);
    }
}
