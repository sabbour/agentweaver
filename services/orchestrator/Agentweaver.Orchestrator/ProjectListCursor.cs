using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Encodes and decodes the opaque, URL-safe keyset cursors used by project list reads.
/// </summary>
/// <remarks>
/// A cursor is base64url JSON. Unknown members are rejected, so a cursor from another list
/// shape fails to decode. Each store checks that the decoded cursor matches its own query.
/// </remarks>
internal static class ProjectListCursor
{
    /// <summary>The longest cursor string accepted.</summary>
    internal const int MaximumLength = 2048;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Serializes a cursor record to an unpadded base64url string.</summary>
    internal static string Encode<T>(T cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Deserializes a cursor string.</summary>
    /// <exception cref="CoordinationException">The cursor is empty, too long, or malformed.</exception>
    internal static T Decode<T>(string value) where T : class
    {
        if (value.Length is 0 or > MaximumLength)
            throw Invalid();
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw Invalid(),
            };
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(base64), JsonOptions) ?? throw Invalid();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw Invalid();
        }
    }

    /// <summary>The 400 error returned for a malformed cursor or a cursor from another query.</summary>
    internal static CoordinationException Invalid() =>
        new("cursor_invalid", StatusCodes.Status400BadRequest);
}
