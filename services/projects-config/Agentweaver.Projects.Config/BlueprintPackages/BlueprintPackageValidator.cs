// Ported without behavior changes from the 0.x Agentweaver.Squad blueprint package code.
// The AgentSuite blueprint converter uses it so 0.x packages are checked by the exact 0.x rules.
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentweaver.Projects.Config.BlueprintPackages;

/// <summary>Strict, side-effect-free validator for Blueprint package v1.</summary>
public static class BlueprintPackageValidator
{
    // IDs: lowercase letters, digits and hyphens, at most 64 characters, starting with a letter or digit.
    private static readonly Regex IdPattern = new(@"\A[a-z0-9](?:[a-z0-9-]{0,63})\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // SHA-256 digests: exactly 64 lowercase hex characters.
    private static readonly Regex HashPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Producer: a short tool name that may also use '.', '_', '/' and '-'.
    private static readonly Regex ProducerPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,127}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Revision: a lowercase hex commit ID, 7 to 64 characters.
    private static readonly Regex RevisionPattern = new(@"\A[0-9a-f]{7,64}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Strict UTF-8 throws on invalid bytes instead of substituting replacement characters.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static BlueprintPackageValidationResult Validate(BlueprintPackageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var errors = new List<string>();
        // Check the size before parsing so an oversized manifest is never loaded into a JSON document.
        if (source.RawManifest.Length > BlueprintPackageLimits.MaximumManifestBytes)
            return BlueprintPackageValidationResult.Failure(["manifest exceeds the maximum byte length."]);

        // Strict parse: no comments, trailing commas or duplicate keys, with bounded depth and number length.
        JsonDocument? document = null;
        try
        {
            document = StrictJson.Parse(source.RawManifest.AsSpan());
        }
        catch (JsonException exception)
        {
            errors.Add($"manifest is not strict JSON: {exception.Message}");
        }

        // Nothing more can be checked without a parsed manifest.
        if (document is null) return BlueprintPackageValidationResult.Failure(errors);
        using (document)
        {
            BlueprintPackageManifest? manifest;
            try
            {
                // Map the JSON to the manifest model, reporting grammar errors along the way.
                manifest = ParseManifest(document.RootElement, errors);
            }
            catch (JsonException exception)
            {
                errors.Add($"manifest is not strict JSON: {exception.Message}");
                return BlueprintPackageValidationResult.Failure(errors);
            }
            // A null manifest means a required field is missing or invalid, so stop here.
            if (manifest is null) return BlueprintPackageValidationResult.Failure(errors);
            // Check the custom schema keywords: canonical paths, HTTPS repository URI and timestamp format.
            BlueprintPackageSchema.ValidateCustomKeywords(document.RootElement, errors);
            // Check payload sizes before hashing or parsing any payload, so oversized input is rejected cheaply.
            var errorsBeforePayloadPreflight = errors.Count;
            PreflightPayloads(source, manifest, errors);
            if (errors.Count > errorsBeforePayloadPreflight) return BlueprintPackageValidationResult.Failure(errors);

            // Every listed payload must exist with the right size and hash, and every payload must be listed.
            ValidateInventory(source, manifest, errors);
            if (errors.Count > 0) return BlueprintPackageValidationResult.Failure(errors);

            // All checks passed. Compute the package identities from the exact input bytes.
            var rawManifest = source.RawManifest;
            var digests = new BlueprintPackageDigests(
                SemanticSha256: CalculateSemanticDigest(manifest, source.Payloads),
                PayloadSetSha256: CalculatePayloadSetDigest(source.Payloads),
                RawManifestSha256: BlueprintPackageHash.Sha256(rawManifest.AsSpan()),
                // A transport digest wins over the manifest value; ValidateInventory already made sure they agree.
                ContainerSha256: source.ContainerSha256 ?? manifest.ContainerSha256);

            return BlueprintPackageValidationResult.Success(new BlueprintPackage(manifest, rawManifest, digests));
        }
    }

