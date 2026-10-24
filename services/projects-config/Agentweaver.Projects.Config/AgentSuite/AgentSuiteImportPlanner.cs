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

    // Blockers that wait on missing platform features (agent runtime support, policy enforcement), not on the
    // package itself. An agent whose only blockers are these is still reported as "supported".
    private static readonly HashSet<string> OwnerPendingBlockers = [RuntimeUnavailable, PolicyNotEnforced];

    // Hosts reserved for documentation (RFC 2606/6761). Images there are format examples, never execution inputs.
    private static readonly string[] ExampleHostSuffixes =
        [".example", ".test", ".invalid", ".localhost", "example.com", "example.net", "example.org"];

    /// <summary>The source identity: the OCI manifest digest, or the content layer digest for a bare layer.</summary>
    // Every accepted suite has at least one of the two digests, so the throw only guards against bugs.
    public static string SourceIdentity(AgentSuiteValidationResult result) =>
        result.ManifestDigest ?? result.ContentDigest
        ?? throw new InvalidOperationException("A validated AgentSuite has no source digest.");

    /// <summary>Returns <c>agentsuite:{suite}:{agent}:{first 12 hex of the source identity}</c>.</summary>
    // Strip the "sha256:" prefix and keep the first 12 hex characters. The short hash keeps the ID readable
    // while still separating agents that come from different source archives.
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
        // The validator already accepted the suite, so a missing graph here is a programming error, not bad input.
        var graph = result.Graph ?? throw new InvalidOperationException("Only accepted suites can be imported.");
        var identity = SourceIdentity(result);
        // Lookup tables: blobs by digest, bound connection ID by "id@version", build profiles by ID.
        var blobs = result.Blobs.ToDictionary(item => item.Digest, StringComparer.Ordinal);
        var bound = connections.ToDictionary(item => item.ToolProvider, item => item.ConnectionId, StringComparer.Ordinal);
        var profiles = graph.BuildProfiles.ToDictionary(item => item.Id, StringComparer.Ordinal);
        // Re-read each tool provider manifest from its retained blob to get remote MCP and tool details.
        var toolProviders = graph.ToolProviders
            .Select(item => ReadToolProvider(item, ReadJson(blobs, item.ManifestDigest)))
            .ToImmutableArray();
        var providers = toolProviders.ToDictionary(item => ToolProviderKey(item.Id, item.Version), StringComparer.Ordinal);
        // Filled while mapping agents: one entry per agent that has a policy.
        var policies = ImmutableArray.CreateBuilder<ProjectAgentSuitePolicy>();

        var agents = graph.Agents.Select(agent =>
        {
            // Compositions say which platforms and build profiles this agent ships for.
            var compositions = graph.Compositions.Where(item => item.Agent == agent.Id).ToArray();
            var blockers = ImmutableArray.CreateBuilder<ProjectAgentSuiteBlocker>();
            // Every agent gets this blocker: there is no AgentSuite runtime, so import is review-only.
            blockers.Add(new(RuntimeUnavailable,
                "Agentweaver has no AgentSuite runtime yet. The agent is imported for review only."));
            // Agentweaver needs a pinned model name. It never picks a substitute model for capability requests.
            if (agent.Model.CapabilityBased || string.IsNullOrEmpty(agent.Model.Model))
                blockers.Add(new("capability_model_unsupported",
                    "Capability-based models need deployment bindings. Agentweaver requires an exact model."));

            var dependencies = ImmutableArray.CreateBuilder<ProjectAgentSuiteToolDependency>();
            foreach (var requirement in agent.ToolProviders)
            {
                var key = ToolProviderKey(requirement.Id, requirement.Version);
                // The validator guarantees every required provider is in the catalog, so this lookup cannot miss.
                var provider = providers[key];
                var remote = provider.Remote is not null;
                // Only remote providers can be bound to a connection; bundled providers never get one.
                var connection = remote ? bound.GetValueOrDefault(key) : null;
                dependencies.Add(new(
                    key, remote ? RemoteMcpKind : BundledKind, connection, [.. provider.Tools.Select(item => item.Name)]));
                // Bundled tools would need to run inside an agent image, which Agentweaver does not do.
                if (!remote)
                    blockers.Add(new("bundled_tool_provider_unsupported",
                        $"Tool provider '{key}' has only bundled tools, which Agentweaver does not run."));
                // A remote provider without a bound connection has no endpoint to call.
                else if (connection is null)
                    blockers.Add(new("tool_connection_unbound",
                        $"Tool provider '{key}' needs an Environment Remote MCP connection."));
                // Header secrets block the agent even when a connection is bound.
                if (provider.Remote is { Headers.IsEmpty: false })
                    blockers.Add(new("tool_connection_header_secrets_unsupported",
                        $"Tool provider '{key}' uses header secrets, which Remote MCP connections do not support."));
            }

            string? policyDigest = null;
            if (agent.HasPolicy)
            {
                // The policy descriptor lives in the agent manifest; the policy body is a separate retained blob.
                var policy = ReadPolicy(agent.Id, ReadJson(blobs, agent.ManifestDigest), blobs);
                policies.Add(policy);
                policyDigest = policy.Digest;
                // No egress enforcer exists yet, so a bound policy always blocks running the agent.
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
            // Agentweaver only targets linux/amd64; other platforms are kept on the record but not usable.
            if (!compositions.Any(item => item.Platform == SupportedPlatform))
                blockers.Add(new("platform_unsupported", $"The agent has no {SupportedPlatform} composition."));
            // Check the build profile of each linux/amd64 composition for what a future runtime would need.
            foreach (var composition in compositions.Where(item => item.Platform == SupportedPlatform))
            {
                // A missing profile was already reported by the validator; skip it rather than fail here.
                if (!profiles.TryGetValue(composition.BuildProfile, out var profile))
                    continue;
                if (!profile.HasExecutionContract)
                    blockers.Add(new("execution_contract_missing",
                        $"Build profile '{profile.Id}' declares no execution contract."));
                // A positive source epoch is needed for reproducible image builds.
                if (profile.SourceEpoch <= 0)
                    blockers.Add(new("source_epoch_missing",
                        $"Build profile '{profile.Id}' needs a positive sourceEpoch."));
                foreach (var image in profile.ImageReferences)
                {
                    // Images must be pinned by digest so the same bytes are always used. Tags can move.
                    if (!image.Contains("@sha256:", StringComparison.Ordinal))
                        blockers.Add(new("image_not_digest_pinned", $"Image '{image}' is not pinned by digest."));
                    // Pinned images on documentation-only hosts are samples and can never be pulled.
                    else if (IsExampleImage(image))
                        blockers.Add(new("example_image", $"Image '{image}' is an example and cannot be used."));
                }
            }

            var finished = blockers.ToImmutable();
            // Supported: only platform-pending blockers remain. Runnable is always false until a runtime exists.
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

        // Field order matches the record type. The record is serialized to canonical JSON, so the same input
        // always produces the same bytes; blobs are sorted by digest for that reason.
        return new ProjectAgentSuiteImport(
            RecordVersion,
            projectId,
            identity,
            result.Format == AgentSuiteArchiveFormat.OciLayoutTar
                ? ImportProjectAgentSuiteRequest.OciLayoutTarFormat
                : ImportProjectAgentSuiteRequest.ContentTarGzipFormat,
            // ManifestDigest is null for a bare content layer. Every accepted suite has a ContentDigest.
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
            // The blueprint display name defaults to the suite name when the caller did not set one.
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
        // Strict decoding throws on invalid UTF-8 instead of silently inserting replacement characters.
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return [.. record.Agents.Select(mapping =>
        {
            // Each mapping came from exactly one graph agent, so Single() is safe here.
            var agent = graph.Agents.Single(item => item.Id == mapping.NativeId);
            // Use the description as the name only if it is short, not blank, and has no control characters.
            var name = agent.Description is { Length: > 0 and <= MaxCharterNameLength } description &&
                !string.IsNullOrWhiteSpace(description) && !description.Any(char.IsControl)
                    ? description
                    : mapping.NativeId;
            // The instruction text is copied as-is into the charter. It is never rewritten or truncated.
            return (new ProjectAgentCharter(
                mapping.EffectiveAgentId,
                name,
                mapping.Role,
                strict.GetString(agent.InstructionsBytes.Span))
            {
                CustomRoleTitle = agent.CustomRoleTitle,
            }, mapping.Role);
        })];
    }

    // Projects one validated tool provider manifest into the record. GetProperty calls cannot fail on required
    // fields because the validator already checked the manifest shape.
    private static ProjectAgentSuiteToolProvider ReadToolProvider(AgentSuiteToolProvider provider, JsonElement manifest)
    {
        ProjectAgentSuiteRemoteMcp? remote = null;
        // Only providers with a "remote" section can be reached over MCP. Others are bundled-only.
        if (manifest.TryGetProperty("remote", out var item))
        {
            var timeouts = item.GetProperty("timeouts");
            // Endpoint, headers and network entries are reference names only. No URL or secret value is read.
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

        // Keep each tool's declared effect and approval as-is. A missing effect stays null, not "read-only".
        var tools = manifest.GetProperty("tools").EnumerateArray().Select(tool =>
        {
            ProjectAgentSuiteDelegatedAuthorization? delegated = null;
            // Delegated authorization keeps only the binding slot name; no credential is resolved.
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
            // Only remote providers without header secrets can be bound to an Environment connection.
            remote is { Headers.IsEmpty: true } ? RemoteMcpKind : "unsupported");
    }

    // Reads the policy bound to one agent. The policy is recorded for review only; nothing here enforces it.
    private static ProjectAgentSuitePolicy ReadPolicy(
        string agentId,
        JsonElement agentManifest,
        IReadOnlyDictionary<string, AgentSuiteBlob> blobs)
    {
        // The agent manifest points at the policy blob by digest and media type.
        var descriptor = agentManifest.GetProperty("policy").GetProperty("descriptor");
        var digest = descriptor.GetProperty("digest").GetString()!;
        var mediaType = descriptor.GetProperty("mediaType").GetString()!;
        // Policy v1 is kept by digest only. Its body is not parsed because its fields are not used.
        if (mediaType != PolicyV2MediaType)
            return new ProjectAgentSuitePolicy(digest, mediaType, null, agentId, null, []);
        var policy = ReadJson(blobs, digest);
        // Null egress means "no network section"; an empty list means "deny all egress". Keep them distinct.
        ImmutableArray<ProjectAgentSuiteEgressRule>? egress = null;
        if (policy.TryGetProperty("network", out var network) && network.TryGetProperty("egressAllow", out var allow))
            egress = [.. allow.EnumerateArray().Select(rule => new ProjectAgentSuiteEgressRule(
                rule.GetProperty("address").GetString()!,
                rule.GetProperty("protocol").GetString()!,
                rule.GetProperty("port").GetInt32()))];
        return new ProjectAgentSuitePolicy(
            digest,
            mediaType,
            // The version is optional; a non-string value is treated as absent.
            policy.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null,
            agentId,
            egress,
            policy.TryGetProperty("requiredCapabilities", out var capabilities)
                ? [.. capabilities.EnumerateArray().Select(capability => capability.GetString()!)]
                : []);
    }

    // Parses one retained blob as JSON. A missing blob means the validator and planner disagree (a bug).
    private static JsonElement ReadJson(IReadOnlyDictionary<string, AgentSuiteBlob> blobs, string digest)
    {
        if (!blobs.TryGetValue(digest, out var blob))
            throw new InvalidOperationException($"Validated AgentSuite blob '{digest}' was not retained.");
        using var document = JsonDocument.Parse(blob.Data);
        // Clone so the element stays valid after the document is disposed.
        return document.RootElement.Clone();
    }

    // True when the image registry host is a reserved documentation or test host (for example "example.com").
    private static bool IsExampleImage(string reference)
    {
        // The registry host is the part before the first '/'.
        var host = reference.Split('/', 2)[0];
        // Drop a ":port" suffix, but not the colons inside a bracketed IPv6 address such as "[::1]".
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith(']'))
            host = host[..colon];
        host = host.ToLowerInvariant();
        // Match the host itself (for example "example.com") or any subdomain of it (for example "r.example.com").
        return host is "localhost" || ExampleHostSuffixes.Any(suffix =>
            host == suffix.TrimStart('.') || host.EndsWith(suffix.StartsWith('.') ? suffix : "." + suffix, StringComparison.Ordinal));
    }
}
