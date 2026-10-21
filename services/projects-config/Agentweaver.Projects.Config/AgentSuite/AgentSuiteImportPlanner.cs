using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// Derives the immutable import record, effective agent IDs, tool and policy bindings, and per-agent blockers
/// from a validated suite. Pure and deterministic: the same source and bindings always yield the same record.
/// Policy and provider details are read from the retained original blobs, which the validator already checked.
/// </summary>
internal static class AgentSuiteImportPlanner
{
    public const int RecordVersion = 1;
    public const string EffectiveIdPrefix = "agentsuite";
    public const string SupportedPlatform = "linux/amd64";
    public const string RuntimeUnavailable = "runtime_unavailable";
    public const string PolicyNotEnforced = "policy_not_enforced";
    public const string RemoteMcpKind = "remote-mcp";
    public const string BundledKind = "bundled";
    public const string PolicyV2MediaType = "application/vnd.agentsuite.policy.v2+json";
    private const int MaxCharterNameLength = 160;
    private const int IdentityHexLength = 12;

    // Blockers that wait on another owner (runtime #2115, policy enforcement) rather than on the package.
    private static readonly HashSet<string> OwnerPendingBlockers = [RuntimeUnavailable, PolicyNotEnforced];

    // Hosts reserved for documentation (RFC 2606/6761). Images there are format examples, never execution inputs.
    private static readonly string[] ExampleHostSuffixes =
        [".example", ".test", ".invalid", ".localhost", "example.com", "example.net", "example.org"];

    /// <summary>The source identity: the OCI manifest digest, or the content layer digest for a bare layer.</summary>
    public static string SourceIdentity(AgentSuiteValidationResult result) =>
        result.ManifestDigest ?? result.ContentDigest
        ?? throw new InvalidOperationException("A validated AgentSuite has no source digest.");

    /// <summary>Returns <c>agentsuite:{suite}:{agent}:{first 12 hex of the source identity}</c>.</summary>
    public static string EffectiveAgentId(string suite, string agent, string sourceIdentity) =>
        $"{EffectiveIdPrefix}:{suite}:{agent}:{sourceIdentity["sha256:".Length..][..IdentityHexLength]}";

    public static string ToolProviderKey(string id, string version) => $"{id}@{version}";

