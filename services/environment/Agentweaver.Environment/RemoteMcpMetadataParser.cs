using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

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

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaximumJsonDepth
    };

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

    internal static RemoteMcpParsedToolCatalog ParseToolsList(ReadOnlyMemory<byte> utf8Json)
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
            var schemaDigest = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(schemaJson));
            var toolRevision = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(
                $"{name}\0{description ?? string.Empty}\0{schemaDigest}"));
            tools.Add(new(name, description, schemaJson, schemaDigest, toolRevision));
        }

        var orderedTools = tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToImmutableArray();
        var catalogMaterial = string.Join(
            '\n',
            orderedTools.Select(tool => $"{tool.Name}\0{tool.ToolRevision}"));
        var catalogDigest = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(catalogMaterial));
        return new(orderedTools, catalogDigest);
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

internal sealed record RemoteMcpRegistryServerSummary(
    string Name,
    string Version,
    string? Title,
    string Description);

internal sealed record RemoteMcpRegistryServerPage(
    ImmutableArray<RemoteMcpRegistryServerSummary> Servers,
    string? NextCursor,
    string ResponseSha256);

internal sealed record RemoteMcpRegistryServerSelection(
    RemoteMcpRegistryServerPin Server,
    ImmutableArray<string> AdvertisedEndpoints,
    string SelectedEndpointUri);

internal sealed record RemoteMcpParsedToolCatalog(
    ImmutableArray<RemoteMcpToolSchemaPin> Tools,
    string CatalogSha256);
