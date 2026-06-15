namespace Scaffolder.Squad.Squad;

/// <summary>
/// Relative path constants (from a project's working directory) for the squad layout.
/// </summary>
internal static class SquadPaths
{
    public const string SquadDir = ".squad";
    public const string TeamMd = ".squad/team.md";
    public const string GitAttributes = ".gitattributes";

    public const string RoutingMd = ".squad/routing.md";
    public const string DecisionsMd = ".squad/decisions.md";

    public const string RaiDir = ".squad/rai";
    public const string RaiPolicyMd = ".squad/rai/policy.md";
    public const string RaiAuditTrailMd = ".squad/rai/audit-trail.md";

    public const string CanonicalDir = ".squad/casting";
    public const string CanonicalPolicy = ".squad/casting/policy.json";
    public const string CanonicalRegistry = ".squad/casting/registry.json";
    public const string CanonicalRegistryEvents = ".squad/casting/registry.events.jsonl";
    public const string CanonicalHistory = ".squad/casting/history.json";
    public const string CanonicalHistoryEvents = ".squad/casting/history.events.jsonl";

    public const string LegacyPolicy = ".squad/casting-policy.json";
    public const string LegacyRegistry = ".squad/casting-registry.json";
    public const string LegacyHistory = ".squad/casting-history.json";

    public static string SlugName(string memberName)
        => memberName.Trim().ToLowerInvariant().Replace(' ', '-');

    /// <summary>
    /// Validates that a member name slug is safe for use as a directory/file path component.
    /// Only lower-case alphanumeric characters, hyphens, and underscores are allowed.
    /// Throws <see cref="ArgumentException"/> if the name contains path separators or traversal sequences.
    /// </summary>
    public static string ValidatedSlugName(string memberName)
    {
        var slug = SlugName(memberName);
        if (string.IsNullOrEmpty(slug))
            throw new ArgumentException("Member name must not be empty.", nameof(memberName));
        // Reject anything that isn't [a-z0-9_-] — path separators, dots, or traversal sequences.
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, @"^[a-z0-9][a-z0-9_\-]*$"))
            throw new ArgumentException(
                $"Member name '{memberName}' is not a valid identifier. Only letters, digits, hyphens, and underscores are allowed.",
                nameof(memberName));
        return slug;
    }

    public static string CharterFor(string memberName)
        => $".squad/agents/{ValidatedSlugName(memberName)}/charter.md";

    public static string HistoryFor(string memberName)
        => $".squad/agents/{ValidatedSlugName(memberName)}/history.md";

    public static string AlumniCharterFor(string memberName)
        => $".squad/agents/_alumni/{ValidatedSlugName(memberName)}/charter.md";

    public static string MafAgentFor(string agentName)
        => $".github/agents/{agentName.ToLowerInvariant()}.agent.md";
}