    /// <param name="connections">Validated bindings, sorted by tool provider.</param>
    /// <param name="blueprint">The normalized blueprint projection.</param>
    public static ProjectAgentSuiteImport CreateRecord(
        string projectId,
        AgentSuiteValidationResult result,
        ImmutableArray<ProjectAgentSuiteToolConnection> connections,
        ProjectAgentSuiteBlueprint blueprint)
    {
        var graph = result.Graph ?? throw new InvalidOperationException("Only accepted suites can be imported.");
        var identity = SourceIdentity(result);
        var blobs = result.Blobs.ToDictionary(item => item.Digest, StringComparer.Ordinal);
        var bound = connections.ToDictionary(item => item.ToolProvider, item => item.ConnectionId, StringComparer.Ordinal);
        var profiles = graph.BuildProfiles.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var toolProviders = graph.ToolProviders
            .Select(item => ReadToolProvider(item, ReadJson(blobs, item.ManifestDigest)))
            .ToImmutableArray();
        var providers = toolProviders.ToDictionary(item => ToolProviderKey(item.Id, item.Version), StringComparer.Ordinal);
        var policies = ImmutableArray.CreateBuilder<ProjectAgentSuitePolicy>();

        var agents = graph.Agents.Select(agent =>
        {
            var compositions = graph.Compositions.Where(item => item.Agent == agent.Id).ToArray();
            var blockers = ImmutableArray.CreateBuilder<ProjectAgentSuiteBlocker>();
            blockers.Add(new(RuntimeUnavailable,
                "Agentweaver has no AgentSuite runtime yet. The agent is imported for review only."));
            if (agent.Model.CapabilityBased || string.IsNullOrEmpty(agent.Model.Model))
                blockers.Add(new("capability_model_unsupported",
                    "Capability-based models need deployment bindings. Agentweaver requires an exact model."));

            var dependencies = ImmutableArray.CreateBuilder<ProjectAgentSuiteToolDependency>();
            foreach (var requirement in agent.ToolProviders)
            {
                var key = ToolProviderKey(requirement.Id, requirement.Version);
                var provider = providers[key];
                var remote = provider.Remote is not null;
                var connection = remote ? bound.GetValueOrDefault(key) : null;
                dependencies.Add(new(
                    key, remote ? RemoteMcpKind : BundledKind, connection, [.. provider.Tools.Select(item => item.Name)]));
                if (!remote)
                    blockers.Add(new("bundled_tool_provider_unsupported",
                        $"Tool provider '{key}' has only bundled tools, which Agentweaver does not run."));
                else if (connection is null)
                    blockers.Add(new("tool_connection_unbound",
                        $"Tool provider '{key}' needs an Environment Remote MCP connection."));
                if (provider.Remote is { Headers.IsEmpty: false })
                    blockers.Add(new("tool_connection_header_secrets_unsupported",
                        $"Tool provider '{key}' uses header secrets, which Remote MCP connections do not support."));
            }

            string? policyDigest = null;
            if (agent.HasPolicy)
            {
                var policy = ReadPolicy(agent.Id, ReadJson(blobs, agent.ManifestDigest), blobs);
                policies.Add(policy);
                policyDigest = policy.Digest;
                blockers.Add(new(PolicyNotEnforced,
                    "The agent policy is retained but not enforced. The agent cannot run until it is enforced."));
                if (policy.MediaType != PolicyV2MediaType)
                    blockers.Add(new("policy_version_unsupported",
                        "Only AgentSuite policy v2 is supported. Policy v1 is retained but not used."));
            }
            foreach (var extension in agent.Extensions)
                blockers.Add(new("extension_unsupported", $"Extension '{extension}' is not supported."));
            if (compositions.Any(item => item.RuntimeProfile is not null))
                blockers.Add(new("runtime_profile_unsupported", "Runtime profiles are not supported."));
            if (!compositions.Any(item => item.Platform == SupportedPlatform))
                blockers.Add(new("platform_unsupported", $"The agent has no {SupportedPlatform} composition."));
            foreach (var composition in compositions.Where(item => item.Platform == SupportedPlatform))
            {
                if (!profiles.TryGetValue(composition.BuildProfile, out var profile))
                    continue;
                if (!profile.HasExecutionContract)
                    blockers.Add(new("execution_contract_missing",
                        $"Build profile '{profile.Id}' declares no execution contract."));
                if (profile.SourceEpoch <= 0)
                    blockers.Add(new("source_epoch_missing",
                        $"Build profile '{profile.Id}' needs a positive sourceEpoch."));
                foreach (var image in profile.ImageReferences)
                {
                    if (!image.Contains("@sha256:", StringComparison.Ordinal))
                        blockers.Add(new("image_not_digest_pinned", $"Image '{image}' is not pinned by digest."));
                    else if (IsExampleImage(image))
                        blockers.Add(new("example_image", $"Image '{image}' is an example and cannot be used."));
                }
            }

            var finished = blockers.ToImmutable();
            return new ProjectAgentSuiteAgentMapping(
                agent.Id,
                agent.ManifestDigest,
                agent.InstructionsDigest,
                EffectiveAgentId(graph.Name, agent.Id, identity),
                agent.Id,
                [.. compositions.Select(item => item.Platform).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. agent.Invokes.Select(item => new ProjectAgentSuiteInvoke(item.Agent, item.MaxConcurrent, item.MaxDepth))],
                dependencies.ToImmutable(),
                policyDigest,
                Supported: finished.All(item => OwnerPendingBlockers.Contains(item.Code)),
                Runnable: false,
                finished);
        }).ToImmutableArray();

        return new ProjectAgentSuiteImport(
            RecordVersion,
            projectId,
            identity,
            result.Format == AgentSuiteArchiveFormat.OciLayoutTar
                ? ImportProjectAgentSuiteRequest.OciLayoutTarFormat
                : ImportProjectAgentSuiteRequest.ContentTarGzipFormat,
            result.ManifestDigest,
            result.ContentDigest!,
            graph.Name,
            graph.SuiteManifestDigest,
            AgentSuiteSchemaBundle.SpecVersion,
            AgentSuiteSchemaBundle.Commit,
            agents,
            toolProviders,
            policies.ToImmutable(),
            connections,
            blueprint with { DisplayName = blueprint.DisplayName ?? graph.Name },
            graph.RuntimeProfiles,
            [.. result.Blobs.Select(blob => new ProjectAgentSuiteBlob(blob.Digest, blob.MediaType, blob.Data.Length))
                .OrderBy(blob => blob.Digest, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Maps each agent to a charter (literal instruction text) and a cast entry with its native ID as role.
    /// The charter name is the agent description (the role title) when it fits, else the native ID.
    /// </summary>
    public static ImmutableArray<(ProjectAgentCharter Charter, string Role)> CreateCharters(
        ProjectAgentSuiteImport record,
        AgentSuiteGraph graph)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return [.. record.Agents.Select(mapping =>
        {
            var agent = graph.Agents.Single(item => item.Id == mapping.NativeId);
            var name = agent.Description is { Length: > 0 and <= MaxCharterNameLength } description &&
                !string.IsNullOrWhiteSpace(description) && !description.Any(char.IsControl)
                    ? description
                    : mapping.NativeId;
            return (new ProjectAgentCharter(
                mapping.EffectiveAgentId,
                name,
                mapping.Role,
                strict.GetString(agent.InstructionsBytes.Span)), mapping.Role);
        })];
    }

    private static ProjectAgentSuiteToolProvider ReadToolProvider(AgentSuiteToolProvider provider, JsonElement manifest)
    {
        ProjectAgentSuiteRemoteMcp? remote = null;
        if (manifest.TryGetProperty("remote", out var item))
        {
            var timeouts = item.GetProperty("timeouts");
            remote = new ProjectAgentSuiteRemoteMcp(
                item.GetProperty("transport").GetString()!,
                item.GetProperty("endpointRef").GetString()!,
                item.TryGetProperty("headers", out var headers)
                    ? [.. headers.EnumerateArray().Select(header =>
                    {
                        var secret = header.GetProperty("secretRef");
                        return new ProjectAgentSuiteRemoteHeader(
                            header.GetProperty("name").GetString()!,
                            secret.GetProperty("name").GetString()!,
                            secret.GetProperty("key").GetString()!);
                    })]
                    : [],
                [.. item.GetProperty("network").EnumerateArray()
                    .Select(destination => destination.GetProperty("destinationRef").GetString()!)],
                timeouts.GetProperty("connectMilliseconds").GetInt64(),
                timeouts.GetProperty("requestMilliseconds").GetInt64());
        }

        var tools = manifest.GetProperty("tools").EnumerateArray().Select(tool =>
        {
            ProjectAgentSuiteDelegatedAuthorization? delegated = null;
            if (tool.TryGetProperty("delegatedAuthorization", out var authorization))
                delegated = new(
                    authorization.GetProperty("service").GetString()!,
                    authorization.GetProperty("scope").GetString()!,
                    authorization.GetProperty("credential").GetProperty("bindingRef").GetString()!);
            return new ProjectAgentSuiteTool(
                tool.GetProperty("name").GetString()!,
                tool.TryGetProperty("effect", out var effect) ? effect.GetString() : null,
                tool.TryGetProperty("approval", out var approval) ? approval.GetString() : null,
                delegated);
        }).ToImmutableArray();

        return new ProjectAgentSuiteToolProvider(
            provider.Id,
            provider.Version,
            provider.ManifestDigest,
            provider.HasBundledVariants,
            remote,
            tools,
            remote is { Headers.IsEmpty: true } ? RemoteMcpKind : "unsupported");
    }

    private static ProjectAgentSuitePolicy ReadPolicy(
        string agentId,
        JsonElement agentManifest,
        IReadOnlyDictionary<string, AgentSuiteBlob> blobs)
    {
        var descriptor = agentManifest.GetProperty("policy").GetProperty("descriptor");
        var digest = descriptor.GetProperty("digest").GetString()!;
        var mediaType = descriptor.GetProperty("mediaType").GetString()!;
        if (mediaType != PolicyV2MediaType)
            return new ProjectAgentSuitePolicy(digest, mediaType, null, agentId, null, []);
        var policy = ReadJson(blobs, digest);
        ImmutableArray<ProjectAgentSuiteEgressRule>? egress = null;
        if (policy.TryGetProperty("network", out var network) && network.TryGetProperty("egressAllow", out var allow))
            egress = [.. allow.EnumerateArray().Select(rule => new ProjectAgentSuiteEgressRule(
                rule.GetProperty("address").GetString()!,
                rule.GetProperty("protocol").GetString()!,
                rule.GetProperty("port").GetInt32()))];
        return new ProjectAgentSuitePolicy(
            digest,
            mediaType,
            policy.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null,
            agentId,
            egress,
            policy.TryGetProperty("requiredCapabilities", out var capabilities)
                ? [.. capabilities.EnumerateArray().Select(capability => capability.GetString()!)]
                : []);
    }

    private static JsonElement ReadJson(IReadOnlyDictionary<string, AgentSuiteBlob> blobs, string digest)
    {
        if (!blobs.TryGetValue(digest, out var blob))
            throw new InvalidOperationException($"Validated AgentSuite blob '{digest}' was not retained.");
        using var document = JsonDocument.Parse(blob.Data);
        return document.RootElement.Clone();
    }

    private static bool IsExampleImage(string reference)
    {
        var host = reference.Split('/', 2)[0];
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith(']'))
            host = host[..colon];
        host = host.ToLowerInvariant();
        return host is "localhost" || ExampleHostSuffixes.Any(suffix =>
            host == suffix.TrimStart('.') || host.EndsWith(suffix.StartsWith('.') ? suffix : "." + suffix, StringComparison.Ordinal));
    }
}