    public static string CalculateSemanticDigest(BlueprintPackageManifest manifest, IReadOnlyDictionary<string, ImmutableArray<byte>> payloads)
    {
        // The semantic digest covers what the package means, not how it is formatted: payloads are canonicalized first.
        // NUL separators keep adjacent fields apart. Provenance is excluded on purpose because it is descriptive only.
        var builder = new StringBuilder();
        builder.Append("blueprint-package-v1\0")
            .Append(manifest.PackageId).Append('\0')
            .Append(manifest.Version).Append('\0');
        // Compatibility changes where the package can be used, so it is hashed. A missing maximum adds an empty field.
        if (manifest.Compatibility is not null)
            builder.Append(manifest.Compatibility.MinimumAgentweaverVersion).Append('\0')
                .Append(manifest.Compatibility.MaximumAgentweaverVersion).Append('\0');

        // Definitions are hashed in ordinal path order so manifest order doesn't change the digest.
        foreach (var definition in manifest.Definitions.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            builder.Append(definition.Kind.ToString().ToLowerInvariant()).Append('\0')
                .Append(definition.Id).Append('\0')
                .Append(definition.Path).Append('\0')
                .Append(CanonicalPayload(definition, payloads[definition.Path])).Append('\0');
        }
        return BlueprintPackageHash.Sha256Utf8(builder.ToString());
    }

    /// <summary>Hashes exact sorted UTF-8 paths and raw payload bytes with length-delimited framing.</summary>
    public static string CalculatePayloadSetDigest(IReadOnlyDictionary<string, ImmutableArray<byte>> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        return BlueprintPackagePayloadSetDigest.Calculate(payloads.Select(payload =>
            new BlueprintPackagePayload(payload.Key, payload.Value.ToArray())));
    }

    private static BlueprintPackageManifest? ParseManifest(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("manifest root must be an object.");
            return null;
        }
        // Unknown properties are errors, so a newer or misspelled manifest is never half-understood.
        RejectUnknown(root, ["schema_version", "package", "definitions", "compatibility", "provenance", "container_sha256"], "manifest", errors);

        // Only schema version "1" is supported.
        var schemaVersion = RequiredString(root, "schema_version", "manifest", errors);
        if (schemaVersion != "1") errors.Add("schema_version must be exactly '1'.");
        var package = RequiredObject(root, "package", "manifest", errors);
        var packageId = package is null ? null : RequiredString(package.Value, "id", "package", errors);
        var versionText = package is null ? null : RequiredString(package.Value, "version", "package", errors);
        if (package is not null) RejectUnknown(package.Value, ["id", "version"], "package", errors);
        if (packageId is not null && !IdPattern.IsMatch(packageId)) errors.Add("package.id has an invalid grammar.");
        // Report an over-long version with its own message; otherwise require valid SemVer.
        SemanticVersion? version = null;
        if (versionText?.Length > BlueprintPackageLimits.MaximumVersionLength)
            errors.Add($"package.version exceeds the maximum length of {BlueprintPackageLimits.MaximumVersionLength} characters.");
        else if (!SemanticVersion.TryParse(versionText, out version))
            errors.Add("package.version must be SemVer 2.0.0.");

        // Keep parsing the other sections after an error so the caller sees as many problems as possible.
        var definitionsElement = RequiredArray(root, "definitions", "manifest", errors);
        var definitions = definitionsElement is null ? [] : ParseDefinitions(definitionsElement.Value, errors);
        var compatibility = root.TryGetProperty("compatibility", out var compatibilityElement)
            ? ParseCompatibility(compatibilityElement, errors) : null;
        var provenance = root.TryGetProperty("provenance", out var provenanceElement)
            ? ParseProvenance(provenanceElement, errors) : null;
        var container = OptionalString(root, "container_sha256", "manifest", errors);
        if (container is not null && !HashPattern.IsMatch(container)) errors.Add("container_sha256 must be lower-case SHA-256.");

