using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// Parses and validates a single review-policy YAML document into a <see cref="ReviewPolicy"/>
/// (Feature 010, FR-025/026/033). All discovery, validation, and composition is server-side; a client
/// never recomputes any of it (Principles III, IV). Parsing never throws to the caller: a malformed or
/// schema-invalid document is returned as an <see cref="ReviewPolicyLoadResult.Invalid"/> with a
/// specific, actionable, file-scoped message so the rest of the set keeps loading.
/// </summary>
public static class ReviewPolicyLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parses+validates a YAML document. Always returns a result (never throws).</summary>
    public static ReviewPolicyLoadResult Load(string yaml, string source, bool isBuiltIn = false)
    {
        ReviewPolicyYamlDto? dto;
        try
        {
            dto = Deserializer.Deserialize<ReviewPolicyYamlDto>(yaml);
        }
        catch (YamlException ex)
        {
            return ReviewPolicyLoadResult.Invalid(source, $"{source}: malformed YAML — {ex.Message}");
        }

        if (dto is null)
            return ReviewPolicyLoadResult.Invalid(source, $"{source}: empty or null review-policy document.");

        if (!TryMapAndValidate(dto, source, out var policy, out var error))
            return ReviewPolicyLoadResult.Invalid(source, error!);

        return ReviewPolicyLoadResult.Valid(source, policy!, isBuiltIn);
    }

    private static bool TryMapAndValidate(
        ReviewPolicyYamlDto dto, string source, out ReviewPolicy? policy, out string? error)
    {
        policy = null;
        error = null;

        if (string.IsNullOrWhiteSpace(dto.Name))
            return Fail(source, "missing required field 'name'.", out error);

        if (dto.Steps is null || dto.Steps.Count == 0)
            return Fail(source, "a review policy must declare at least one step.", out error);

        var steps = new List<ReviewStep>(dto.Steps.Count);
        foreach (var s in dto.Steps)
        {
            if (string.IsNullOrWhiteSpace(s.Kind))
                return Fail(source, "a step is missing its required 'kind' (rai | rubberduck | human-review).", out error);
            if (!TryParseStepKind(s.Kind, out var kind))
                return Fail(source, $"step has unknown kind '{s.Kind}' (expected rai | rubberduck | human-review).", out error);

            steps.Add(new ReviewStep
            {
                Kind = kind,
                Label = string.IsNullOrWhiteSpace(s.Label) ? null : s.Label,
            });
        }

        policy = new ReviewPolicy
        {
            Name = dto.Name!,
            Description = dto.Description,
            Steps = steps,
        };
        return true;
    }

    private static bool Fail(string source, string message, out string? error)
    {
        error = $"{source}: {message}";
        return false;
    }

    private static string Normalize(string raw) =>
        raw.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();

    private static bool TryParseStepKind(string raw, out ReviewStepKind kind)
    {
        switch (Normalize(raw))
        {
            case "rai": kind = ReviewStepKind.Rai; return true;
            case "rubberduck":
            case "rubber_duck": kind = ReviewStepKind.Rubberduck; return true;
            case "human_review":
            case "human": kind = ReviewStepKind.HumanReview; return true;
            default: kind = default; return false;
        }
    }
}

// ── YAML DTOs (snake_case via UnderscoredNamingConvention) ──────────────────────────────────────

/// <summary>Root YAML DTO for a review-policy document. All fields nullable; required-ness is enforced
/// by <see cref="ReviewPolicyLoader"/> with file-scoped messages.</summary>
internal sealed class ReviewPolicyYamlDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<ReviewStepYamlDto>? Steps { get; set; }
}

internal sealed class ReviewStepYamlDto
{
    public string? Kind { get; set; }
    public string? Label { get; set; }
}
