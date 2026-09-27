namespace Agentweaver.Api.Backlog;

public static class BacklogPrerequisiteOutcome
{
    public static bool IsSatisfied(string? status, string? result, string? commit, string? tree) =>
        status == "completed" && result == "confirmed"
        || IsIntegrated(status, result)
            && !string.IsNullOrWhiteSpace(commit) && !string.IsNullOrWhiteSpace(tree);

    public static string Reason(bool archived, string? status, string? result, string? commit, string? tree)
    {
        if (archived) return "archived";
        if (status == "completed" && result == "confirmed") return "accepted_no_change";
        if (IsIntegrated(status, result))
            return IsSatisfied(status, result, commit, tree) ? "integrated" : "upstream_output_identity_unavailable";
        if (result == "delegated_to_backlog") return "delegated";
        if (status == "failed" && result is not null && result.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return "cancelled";
        if (status is "failed" or "merge_failed" or "declined") return "failed";
        return "pending";
    }

    private static bool IsIntegrated(string? status, string? result) =>
        status == "merged" || status == "completed" && result is "assembly_complete" or "complete";
}
