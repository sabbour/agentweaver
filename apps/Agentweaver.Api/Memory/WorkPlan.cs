using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class WorkPlan
{
    [Key] public int Id { get; set; }
    public int OutcomeSpecId { get; set; }
    public required string ProjectId { get; set; }
    public required string CoordinatorRunId { get; set; }
    public string? IsolationSummary { get; set; }
    public string? IntegrationBranch { get; set; }

    /// <summary>
    /// The id of the functional workflow the coordinator selected for this task (Feature 015 US5).
    /// Persisted so downstream phases (decomposition, dispatch) drive execution from the selected
    /// topology rather than treating selection as advisory. Null when the project carries a single
    /// workflow (selection skipped) — downstream consumers fall back to the project default.
    /// </summary>
    public string? WorkflowId { get; set; }
    public required string Status { get; set; }      // planned | dispatching | awaiting_assembly | assembling | in_review | complete | assembly_blocked | assembly_failed | assembly_declined

    /// <summary>
    /// Phase 3 collective-assembly progress stage (null until assembly starts): rai | review |
    /// merge | scribe | done. Drives the coordinator graph node-flip (planned -&gt; live).
    /// </summary>
    public string? AssemblyStage { get; set; }

    /// <summary>Timestamp the work plan transitioned awaiting_assembly -&gt; assembling (the
    /// exactly-once CAS claim). Null until assembly is claimed.</summary>
    public DateTimeOffset? AssemblyStartedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
