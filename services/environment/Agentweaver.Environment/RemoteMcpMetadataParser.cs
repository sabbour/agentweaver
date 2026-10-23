using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Parses bounded Remote MCP registry and tools/list JSON into validated, digest-pinned contracts.</summary>
internal static class RemoteMcpMetadataParser
{
    internal const int MaximumDocumentBytes = 1024 * 1024;
    internal const int MaximumPageItems = 100;
    internal const int MaximumTools = 128;
    internal const int MaximumJsonDepth = 32;
    internal const int MaximumCursorLength = 2048;
    internal const int MaximumNameLength = 200;
    internal const int MaximumDescriptionLength = 4096;
    internal const int MaximumSchemaBytes = 256 * 1024;

    private static readonly HashSet<string> SchemaTypeNames = new(StringComparer.Ordinal)
    {
        "array",
        "boolean",
        "integer",
        "null",
        "number",
        "object",
        "string",
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaximumJsonDepth
    };

    /// <summary>Reads one bounded registry page and returns its server summaries, cursor, and source digest.</summary>
    internal static RemoteMcpRegistryServerPage ParseRegistryServerPage(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = ParseDocument(utf8Json);
        var root = RequireObject(document.RootElement, "Registry page");
        if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            throw Invalid("Registry page must contain a servers array.");
        if (servers.GetArrayLength() > MaximumPageItems)
            throw Invalid("Registry page exceeded the item limit.");

        var entries = ImmutableArray.CreateBuilder<RemoteMcpRegistryServerSummary>(servers.GetArrayLength());
        foreach (var response in servers.EnumerateArray())
        {
            var wrapper = RequireObject(response, "Registry server response");
            if (!wrapper.TryGetProperty("server", out var serverElement))
                throw Invalid("Registry server response is missing its server object.");
            var server = RequireObject(serverElement, "Registry server");
            var name = RequiredString(server, "name", MaximumNameLength);
            var version = RequiredString(server, "version", 255);
            var title = OptionalString(server, "title", 100);
            var description = RequiredString(server, "description", 100);
            entries.Add(new(name, version, title, description));
        }

        string? nextCursor = null;
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind != JsonValueKind.Null)
        {
            var metadataObject = RequireObject(metadata, "Registry page metadata");
            if (metadataObject.TryGetProperty("nextCursor", out var cursor) &&
                cursor.ValueKind != JsonValueKind.Null)
            {
                if (cursor.ValueKind != JsonValueKind.String)
                    throw Invalid("Registry nextCursor must be a string or null.");
                nextCursor = cursor.GetString();
                if (nextCursor is { Length: > MaximumCursorLength } ||
                    nextCursor?.Any(char.IsControl) == true)
                    throw Invalid("Registry nextCursor exceeded its length limit or contained control characters.");
                if (string.IsNullOrEmpty(nextCursor))
                    nextCursor = null;
            }
        }

