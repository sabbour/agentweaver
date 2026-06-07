namespace Scaffolder.Api.Persistence.Entities;

/// <summary>
/// Compliance and governance record for a run.
/// 
/// IMPORTANT: This is DISTINCT from the Event log (FR-028, research Decision 12).
/// The Event log is the real-time observable stream.
/// This record is the compliance store for auditors and governance reviewers.
/// 
/// Every governance policy decision made by GovernancePolicyEngine during a run
/// is captured here as a timestamped policyTrace entry.
/// A compliance reviewer must be able to reconstruct all policy outcomes from
/// policyTrace alone, without the Event log (SC-010).
/// </summary>
public sealed class OperationalRecordEntity
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    /// <summary>
    /// The identity of the user who submitted the run (echoed from Run.SubmittedBy).
    /// Preserved here for compliance queries that span the retention window.
    /// </summary>
    public required string SubmittedBy { get; set; }

    public ModelSource ModelSource { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// Total number of agent loop steps executed in this run.
    /// </summary>
    public int StepCount { get; set; }

    public RunOutcome? Outcome { get; set; }

    /// <summary>
    /// JSON array of timestamped governance policy decisions.
    /// Each entry contains: timestamp, policyType (tool_allowlist, model_source,
    /// sandbox_boundary, human_approval_gate, run_limits), decision (pass|reject),
    /// and details. Must be a complete and ordered trace of all policy decisions
    /// during the run (SC-010, FR-028).
    /// </summary>
    public string PolicyTrace { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public RunEntity Run { get; set; } = null!;
}
