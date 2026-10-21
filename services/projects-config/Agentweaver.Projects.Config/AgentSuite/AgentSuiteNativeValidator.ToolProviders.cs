using System.Text;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>Tool provider checks from upstream validate.go.</summary>
internal sealed partial class AgentSuiteNativeValidator
{
    /// <summary>Upstream <c>validateToolProvider</c>.</summary>
    private string? ValidateToolProvider(J provider, byte[] raw)
    {
        var errs = new List<string?> { ValidateRawToolPolicyFields(raw) };
        if (provider["schemaVersion"].S != AgentSuiteTypes.SpecVersion || provider["mediaType"].S != AgentSuiteTypes.MediaTypeToolProvider)
            errs.Add("unsupported schemaVersion or mediaType");
        if (!Id(provider["id"].S) || !Version(provider["version"].S))
            errs.Add("tool provider id or exact version is invalid");
        var retained = provider["retained"];
        if (!retained.IsNull && !retained.B)
            errs.Add("retained, when present, must be true");
        if (provider["protocol"].S != "mcp" || provider["revision"].S.Length == 0 || provider["tools"].Count == 0)
            errs.Add("the draft tool provider must declare an MCP revision and at least one tool");
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in provider["tools"].A)
        {
            var name = tool["name"].S;
            if (!Id(name) || tools.Contains(name))
                errs.Add($"tool {Go.Quote(name)} is invalid or duplicated");
            tools.Add(name);
            if (ValidateFileRef(tool["inputSchema"], AgentSuiteTypes.MediaTypeJsonSchema) is { } inputError)
                errs.Add($"tool {Go.Quote(name)} input schema: {inputError}");
            if (!tool["outputSchema"].IsNull && ValidateFileRef(tool["outputSchema"], AgentSuiteTypes.MediaTypeJsonSchema) is { } outputError)
                errs.Add($"tool {Go.Quote(name)} output schema: {outputError}");
            var effects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var effect in tool["effects"].A.Select(value => value.S))
            {
                if (effect.Length == 0 || effects.Contains(effect))
                    errs.Add($"tool {Go.Quote(name)} has an empty or duplicate effect");
                effects.Add(effect);
            }
            if (ValidateToolEffectApproval(tool) is { } approvalError)
                errs.Add($"tool {Go.Quote(name)}: {approvalError}");
            if (!tool["delegatedAuthorization"].IsNull && ValidateDelegatedAuthorization(tool["delegatedAuthorization"]) is { } authError)
                errs.Add($"tool {Go.Quote(name)} delegated authorization: {authError}");
        }
        if (provider["variants"].Count == 0 && provider["remote"].IsNull)
            errs.Add("tool provider must declare bundled variants, remote MCP, or both");
        var platforms = new HashSet<string>(StringComparer.Ordinal);
        var digestError = RawVariantDigests(raw, out var variantDigests);
        if (digestError is not null)
            errs.Add($"variant identities: {digestError}");
        var variants = provider["variants"].A;
        if (variantDigests.Count != variants.Count)
            errs.Add("raw and decoded variant counts differ");
        for (var i = 0; i < variants.Count; i++)
        {
            var key = variants[i]["platform"].Platform;
            if (!platforms.Add(key))
                errs.Add($"duplicate platform variant {key}");
            errs.Add(ValidateVariant(variants[i], i < variantDigests.Count ? variantDigests[i] : string.Empty));
        }
        if (!provider["remote"].IsNull)
            errs.Add(ValidateRemoteToolProvider(provider["remote"]));
        errs.Add(ValidateExtensions(provider["extensions"]));
        return Go.Join(errs);
    }

    /// <summary>Upstream <c>validateRawToolPolicyFields</c> (encoding/json presence checks).</summary>
    private static string? ValidateRawToolPolicyFields(byte[] raw)
    {
        if (AgentSuiteJson.ParseLoose(raw, out var root) is { } parseError)
            return parseError;
        var errs = new List<string?>();
        var tools = new J(root)["tools"].A;
        for (var i = 0; i < tools.Count; i++)
        {
            foreach (var field in new[] { "effect", "approval" })
            {
                var value = tools[i][field];
                if (!value.Present)
                    continue;
                // json.Unmarshal of null into a string is a no-op, leaving "".
                if (value.Node!.Kind != JsonKind.String || value.S.Length == 0)
                    errs.Add($"tool {i} {field} must be a non-empty string");
            }
            if (tools[i]["delegatedAuthorization"] is { Present: true, IsNull: true })
                errs.Add($"tool {i} delegatedAuthorization must not be null");
        }
        return Go.Join(errs);
    }

    private static string? ValidateRemoteToolProvider(J remote)
    {
        var errs = new List<string?>();
        if (remote["transport"].S != "streamable-http")
            errs.Add("remote transport must be streamable-http");
        if (!Id(remote["endpointRef"].S))
            errs.Add("remote endpointRef is invalid");
        if (remote["cancellation"].S != "propagate")
            errs.Add("remote cancellation must be propagate");
        if (remote["connection"].S != "session-aware")
            errs.Add("remote connection must be session-aware");
        var connect = remote["timeouts"]["connectMilliseconds"].I;
        if (connect <= 0 || connect > 3_600_000)
            errs.Add("remote connect timeout is invalid");
        var request = remote["timeouts"]["requestMilliseconds"].I;
        if (request <= 0 || request > 86_400_000)
            errs.Add("remote request timeout is invalid");
        var headers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in remote["headers"].A)
        {
            var original = header["name"].S;
            var name = GoToLower(original);
            if (!Go.HeaderNamePattern.IsMatch(original) || headers.Contains(name) || ReservedRemoteHeader(name))
                errs.Add($"remote header {Go.Quote(original)} is invalid, duplicated, or transport-managed");
            headers.Add(name);
            if (!Id(header["secretRef"]["name"].S) || !Go.SecretKeyPattern.IsMatch(header["secretRef"]["key"].S))
                errs.Add($"remote header {Go.Quote(original)} secret reference is invalid");
        }
        if (remote["network"].Count == 0)
            errs.Add("remote network must contain at least one destination reference");
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var network in remote["network"].A)
        {
            var destination = network["destinationRef"].S;
            if (!Id(destination) || destinations.Contains(destination))
                errs.Add($"remote network destination {Go.Quote(destination)} is invalid or duplicated");
            destinations.Add(destination);
        }
        return Go.Join(errs);
    }

    // strings.ToLower lowers Unicode too, but only the result's equality is observable here.
    private static string GoToLower(string value) => value.ToLowerInvariant();

    private static bool ReservedRemoteHeader(string name) => name is "accept" or "connection" or "content-length" or "content-type" or
        "host" or "last-event-id" or "mcp-protocol-version" or "mcp-session-id" or "transfer-encoding";

    private static string? ValidateToolEffectApproval(J tool)
    {
        var errs = new List<string?>();
        var effect = tool["effect"].S;
        var approval = tool["approval"].S;
        if (effect.Length != 0 && effect is not ("read-only" or "mutating"))
            errs.Add("effect must be read-only or mutating");
        if (approval.Length != 0 && approval is not ("required" or "policy-controlled"))
            errs.Add("approval must be required or policy-controlled");
        if (!tool["delegatedAuthorization"].IsNull && (effect.Length == 0 || approval.Length == 0))
            errs.Add("delegated authorization requires explicit tool effect and approval");
        return Go.Join(errs);
    }

    private static string? ValidateDelegatedAuthorization(J auth)
    {
        var errs = new List<string?>();
        if (!Id(auth["service"].S))
            errs.Add("delegated authorization service is invalid");
        if (!Go.CapabilityPattern.IsMatch(auth["scope"].S))
            errs.Add("delegated authorization scope is invalid");
        if (auth["credential"]["resolution"].S != "runtime-brokered")
            errs.Add("delegated authorization credential resolution must be runtime-brokered");
        if (!Id(auth["credential"]["bindingRef"].S))
            errs.Add("delegated authorization credential bindingRef is invalid");
        return Go.Join(errs);
    }

    private string? ValidateFileRef(J reference, string expectedMediaType)
    {
        var path = reference["path"].S;
        var descriptor = reference["descriptor"];
        if (AgentSuiteContent.ValidateContentPath(path, out _) is not null || !Go.IsValidDigest(descriptor["digest"].S) ||
            descriptor["mediaType"].S != expectedMediaType || descriptor["size"].I < 0)
            return "reference is invalid";
        var entry = _content.Get(path);
        if (entry is null || entry.Type != "file" || entry.Digest != descriptor["digest"].S || entry.Size != descriptor["size"].I)
            return "referenced file is absent or has a different digest";
        Note(path, expectedMediaType);
        return null;
    }

    /// <summary>Upstream <c>validateVariant</c>.</summary>
    private string? ValidateVariant(J variant, string expectedDigest)
    {
        var errs = new List<string?> { ValidatePlatform(variant["platform"]) };
        var installRoot = variant["installRoot"].S;
        var payloadRoot = variant["payloadRoot"].S;
        var entrypoint = variant["entrypoint"].S;
        if (installRoot.Length == 0 || !installRoot.StartsWith('/') || Go.CleanPath(installRoot) != installRoot)
            errs.Add("installRoot must be a normalized absolute path");
        if (AgentSuiteContent.ValidateContentPath(payloadRoot, out _) is not null)
            errs.Add("payloadRoot is invalid");
        if (entrypoint.Length == 0 || !entrypoint.StartsWith(installRoot + "/", StringComparison.Ordinal))
            errs.Add("entrypoint must be below installRoot");
        if (variant["runtime"]["abi"].S is not ("static" or "gnu" or "musl"))
            errs.Add("runtime.abi must be static, gnu, or musl");
        if (variant["runtime"]["cpuBaseline"].S.Length == 0)
            errs.Add("runtime.cpuBaseline is required");
        if (variant["files"].Count == 0)
            errs.Add("files must contain a complete payload inventory");
        errs.Add(ValidateAbsolutePathList("searchPath", variant["searchPath"]));
        errs.Add(ValidateAbsolutePathList("writablePaths", variant["writablePaths"]));
        errs.Add(ValidateAbsolutePathList("runtime.requiredBasePaths", variant["runtime"]["requiredBasePaths"]));
        foreach (var argument in variant["arguments"].A.Select(value => value.S))
        {
            if (argument.Contains("${", StringComparison.Ordinal) || argument.Contains("$(", StringComparison.Ordinal))
                errs.Add("arguments must not interpolate environment or secret values");
        }
        var envSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var env in variant["environment"].A)
        {
            var name = env["name"].S;
            var delivery = env["delivery"].S;
            if (!Go.EnvPattern.IsMatch(name) || envSeen.Contains(name))
                errs.Add($"environment name {Go.Quote(name)} is invalid or duplicated");
            envSeen.Add(name);
            if (env["secret"].B && delivery is not ("env" or "file"))
                errs.Add($"secret environment {name} must declare env or file delivery");
            if (!env["secret"].B && delivery.Length != 0)
                errs.Add($"non-secret environment {name} must not declare secret delivery");
            if (UnsafeInjectionEnv(name))
                errs.Add($"environment name {name} can inject executable code");
        }
        var networkSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var access in variant["network"].A)
        {
            var port = access["port"].I;
            var key = $"{access["scheme"].S}://{access["host"].S}:{port}";
            if (!Go.SchemePattern.IsMatch(access["scheme"].S) || access["host"].S.Length == 0 || port < 1 || port > 65535 ||
                networkSeen.Contains(key))
                errs.Add($"network access {Go.Quote(key)} is invalid or duplicated");
            networkSeen.Add(key);
        }
        if (!variant["sbom"].IsNull && !ValidDescriptor(variant["sbom"]))
            errs.Add("sbom descriptor is invalid");
        if (!variant["provenance"].IsNull && !ValidDescriptor(variant["provenance"]))
            errs.Add("provenance descriptor is invalid");
        var inventory = new Dictionary<string, J>(StringComparer.Ordinal);
        foreach (var entry in variant["files"].A)
        {
            var path = entry["path"].S;
            if (AgentSuiteContent.ValidateContentPath(path, out _) is not null)
            {
                errs.Add($"inventory path {Go.Quote(path)} is invalid");
                continue;
            }
            if (inventory.ContainsKey(path))
            {
                errs.Add($"duplicate inventory path {path}");
                continue;
            }
            var type = entry["type"].S;
            var size = entry["size"].I;
            var digest = entry["digest"].S;
            var linkTarget = entry["linkTarget"].S;
            switch (type)
            {
                case "file":
                    if (size < 0 || !Go.IsValidDigest(digest) || linkTarget.Length != 0)
                        errs.Add($"inventory file {path} has invalid size, digest, or link target");
                    break;
                case "directory":
                    if (size != 0 || digest.Length != 0 || linkTarget.Length != 0)
                        errs.Add($"inventory directory {path} has file or link metadata");
                    break;
                case "symlink":
                case "hardlink":
                    if (size != 0 || digest.Length != 0 || AgentSuiteContent.ValidateLinkTarget(path, linkTarget) is not null)
                        errs.Add($"inventory link {path} has invalid metadata or target");
                    break;
                default:
                    errs.Add($"inventory path {path} has unsupported type {Go.Quote(type)}");
                    break;
            }
            var mode = entry["mode"].I;
            if (mode > 0x1FF)
                errs.Add($"inventory path {path} has invalid mode {Go.AlternateOctal(mode)}");
            inventory[path] = entry;
            var actual = _content.Get(Go.PathJoin(payloadRoot, path));
            if (actual is null)
            {
                errs.Add($"inventory path {path} is absent from payload");
                continue;
            }
            if (actual.Type != type || actual.Mode != mode || actual.Size != size || actual.Digest != digest ||
                actual.LinkTarget != linkTarget || actual.Uid != entry["uid"].I || actual.Gid != entry["gid"].I)
                errs.Add($"inventory path {path} does not match payload metadata");
            if (entry["uid"].I != 0 || entry["gid"].I != 0)
                errs.Add($"inventory path {path} must be root-owned");
            if ((mode & 0x12) != 0 && type is "file" or "hardlink")
                errs.Add($"inventory path {path} is group or world writable");
        }
        var payloadPrefix = payloadRoot + "/";
        foreach (var entry in _content.Entries)
        {
            if (entry.Path == payloadRoot || !entry.Path.StartsWith(payloadPrefix, StringComparison.Ordinal) || entry.Type == "directory")
                continue;
            if (!inventory.ContainsKey(entry.Path[payloadPrefix.Length..]))
                errs.Add($"payload contains untracked path {entry.Path}");
        }
        var installPrefix = installRoot + "/";
        var entryRelative = entrypoint.StartsWith(installPrefix, StringComparison.Ordinal) ? entrypoint[installPrefix.Length..] : entrypoint;
        if (!inventory.TryGetValue(entryRelative, out var executable) || executable["type"].S != "file" || (executable["mode"].I & 0x49) == 0)
            errs.Add("entrypoint must identify an executable regular inventory file");
        if (expectedDigest.Length == 0 || expectedDigest != variant["variantDigest"].S)
            errs.Add("variantDigest does not match canonical variant metadata");
        return Go.Join(errs);
    }

    private static bool ValidDescriptor(J descriptor) =>
        descriptor["mediaType"].S.Length != 0 && Go.IsValidDigest(descriptor["digest"].S) && descriptor["size"].I >= 0;

    private static string? ValidateAbsolutePathList(string name, J values)
    {
        var errs = new List<string?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.A.Select(item => item.S))
        {
            if (value.Length == 0 || !value.StartsWith('/') || Go.CleanPath(value) != value || seen.Contains(value))
                errs.Add($"{name} contains invalid or duplicate path {Go.Quote(value)}");
            seen.Add(value);
        }
        return Go.Join(errs);
    }

    /// <summary>
    /// Upstream <c>rawVariantDigests</c>: the JCS digest of each raw variant object with
    /// <c>variantDigest</c> replaced by the empty string.
    /// </summary>
    private static string? RawVariantDigests(byte[] raw, out List<string> digests)
    {
        digests = [];
        if (AgentSuiteJson.ParseLoose(raw, out var root) is { } parseError)
            return parseError;
        var variants = new J(root)["variants"];
        if (!variants.Present || variants.IsNull)
            return null;
        var result = new List<string>();
        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants.A[i];
            if (!variant.Has("variantDigest"))
                return $"variant {i} has no variantDigest";
            var builder = new List<byte>();
            builder.Add((byte)'{');
            var first = true;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in variant.Node!.Members!.AsEnumerable().Reverse())
            {
                // encoding/json map decoding keeps the last duplicate.
                if (!seen.Add(key))
                    continue;
                if (!first)
                    builder.Add((byte)',');
                first = false;
                builder.AddRange(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(key)));
                builder.Add((byte)':');
                builder.AddRange(key == "variantDigest" ? "\"\""u8.ToArray() : value.RawBytes());
            }
            builder.Add((byte)'}');
            var canonical = AgentSuiteJson.Canonicalize(builder.ToArray());
            if (canonical is null)
                return "jcs: invalid JSON";
            result.Add(Go.DigestBytes(canonical));
        }
        digests = result;
        return null;
    }

    /// <summary>Upstream <c>bundleClosure</c>: the provider variants installed with a root variant.</summary>
    private string? BundleClosure(string rootKey, J root, out List<(string ProviderKey, J Variant)> closure)
    {
        var selected = new List<(string, J)>();
        var errs = new List<string?>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string providerKey, J variant)
        {
            var identity = providerKey + "#" + variant["variantDigest"].S;
            if (active.Contains(identity))
            {
                errs.Add($"tool provider {rootKey} has a dependency cycle through {providerKey}");
                return;
            }
            if (visited.Contains(identity))
                return;
            active.Add(identity);
            selected.Add((providerKey, variant));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in variant["dependencies"].A)
            {
                var key = dependency["id"].S + "@" + dependency["version"].S;
                if (key == providerKey || seen.Contains(key) || !Id(dependency["id"].S) || !Version(dependency["version"].S) ||
                    !Go.IsValidDigest(dependency["variantDigest"].S))
                {
                    errs.Add($"tool provider {providerKey} has invalid or duplicate dependency {key}");
                    continue;
                }
                seen.Add(key);
                if (!ToolProviders.TryGetValue(key, out var dependencyProvider))
                {
                    errs.Add($"tool provider {providerKey} depends on missing tool provider {key}");
                    continue;
                }
                var (match, matches) = ExactVariant(dependencyProvider["variants"], variant["platform"]);
                if (matches != 1 || match["variantDigest"].S != dependency["variantDigest"].S)
                {
                    errs.Add($"tool provider {providerKey} dependency {key} does not bind one {variant["platform"].Platform} variant");
                    continue;
                }
                Visit(key, match);
            }
            active.Remove(identity);
            visited.Add(identity);
        }

        Visit(rootKey, root);
        closure = selected;
        return Go.Join(errs);
    }

    private static string? AddVariantDestinations(Dictionary<string, J> destinations, J variant)
    {
        var errs = new List<string?>();
        foreach (var entry in variant["files"].A)
        {
            var destination = Go.PathJoin(variant["installRoot"].S, entry["path"].S);
            if (destinations.TryGetValue(destination, out var existing) && !SameInstallEntry(existing, entry))
                errs.Add($"non-identical destination collision at {destination}");
            else
                destinations[destination] = entry;
        }
        return Go.Join(errs);
    }

    private static bool SameInstallEntry(J a, J b) =>
        a["type"].S == b["type"].S && a["mode"].I == b["mode"].I && a["uid"].I == b["uid"].I && a["gid"].I == b["gid"].I &&
        a["size"].I == b["size"].I && a["digest"].S == b["digest"].S && a["linkTarget"].S == b["linkTarget"].S;
}
