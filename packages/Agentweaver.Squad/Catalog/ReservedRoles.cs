namespace Agentweaver.Squad.Catalog;

/// <summary>
/// Denylist of reserved, platform-owned orchestration roles/agents that must never be offered,
/// proposed, minted, or castable as a domain team member in a blueprint- or workflow-generated
/// roster. Scribe (session logging/memory), Work Monitor (backlog/keep-alive polling, cast name
/// "Ralph"), Rai (responsible-AI safety review), and Coordinator (run orchestration itself) are
/// provisioned automatically for every team by <c>CastingService</c> -- they are never a domain
/// persona a blueprint or workflow should assign work to.
/// </summary>
public static class ReservedRoles
{
    /// <summary>Cast/agent display names reserved for built-in orchestration agents.</summary>
    public static readonly IReadOnlyCollection<string> ReservedNames =
        ["Scribe", "Ralph", "Rai", "Coordinator"];

    private static readonly HashSet<string> _reservedIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Catalog/role ids.
        "scribe",
        "work-monitor",
        "ralph",
        "rai",
        "rai-reviewer",
        "coordinator",
    };

    /// <summary>
    /// Returns whether the given role id, bespoke role id, or agent/role display name refers to a
    /// reserved orchestration role. Callers assembling a castable roster (blueprint generation,
    /// workflow generation, manual casting) MUST reject a value for which this returns true rather
    /// than allow it into the roster.
    /// </summary>
    public static bool IsReserved(string? roleIdOrName)
    {
        if (string.IsNullOrWhiteSpace(roleIdOrName)) return false;

        var trimmed = roleIdOrName.Trim();
        if (_reservedIdentifiers.Contains(trimmed)) return true;

        // Normalize "Work Monitor" / "work_monitor" style variants to the kebab-case id form.
        var normalized = trimmed.Replace(' ', '-').Replace('_', '-');
        return _reservedIdentifiers.Contains(normalized);
    }
}
