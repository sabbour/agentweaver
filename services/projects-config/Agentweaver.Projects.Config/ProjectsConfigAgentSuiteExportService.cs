using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config.AgentSuite;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using JsonNode = System.Text.Json.Nodes.JsonNode;

namespace Agentweaver.Projects.Config;

public sealed partial class ProjectsConfigService
{
    private const int MaximumExportArchiveBytes = 5 * 1024 * 1024;
    private const string PortableAgentHostImage =
        "ghcr.io/sabbour/agentweaver-agenthost@sha256:56692eae59483dcae483233423c83fbf0246faf080e45a43615ea0271260b53e";
    private const long PortableAgentHostManifestBytes = 2580;

    /// <summary>Exports the agents and relationships at one configuration revision as a portable AgentSuite archive.</summary>
    public Task<ProjectAgentSuiteExport> ExportAgentSuiteTeamAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long? revision,
        IObjectStore objectStore,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ArgumentNullException.ThrowIfNull(objectStore);
        if (revision is <= 0)
            throw ProjectConfigException.Invalid(
                "invalid_configuration_revision",
                "The requested project configuration revision must be positive.");

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            // Keep the project's current authorization and revision fence stable while the package is assembled.
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            var selectedRevision = revision ?? project.ConfigurationRevision;
            if (selectedRevision > project.ConfigurationRevision)
                throw ProjectConfigException.NotFound();
            var configurationRecord = await context.ProjectConfigurationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == project.ProjectId && item.Revision == selectedRevision,
                    token)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var configuration = Deserialize<ProjectConfiguration>(configurationRecord.ConfigurationJson);
            var casting = configuration.Casting
                .OrderBy(item => item.Order)
                .ThenBy(item => item.AgentId, StringComparer.Ordinal)
                .ToImmutableArray();
            if (casting.IsDefaultOrEmpty)
                throw TeamExportRejected("An empty team cannot be exported as an AgentSuite package.");

            // Read the immutable import records only for active agents; inactive source agents are not exported.
            var retainedStore = new AgentSuiteObjectStore(objectStore);
            var sourcesByIdentity = new Dictionary<string, AgentSuiteExportSource>(StringComparer.Ordinal);
            var importedAgents = new Dictionary<string, AgentSuiteExportAgent>(StringComparer.Ordinal);
            foreach (var cast in casting)
            {
                var identity = await retainedStore.ReadAgentIndexAsync(projectId, cast.AgentId, token)
                    .ConfigureAwait(false);
                if (identity is null)
                    continue;
                if (!sourcesByIdentity.TryGetValue(identity, out var source))
                {
                    var record = await retainedStore.ReadRecordAsync(projectId, identity, JsonOptions, token)
                        .ConfigureAwait(false)
                        ?? throw AgentSuiteExportIntegrityError("An imported agent has no retained import record.");
                    var layer = await retainedStore.ReadBlobAsync(record.ContentDigest, token).ConfigureAwait(false);
                    var contentError = AgentSuiteContent.LoadContentLayer(layer, out var content);
                    if (contentError is not null || content is null)
                        throw AgentSuiteExportIntegrityError(
                            $"A retained AgentSuite content layer is invalid: {contentError ?? "content is missing"}");
                    source = new AgentSuiteExportSource(record, content);
                    sourcesByIdentity.Add(identity, source);
                }

                // A project index must point to exactly one mapping in its owning import record.
                var mapping = source.Record.Agents.SingleOrDefault(item => item.EffectiveAgentId == cast.AgentId)
                    ?? throw AgentSuiteExportIntegrityError("An imported agent index does not match its record.");
                var policy = mapping.PolicyDigest is null
                    ? null
                    : source.Record.Policies.SingleOrDefault(item =>
                        item.Digest == mapping.PolicyDigest && item.OwningAgent == mapping.NativeId)
                        ?? throw AgentSuiteExportIntegrityError("An imported agent policy is missing from its record.");
                // Egress allow lists can name source-environment destinations. Refuse them instead of dropping the policy.
                if (policy?.EgressAllow is { } egressAllow && !egressAllow.IsEmpty)
                    throw TeamExportRejected(
                        $"Agent '{mapping.NativeId}' has an egress policy with environment-specific destinations.");
                var policyBytes = policy is null
                    ? null
                    : await retainedStore.ReadBlobAsync(policy.Digest, token).ConfigureAwait(false);
                importedAgents.Add(cast.AgentId, new AgentSuiteExportAgent(source, mapping, policy, policyBytes));
            }

            // The converter supplies the required native manifests and build graph; project data replaces its roster.
            var package = BuildProjectTeamPackage(configuration, configurationRecord.CreatedAt, casting, importedAgents);
            if (package.ContentArchive.Length > MaximumExportArchiveBytes)
                throw new ProjectConfigException(
                    "agentsuite_export_too_large",
                    $"The exported archive exceeds {MaximumExportArchiveBytes} bytes.",
                    StatusCodes.Status413PayloadTooLarge);
            var validation = AgentSuiteValidator.Validate(
                AgentSuiteArchiveFormat.ContentTarGzip, package.ContentArchive.ToArray());
            if (!validation.Accepted)
                throw AgentSuiteExportIntegrityError(
                    "The generated AgentSuite package did not pass its validator: " +
                    string.Join(" ", validation.NativeErrors.Concat(validation.ProfileErrors.Select(item => item.Message))));

            return new ProjectAgentSuiteExport(
                ImportProjectAgentSuiteRequest.ContentTarGzipFormat,
                package.SuiteName,
                selectedRevision,
                Convert.ToBase64String(package.ContentArchive.AsSpan()));
        }, cancellationToken);
    }

    private static AgentSuiteBlueprintPackage BuildProjectTeamPackage(
        ProjectConfiguration configuration,
        DateTimeOffset revisionCreatedAt,
        ImmutableArray<ProjectAgentCast> casting,
        IReadOnlyDictionary<string, AgentSuiteExportAgent> importedAgents)
    {
        // AgentSuite uses the role identifier as its native agent ID, so ambiguous duplicate roles are refused.
        var charters = configuration.AgentCharters.ToDictionary(item => item.AgentId, StringComparer.Ordinal);
        var active = casting.Select(cast =>
        {
            if (!charters.TryGetValue(cast.AgentId, out var charter))
                throw AgentSuiteExportIntegrityError("The current cast refers to a missing agent charter.");
            return (Cast: cast, Charter: charter);
        }).ToArray();
        var duplicateRole = active.GroupBy(item => item.Cast.Role, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRole is not null)
            throw TeamExportRejected($"Role '{duplicateRole.Key}' is assigned to more than one agent.");

        // Temporary converter roles must not shadow the project role catalog; the package is remapped after conversion.
        var catalogRoleIds = ProjectCastingCatalog.Roles.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var exportIdByRole = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextExportId = 0;
        foreach (var item in active)
        {
            string exportId;
            do
            {
                exportId = $"project-team-agent-{nextExportId++}";
            }
            while (catalogRoleIds.Contains(exportId) || ProjectCastingCatalog.IsReservedRole(exportId));
            exportIdByRole.Add(item.Cast.Role, exportId);
        }

        var blueprint = new BlueprintDefinition
        {
            Id = "project-team",
            Name = "Project team",
            Description = "Project team exported from Agentweaver.",
            Roster = [.. active.Select(item => exportIdByRole[item.Cast.Role])],
            Workflows = ["default"],
            ReviewPolicy = ProjectBlueprintCatalog.DefaultReviewPolicy,
            SandboxProfile = ProjectBlueprintCatalog.DefaultSandboxProfile,
            BespokeRoles = [.. active.Select(item => new BlueprintBespokeRole
            {
                Id = exportIdByRole[item.Cast.Role],
                Title = item.Charter.Name,
                // The converter validates authoring blueprints at 8,192 characters; project charters allow 20,000.
                Charter = "Project instructions are copied from the configuration revision.",
            })],
        };

        // Export only model names; credentials, connection IDs, and endpoint settings stay in their owning services.
        var defaultModel = ResolveExportModel(configuration, null);
        var roleModels = ImmutableDictionary.CreateBuilder<string, AgentSuiteModelInput>(StringComparer.Ordinal);
        foreach (var item in active)
            roleModels[exportIdByRole[item.Cast.Role]] =
                new("openai-compatible", ResolveExportModel(configuration, item.Cast));

        // The pinned public AgentHost manifest fills the schema-required image slots without project endpoints.
        var image = new AgentSuiteImageInput("amd64", PortableAgentHostImage, PortableAgentHostManifestBytes);
        var buildInputs = new AgentSuiteBuildInputs
        {
            BuildProfileId = "project-team",
            SourceEpoch = Math.Max(1, revisionCreatedAt.ToUnixTimeSeconds()),
            RuntimeBase = [image],
            Harness = [image],
            Model = new("openai-compatible", defaultModel),
            RoleModels = roleModels.ToImmutable(),
        };
        AgentSuiteBlueprintPackage package;
        try
        {
            package = AgentSuiteBlueprintConverter.Convert(blueprint, buildInputs);
        }
        catch (AgentSuiteConversionException exception)
        {
            throw TeamExportRejected(string.Join(" ", exception.Errors));
        }

        // Rewrite the generated manifests with retained AgentSuite relationships and remote tool declarations.
        var files = RemapConvertedBlueprintRoles(package, exportIdByRole, active);
        var fileModes = new Dictionary<string, int>(StringComparer.Ordinal);
        var providerReferences = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var roleByEffectiveId = active.ToDictionary(
            item => item.Cast.AgentId,
            item => item.Cast.Role,
            StringComparer.Ordinal);
        foreach (var item in active)
        {
            var agentPath = $"agents/{item.Cast.Role}.json";
            var agent = ParseJsonObject(files[agentPath], "An exported agent manifest is invalid.");
            var invokes = new JsonArray();
            var requirements = new Dictionary<string, (string Id, string Version)>(StringComparer.Ordinal);
            if (importedAgents.TryGetValue(item.Cast.AgentId, out var imported))
            {
                // Keep invokes only when their targets remain in the current cast.
                var nativeToCurrentRole = imported.Source.Record.Agents
                    .Where(mapping => roleByEffectiveId.ContainsKey(mapping.EffectiveAgentId))
                    .ToDictionary(
                        mapping => mapping.NativeId,
                        mapping => roleByEffectiveId[mapping.EffectiveAgentId],
                        StringComparer.Ordinal);
                foreach (var invoke in imported.Mapping.Invokes)
                {
                    if (!nativeToCurrentRole.TryGetValue(invoke.Agent, out var targetRole))
                        continue;
                    invokes.Add(new JsonObject
                    {
                        ["agent"] = targetRole,
                        ["maxConcurrent"] = invoke.MaxConcurrent,
                        ["maxDepth"] = invoke.MaxDepth,
                    });
                }

                // Only remote providers can be rebound to Environment connections in the target project.
                foreach (var dependency in imported.Mapping.ToolDependencies)
                {
                    if (dependency.Kind != AgentSuiteImportPlanner.RemoteMcpKind)
                        throw TeamExportRejected(
                            $"Tool provider '{dependency.ToolProvider}' is bundled and cannot bind to an Environment connection.");
                    var separator = dependency.ToolProvider.LastIndexOf('@');
                    if (separator <= 0 || separator == dependency.ToolProvider.Length - 1)
                        throw AgentSuiteExportIntegrityError("An imported tool provider identity is invalid.");
                    var providerId = dependency.ToolProvider[..separator];
                    var version = dependency.ToolProvider[(separator + 1)..];
                    var providerKey = dependency.ToolProvider;
                    requirements[providerKey] = (providerId, version);
                    var providerReference = AddPortableToolProvider(
                        imported.Source.Content, providerId, version, files, fileModes);
                    if (providerReferences.TryGetValue(providerKey, out var existingReference))
                    {
                        var existingDigest = existingReference["descriptor"]?["digest"]?.GetValue<string>();
                        var candidateDigest = providerReference["descriptor"]?["digest"]?.GetValue<string>();
                        if (!string.Equals(existingDigest, candidateDigest, StringComparison.Ordinal))
                            throw TeamExportRejected($"Conflicting AgentSuite providers were found for '{providerKey}'.");
                    }
                    else
                    {
                        providerReferences.Add(providerKey, providerReference);
                    }
                }
            }

            agent["invokes"] = invokes;
            if (importedAgents.TryGetValue(item.Cast.AgentId, out var importedPolicy) &&
                importedPolicy.Policy is { } policy)
            {
                // Keep portable policy bytes and their original digest; egress destinations were rejected above.
                var policyBytes = importedPolicy.PolicyBytes
                    ?? throw AgentSuiteExportIntegrityError("An imported agent policy has no retained bytes.");
                if (!string.Equals(AgentSuiteObjectStore.Digest(policyBytes), policy.Digest, StringComparison.Ordinal))
                    throw AgentSuiteExportIntegrityError("A retained agent policy does not match its digest.");
                var policyPath = $"policies/{item.Cast.Role}.json";
                AddFileWithoutConflict(files, fileModes, policyPath, policyBytes);
                agent["policy"] = new JsonObject
                {
                    ["path"] = policyPath,
                    ["descriptor"] = new JsonObject
                    {
                        ["mediaType"] = policy.MediaType,
                        ["digest"] = policy.Digest,
                        ["size"] = policyBytes.LongLength,
                    },
                };
            }
            agent["toolProviders"] = new JsonArray();
            var metadataProviders = new JsonArray(requirements.Values
                .OrderBy(provider => provider.Id, StringComparer.Ordinal)
                .ThenBy(provider => provider.Version, StringComparer.Ordinal)
                .Select(provider => (JsonNode)new JsonObject
                {
                    ["id"] = provider.Id,
                    ["version"] = provider.Version,
                })
                .ToArray());
            if (item.Charter.CustomRoleTitle is not null || metadataProviders.Count > 0)
            {
                // Remote dependencies have no bundled variant to pin; keep their portable IDs in this non-critical extension.
                var metadata = new JsonObject
                {
                    ["customRoleTitle"] = item.Charter.CustomRoleTitle,
                    ["remoteToolProviders"] = metadataProviders,
                };
                var metadataBytes = Canonical(metadata);
                AddFileWithoutConflict(
                    files,
                    fileModes,
                    AgentSuiteProjectTeamExtension.ContentPath(item.Cast.Role),
                    metadataBytes);
                agent["extensions"] = new JsonArray(new JsonObject
                {
                    ["name"] = AgentSuiteProjectTeamExtension.Name,
                    ["critical"] = false,
                    ["digest"] = AgentSuiteBlueprintConverter.Digest(metadataBytes),
                });
            }
            files[agentPath] = Canonical(agent);
        }

        // The converter's compositions remain free of provider pins because remote providers have no variants.
        var suite = ParseJsonObject(files["agentsuite.json"], "The generated suite manifest is invalid.");
        var compositions = suite["compositions"] as JsonArray
            ?? throw AgentSuiteExportIntegrityError("The generated suite has no compositions array.");

        // Remote provider manifests are catalog entries; imported project-team metadata carries their dependencies.
        var catalogPath = "tool-providers/catalog.json";
        var catalog = new JsonObject
        {
            ["schemaVersion"] = AgentSuiteBlueprintConverter.SpecVersion,
            ["mediaType"] = AgentSuiteBlueprintConverter.ToolProviderCatalogMediaType,
            ["toolProviders"] = new JsonArray(providerReferences
                .OrderBy(item => item.Value["id"]!.GetValue<string>(), StringComparer.Ordinal)
                .ThenBy(item => item.Value["version"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(item => (JsonNode)new JsonObject
                {
                    ["id"] = item.Value["id"]!.DeepClone(),
                    ["version"] = item.Value["version"]!.DeepClone(),
                    ["path"] = item.Value["path"]!.DeepClone(),
                    ["descriptor"] = item.Value["descriptor"]!.DeepClone(),
                })
                .ToArray()),
        };
        files[catalogPath] = Canonical(catalog);

        // Every digest in the suite root describes the final bytes, not the converter's earlier versions.
        var suiteAgents = suite["agents"] as JsonArray
            ?? throw AgentSuiteExportIntegrityError("The generated suite has no agents array.");
        foreach (var reference in suiteAgents.OfType<JsonObject>())
            UpdateDescriptor(reference["descriptor"] as JsonObject, files[reference["path"]!.GetValue<string>()],
                AgentSuiteBlueprintConverter.AgentMediaType);
        var catalogReference = suite["toolProviderCatalog"] as JsonObject
            ?? throw AgentSuiteExportIntegrityError("The generated suite has no tool provider catalog.");
        UpdateDescriptor(catalogReference["descriptor"] as JsonObject, files[catalogPath],
            AgentSuiteBlueprintConverter.ToolProviderCatalogMediaType);
        foreach (var reference in compositions.OfType<JsonObject>())
            UpdateDescriptor(reference["descriptor"] as JsonObject, files[reference["path"]!.GetValue<string>()],
                AgentSuiteBlueprintConverter.CompositionMediaType);
        var hasBundledVariants = providerReferences.Values.Any(reference =>
        {
            var provider = ParseJsonObject(
                files[reference["path"]!.GetValue<string>()], "An exported tool provider is invalid.");
            return (provider["variants"] as JsonArray)?.Count > 0;
        });
        suite["capabilities"] = hasBundledVariants
            ? new JsonArray("bundled-stdio-mcp")
            : new JsonArray();
        files["agentsuite.json"] = Canonical(suite);

        var outputFiles = files
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new AgentSuiteFile(item.Key, ImmutableArray.Create(item.Value)))
            .ToImmutableArray();
        var archive = AgentSuiteBlueprintConverter.WriteContentArchive(
            outputFiles, fileModes, null);
        return package with
        {
            Files = outputFiles,
            ContentArchive = ImmutableArray.Create(archive),
            ContentDigest = AgentSuiteBlueprintConverter.Digest(archive),
        };
    }

    private static Dictionary<string, byte[]> RemapConvertedBlueprintRoles(
        AgentSuiteBlueprintPackage package,
        IReadOnlyDictionary<string, string> exportIdByRole,
        IReadOnlyCollection<(ProjectAgentCast Cast, ProjectAgentCharter Charter)> active)
    {
        // Rebuild the converted files under the current project roles, not the converter-only safe aliases.
        var files = package.Files.ToDictionary(
            item => item.Path,
            item => item.Content.ToArray(),
            StringComparer.Ordinal);
        var roleByExportId = exportIdByRole.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var charterByRole = active.ToDictionary(item => item.Cast.Role, item => item.Charter, StringComparer.Ordinal);
        var output = files
            .Where(item => !item.Key.StartsWith("agents/", StringComparison.Ordinal) &&
                !item.Key.StartsWith("instructions/", StringComparison.Ordinal) &&
                !item.Key.StartsWith("compositions/", StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var suite = ParseJsonObject(files["agentsuite.json"], "The generated suite manifest is invalid.");

        // Rewrite each agent and its instruction path while preserving the exact charter bytes.
        foreach (var reference in (suite["agents"] as JsonArray
                     ?? throw AgentSuiteExportIntegrityError("The generated suite has no agents array."))
                 .OfType<JsonObject>())
        {
            var exportId = reference["id"]?.GetValue<string>()
                ?? throw AgentSuiteExportIntegrityError("A generated agent has no ID.");
            if (!roleByExportId.TryGetValue(exportId, out var role))
                throw AgentSuiteExportIntegrityError("A generated agent ID has no project role.");
            if (!charterByRole.TryGetValue(role, out var charter))
                throw AgentSuiteExportIntegrityError("A generated agent has no project charter.");
            var oldAgentPath = reference["path"]?.GetValue<string>()
                ?? throw AgentSuiteExportIntegrityError("A generated agent has no content path.");
            var agent = ParseJsonObject(files[oldAgentPath], "A generated agent manifest is invalid.");
            agent["id"] = role;
            var instructions = agent["instructions"] as JsonObject
                ?? throw AgentSuiteExportIntegrityError("A generated agent has no instructions.");
            var newInstructionsPath = $"instructions/{role}.md";
            var instructionBytes = new UTF8Encoding(false, true).GetBytes(charter.Charter);
            output[newInstructionsPath] = instructionBytes;
            instructions["path"] = newInstructionsPath;
            UpdateDescriptor(
                instructions["descriptor"] as JsonObject,
                instructionBytes,
                AgentSuiteBlueprintConverter.InstructionsMediaType);
            var agentBytes = Canonical(agent);
            var newAgentPath = $"agents/{role}.json";
            output[newAgentPath] = agentBytes;
            reference["id"] = role;
            reference["path"] = newAgentPath;
            UpdateDescriptor(reference["descriptor"] as JsonObject, agentBytes, AgentSuiteBlueprintConverter.AgentMediaType);
        }

        // Rewrite each composition's agent and file path so native references use the same project roles.
        foreach (var reference in (suite["compositions"] as JsonArray
                     ?? throw AgentSuiteExportIntegrityError("The generated suite has no compositions array."))
                 .OfType<JsonObject>())
        {
            var exportId = reference["agent"]?.GetValue<string>()
                ?? throw AgentSuiteExportIntegrityError("A generated composition has no agent.");
            if (!roleByExportId.TryGetValue(exportId, out var role))
                throw AgentSuiteExportIntegrityError("A generated composition has no project role.");
            var oldPath = reference["path"]?.GetValue<string>()
                ?? throw AgentSuiteExportIntegrityError("A generated composition has no content path.");
            var composition = ParseJsonObject(files[oldPath], "A generated composition is invalid.");
            composition["agent"] = role;
            var newPath = oldPath.Replace($"compositions/{exportId}-", $"compositions/{role}-", StringComparison.Ordinal);
            var compositionBytes = Canonical(composition);
            output[newPath] = compositionBytes;
            reference["agent"] = role;
            reference["path"] = newPath;
            UpdateDescriptor(
                reference["descriptor"] as JsonObject,
                compositionBytes,
                AgentSuiteBlueprintConverter.CompositionMediaType);
        }

        // The suite digest is not externally referenced, but canonical bytes keep the archive deterministic.
        output["agentsuite.json"] = Canonical(suite);
        return output;
    }

    private static string ResolveExportModel(
        ProjectConfiguration configuration,
        ProjectAgentCast? cast)
    {
        // The project-wide selection wins; role and agent overrides fill the model when no global choice exists.
        var model = configuration.ModelSelection?.Reference;
        if (string.IsNullOrWhiteSpace(model))
            model = configuration.ModelTaskOverrides
                .FirstOrDefault(item => item.TaskKind == ModelTaskKind.Planning)?.Selection.Reference;
        if (string.IsNullOrWhiteSpace(model))
            model = ModelTaskSelectionDefaults.Create()
                .First(item => item.TaskKind == ModelTaskKind.Planning).Selection.Reference;
        if (configuration.ModelSelection is null && cast is not null)
        {
            model = configuration.ModelRoleOverrides
                .FirstOrDefault(item => item.RoleId == cast.Role)?.Selection.Reference ?? model;
            model = configuration.ModelAgentOverrides
                .FirstOrDefault(item => item.AgentId == cast.AgentId)?.Selection.Reference ?? model;
        }

        // A model field is a name, not an endpoint; never copy a URI into the portable package.
        return Uri.TryCreate(model, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https"
                ? ModelTaskSelectionDefaults.Create()
                    .First(item => item.TaskKind == ModelTaskKind.Planning).Selection.Reference!
                : model!;
    }

    private static JsonObject AddPortableToolProvider(
        ContentSet source,
        string id,
        string version,
        Dictionary<string, byte[]> outputFiles,
        Dictionary<string, int> outputFileModes)
    {
        // Locate the provider through its checked suite catalog instead of guessing a content path.
        var suite = ParseJsonObject(GetContentFile(source, "agentsuite.json"),
            "A retained suite manifest is invalid.");
        var catalogReference = suite["toolProviderCatalog"] as JsonObject
            ?? throw AgentSuiteExportIntegrityError("A retained suite has no tool provider catalog.");
        var sourceCatalog = ParseJsonObject(
            GetContentFile(source, catalogReference["path"]!.GetValue<string>()),
            "A retained tool provider catalog is invalid.");
        var references = sourceCatalog["toolProviders"] as JsonArray
            ?? throw AgentSuiteExportIntegrityError("A retained tool provider catalog has no provider list.");
        var reference = references.OfType<JsonObject>().SingleOrDefault(item =>
            item["id"]?.GetValue<string>() == id && item["version"]?.GetValue<string>() == version)
            ?? throw AgentSuiteExportIntegrityError($"Tool provider '{id}@{version}' is missing from its catalog.");
        var sourcePath = reference["path"]!.GetValue<string>();
        var provider = ParseJsonObject(GetContentFile(source, sourcePath),
            $"Tool provider '{id}@{version}' is invalid.");
        var remote = provider["remote"] as JsonObject
            ?? throw TeamExportRejected($"Tool provider '{id}@{version}' has no remote MCP connection.");

        // Replace endpoint and network references; source header names and values are not portable.
        remote["endpointRef"] = "target-environment-mcp";
        remote["headers"] = new JsonArray();
        remote["network"] = new JsonArray(new JsonObject { ["destinationRef"] = "target-environment-mcp" });
        // Bundled payloads and literal launcher arguments are source-authored and cannot be proven portable.
        provider.Remove("variants");
        provider["retained"] = true;
        foreach (var tool in (provider["tools"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var authorization = tool["delegatedAuthorization"] as JsonObject;
            var credential = authorization?["credential"] as JsonObject;
            if (credential is not null)
                credential["bindingRef"] = "target-environment-authorization";
        }

        // Separate provider content from the catalog file and keep IDs and versions in distinct segments.
        var tools = provider["tools"] as JsonArray
            ?? throw AgentSuiteExportIntegrityError($"Tool provider '{id}@{version}' has no tools.");
        var schemaDirectory = $"tool-providers/by-id/{id}/{version}/schemas";
        for (var index = 0; index < tools.Count; index++)
        {
            if (tools[index] is not JsonObject tool)
                throw AgentSuiteExportIntegrityError($"Tool provider '{id}@{version}' has an invalid tool.");
            CopySchemaReference(
                source, tool, "inputSchema", $"{schemaDirectory}/{index}-input.json", outputFiles, outputFileModes);
            CopySchemaReference(
                source, tool, "outputSchema", $"{schemaDirectory}/{index}-output.json", outputFiles, outputFileModes);
        }

        var manifestPath = $"tool-providers/by-id/{id}/{version}/manifest.json";
        var manifestBytes = Canonical(provider);
        AddFileWithoutConflict(outputFiles, outputFileModes, manifestPath, manifestBytes);
        var descriptor = new JsonObject
        {
            ["mediaType"] = AgentSuiteTypes.MediaTypeToolProvider,
            ["digest"] = AgentSuiteBlueprintConverter.Digest(manifestBytes),
            ["size"] = manifestBytes.LongLength,
        };
        return new JsonObject
        {
            ["id"] = id,
            ["version"] = version,
            ["path"] = manifestPath,
            ["descriptor"] = descriptor,
        };
    }

    private static void CopySchemaReference(
        ContentSet source,
        JsonObject tool,
        string field,
        string outputPath,
        Dictionary<string, byte[]> outputFiles,
        Dictionary<string, int> outputFileModes)
    {
        // Output schemas are optional; input schemas are required by native validation.
        if (tool[field] is not JsonObject reference)
        {
            if (field == "inputSchema")
                throw AgentSuiteExportIntegrityError("An imported tool has no input schema.");
            return;
        }
        var sourcePath = reference["path"]?.GetValue<string>()
            ?? throw AgentSuiteExportIntegrityError("An imported tool schema has no content path.");
        var bytes = GetContentFile(source, sourcePath);
        AddFileWithoutConflict(outputFiles, outputFileModes, outputPath, bytes);
        reference["path"] = outputPath;
    }

    private static void AddFileWithoutConflict(
        Dictionary<string, byte[]> files,
        Dictionary<string, int> fileModes,
        string path,
        byte[] content,
        int mode = 0x1A4)
    {
        // Identical provider identities may be shared by several source imports only when their files match.
        if (files.TryGetValue(path, out var existing))
        {
            if (!existing.AsSpan().SequenceEqual(content) || fileModes.GetValueOrDefault(path, 0x1A4) != mode)
                throw TeamExportRejected($"Conflicting AgentSuite content was found at '{path}'.");
            return;
        }
        files.Add(path, content);
        fileModes[path] = mode;
    }

    private static byte[] GetContentFile(ContentSet content, string path) =>
        content.Get(path) is { Type: "file", Bytes: { } bytes }
            ? bytes
            : throw AgentSuiteExportIntegrityError($"A retained AgentSuite file '{path}' is missing.");

    private static JsonObject ParseJsonObject(byte[] bytes, string message)
    {
        // Stored package files were validated during import; parsing failures now indicate damaged retained content.
        try
        {
            return JsonNode.Parse(bytes)?.AsObject()
                ?? throw AgentSuiteExportIntegrityError(message);
        }
        catch (JsonException)
        {
            throw AgentSuiteExportIntegrityError(message);
        }
    }

    private static byte[] Canonical(JsonObject value) =>
        CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(value.ToJsonString()));

    private static void UpdateDescriptor(JsonObject? descriptor, byte[] bytes, string mediaType)
    {
        // Each manifest reference is pinned to the canonical bytes that will be written into the content layer.
        if (descriptor is null)
            throw AgentSuiteExportIntegrityError("A generated AgentSuite descriptor is missing.");
        descriptor["mediaType"] = mediaType;
        descriptor["digest"] = AgentSuiteBlueprintConverter.Digest(bytes);
        descriptor["size"] = bytes.LongLength;
    }

    private static ProjectConfigException TeamExportRejected(string message) =>
        new(
            "agentsuite_team_not_exportable",
            message,
            StatusCodes.Status422UnprocessableEntity);

    private static ProjectConfigException AgentSuiteExportIntegrityError(string message) =>
        new(
            "agentsuite_export_integrity_error",
            message,
            StatusCodes.Status500InternalServerError);

    private sealed record AgentSuiteExportSource(ProjectAgentSuiteImport Record, ContentSet Content);
    private sealed record AgentSuiteExportAgent(
        AgentSuiteExportSource Source,
        ProjectAgentSuiteAgentMapping Mapping,
        ProjectAgentSuitePolicy? Policy,
        byte[]? PolicyBytes);
}
