using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentweaver.Squad.BlueprintPackages;

/// <summary>Strict, side-effect-free validator for Blueprint package v1.</summary>
public static class BlueprintPackageValidator
{
    private static readonly Regex IdPattern = new(@"\A[a-z0-9](?:[a-z0-9-]{0,63})\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex HashPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ProducerPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,127}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RevisionPattern = new(@"\A[0-9a-f]{7,64}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RepositoryPattern = new(
        @"\Ahttps://(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])(?::(?:0|[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:[/?#](?:[A-Za-z0-9\-._~:/?#[\]@!$&'()*+,;=]|%[0-9A-Fa-f]{2})*)?\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex Rfc3339Pattern = new(
        @"\A[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]+)?(?:Z|[+-](?:(?:0[0-9]|1[0-3]):[0-5][0-9]|14:00))\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly byte[] PayloadSetPrefix = "blueprint-package-payload-set-v1\0"u8.ToArray();

    public static BlueprintPackageValidationResult Validate(BlueprintPackageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var errors = new List<string>();
        if (source.RawManifest.Length > BlueprintPackageLimits.MaximumManifestBytes)
            return BlueprintPackageValidationResult.Failure(["manifest exceeds the maximum byte length."]);

        JsonDocument? document = null;
        try
        {
            document = StrictJson.Parse(source.RawManifest.AsSpan());
        }
        catch (JsonException exception)
        {
            errors.Add($"manifest is not strict JSON: {exception.Message}");
        }

        if (document is null) return BlueprintPackageValidationResult.Failure(errors);
        using (document)
        {
            BlueprintPackageManifest? manifest;
            try
            {
                manifest = ParseManifest(document.RootElement, errors);
            }
            catch (JsonException exception)
            {
                errors.Add($"manifest is not strict JSON: {exception.Message}");
                return BlueprintPackageValidationResult.Failure(errors);
            }
            if (manifest is null) return BlueprintPackageValidationResult.Failure(errors);
            BlueprintPackageSchema.ValidateCustomKeywords(document.RootElement, errors);
            var errorsBeforePayloadPreflight = errors.Count;
            PreflightPayloads(source, manifest, errors);
            if (errors.Count > errorsBeforePayloadPreflight) return BlueprintPackageValidationResult.Failure(errors);

            ValidateInventory(source, manifest, errors);
            if (errors.Count > 0) return BlueprintPackageValidationResult.Failure(errors);

            var rawManifest = source.RawManifest;
            var digests = new BlueprintPackageDigests(
                SemanticSha256: CalculateSemanticDigest(manifest, source.Payloads),
                PayloadSetSha256: CalculatePayloadSetDigest(source.Payloads),
                RawManifestSha256: BlueprintPackageHash.Sha256(rawManifest.AsSpan()),
                ContainerSha256: source.ContainerSha256 ?? manifest.ContainerSha256);

            return BlueprintPackageValidationResult.Success(new BlueprintPackage(manifest, rawManifest, digests));
        }
    }

    public static string CalculateSemanticDigest(BlueprintPackageManifest manifest, IReadOnlyDictionary<string, ImmutableArray<byte>> payloads)
    {
        var builder = new StringBuilder();
        builder.Append("blueprint-package-v1\0")
            .Append(manifest.PackageId).Append('\0')
            .Append(manifest.Version).Append('\0');
        if (manifest.Compatibility is not null)
            builder.Append(manifest.Compatibility.MinimumAgentweaverVersion).Append('\0')
                .Append(manifest.Compatibility.MaximumAgentweaverVersion).Append('\0');

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
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(PayloadSetPrefix);
        foreach (var payload in payloads.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(payload.Key));
            AppendLengthPrefixed(hash, payload.Value.AsSpan());
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static BlueprintPackageManifest? ParseManifest(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("manifest root must be an object.");
            return null;
        }
        RejectUnknown(root, ["schema_version", "package", "definitions", "compatibility", "provenance", "container_sha256"], "manifest", errors);

        var schemaVersion = RequiredString(root, "schema_version", "manifest", errors);
        if (schemaVersion != "1") errors.Add("schema_version must be exactly '1'.");
        var package = RequiredObject(root, "package", "manifest", errors);
        var packageId = package is null ? null : RequiredString(package.Value, "id", "package", errors);
        var versionText = package is null ? null : RequiredString(package.Value, "version", "package", errors);
        if (package is not null) RejectUnknown(package.Value, ["id", "version"], "package", errors);
        if (packageId is not null && !IdPattern.IsMatch(packageId)) errors.Add("package.id has an invalid grammar.");
        if (!SemanticVersion.TryParse(versionText, out var version)) errors.Add("package.version must be SemVer 2.0.0.");

        var definitionsElement = RequiredArray(root, "definitions", "manifest", errors);
        var definitions = definitionsElement is null ? [] : ParseDefinitions(definitionsElement.Value, errors);
        var compatibility = root.TryGetProperty("compatibility", out var compatibilityElement)
            ? ParseCompatibility(compatibilityElement, errors) : null;
        var provenance = root.TryGetProperty("provenance", out var provenanceElement)
            ? ParseProvenance(provenanceElement, errors) : null;
        var container = OptionalString(root, "container_sha256", "manifest", errors);
        if (container is not null && !HashPattern.IsMatch(container)) errors.Add("container_sha256 must be lower-case SHA-256.");

        return schemaVersion is null || packageId is null || version is null || definitionsElement is null
            ? null
            : new BlueprintPackageManifest(schemaVersion, packageId, version, definitions, compatibility, provenance, container);
    }

    private static ImmutableArray<BlueprintPackageDefinition> ParseDefinitions(JsonElement definitions, List<string> errors)
    {
        if (definitions.GetArrayLength() is < 1 or > BlueprintPackageLimits.MaximumDefinitions)
        {
            errors.Add($"definitions must contain 1 to {BlueprintPackageLimits.MaximumDefinitions} entries.");
            return [];
        }

        var result = new List<BlueprintPackageDefinition>();
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
            if (kindText is not null && id is not null && !identities.Add($"{kindText}\0{id}")) errors.Add("definition kind/id occurs more than once.");
            if (hash is not null && !HashPattern.IsMatch(hash)) errors.Add("definition.sha256 must be lower-case SHA-256.");
            if (size < 0) errors.Add("definition.size must be non-negative.");
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
        if (source is not ("catalog" or "generated" or "imported")) errors.Add("provenance.source is invalid.");
        if (producer is not null && !ProducerPattern.IsMatch(producer)) errors.Add("provenance.producer has an invalid grammar.");
        if (repository is not null && !IsRepositoryUri(repository))
            errors.Add("provenance.repository must be a strict absolute HTTPS URI.");
        if (revision is not null && !RevisionPattern.IsMatch(revision)) errors.Add("provenance.revision must be a lower-case hexadecimal revision.");
        DateTimeOffset? created = null;
        if (createdText is not null)
        {
            if (!Rfc3339Pattern.IsMatch(createdText) || !DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                errors.Add("provenance.created_at must use the package RFC 3339 timestamp profile.");
            else
                created = parsed;
        }
        return source is null ? null : new BlueprintPackageProvenance(source, producer, repository, revision, created);
    }

    private static void ValidateInventory(BlueprintPackageSource source, BlueprintPackageManifest manifest, List<string> errors)
    {
        if (source.Payloads.ContainsKey("manifest.json")) errors.Add("manifest.json is not a payload and must not appear in the source payload set.");
        if (source.ContainerSha256 is not null && !HashPattern.IsMatch(source.ContainerSha256)) errors.Add("provided container SHA-256 must be lower-case SHA-256.");
        if (source.ContainerSha256 is not null && manifest.ContainerSha256 is not null && source.ContainerSha256 != manifest.ContainerSha256)
            errors.Add("provided container SHA-256 conflicts with manifest container_sha256.");

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
        foreach (var path in source.Payloads.Keys)
            if (!manifest.Definitions.Any(definition => definition.Path == path))
                errors.Add($"payload is not listed in the inventory: {path}");
    }

    private static void PreflightPayloads(BlueprintPackageSource source, BlueprintPackageManifest manifest, List<string> errors)
    {
        if (manifest.Definitions.Any(definition => definition.Size > BlueprintPackageLimits.MaximumPayloadBytes))
            errors.Add("definition.size exceeds the payload limit.");

        var total = 0L;
        foreach (var (path, bytes) in source.Payloads)
        {
            if (bytes.Length > BlueprintPackageLimits.MaximumPayloadBytes)
                errors.Add($"payload exceeds the byte limit: {path}");
            if (total > BlueprintPackageLimits.MaximumTotalPayloadBytes - bytes.Length)
            {
                errors.Add("payload set exceeds the total byte limit.");
                break;
            }
            total += bytes.Length;
        }
    }

    private static bool IsRepositoryUri(string repository)
    {
        if (repository.Length > 2048 || !RepositoryPattern.IsMatch(repository)) return false;
        return Uri.TryCreate(repository, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.Port is >= -1 and <= 65535;
    }

    private static void ValidatePayload(BlueprintPackageDefinition definition, ImmutableArray<byte> bytes, List<string> errors)
    {
        if (bytes.Length > BlueprintPackageLimits.MaximumPayloadBytes)
        {
            errors.Add($"payload exceeds the byte limit: {definition.Path}");
            return;
        }
        if (definition.Kind is BlueprintPackageDefinitionKind.Blueprint or BlueprintPackageDefinitionKind.Role)
        {
            try { using var ignored = StrictJson.Parse(bytes.AsSpan()); }
            catch (JsonException exception) { errors.Add($"JSON payload is invalid ({definition.Path}): {exception.Message}"); }
        }
        else
        {
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes.AsSpan());
                if (text.IndexOf('\0') >= 0) errors.Add($"text payload contains NUL: {definition.Path}");
            }
            catch (DecoderFallbackException) { errors.Add($"text payload is not valid UTF-8: {definition.Path}"); }
        }
    }

    private static string CanonicalPayload(BlueprintPackageDefinition definition, ImmutableArray<byte> bytes)
    {
        if (definition.Kind is BlueprintPackageDefinitionKind.Blueprint or BlueprintPackageDefinitionKind.Role)
        {
            using var document = StrictJson.Parse(bytes.AsSpan());
            return CanonicalJson.Write(document.RootElement);
        }
        var text = new UTF8Encoding(false, true).GetString(bytes.AsSpan()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text.EndsWith('\n') ? text : $"{text}\n";
    }

    private static bool TryKind(string? text, out BlueprintPackageDefinitionKind kind) => text switch
    {
        "blueprint" => Set(BlueprintPackageDefinitionKind.Blueprint, out kind),
        "role" => Set(BlueprintPackageDefinitionKind.Role, out kind),
        "workflow" => Set(BlueprintPackageDefinitionKind.Workflow, out kind),
        "skill" => Set(BlueprintPackageDefinitionKind.Skill, out kind),
        _ => Set(default, out kind, false),
    };

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
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            errors.Add($"{context}.{name} is not a valid JSON string.");
            return null;
        }
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}

internal static class StrictJson
{
    public static JsonDocument Parse(ReadOnlySpan<byte> utf8)
    {
        ValidateLexicalConstraints(utf8);
        var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        try
        {
            RejectDuplicatePropertyNames(document.RootElement);
            return document;
        }
        catch
        {
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
        var inString = false;
        var escaped = false;
        var highSurrogatePending = false;
        for (var index = 0; index < utf8.Length; index++)
        {
            var current = utf8[index];
            if (!inString)
            {
                if (current == (byte)'"') inString = true;
                else if (current is >= (byte)'0' and <= (byte)'9' or (byte)'-')
                {
                    var start = index;
                    while (index + 1 < utf8.Length && IsNumberTokenCharacter(utf8[index + 1])) index++;
                    if (index - start + 1 > BlueprintPackageLimits.MaximumCanonicalNumberTokenLength)
                        throw new JsonException($"JSON number token exceeds the maximum length of {BlueprintPackageLimits.MaximumCanonicalNumberTokenLength} characters.");
                }
                continue;
            }
            if (!escaped)
            {
                if (current == (byte)'\\') { escaped = true; continue; }
                if (highSurrogatePending) throw new JsonException("unpaired Unicode high surrogate.");
                if (current == (byte)'"')
                {
                    inString = false;
                }
                continue;
            }
            escaped = false;
            if (highSurrogatePending && current != (byte)'u') throw new JsonException("unpaired Unicode high surrogate.");
            if (current != (byte)'u') continue;
            if (index + 4 >= utf8.Length) throw new JsonException("incomplete Unicode escape.");
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

internal static class CanonicalJson
{
    public static string Write(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => $"{JsonSerializer.Serialize(property.Name)}:{Write(property.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Write)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number => NormalizeNumber(element.GetRawText()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => throw new JsonException("unsupported JSON token."),
    };

    // The result uses a one-digit-free normalized mantissa and decimal exponent. No floating point
    // conversion occurs: 1, 1.0, and 1e0 normalize identically within the package token limit.
    private static string NormalizeNumber(string raw)
    {
        EnsureBoundedNumberToken(raw);
        var index = 0;
        var negative = raw[index] == '-';
        if (negative) index++;
        var exponentPosition = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentPosition < 0 ? raw[index..] : raw[index..exponentPosition];
        var exponent = exponentPosition < 0 ? 0 : ParseBoundedExponent(raw[(exponentPosition + 1)..]);
        var decimalPosition = mantissa.IndexOf('.');
        var integer = decimalPosition < 0 ? mantissa : mantissa[..decimalPosition];
        var fraction = decimalPosition < 0 ? string.Empty : mantissa[(decimalPosition + 1)..];
        var digits = (integer + fraction).TrimStart('0');
        if (digits.Length == 0) return "0";
        exponent -= fraction.Length;
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
        return System.Numerics.BigInteger.Parse($"{sign}{digits}", CultureInfo.InvariantCulture);
    }

    private static void EnsureBoundedNumberToken(string raw)
    {
        if (raw.Length > BlueprintPackageLimits.MaximumCanonicalNumberTokenLength)
            throw new JsonException($"JSON number token exceeds the maximum length of {BlueprintPackageLimits.MaximumCanonicalNumberTokenLength} characters.");
    }
}