        // Return a model only when every required field parsed; otherwise the errors list holds the reasons.
        return schemaVersion is null || packageId is null || version is null || definitionsElement is null
            ? null
            : new BlueprintPackageManifest(schemaVersion, packageId, version, definitions, compatibility, provenance, container);
    }

    private static ImmutableArray<BlueprintPackageDefinition> ParseDefinitions(JsonElement definitions, List<string> errors)
    {
        // A package must define at least one item, and the number of items is capped.
        if (definitions.GetArrayLength() is < 1 or > BlueprintPackageLimits.MaximumDefinitions)
        {
            errors.Add($"definitions must contain 1 to {BlueprintPackageLimits.MaximumDefinitions} entries.");
            return [];
        }

        var result = new List<BlueprintPackageDefinition>();
        // Paths and kind/ID pairs must each be unique across the inventory.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in definitions.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add("every definitions entry must be an object.");
                continue;
            }
            RejectUnknown(entry, ["kind", "id", "path", "size", "sha256"], "definition", errors);
            var kindText = RequiredString(entry, "kind", "definition", errors);
            var id = RequiredString(entry, "id", "definition", errors);
            var path = RequiredString(entry, "path", "definition", errors);
            var hash = RequiredString(entry, "sha256", "definition", errors);
            var size = RequiredInteger(entry, "size", "definition", errors);
            if (!TryKind(kindText, out var kind)) errors.Add("definition.kind must be blueprint, role, workflow, or skill.");
            if (id is not null && !IdPattern.IsMatch(id)) errors.Add("definition.id has an invalid grammar.");
            if (path is not null && !paths.Add(path)) errors.Add("definition.path occurs more than once.");
            // The NUL separator keeps kind and ID apart in the set key.
            if (kindText is not null && id is not null && !identities.Add($"{kindText}\0{id}")) errors.Add("definition kind/id occurs more than once.");
            if (hash is not null && !HashPattern.IsMatch(hash)) errors.Add("definition.sha256 must be lower-case SHA-256.");
            // Negative sizes are invalid. A missing or non-integer size also arrives here as -1.
            if (size < 0) errors.Add("definition.size must be non-negative.");
            // Only fully valid entries are added to the model.
            if (kindText is not null && id is not null && path is not null && hash is not null && size >= 0 && TryKind(kindText, out kind))
                result.Add(new BlueprintPackageDefinition(kind, id, path, size, hash));
        }
        return [.. result];
    }

    private static BlueprintPackageCompatibility? ParseCompatibility(JsonElement element, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("compatibility must be an object.");
            return null;
        }
        RejectUnknown(element, ["minimum_agentweaver_version", "maximum_agentweaver_version"], "compatibility", errors);
        var minimum = RequiredString(element, "minimum_agentweaver_version", "compatibility", errors);
        var maximum = OptionalString(element, "maximum_agentweaver_version", "compatibility", errors);
        if (!SemanticVersion.TryParse(minimum, out var min)) errors.Add("compatibility.minimum_agentweaver_version must be SemVer 2.0.0.");
        SemanticVersion? max = null;
        if (maximum is not null && !SemanticVersion.TryParse(maximum, out max))
            errors.Add("compatibility.maximum_agentweaver_version must be SemVer 2.0.0.");
        // The range is inclusive, so the minimum may equal the maximum.
        if (min is not null && max is not null && min.CompareTo(max) > 0) errors.Add("compatibility minimum cannot exceed maximum.");
        return min is null ? null : new BlueprintPackageCompatibility(min, max);
    }

    private static BlueprintPackageProvenance? ParseProvenance(JsonElement element, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("provenance must be an object.");
            return null;
        }
        RejectUnknown(element, ["source", "producer", "repository", "revision", "created_at"], "provenance", errors);
        var source = RequiredString(element, "source", "provenance", errors);
        var producer = OptionalString(element, "producer", "provenance", errors);
        var repository = OptionalString(element, "repository", "provenance", errors);
        var revision = OptionalString(element, "revision", "provenance", errors);
        var createdText = OptionalString(element, "created_at", "provenance", errors);
        // Source must be a known value. Repository and created_at formats are checked by the custom schema keywords.
        if (source is not ("catalog" or "generated" or "imported")) errors.Add("provenance.source is invalid.");
        if (producer is not null && !ProducerPattern.IsMatch(producer)) errors.Add("provenance.producer has an invalid grammar.");
        if (revision is not null && !RevisionPattern.IsMatch(revision)) errors.Add("provenance.revision must be a lower-case hexadecimal revision.");
        return source is null ? null : new BlueprintPackageProvenance(source, producer, repository, revision, createdText);
    }

    private static void ValidateInventory(BlueprintPackageSource source, BlueprintPackageManifest manifest, List<string> errors)
    {
        // The manifest describes the payloads, so it must not also be listed as one.
        if (source.Payloads.ContainsKey("manifest.json")) errors.Add("manifest.json is not a payload and must not appear in the source payload set.");
        if (source.ContainerSha256 is not null && !HashPattern.IsMatch(source.ContainerSha256)) errors.Add("provided container SHA-256 must be lower-case SHA-256.");
        // When both the transport and the manifest give a container digest, they must agree.
        if (source.ContainerSha256 is not null && manifest.ContainerSha256 is not null && source.ContainerSha256 != manifest.ContainerSha256)
            errors.Add("provided container SHA-256 conflicts with manifest container_sha256.");

        // Each listed payload must be present, match its declared size and hash, and have valid content.
        foreach (var definition in manifest.Definitions)
        {
            if (!source.Payloads.TryGetValue(definition.Path, out var bytes))
            {
                errors.Add($"inventory payload is missing: {definition.Path}");
                continue;
            }
            if (bytes.Length != definition.Size) errors.Add($"inventory size does not match: {definition.Path}");
            if (BlueprintPackageHash.Sha256(bytes.AsSpan()) != definition.Sha256) errors.Add($"inventory SHA-256 does not match: {definition.Path}");
            ValidatePayload(definition, bytes, errors);
        }
        // Extra payloads that aren't in the inventory are rejected, so nothing unhashed can ride along.
        foreach (var path in source.Payloads.Keys)
            if (!manifest.Definitions.Any(definition => definition.Path == path))
                errors.Add($"payload is not listed in the inventory: {path}");
    }

    private static void PreflightPayloads(BlueprintPackageSource source, BlueprintPackageManifest manifest, List<string> errors)
    {
        // Declared sizes over the limit fail early, before any payload is read.
        if (manifest.Definitions.Any(definition => definition.Size > BlueprintPackageLimits.MaximumPayloadBytes))
            errors.Add("definition.size exceeds the payload limit.");

        // Sum the actual sizes and stop at the first time the total limit is exceeded.
        var total = 0L;
        foreach (var (path, bytes) in source.Payloads)
        {
            if (bytes.Length > BlueprintPackageLimits.MaximumPayloadBytes)
                errors.Add($"payload exceeds the byte limit: {path}");
            // Written as a subtraction so the check itself can't overflow.
            if (total > BlueprintPackageLimits.MaximumTotalPayloadBytes - bytes.Length)
            {
                errors.Add("payload set exceeds the total byte limit.");
                break;
            }
            total += bytes.Length;
        }
    }

    private static void ValidatePayload(BlueprintPackageDefinition definition, ImmutableArray<byte> bytes, List<string> errors)
    {
        // Repeat the size check here so this method is safe even without the preflight.
        if (bytes.Length > BlueprintPackageLimits.MaximumPayloadBytes)
        {
            errors.Add($"payload exceeds the byte limit: {definition.Path}");
            return;
        }
        // Blueprint and role payloads are JSON and must pass the same strict parse as the manifest.
        if (definition.Kind is BlueprintPackageDefinitionKind.Blueprint or BlueprintPackageDefinitionKind.Role)
        {
            try { using var ignored = StrictJson.Parse(bytes.AsSpan()); }
            catch (JsonException exception) { errors.Add($"JSON payload is invalid ({definition.Path}): {exception.Message}"); }
        }
        else
        {
            try
            {
                // Workflow (YAML) and skill (Markdown) payloads are text: valid UTF-8 with no NUL characters.
                var text = StrictUtf8.GetString(bytes.AsSpan());
                if (text.IndexOf('\0') >= 0) errors.Add($"text payload contains NUL: {definition.Path}");
            }
            catch (DecoderFallbackException) { errors.Add($"text payload is not valid UTF-8: {definition.Path}"); }
        }
    }

    private static string CanonicalPayload(BlueprintPackageDefinition definition, ImmutableArray<byte> bytes)
    {
        // JSON payloads are canonicalized so formatting changes don't change the semantic digest.
        if (definition.Kind is BlueprintPackageDefinitionKind.Blueprint or BlueprintPackageDefinitionKind.Role)
        {
            using var document = StrictJson.Parse(bytes.AsSpan());
            return CanonicalJson.Write(document.RootElement);
        }
        // Text payloads: normalize line endings to LF and make sure the text ends with a newline.
        var text = StrictUtf8.GetString(bytes.AsSpan()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text.EndsWith('\n') ? text : $"{text}\n";
    }

    private static bool TryKind(string? text, out BlueprintPackageDefinitionKind kind) => text switch
    {
        "blueprint" => Set(BlueprintPackageDefinitionKind.Blueprint, out kind),
        "role" => Set(BlueprintPackageDefinitionKind.Role, out kind),
        "workflow" => Set(BlueprintPackageDefinitionKind.Workflow, out kind),
        "skill" => Set(BlueprintPackageDefinitionKind.Skill, out kind),
        // An unknown kind fails and leaves kind at its default value.
        _ => Set(default, out kind, false),
    };

    // Helper that lets the switch expression above assign the out value and return success in one step.
    private static bool Set(BlueprintPackageDefinitionKind value, out BlueprintPackageDefinitionKind result, bool success = true)
    {
        result = value;
        return success;
    }

    private static void RejectUnknown(JsonElement objectElement, string[] allowed, string context, List<string> errors)
    {
        foreach (var property in objectElement.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) errors.Add($"{context} contains an unknown property: {property.Name}");
    }

    private static JsonElement? RequiredObject(JsonElement parent, string name, string context, List<string> errors) =>
        Required(parent, name, JsonValueKind.Object, context, errors);
    private static JsonElement? RequiredArray(JsonElement parent, string name, string context, List<string> errors) =>
        Required(parent, name, JsonValueKind.Array, context, errors);
    private static string? RequiredString(JsonElement parent, string name, string context, List<string> errors)
    {
        var value = Required(parent, name, JsonValueKind.String, context, errors);
        return value is null ? null : ReadString(value.Value, name, context, errors);
    }

    private static string? OptionalString(JsonElement parent, string name, string context, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{context}.{name} must be a string.");
            return null;
        }
        return ReadString(value, name, context, errors);
    }

    private static long RequiredInteger(JsonElement parent, string name, string context, List<string> errors)
    {
        var value = Required(parent, name, JsonValueKind.Number, context, errors);
        // Only numbers that are exact Int64 integers are accepted; anything else reports an error and returns -1.
        return value is not null && value.Value.TryGetInt64(out var integer) ? integer : ReportInvalidInteger(context, name, errors);
    }

    private static long ReportInvalidInteger(string context, string name, List<string> errors)
    {
        errors.Add($"{context}.{name} must be an exact Int64 JSON integer.");
        return -1;
    }

    private static JsonElement? Required(JsonElement parent, string name, JsonValueKind kind, string context, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            errors.Add($"{context}.{name} is required.");
            return null;
        }
        if (value.ValueKind != kind)
        {
            errors.Add($"{context}.{name} has an invalid JSON type.");
            return null;
        }
        return value;
    }

    private static string? ReadString(JsonElement value, string name, string context, List<string> errors)
    {
        try
        {
            return value.GetString();
        }
        // GetString can throw on text that isn't valid Unicode; report a bad string instead of crashing.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            errors.Add($"{context}.{name} is not a valid JSON string.");
            return null;
        }
    }

}

