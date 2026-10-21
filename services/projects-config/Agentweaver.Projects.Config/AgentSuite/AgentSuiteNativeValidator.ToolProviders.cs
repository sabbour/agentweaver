using System.Text;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// Ported upstream checks for tool provider declarations: MCP tool lists, bundled platform
/// variants with their file inventory, and remote Streamable HTTP MCP endpoints. Results are
/// recorded as requirements only; nothing is installed, executed, or connected.
/// </summary>
internal sealed partial class AgentSuiteNativeValidator
{
    /// <summary>Upstream <c>validateToolProvider</c>.</summary>
    private string? ValidateToolProvider(J provider, byte[] raw)
    {
        // Some checks need the raw JSON (field presence, original key order), so "raw" is
        // passed alongside the decoded "provider". All errors are collected, not just the first.
        var errs = new List<string?> { ValidateRawToolPolicyFields(raw) };
        if (provider["schemaVersion"].S != AgentSuiteTypes.SpecVersion || provider["mediaType"].S != AgentSuiteTypes.MediaTypeToolProvider)
            errs.Add("unsupported schemaVersion or mediaType");
        if (!Id(provider["id"].S) || !Version(provider["version"].S))
            errs.Add("tool provider id or exact version is invalid");
        // "retained" is an optional marker; if written at all it must be true.
        var retained = provider["retained"];
        if (!retained.IsNull && !retained.B)
            errs.Add("retained, when present, must be true");
        // Only MCP providers are supported, with a pinned protocol revision and at least one tool.
        if (provider["protocol"].S != "mcp" || provider["revision"].S.Length == 0 || provider["tools"].Count == 0)
            errs.Add("the draft tool provider must declare an MCP revision and at least one tool");
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in provider["tools"].A)
        {
            // Tool names must be valid IDs and unique within this provider.
            var name = tool["name"].S;
            if (!Id(name) || tools.Contains(name))
                errs.Add($"tool {Go.Quote(name)} is invalid or duplicated");
            tools.Add(name);
            // Input schema is required; output schema is optional. Both must point at a JSON
            // Schema file inside the archive with a matching digest.
            if (ValidateFileRef(tool["inputSchema"], AgentSuiteTypes.MediaTypeJsonSchema) is { } inputError)
                errs.Add($"tool {Go.Quote(name)} input schema: {inputError}");
            if (!tool["outputSchema"].IsNull && ValidateFileRef(tool["outputSchema"], AgentSuiteTypes.MediaTypeJsonSchema) is { } outputError)
                errs.Add($"tool {Go.Quote(name)} output schema: {outputError}");
            // "effects" is a free-form label list; labels must be non-empty and unique.
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
        // A provider must be runnable somehow: shipped binaries (variants), a remote MCP server, or both.
        if (provider["variants"].Count == 0 && provider["remote"].IsNull)
            errs.Add("tool provider must declare bundled variants, remote MCP, or both");
        var platforms = new HashSet<string>(StringComparer.Ordinal);
        // Recompute each variant's identity digest from the raw JSON, to compare with the
        // variantDigest the package claims.
        var digestError = RawVariantDigests(raw, out var variantDigests);
        if (digestError is not null)
            errs.Add($"variant identities: {digestError}");
        // The raw and decoded lists must line up index by index for that comparison to be valid.
        var variants = provider["variants"].A;
        if (variantDigests.Count != variants.Count)
            errs.Add("raw and decoded variant counts differ");
        for (var i = 0; i < variants.Count; i++)
        {
            // At most one variant per platform (OS/architecture), so selection is unambiguous.
            var key = variants[i]["platform"].Platform;
            if (!platforms.Add(key))
                errs.Add($"duplicate platform variant {key}");
            // A missing recomputed digest becomes "", which ValidateVariant reports as a mismatch.
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
        // The decoded view can't tell "field missing" from "field set to null or empty", so
        // re-read the raw JSON loosely to see which fields were actually written.
        if (AgentSuiteJson.ParseLoose(raw, out var root) is { } parseError)
            return parseError;
        var errs = new List<string?>();
        var tools = new J(root)["tools"].A;
        for (var i = 0; i < tools.Count; i++)
        {
            // "effect" and "approval" are optional, but if written they must be real, non-empty strings.
            foreach (var field in new[] { "effect", "approval" })
            {
                var value = tools[i][field];
                if (!value.Present)
                    continue;
                // Go would silently turn null into "" here, so null is caught explicitly.
                if (value.Node!.Kind != JsonKind.String || value.S.Length == 0)
                    errs.Add($"tool {i} {field} must be a non-empty string");
            }
            // An explicit null would otherwise look the same as "no delegated authorization".
            if (tools[i]["delegatedAuthorization"] is { Present: true, IsNull: true })
                errs.Add($"tool {i} delegatedAuthorization must not be null");
        }
        return Go.Join(errs);
    }

    private static string? ValidateRemoteToolProvider(J remote)
    {
        var errs = new List<string?>();
        // Remote MCP must use Streamable HTTP. The package never carries the URL itself; it names
        // an endpointRef that is later bound to an Environment-owned connection.
        if (remote["transport"].S != "streamable-http")
            errs.Add("remote transport must be streamable-http");
        if (!Id(remote["endpointRef"].S))
            errs.Add("remote endpointRef is invalid");
        // Fixed session behavior: cancellations are forwarded and the connection keeps MCP session state.
        if (remote["cancellation"].S != "propagate")
            errs.Add("remote cancellation must be propagate");
        if (remote["connection"].S != "session-aware")
            errs.Add("remote connection must be session-aware");
        // Timeouts must be positive and bounded: connect at most 1 hour, request at most 24 hours.
        var connect = remote["timeouts"]["connectMilliseconds"].I;
        if (connect <= 0 || connect > 3_600_000)
            errs.Add("remote connect timeout is invalid");
        var request = remote["timeouts"]["requestMilliseconds"].I;
        if (request <= 0 || request > 86_400_000)
            errs.Add("remote request timeout is invalid");
        var headers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in remote["headers"].A)
        {
            // HTTP header names are case-insensitive, so duplicates are checked in lowercase.
            var original = header["name"].S;
            var name = GoToLower(original);
            // Headers the transport sets itself (Host, Content-Type, MCP session IDs, ...) can't be overridden.
            if (!Go.HeaderNamePattern.IsMatch(original) || headers.Contains(name) || ReservedRemoteHeader(name))
                errs.Add($"remote header {Go.Quote(original)} is invalid, duplicated, or transport-managed");
            headers.Add(name);
            // Header values are never inline: each header only names a secret (name + key)
            // that the host would resolve. The secret value never appears in the package.
            if (!Id(header["secretRef"]["name"].S) || !Go.SecretKeyPattern.IsMatch(header["secretRef"]["key"].S))
                errs.Add($"remote header {Go.Quote(original)} secret reference is invalid");
        }
        // Remote providers must declare which network destinations they reach (by reference, not address).
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

    // Go lowercases Unicode letters too. That small difference doesn't matter here, because the
    // result is only used to compare header names, and those must be ASCII anyway.
    private static string GoToLower(string value) => value.ToLowerInvariant();

    // Headers managed by the HTTP/MCP transport; a package may not set them.
    private static bool ReservedRemoteHeader(string name) => name is "accept" or "connection" or "content-length" or "content-type" or
        "host" or "last-event-id" or "mcp-protocol-version" or "mcp-session-id" or "transfer-encoding";

    private static string? ValidateToolEffectApproval(J tool)
    {
        var errs = new List<string?>();
        var effect = tool["effect"].S;
        var approval = tool["approval"].S;
        // Both fields are optional ("" = not declared), but if declared must use known values.
        if (effect.Length != 0 && effect is not ("read-only" or "mutating"))
            errs.Add("effect must be read-only or mutating");
        if (approval.Length != 0 && approval is not ("required" or "policy-controlled"))
            errs.Add("approval must be required or policy-controlled");
        // A tool acting with a user's delegated credentials must state plainly what it does
        // and how it is approved.
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
        // The credential must be supplied by the runtime at call time (by binding reference),
        // never embedded in the package.
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
        // First check the reference itself: safe in-archive path, valid digest, expected media type.
        if (AgentSuiteContent.ValidateContentPath(path, out _) is not null || !Go.IsValidDigest(descriptor["digest"].S) ||
            descriptor["mediaType"].S != expectedMediaType || descriptor["size"].I < 0)
            return "reference is invalid";
        // Then confirm the archive really has a regular file there with the same digest and size.
        var entry = _content.Get(path);
        if (entry is null || entry.Type != "file" || entry.Digest != descriptor["digest"].S || entry.Size != descriptor["size"].I)
            return "referenced file is absent or has a different digest";
        // Record the file and its media type for later checks.
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
        // A variant is a prebuilt payload for one platform. "payloadRoot" is where its files sit
        // inside the archive; "installRoot" is the absolute path they would be installed to.
        if (installRoot.Length == 0 || !installRoot.StartsWith('/') || Go.CleanPath(installRoot) != installRoot)
            errs.Add("installRoot must be a normalized absolute path");
        if (AgentSuiteContent.ValidateContentPath(payloadRoot, out _) is not null)
            errs.Add("payloadRoot is invalid");
        // The program to start must live inside the install root.
        if (entrypoint.Length == 0 || !entrypoint.StartsWith(installRoot + "/", StringComparison.Ordinal))
            errs.Add("entrypoint must be below installRoot");
        // C library flavor the binary expects (static, glibc, or musl).
        if (variant["runtime"]["abi"].S is not ("static" or "gnu" or "musl"))
            errs.Add("runtime.abi must be static, gnu, or musl");
        if (variant["runtime"]["cpuBaseline"].S.Length == 0)
            errs.Add("runtime.cpuBaseline is required");
        if (variant["files"].Count == 0)
            errs.Add("files must contain a complete payload inventory");
        errs.Add(ValidateAbsolutePathList("searchPath", variant["searchPath"]));
        errs.Add(ValidateAbsolutePathList("writablePaths", variant["writablePaths"]));
        errs.Add(ValidateAbsolutePathList("runtime.requiredBasePaths", variant["runtime"]["requiredBasePaths"]));
        // Arguments are passed literally. Shell-style "${...}" or "$(...)" could leak environment
        // values or secrets into the command line, so they are refused.
        foreach (var argument in variant["arguments"].A.Select(value => value.S))
        {
            if (argument.Contains("${", StringComparison.Ordinal) || argument.Contains("$(", StringComparison.Ordinal))
                errs.Add("arguments must not interpolate environment or secret values");
        }
        // Environment variables the binary expects. Secret ones are only declared here
        // (name + delivery method); their values never come from the package.
        var envSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var env in variant["environment"].A)
        {
            var name = env["name"].S;
            var delivery = env["delivery"].S;
            if (!Go.EnvPattern.IsMatch(name) || envSeen.Contains(name))
                errs.Add($"environment name {Go.Quote(name)} is invalid or duplicated");
            envSeen.Add(name);
            // Secrets must say how they are delivered (as an env var or a file); non-secrets must not.
            if (env["secret"].B && delivery is not ("env" or "file"))
                errs.Add($"secret environment {name} must declare env or file delivery");
            if (!env["secret"].B && delivery.Length != 0)
                errs.Add($"non-secret environment {name} must not declare secret delivery");
            // Variables such as LD_PRELOAD can make the process load foreign code; refuse them.
            if (UnsafeInjectionEnv(name))
                errs.Add($"environment name {name} can inject executable code");
        }
        // Outbound network access the binary declares, keyed as scheme://host:port to spot duplicates.
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
        // Optional software bill of materials and build provenance documents.
        if (!variant["sbom"].IsNull && !ValidDescriptor(variant["sbom"]))
            errs.Add("sbom descriptor is invalid");
        if (!variant["provenance"].IsNull && !ValidDescriptor(variant["provenance"]))
            errs.Add("provenance descriptor is invalid");
        // "files" is the complete inventory of the payload. Each entry is checked on its own,
        // then against the real archive entry, so the declared and actual contents match exactly.
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
            // Each entry type may only carry the metadata that makes sense for it.
            switch (type)
            {
                // Regular file: needs a size and digest, no link target.
                case "file":
                    if (size < 0 || !Go.IsValidDigest(digest) || linkTarget.Length != 0)
                        errs.Add($"inventory file {path} has invalid size, digest, or link target");
                    break;
                case "directory":
                    if (size != 0 || digest.Length != 0 || linkTarget.Length != 0)
                        errs.Add($"inventory directory {path} has file or link metadata");
                    break;
                // Links: no content of their own, and the target must stay inside the payload.
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
            // Only the plain permission bits (octal 0777) are allowed; setuid, setgid, and sticky are refused.
            if (mode > 0x1FF)
                errs.Add($"inventory path {path} has invalid mode {Go.AlternateOctal(mode)}");
            inventory[path] = entry;
            // Look up the matching archive entry under payloadRoot.
            var actual = _content.Get(Go.PathJoin(payloadRoot, path));
            if (actual is null)
            {
                errs.Add($"inventory path {path} is absent from payload");
                continue;
            }
            // Declared metadata must match the archive exactly (type, mode, size, digest, link, owner).
            if (actual.Type != type || actual.Mode != mode || actual.Size != size || actual.Digest != digest ||
                actual.LinkTarget != linkTarget || actual.Uid != entry["uid"].I || actual.Gid != entry["gid"].I)
                errs.Add($"inventory path {path} does not match payload metadata");
            // Root ownership (uid/gid 0) so a non-root user can't own and change installed files.
            if (entry["uid"].I != 0 || entry["gid"].I != 0)
                errs.Add($"inventory path {path} must be root-owned");
            // 0x12 = group-write (020) + other-write (002). Writable content files could be tampered with.
            if ((mode & 0x12) != 0 && type is "file" or "hardlink")
                errs.Add($"inventory path {path} is group or world writable");
        }
        // Reverse check: every non-directory archive entry under payloadRoot must be in the inventory,
        // so nothing undeclared is shipped alongside the payload.
        var payloadPrefix = payloadRoot + "/";
        foreach (var entry in _content.Entries)
        {
            if (entry.Path == payloadRoot || !entry.Path.StartsWith(payloadPrefix, StringComparison.Ordinal) || entry.Type == "directory")
                continue;
            if (!inventory.ContainsKey(entry.Path[payloadPrefix.Length..]))
                errs.Add($"payload contains untracked path {entry.Path}");
        }
        // Map the absolute entrypoint back to an inventory path (relative to installRoot).
        var installPrefix = installRoot + "/";
        var entryRelative = entrypoint.StartsWith(installPrefix, StringComparison.Ordinal) ? entrypoint[installPrefix.Length..] : entrypoint;
        // 0x49 = execute bits for owner, group, and other (0111); at least one must be set.
        if (!inventory.TryGetValue(entryRelative, out var executable) || executable["type"].S != "file" || (executable["mode"].I & 0x49) == 0)
            errs.Add("entrypoint must identify an executable regular inventory file");
        // The claimed variantDigest must equal the digest recomputed from the raw JSON.
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
            // Absolute, already normalized (so "/a/../b" is refused), and listed once.
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
        // Work from raw JSON because the digest must cover the exact fields the author wrote,
        // including ones this validator does not model.
        if (AgentSuiteJson.ParseLoose(raw, out var root) is { } parseError)
            return parseError;
        var variants = new J(root)["variants"];
        // No variants (remote-only provider) is fine; there is nothing to digest.
        if (!variants.Present || variants.IsNull)
            return null;
        var result = new List<string>();
        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants.A[i];
            if (!variant.Has("variantDigest"))
                return $"variant {i} has no variantDigest";
            // Rebuild the variant object as JSON bytes, with variantDigest blanked out (a digest
            // can't include itself).
            var builder = new List<byte>();
            builder.Add((byte)'{');
            var first = true;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Walk keys from last to first so that, for a repeated key, the last value wins.
            foreach (var (key, value) in variant.Node!.Members!.AsEnumerable().Reverse())
            {
                // Go's JSON decoder keeps the last value of a duplicate key; skip earlier copies.
                if (!seen.Add(key))
                    continue;
                if (!first)
                    builder.Add((byte)',');
                first = false;
                builder.AddRange(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(key)));
                builder.Add((byte)':');
                // Other values are copied byte-for-byte from the original JSON.
                builder.AddRange(key == "variantDigest" ? "\"\""u8.ToArray() : value.RawBytes());
            }
            builder.Add((byte)'}');
            // Canonical JSON (RFC 8785) sorts keys and normalizes numbers and strings, so the
            // reversed key order above does not affect the digest.
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
        // Depth-first walk of the dependency graph starting at the root variant. "selected"
        // collects every variant that would be installed together with the root.
        var selected = new List<(string, J)>();
        var errs = new List<string?>();
        // "visited" = fully processed; "active" = on the current path, used to detect cycles.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string providerKey, J variant)
        {
            // A node is one exact build: provider id@version plus the variant digest.
            var identity = providerKey + "#" + variant["variantDigest"].S;
            // Reaching a node already on the current path means the dependencies loop.
            if (active.Contains(identity))
            {
                errs.Add($"tool provider {rootKey} has a dependency cycle through {providerKey}");
                return;
            }
            // Shared dependencies (diamonds) are only added once.
            if (visited.Contains(identity))
                return;
            active.Add(identity);
            selected.Add((providerKey, variant));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in variant["dependencies"].A)
            {
                // Each dependency pins an exact provider version and variant digest; it may not be
                // the provider itself or listed twice.
                var key = dependency["id"].S + "@" + dependency["version"].S;
                if (key == providerKey || seen.Contains(key) || !Id(dependency["id"].S) || !Version(dependency["version"].S) ||
                    !Go.IsValidDigest(dependency["variantDigest"].S))
                {
                    errs.Add($"tool provider {providerKey} has invalid or duplicate dependency {key}");
                    continue;
                }
                seen.Add(key);
                // The dependency must be another provider in this same suite.
                if (!ToolProviders.TryGetValue(key, out var dependencyProvider))
                {
                    errs.Add($"tool provider {providerKey} depends on missing tool provider {key}");
                    continue;
                }
                // It must have exactly one variant for the same platform, and that variant's digest
                // must be the one the dependency pinned.
                var (match, matches) = ExactVariant(dependencyProvider["variants"], variant["platform"]);
                if (matches != 1 || match["variantDigest"].S != dependency["variantDigest"].S)
                {
                    errs.Add($"tool provider {providerKey} dependency {key} does not bind one {variant["platform"].Platform} variant");
                    continue;
                }
                Visit(key, match);
            }
            // Done with this node: take it off the current path and mark it finished.
            active.Remove(identity);
            visited.Add(identity);
        }

        Visit(rootKey, root);
        closure = selected;
        return Go.Join(errs);
    }

    private static string? AddVariantDestinations(Dictionary<string, J> destinations, J variant)
    {
        // Variants installed together share one filesystem. Two variants may write the same
        // absolute path only if the entries are identical; otherwise one would overwrite the other.
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

    // Two inventory entries are interchangeable when every installed attribute matches.
    private static bool SameInstallEntry(J a, J b) =>
        a["type"].S == b["type"].S && a["mode"].I == b["mode"].I && a["uid"].I == b["uid"].I && a["gid"].I == b["gid"].I &&
        a["size"].I == b["size"].I && a["digest"].S == b["digest"].S && a["linkTarget"].S == b["linkTarget"].S;
}
