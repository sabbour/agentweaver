namespace Agentweaver.Api.Backlog;

public static class BacklogPrerequisiteOutcome
{
    public static bool IsSatisfied(string? status, string? result) =>
        status == "merged" || status == "completed" &&
        result is "assembly_complete" or "complete" or "confirmed";

    public static string Reason(bool archived, string? status, string? result)
    {
        if (archived) return "archived";
        if (status == "merged" || status == "completed" && result is "assembly_complete" or "complete")
            return "integrated";
        if (status == "completed" && result == "confirmed") return "accepted_no_change";
        if (result == "delegated_to_backlog") return "delegated";
        if (status == "failed" && result is not null && result.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return "cancelled";
        if (status is "failed" or "merge_failed" or "declined") return "failed";
        return "pending";
    }
}
