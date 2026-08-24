using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentweaver.Domain;

/// <summary>
/// Recursively redacts values for well-known sensitive key names (token, password, secret, key,
/// credential, authorization, etc.) from structured event payloads — primarily tool call
/// arguments and tool result content.
///
/// Tool arguments/output can carry tokens, auth headers, passwords, or connection strings, and
/// the persisted event log is returned as-is by <c>GET /api/runs/{id}/events</c> and rendered
/// verbatim by the web trace panel and execution timeline. Redaction happens in two layers so raw
/// secrets are never observable through either path:
///   1. At event creation — every emitter (<c>CopilotAIAgent</c>, <c>GitHubCopilotAgentRunner</c>,
///      <c>FoundryAgentRunner</c>) calls this before emitting <c>tool.call</c>/<c>tool.result</c>/
///      <c>tool.error</c>, so redacted values are what get persisted and streamed live.
///   2. Defensively at API rendering — <c>RunEndpoints</c>' persisted-events endpoint redacts
///      again when reading rows back, so any already-persisted un-redacted row (e.g. from before
///      this fix shipped) is still masked in the response.
/// (Issue #850 security follow-up.)
/// </summary>
public static class SensitiveDataRedactor
{
    public const string RedactedPlaceholder = "***REDACTED***";

    /// <summary>
    /// Substring fragments (matched case-insensitively against a normalized property name with
    /// separators stripped) that mark a key as sensitive. Deliberately broad/over-inclusive —
    /// false positives (redacting a benign field whose name merely contains "key") are an
    /// acceptable trade-off against leaking real secrets.
    /// </summary>
    private static readonly string[] SensitiveKeyFragments =
    [
        "token",
        "authorization",
        "password",
        "secret",
        "credential",
        "connectionstring",
        "apikey",
        "privatekey",
        "clientsecret",
        "accesskey",
        "bearer",
        "key",
    ];

    public static bool IsSensitiveKey(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return false;
        var normalized = propertyName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        foreach (var fragment in SensitiveKeyFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Recursively redacts sensitive object keys within a JSON node tree. Returns a new tree; the
    /// input is not mutated (so callers can safely redact a node still referenced elsewhere).
    /// </summary>
    public static JsonNode? RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var property in obj)
                {
                    result[property.Key] = IsSensitiveKey(property.Key)
                        ? JsonValue.Create(RedactedPlaceholder)
                        : RedactNode(property.Value);
                }
                return result;
            }
            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                    result.Add(RedactNode(item));
                return result;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Redacts an arbitrary object graph (a POCO, anonymous type, dictionary, <see cref="JsonElement"/>,
    /// or <see cref="JsonNode"/>) by round-tripping it through JSON. Returns a <see cref="JsonNode"/>
    /// suitable for re-embedding directly in an event payload object (System.Text.Json serializes
    /// <see cref="JsonNode"/> values natively). Returns null for a null input.
    /// </summary>
    public static JsonNode? RedactObject(object? value)
    {
        if (value is null) return null;
        var json = value switch
        {
            JsonElement element => element.GetRawText(),
            JsonNode node => node.ToJsonString(),
            string s => s, // assume already-serialized JSON string when passed directly
            _ => JsonSerializer.Serialize(value),
        };
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        return RedactNode(parsed);
    }

    /// <summary>Redacts a <see cref="JsonElement"/> in place (used when rendering already-persisted
    /// payloads back through the REST API — the defensive second layer).</summary>
    public static JsonElement RedactElement(JsonElement element)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(element.GetRawText()); }
        catch (JsonException) { return element; }
        var redacted = RedactNode(node);
        if (redacted is null) return element;
        using var doc = JsonDocument.Parse(redacted.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// If <paramref name="content"/> parses as a JSON object/array, redacts sensitive keys within
    /// it and returns the re-serialized string; otherwise returns the content unchanged. Free-text
    /// tool output is not scanned for embedded secrets — only structured (object/array) content is
    /// covered, matching the "sensitive key name" redaction model used elsewhere in this type.
    /// </summary>
    public static string RedactJsonStringIfApplicable(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content ?? string.Empty;
        JsonNode? node;
        try { node = JsonNode.Parse(content); }
        catch (JsonException) { return content; }
        if (node is not (JsonObject or JsonArray)) return content;
        var redacted = RedactNode(node);
        return redacted?.ToJsonString() ?? content;
    }
}
