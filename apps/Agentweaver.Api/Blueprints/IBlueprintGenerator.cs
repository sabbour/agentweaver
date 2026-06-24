using System.Text.Json;
using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Blueprints;

/// <summary>
/// Produces a blueprint from a free-text description using the model. Implementations return the
/// raw model response text; <see cref="BlueprintGenerationParser"/> turns it into a validated
/// blueprint plus any new roles it introduces. The seam keeps the model call isolated so the
/// parse/validate logic is exercisable without the live model.
/// </summary>
public interface IBlueprintGenerator
{
    Task<string> GenerateRawAsync(string description, CancellationToken ct);
}

/// <summary>The outcome of parsing a model-produced blueprint response.</summary>
public sealed record BlueprintGenerationResult(
    Blueprint? Blueprint,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Blueprint is not null && Errors.Count == 0;
}

/// <summary>
/// Tolerant parser for a model-produced blueprint. Extracts the first balanced JSON object from the
/// response (the model may wrap it in prose) and maps it to a <see cref="Blueprint"/>. Blueprints
/// never mint roles, so the roster is taken as-is and validated against the catalog by the caller.
/// Shape errors are collected rather than thrown so the generate endpoint can answer 422 with a
/// clear message.
/// </summary>
public static class BlueprintGenerationParser
{
    public static BlueprintGenerationResult Parse(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new BlueprintGenerationResult(null, ["The model returned an empty response."]);

        var start = rawResponse.IndexOf('{');
        var end = rawResponse.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new BlueprintGenerationResult(null, ["The model response did not contain a JSON object."]);

        JsonElement root;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawResponse[start..(end + 1)]);
            root = doc.RootElement;
        }
        catch (JsonException ex)
        {
            return new BlueprintGenerationResult(null, [$"The model response was not valid JSON: {ex.Message}"]);
        }

        using (doc)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return new BlueprintGenerationResult(null, ["The model response was not a JSON object."]);

            string? Str(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

            IReadOnlyList<string> StrArray(string name)
            {
                if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return [];
                return el.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }

            var blueprint = new Blueprint(
                Str("id") ?? string.Empty,
                Str("name") ?? string.Empty,
                Str("description") ?? string.Empty,
                StrArray("roster"),
                StrArray("workflows") is { Count: > 0 } wfs ? wfs : [Str("workflow") ?? "default"],
                Str("review_policy") ?? "default",
                Str("sandbox_profile") ?? "default");

            return new BlueprintGenerationResult(blueprint, []);
        }
    }
}