// Strict JSON parsing used for the manifest and for JSON payloads.
internal static class StrictJson
{
    public static JsonDocument Parse(ReadOnlySpan<byte> utf8)
    {
        // Lexical checks first: bounded number tokens and correctly paired Unicode surrogate escapes.
        ValidateLexicalConstraints(utf8);
        // System.Text.Json then enforces no comments, no trailing commas and a nesting limit of 64.
        var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        try
        {
            // JsonDocument accepts duplicate keys, so reject them here at every depth.
            RejectDuplicatePropertyNames(document.RootElement);
            return document;
        }
        catch
        {
            // Dispose the document before rethrowing so it isn't leaked.
            document.Dispose();
            throw;
        }
    }

    private static void RejectDuplicatePropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException($"duplicate JSON property: {property.Name}");
                    RejectDuplicatePropertyNames(property.Value);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) RejectDuplicatePropertyNames(item);
                break;
        }
    }

    private static void ValidateLexicalConstraints(ReadOnlySpan<byte> utf8)
    {
        // A small scanner over the raw bytes. It tracks whether it is inside a string and right after a backslash.
        var inString = false;
        var escaped = false;
        var highSurrogatePending = false;
        for (var index = 0; index < utf8.Length; index++)
        {
            var current = utf8[index];
            if (!inString)
            {
                // Outside strings: note where strings start and measure each number token.
                if (current == (byte)'"') inString = true;
                else if (current is >= (byte)'0' and <= (byte)'9' or (byte)'-')
                {
                    var start = index;
                    while (index + 1 < utf8.Length && IsNumberTokenCharacter(utf8[index + 1])) index++;
                    // Very long number tokens are rejected so canonicalization stays cheap.
                    if (index - start + 1 > BlueprintPackageLimits.MaximumCanonicalNumberTokenLength)
                        throw new JsonException($"JSON number token exceeds the maximum length of {BlueprintPackageLimits.MaximumCanonicalNumberTokenLength} characters.");
                }
                continue;
            }
            // Inside a string and not right after a backslash.
            if (!escaped)
            {
                if (current == (byte)'\\') { escaped = true; continue; }
                // A pending high surrogate must be followed directly by another 'u' escape (the low surrogate).
                if (highSurrogatePending) throw new JsonException("unpaired Unicode high surrogate.");
                if (current == (byte)'"')
                {
                    inString = false;
                }
                continue;
            }
            // This byte is the character right after a backslash.
            escaped = false;
            if (highSurrogatePending && current != (byte)'u') throw new JsonException("unpaired Unicode high surrogate.");
            // Only 'u' escapes (four hex digits) need more checks; other escapes are one character.
            if (current != (byte)'u') continue;
            if (index + 4 >= utf8.Length) throw new JsonException("incomplete Unicode escape.");
            // Decode the four hex digits and track surrogate pairing.
            var codeUnit = ParseHex(utf8[(index + 1)..(index + 5)]);
            index += 4;
            if (char.IsHighSurrogate((char)codeUnit))
            {
                if (highSurrogatePending) throw new JsonException("unpaired Unicode high surrogate.");
                highSurrogatePending = true;
            }
            else if (char.IsLowSurrogate((char)codeUnit))
            {
                if (!highSurrogatePending) throw new JsonException("unpaired Unicode low surrogate.");
                highSurrogatePending = false;
            }
            else if (highSurrogatePending) throw new JsonException("unpaired Unicode high surrogate.");
        }
        // The input must not end inside a string or with a dangling high surrogate.
        if (inString || highSurrogatePending) throw new JsonException("unterminated JSON string or Unicode surrogate.");
    }

    private static bool IsNumberTokenCharacter(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E';

    private static int ParseHex(ReadOnlySpan<byte> value)
    {
        var result = 0;
        foreach (var item in value)
        {
            var digit = item switch
            {
                >= (byte)'0' and <= (byte)'9' => item - '0',
                >= (byte)'a' and <= (byte)'f' => item - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => item - 'A' + 10,
                _ => throw new JsonException("invalid Unicode escape."),
            };
            result = (result * 16) + digit;
        }
        return result;
    }
}

