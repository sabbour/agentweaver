namespace Scaffolder.Domain;

/// <summary>
/// Tracks operator approvals for pending destructive shell commands.
/// Approvals are keyed by (runId, commandHash) and cleared when the run ends.
/// </summary>
public interface IShellApprovalStore
{
    /// <summary>Records operator approval for a command hash within a run.</summary>
    void Approve(string runId, string commandHash);

    /// <summary>Returns true if the command hash has been approved for this run.</summary>
    bool IsApproved(string runId, string commandHash);

    /// <summary>Clears all approvals for a run (called on run completion).</summary>
    void Clear(string runId);
}