        return new(entries.ToImmutable(), nextCursor, RemoteMcpDigest.Sha256(utf8Json.Span));
    }

    /// <summary>
    /// Reads one exact registry server version and selects only a static, canonical HTTPS Streamable HTTP endpoint.
    /// Registry templates, injected headers, duplicate endpoints, and floating versions are rejected.
    /// </summary>
    internal static RemoteMcpRegistryServerSelection ParseRegistryServerVersion(
        ReadOnlyMemory<byte> utf8Json,
        string expectedName,
        string exactVersion,
        string? selectedEndpointUri = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactVersion);
        if (string.Equals(exactVersion, "latest", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Registry selection requires an exact server version.");

        using var document = ParseDocument(utf8Json);
        var wrapper = RequireObject(document.RootElement, "Registry server response");
        if (!wrapper.TryGetProperty("server", out var serverElement))
            throw Invalid("Registry server response is missing its server object.");
        var server = RequireObject(serverElement, "Registry server");
        var name = RequiredString(server, "name", MaximumNameLength);
        var version = RequiredString(server, "version", 255);
        if (!string.Equals(name, expectedName, StringComparison.Ordinal) ||
            !string.Equals(version, exactVersion, StringComparison.Ordinal))
            throw Invalid("Registry returned a different server identity or version than requested.");

        var endpoints = ImmutableArray.CreateBuilder<string>();
        if (server.TryGetProperty("remotes", out var remotes) && remotes.ValueKind != JsonValueKind.Null)
        {
            if (remotes.ValueKind != JsonValueKind.Array || remotes.GetArrayLength() > MaximumPageItems)
                throw Invalid("Registry remote entries must be a bounded array.");

            foreach (var remoteElement in remotes.EnumerateArray())
            {
                var remote = RequireObject(remoteElement, "Registry remote transport");
                var type = RequiredString(remote, "type", 64);
                if (!string.Equals(type, "streamable-http", StringComparison.Ordinal))
                    continue;
                if (remote.TryGetProperty("variables", out _) ||
                    remote.TryGetProperty("headers", out var headers) &&
                    (headers.ValueKind != JsonValueKind.Array || headers.GetArrayLength() != 0))
                    throw Invalid("Templated Registry endpoints and Registry-supplied headers are unsupported.");

                var endpoint = RequiredString(remote, "url", 2048);
                if (endpoint.Contains('{') || endpoint.Contains('}'))
                    throw Invalid("Templated Registry endpoints are unsupported.");
                endpoints.Add(RemoteMcpUri.CanonicalizeHttps(endpoint, "url"));
            }
        }

        if (endpoints.Count == 0)
            throw Invalid("The exact Registry version has no supported static Streamable HTTP endpoint.");
        if (endpoints.Distinct(StringComparer.Ordinal).Count() != endpoints.Count)
            throw Invalid("The exact Registry version contains duplicate Streamable HTTP endpoints.");

        string selectedEndpoint;
        if (selectedEndpointUri is null)
        {
            if (endpoints.Count != 1)
                throw Invalid("The Registry version has multiple endpoints; select one exact endpoint.");
            selectedEndpoint = endpoints[0];
        }
        else
        {
            selectedEndpoint = RemoteMcpUri.CanonicalizeHttps(selectedEndpointUri, nameof(selectedEndpointUri));
            if (!endpoints.Contains(selectedEndpoint, StringComparer.Ordinal))
                throw Invalid("The selected endpoint is not advertised by the exact Registry version.");
        }

        var metadataDigest = RemoteMcpDigest.Sha256(utf8Json.Span);
        return new(
            new RemoteMcpRegistryServerPin(name, version, metadataDigest),
            endpoints.ToImmutable(),
            selectedEndpoint);
    }

    /// <summary>
    /// Validates and pins a complete single-page tools/list response. A response that has a
    /// nextCursor is rejected, because a partial catalog must never be pinned.
    /// </summary>
    internal static RemoteMcpParsedToolCatalog ParseToolsList(ReadOnlyMemory<byte> utf8Json)
    {
        // This entry point is for callers that have exactly one page. A continuation cursor means the
        // server holds more tools, and pinning only the first page would hide them from review.
        var page = ParseToolsListPage(utf8Json);
        if (page.NextCursor is not null)
            throw Invalid("MCP tools/list response has more pages; use paged discovery.");
        return BuildCatalog(page.Tools);
    }

    /// <summary>Combines validated tools/list pages into one sorted, digest-pinned catalog.</summary>
    internal static RemoteMcpParsedToolCatalog BuildCatalog(IEnumerable<RemoteMcpToolSchemaPin> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        // Sort by ordinal name so the digest doesn't depend on the order the server returned pages or tools.
        var orderedTools = tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToImmutableArray();
        // Each page is checked on its own, so check the combined total here too.
        if (orderedTools.Length > MaximumTools)
            throw Invalid("MCP tools/list result exceeded the tool limit.");
        // After sorting, duplicates sit next to each other. A name repeated across pages is rejected,
        // because a reviewer can't tell which definition a call would use.
        for (var index = 1; index < orderedTools.Length; index++)
        {
            if (string.Equals(orderedTools[index - 1].Name, orderedTools[index].Name, StringComparison.Ordinal))
                throw Invalid("MCP tools/list result contains duplicate tool names.");
        }

        // The catalog digest covers each name and tool revision. A NUL separates name from revision and a
        // newline separates tools; neither can appear in a valid name, so two catalogs can't share material.
        var catalogMaterial = string.Join(
            '\n',
            orderedTools.Select(tool => $"{tool.Name}\0{tool.ToolRevision}"));
        var catalogDigest = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(catalogMaterial));
        return new(orderedTools, catalogDigest);
    }

    /// <summary>Validates one bounded tools/list page and returns its tools and optional continuation cursor.</summary>
    internal static RemoteMcpToolsListPage ParseToolsListPage(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = ParseDocument(utf8Json);
        var root = RequireObject(document.RootElement, "MCP tools/list response");
        if (!root.TryGetProperty("result", out var resultElement))
            throw Invalid("MCP tools/list response is missing its result.");
        var result = RequireObject(resultElement, "MCP tools/list result");
        if (!result.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
            throw Invalid("MCP tools/list result must contain a tools array.");
        if (toolsElement.GetArrayLength() > MaximumTools)
            throw Invalid("MCP tools/list result exceeded the tool limit.");

        var tools = ImmutableArray.CreateBuilder<RemoteMcpToolSchemaPin>(toolsElement.GetArrayLength());
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolElement in toolsElement.EnumerateArray())
        {
            var tool = RequireObject(toolElement, "MCP tool");
            var name = RequiredString(tool, "name", 128);
            if (!names.Add(name))
                throw Invalid("MCP tools/list result contains duplicate tool names.");
            var description = OptionalString(tool, "description", MaximumDescriptionLength);
            if (!tool.TryGetProperty("inputSchema", out var schema) ||
                schema.ValueKind != JsonValueKind.Object ||
                !schema.TryGetProperty("type", out var schemaType) ||
                schemaType.ValueKind != JsonValueKind.String ||
                !string.Equals(schemaType.GetString(), "object", StringComparison.Ordinal))
                throw Invalid("Each MCP tool must provide an object input schema.");

            var schemaJson = schema.GetRawText();
            if (Encoding.UTF8.GetByteCount(schemaJson) > MaximumSchemaBytes)
                throw Invalid("MCP tool input schema exceeded the byte limit.");
            ValidateSchemaShape(schema, 0);
            var schemaDigest = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(schemaJson));
            var toolRevision = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(
                $"{name}\0{description ?? string.Empty}\0{schemaDigest}"));
            tools.Add(new(name, description, schemaJson, schemaDigest, toolRevision));
        }

        // A missing or null nextCursor ends paging. A present cursor is sent back to the server on the
        // next request, so it gets the same length and control-character limits as a caller cursor.
        string? nextCursor = null;
        if (result.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
        {
            if (cursor.ValueKind != JsonValueKind.String)
                throw Invalid("MCP tools/list nextCursor must be a string or null.");
            nextCursor = cursor.GetString();
            if (nextCursor is { Length: > MaximumCursorLength } || nextCursor?.Any(char.IsControl) == true)
                throw Invalid("MCP tools/list nextCursor exceeded its length limit or contained control characters.");
            // Some servers send an empty string on the last page. Treat it as the end of paging.
            if (string.IsNullOrEmpty(nextCursor))
                nextCursor = null;
        }

        return new(tools.ToImmutable(), nextCursor);
    }

    private static void ValidateSchemaShape(JsonElement schema, int depth)
    {
        // Boolean schemas are valid below the MCP-required object root; they make an individual
        // property unconditionally accepted or rejected without requiring a special case later.
        if (depth > MaximumJsonDepth)
            throw Invalid("MCP tool input schema exceeded the nesting limit.");
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return;
        if (schema.ValueKind != JsonValueKind.Object)
            throw Invalid("MCP tool input schema contains a value that is not a schema object.");

        foreach (var property in schema.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "type":
                    ValidateSchemaType(value);
                    break;
                case "properties":
                case "patternProperties":
                case "$defs":
                case "definitions":
                case "dependentSchemas":
                    ValidateSchemaMap(value, depth);
                    break;
                case "required":
                    ValidateSchemaStringArray(value, property.Name);
                    break;
                case "dependencies":
                    ValidateDependencies(value, depth);
                    break;
                case "dependentRequired":
                    ValidateDependentRequired(value);
                    break;
                case "allOf":
                case "anyOf":
                case "oneOf":
                    ValidateSchemaArray(value, depth, property.Name, requireItem: true);
                    break;
                case "prefixItems":
                    ValidateSchemaArray(value, depth, property.Name, requireItem: true);
                    break;
                case "items":
                    if (value.ValueKind == JsonValueKind.Array)
                        ValidateSchemaArray(value, depth, property.Name, requireItem: true);
                    else
                        ValidateSchemaShape(value, depth + 1);
                    break;
                case "additionalItems":
                case "additionalProperties":
                case "unevaluatedProperties":
                case "unevaluatedItems":
                case "propertyNames":
                case "contains":
                case "not":
                case "if":
                case "then":
                case "else":
                case "contentSchema":
                    ValidateSchemaShape(value, depth + 1);
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                        throw Invalid("MCP tool input schema enum must be a non-empty array.");
                    break;
                case "minimum":
                case "maximum":
                    ValidateSchemaNumber(value, property.Name);
                    break;
                case "exclusiveMinimum":
                case "exclusiveMaximum":
                    // Older JSON Schema drafts express exclusive bounds as booleans; newer
                    // drafts use numbers. The source dialect is optional, so accept either shape.
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        ValidateSchemaNumber(value, property.Name);
                    break;
                case "multipleOf":
                    ValidateSchemaNumber(value, property.Name);
                    if (value.GetDouble() <= 0)
                        throw Invalid("MCP tool input schema multipleOf must be greater than zero.");
                    break;
                case "minLength":
                case "maxLength":
                case "minItems":
                case "maxItems":
                case "minProperties":
                case "maxProperties":
                case "minContains":
                case "maxContains":
                    ValidateSchemaNonNegativeInteger(value, property.Name);
                    break;
                case "uniqueItems":
                case "deprecated":
                case "readOnly":
                case "writeOnly":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw Invalid($"MCP tool input schema {property.Name} must be a boolean.");
                    break;
                case "pattern":
                case "format":
                case "title":
                case "description":
                case "$comment":
                case "$id":
                case "$schema":
                case "$ref":
                case "$anchor":
                case "$dynamicRef":
                case "$dynamicAnchor":
                case "contentEncoding":
                case "contentMediaType":
                    if (value.ValueKind != JsonValueKind.String)
                        throw Invalid($"MCP tool input schema {property.Name} must be a string.");
                    break;
            }
        }
    }

    private static void ValidateSchemaType(JsonElement value)
    {
        // Drafts allow one type or a non-empty union. Reject duplicates and unknown names so
        // downstream consumers cannot silently interpret different type sets.
        if (value.ValueKind == JsonValueKind.String)
        {
            if (!SchemaTypeNames.Contains(value.GetString()!))
                throw Invalid("MCP tool input schema contains an unsupported type name.");
            return;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw Invalid("MCP tool input schema type must be a supported name or a non-empty array.");

        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in value.EnumerateArray())
        {
            if (type.ValueKind != JsonValueKind.String ||
                !SchemaTypeNames.Contains(type.GetString()!) ||
                !types.Add(type.GetString()!))
                throw Invalid("MCP tool input schema type contains an unsupported or duplicate name.");
        }
    }

    private static void ValidateSchemaMap(JsonElement value, int depth)
    {
        // Map keywords associate each name with a schema; validating every value prevents
        // malformed nested definitions from being retained in an otherwise valid catalog.
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("MCP tool input schema map keyword must be an object.");
        foreach (var entry in value.EnumerateObject())
            ValidateSchemaShape(entry.Value, depth + 1);
    }

    private static void ValidateDependentRequired(JsonElement value)
    {
        // Each dependentRequired entry is a set of property names, not a nested schema.
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("MCP tool input schema dependentRequired must be an object.");
        foreach (var entry in value.EnumerateObject())
            ValidateSchemaStringArray(entry.Value, "dependentRequired");
    }

    private static void ValidateDependencies(JsonElement value, int depth)
    {
        // Legacy dependencies values are either non-empty property-name sets or schemas.
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("MCP tool input schema dependencies must be an object.");
        foreach (var entry in value.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Array)
                ValidateSchemaStringArray(entry.Value, "dependencies");
            else
                ValidateSchemaShape(entry.Value, depth + 1);
        }
    }

    private static void ValidateSchemaArray(
        JsonElement value,
        int depth,
        string keyword,
        bool requireItem)
    {
        // Composition, tuple, and prefix arrays require at least one schema branch.
        if (value.ValueKind != JsonValueKind.Array ||
            requireItem && value.GetArrayLength() == 0)
            throw Invalid($"MCP tool input schema {keyword} must be a valid array.");
        foreach (var item in value.EnumerateArray())
            ValidateSchemaShape(item, depth + 1);
    }

    private static void ValidateSchemaStringArray(JsonElement value, string keyword)
    {
        // Required/dependency lists are sets of names; duplicates make their meaning ambiguous.
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid($"MCP tool input schema {keyword} must be an array of strings.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in value.EnumerateArray())
        {
            // JSON object property names may be empty or whitespace; only type and uniqueness
            // are schema invariants for these name sets.
            if (name.ValueKind != JsonValueKind.String || !names.Add(name.GetString()!))
                throw Invalid($"MCP tool input schema {keyword} contains an invalid or duplicate name.");
        }
    }

    private static void ValidateSchemaNumber(JsonElement value, string keyword)
    {
        // JSON Schema numeric bounds must be finite JSON numbers; booleans and overflow values
        // are rejected before any downstream consumer interprets the constraint.
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) ||
            !double.IsFinite(number))
            throw Invalid($"MCP tool input schema {keyword} must be a finite number.");
    }

    private static void ValidateSchemaNonNegativeInteger(JsonElement value, string keyword)
    {
        // Count/length constraints are mathematically integral and bounded; checking the raw token
        // avoids rounding decimal or exponent forms into a different value.
        if (value.ValueKind != JsonValueKind.Number || !TryGetNonNegativeInt32(value, out _))
            throw Invalid($"MCP tool input schema {keyword} must be a non-negative integer.");
    }

    private static bool TryGetNonNegativeInt32(JsonElement value, out int result)
    {
        result = 0;
        var raw = value.GetRawText();
        var exponentIndex = raw.IndexOf('e');
        if (exponentIndex < 0)
            exponentIndex = raw.IndexOf('E');
        var mantissaEnd = exponentIndex < 0 ? raw.Length : exponentIndex;
        var negative = raw[0] == '-';
        var firstDigit = negative ? 1 : 0;
        var fractionalDigits = 0;
        var inFraction = false;
        var digits = new StringBuilder(mantissaEnd - firstDigit);
        for (var index = firstDigit; index < mantissaEnd; index++)
        {
            if (raw[index] == '.')
            {
                inFraction = true;
                continue;
            }
            if (inFraction)
                fractionalDigits++;
            digits.Append(raw[index]);
        }

        // Negative zero is numerically zero; any other negative value violates a non-negative constraint.
        var firstSignificantDigit = 0;
        while (firstSignificantDigit < digits.Length && digits[firstSignificantDigit] == '0')
            firstSignificantDigit++;
        if (firstSignificantDigit == digits.Length)
            return true;
        if (negative)
            return false;

        var significantDigits = digits.ToString(firstSignificantDigit, digits.Length - firstSignificantDigit);
        long exponent = 0;
        if (exponentIndex >= 0)
        {
            var index = exponentIndex + 1;
            var exponentIsNegative = raw[index] == '-';
            if (raw[index] is '-' or '+')
                index++;
            // Values beyond the token length cannot bring a nonzero mantissa into Int32's range.
            var exponentLimit = raw.Length + 1L;
            for (; index < raw.Length; index++)
                exponent = Math.Min(exponentLimit, exponent * 10 + (raw[index] - '0'));
            if (exponentIsNegative)
                exponent = -exponent;
        }

        // Shift the decimal point by the exponent; discarded digits must be zeros for an exact integer.
        var scale = fractionalDigits - exponent;
        if (scale > 0)
        {
            if (scale >= significantDigits.Length)
                return false;
            var discardedDigits = (int)scale;
            for (var index = significantDigits.Length - discardedDigits; index < significantDigits.Length; index++)
                if (significantDigits[index] != '0')
                    return false;
            significantDigits = significantDigits[..^discardedDigits];
        }
        else
        {
            var appendedZeros = -scale;
            if (significantDigits.Length + appendedZeros > 10)
                return false;
            significantDigits += new string('0', (int)appendedZeros);
        }

        return int.TryParse(
                significantDigits,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out result) &&
            result >= 0;
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaximumDocumentBytes)
            throw Invalid("JSON document is empty or exceeded the byte limit.");
        try
        {
            var document = JsonDocument.Parse(utf8Json, DocumentOptions);
            try
            {
                ValidateNoDuplicateProperties(document.RootElement, 0);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("JSON document is malformed or exceeded the nesting limit.", exception);
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element, int depth)
    {
        if (depth > MaximumJsonDepth)
            throw Invalid("JSON document exceeded the nesting limit.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw InvalidDataExceptionForDuplicate();
                ValidateNoDuplicateProperties(property.Value, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateNoDuplicateProperties(item, depth + 1);
        }
    }

    private static JsonElement RequireObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw Invalid($"{name} must be an object.");

    private static string RequiredString(JsonElement element, string property, int maximumLength)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw Invalid($"Required string '{property}' is missing.");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl))
            throw Invalid($"String '{property}' is empty, too long, or contains control characters.");
        return text;
    }

    private static string? OptionalString(JsonElement element, string property, int maximumLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid($"Optional string '{property}' has an invalid type.");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl))
            throw Invalid($"Optional string '{property}' is empty, too long, or contains control characters.");
        return text;
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private static InvalidDataException InvalidDataExceptionForDuplicate() =>
        Invalid("JSON document contains a duplicate object property.");
}

/// <summary>Registry page summary for one server name, exact version, optional title, and description.</summary>
internal sealed record RemoteMcpRegistryServerSummary(
    string Name,
    string Version,
    string? Title,
    string Description);

/// <summary>Parsed registry page with its optional continuation cursor and raw-document digest.</summary>
internal sealed record RemoteMcpRegistryServerPage(
    ImmutableArray<RemoteMcpRegistryServerSummary> Servers,
    string? NextCursor,
    string ResponseSha256);

/// <summary>Exact registry version pin, available endpoints, and the one selected endpoint.</summary>
internal sealed record RemoteMcpRegistryServerSelection(
    RemoteMcpRegistryServerPin Server,
    ImmutableArray<string> AdvertisedEndpoints,
    string SelectedEndpointUri);

/// <summary>One validated tools/list page and its optional continuation cursor.</summary>
internal sealed record RemoteMcpToolsListPage(
    ImmutableArray<RemoteMcpToolSchemaPin> Tools,
    string? NextCursor);

/// <summary>Validated tool schemas and the digest used to pin one Remote MCP discovery result.</summary>
internal sealed record RemoteMcpParsedToolCatalog(
    ImmutableArray<RemoteMcpToolSchemaPin> Tools,
    string CatalogSha256);
