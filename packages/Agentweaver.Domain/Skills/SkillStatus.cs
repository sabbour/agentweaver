namespace Agentweaver.Domain.Skills;

/// <summary>
/// Lifecycle status of a catalog skill. Only <see cref="Active"/> skills are injected into agent
/// prompts; <see cref="Missing"/> (source disappeared) and <see cref="Malformed"/> skills are kept
/// visible in the catalog for feedback but are never silently applied to agents.
/// </summary>
public enum SkillStatus
{
    /// <summary>Valid and eligible for assignment + prompt injection.</summary>
    Active,

    /// <summary>Synced skill whose source disappeared from the connected repository.</summary>
    Missing,

    /// <summary>Structure/metadata validation failed on the last sync/import.</summary>
    Malformed,
}

public static class SkillStatusExtensions
{
    public static string ToApiString(this SkillStatus s) => s switch
    {
        SkillStatus.Active => "active",
        SkillStatus.Missing => "missing",
        SkillStatus.Malformed => "malformed",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static SkillStatus ParseStatus(string s) => s switch
    {
        "active" => SkillStatus.Active,
        "missing" => SkillStatus.Missing,
        "malformed" => SkillStatus.Malformed,
        _ => throw new ArgumentException($"Unknown skill status: {s}"),
    };
}
