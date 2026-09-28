namespace Agentweaver.Api.Backlog;

public static class BacklogPrerequisiteOutcome
{
    public static bool IsSatisfied(string? status, string? result, string? commit, string? tree,
        bool hasCollectiveRevision = false, bool acceptedNoChange = false) =>
        IsIntegrated(status, result)
            && !string.IsNullOrWhiteSpace(commit) && !string.IsNullOrWhiteSpace(tree)
            && (status != "completed" || hasCollectiveRevision)
            && (status != "completed" || result != "confirmed" || acceptedNoChange);

    public static string Reason(bool archived, string? status, string? result, string? commit, string? tree,
        bool hasCollectiveRevision = false, bool acceptedNoChange = false)
    {
        if (archived) return "archived";
        if (status == "completed" && result == "confirmed")
            return hasCollectiveRevision && acceptedNoChange
                ? "accepted_no_change"
                : "upstream_output_revision_unavailable";
        if (IsIntegrated(status, result))
        {
            if (string.IsNullOrWhiteSpace(commit) || string.IsNullOrWhiteSpace(tree))
                return "upstream_output_identity_unavailable";
            if (status == "completed" && !hasCollectiveRevision)
                return "upstream_output_revision_unavailable";
            return acceptedNoChange ? "accepted_no_change" : "integrated";
        }
        if (result == "delegated_to_backlog") return "delegated";
        if (status == "failed" && result is not null && result.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return "cancelled";
        if (status is "failed" or "merge_failed" or "declined") return "failed";
        return "pending";
    }

    private static bool IsIntegrated(string? status, string? result) =>
        status == "merged" || status == "completed" && result is "assembly_complete" or "complete" or "confirmed";
}