// Canonical JSON text used for the semantic digest.
internal static class CanonicalJson
{
    public static string Write(JsonElement element) => element.ValueKind switch
    {
        // Objects: keys sorted ordinally, no whitespace. Strings use the standard serializer escaping.
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => $"{JsonSerializer.Serialize(property.Name)}:{Write(property.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Write)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number => NormalizeNumber(element.GetRawText()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => throw new JsonException("unsupported JSON token."),
    };

    // The result is the significant digits, with no leading or trailing zeros, plus a decimal exponent. No floating point
    // conversion occurs: 1, 1.0, and 1e0 normalize identically within the package token limit.
    private static string NormalizeNumber(string raw)
    {
        EnsureBoundedNumberToken(raw);
        var index = 0;
        var negative = raw[index] == '-';
        if (negative) index++;
        // Split the token into mantissa and exponent.
        var exponentPosition = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentPosition < 0 ? raw[index..] : raw[index..exponentPosition];
        var exponent = exponentPosition < 0 ? 0 : ParseBoundedExponent(raw[(exponentPosition + 1)..]);
        // Join integer and fraction digits; each fraction digit lowers the exponent by one.
        var decimalPosition = mantissa.IndexOf('.');
        var integer = decimalPosition < 0 ? mantissa : mantissa[..decimalPosition];
        var fraction = decimalPosition < 0 ? string.Empty : mantissa[(decimalPosition + 1)..];
        var digits = (integer + fraction).TrimStart('0');
        // All zeros (including -0) normalize to "0".
        if (digits.Length == 0) return "0";
        exponent -= fraction.Length;
        // Move trailing zeros into the exponent.
        var trailing = digits.Length - digits.TrimEnd('0').Length;
        if (trailing > 0)
        {
            digits = digits[..^trailing];
            exponent += trailing;
        }
        return $"{(negative ? "-" : string.Empty)}{digits}{(exponent == 0 ? string.Empty : $"e{exponent.ToString(CultureInfo.InvariantCulture)}")}";
    }

    private static System.Numerics.BigInteger ParseBoundedExponent(string raw)
    {
        EnsureBoundedNumberToken(raw);
        var sign = raw[0] is '+' or '-' ? raw[..1] : string.Empty;
        var digits = sign.Length == 0 ? raw : raw[1..];
        // BigInteger keeps very large exponents exact; the token length limit keeps this cheap.
        return System.Numerics.BigInteger.Parse($"{sign}{digits}", CultureInfo.InvariantCulture);
    }

    private static void EnsureBoundedNumberToken(string raw)
    {
        if (raw.Length > BlueprintPackageLimits.MaximumCanonicalNumberTokenLength)
            throw new JsonException($"JSON number token exceeds the maximum length of {BlueprintPackageLimits.MaximumCanonicalNumberTokenLength} characters.");
    }
}
