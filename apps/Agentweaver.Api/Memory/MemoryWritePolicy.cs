namespace Agentweaver.Api.Memory;

public static class MemoryWritePolicy
{
    private static readonly HashSet<string> MemoryTypes =
        new(["core_context", "learning", "pattern", "update"], StringComparer.Ordinal);

    private static readonly HashSet<string> ImportanceLevels =
        new(["low", "medium", "high"], StringComparer.Ordinal);

    private static readonly HashSet<string> InboxTypes =
        new(["architectural", "scope", "process", "technical", "pattern", "learning", "update"], StringComparer.Ordinal);

    private static readonly HashSet<string> DecisionTypes =
        new(["architectural", "scope", "process", "technical"], StringComparer.Ordinal);

    public static bool IsMemoryType(string value) => MemoryTypes.Contains(value);
    public static bool IsImportance(string value) => ImportanceLevels.Contains(value);
    public static bool IsInboxType(string value) => InboxTypes.Contains(value);
    public static bool IsDecisionType(string value) => DecisionTypes.Contains(value);

    public static string? NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return null;

        var normalized = tags.Split(',')
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0 ? null : $",{string.Join(",", normalized)},";
    }
}
