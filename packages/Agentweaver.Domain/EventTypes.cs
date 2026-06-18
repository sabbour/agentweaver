namespace Agentweaver.Domain;

/// <summary>Canonical event type strings used in the run event stream (FR-018).</summary>
public static class EventTypes
{
    public const string RunStarted   = "run.started";
    public const string RunCompleted = "run.completed";
    public const string RunFailed    = "run.failed";
    public const string RunBounded   = "run.bounded";
    /// <summary>
    /// Non-terminal error event emitted when an operation fails but the run is
    /// reverted to a retryable state (e.g., AwaitingReview after merge InternalError).
    /// Does NOT mark the stream as terminal.
    /// </summary>
    public const string RunError = "run.error";

    public const string ReviewRequested = "review.requested";
    public const string ReviewApproved  = "review.approved";
    public const string ReviewDeclined  = "review.declined";

    public const string MergeStarted    = "merge.started";
    public const string MergeCompleted  = "merge.completed";
    public const string MergeFailed     = "merge.failed";
    /// <summary>
    /// Emitted when a merge attempt encounters conflicts that require human resolution.
    /// Payload: { conflicting_files: string[] }
    /// </summary>
    public const string MergeConflicted = "merge.conflicted";

    public const string AgentMessage      = "agent.message";
    public const string AgentMessageDelta = "agent.message.delta";
    public const string AgentIntent       = "agent.intent";
    /// <summary>
    /// Emitted when the agent calls report_outcome at the end of a run.
    /// Payload: { achieved: bool, reason: string }
    /// </summary>
    public const string RunOutcome        = "run.outcome";
    public const string ToolCall          = "tool.call";
    public const string ToolResult        = "tool.result";
    public const string ToolError         = "tool.error";
    /// <summary>
    /// Emitted when the sandbox intercepts a tool call (e.g. web_fetch) that requires
    /// operator approval before proceeding. The run stream pauses at the permission gate
    /// until the operator grants or denies via the tool-approvals/tool-denials endpoints.
    /// Payload: { requestId, toolName, url?, intention?, message }
    /// </summary>
    public const string ToolApprovalRequired = "tool.approval_required";

    public const string ReviewChangesRequested = "review.changes_requested";
    public const string RevisionStarted        = "revision.started";
    public const string RunCancelled           = "run.cancelled";

    /// <summary>
    /// Emitted at each major MAF workflow executor transition, as it starts and completes, for every
    /// executor node in both the full and trimmed child pipelines (agent, rai, assemble-ready, merge,
    /// scribe, review). The agent/rai/merge/scribe nodes self-emit from their executors (with extra
    /// "revise"/"skipped" statuses); the child assemble-ready terminal is emitted by the watch loop's
    /// MAF executor-lifecycle translation; review is HITL-driven. <c>step</c> equals the descriptor
    /// node id the frontend keys on, and <c>timestamp_utc</c> on the "started" event drives the live
    /// elapsed timer.
    /// Payload: { step: string, status: "started"|"completed"|"skipped"|"failed", label: string,
    ///            timestamp_utc: string (ISO 8601 "O"), agent_name?: string, message?: string }
    /// </summary>
    public const string WorkflowStep = "workflow.step";

    /// <summary>
    /// Emitted once at run start with a full snapshot of the run's workflow graph descriptor
    /// (the dynamic per-run visualization). Built from the same code that wires the MAF workflow,
    /// so plumbing nodes are already collapsed/dropped. Persisted like other RunEvents so the
    /// REST seed path (/api/runs/{id}/events) and /api/runs/{id}/graph work for finished runs.
    /// Payload: GraphDescriptor { graph_id, variant, start_node_id, nodes[], edges[] }.
    /// </summary>
    public const string WorkflowGraph = "run.workflow_graph";

    /// <summary>
    /// Emitted when the sandbox blocks at least one tool call during a run.
    /// Non-terminal — the run continues, but the outcome is degraded.
    /// Payload: { toolName: string, reason: string }
    /// </summary>
    public const string RunDegraded = "run.degraded";

    /// <summary>
    /// Emitted by <see cref="RaiTurnExecutor"/> with the verdict issued by the RAI reviewer.
    /// Payload: { verdict: "green"|"yellow"|"red", runId: string }
    /// Written to the Rai sub-stream ({runId}-rai).
    /// </summary>
    public const string RaiVerdict = "rai.verdict";

    /// <summary>
    /// Emitted when a coordinator (orchestration) run begins.
    /// Payload: { goal: string }
    /// </summary>
    public const string CoordinatorStarted = "coordinator.started";

    /// <summary>
    /// Emitted when the coordinator presents an outcome-spec draft (or revision) for
    /// human confirmation. The run is suspended at the await-confirmation gate after this.
    /// Payload: { specId, status, desiredOutcome, scope, assumptions, clarifyingQuestions }
    /// </summary>
    public const string CoordinatorOutcomeSpec = "coordinator.outcome_spec";

    /// <summary>
    /// Emitted when a human confirms the coordinator's outcome spec, unblocking the run.
    /// Payload: { specId, confirmedBy }
    /// </summary>
    public const string CoordinatorOutcomeSpecConfirmed = "coordinator.outcome_spec.confirmed";

    /// <summary>
    /// Emitted once, at plan time, after the coordinator decomposes a confirmed outcome spec into
    /// a persisted work plan (WorkPlan + Subtask rows + SubtaskDependency edges). Carries the full
    /// plan-time snapshot so clients can render the plan without any client-side computation. The
    /// richer <c>coordinator.topology</c> delta stream (dispatch/observe transitions) is a later wave.
    /// Payload: { workPlanId, status, subtasks: [ { id, title, assignedAgent, selectedModelId,
    /// phase, isolation, dependsOn: number[] } ] }
    /// </summary>
    public const string CoordinatorWorkPlan = "coordinator.work_plan";

    /// <summary>
    /// Terminal signal emitted on a coordinator CHILD run's existing run stream when the child
    /// completes its trimmed pipeline (agent + RAI) and is ready to be collected/assembled by
    /// the coordinator. This is the minimal child-side hand-off; the coordinator-level
    /// <c>subtask.*</c> projection events are produced by the dispatch wave.
    /// Payload: { runId, subtaskId?, parentRunId?, worktreeBranch, treeHash, hasChanges, stepCount, raiSafetyFlagged }
    /// </summary>
    public const string RunAssembleReady = "run.assemble_ready";

    // -----------------------------------------------------------------------
    // Coordinator dispatch + observe (Feature 008 Phase 2) — subtask lifecycle.
    // All of these are projected onto the COORDINATOR run's stream (never the child's). Each
    // carries the same shape so a client can apply them uniformly:
    //   { subtaskId, childRunId, assignedAgent, selectedModelId, status }
    // where status is the new Subtask.Status (pending|dispatched|running|rai_flagged|
    // assemble_ready|completed|failed).
    // -----------------------------------------------------------------------

    /// <summary>Emitted when a subtask's child run has been launched (Subtask.Status -> dispatched).</summary>
    public const string SubtaskDispatched = "subtask.dispatched";

    /// <summary>Emitted once the dispatched child run is executing (Subtask.Status -> running).</summary>
    public const string SubtaskRunning = "subtask.running";

    /// <summary>Emitted when the child run reached assemble-ready (Subtask.Status -> assemble_ready).</summary>
    public const string SubtaskAssembleReady = "subtask.assemble_ready";

    /// <summary>Emitted when the child run was flagged by RAI (Subtask.Status -> rai_flagged).</summary>
    public const string SubtaskRaiFlagged = "subtask.rai_flagged";

    /// <summary>Emitted when the child run terminated with no changes (Subtask.Status -> completed).</summary>
    public const string SubtaskCompleted = "subtask.completed";

    /// <summary>Emitted when the child run failed or was cancelled (Subtask.Status -> failed).</summary>
    public const string SubtaskFailed = "subtask.failed";

    /// <summary>
    /// Live topology of the coordinator graph, emitted on the coordinator run stream. A FULL
    /// snapshot (<c>kind == "snapshot"</c>) is emitted once at dispatch time; a DELTA
    /// (<c>kind == "delta"</c>) is emitted on every subsequent lifecycle transition. The client
    /// renders the graph thin from these payloads with NO client-side topology computation: the
    /// snapshot provides every node + edge, and each delta replaces the changed node(s) by id.
    /// Snapshot payload: { version, kind:"snapshot", coordinatorRunId, workPlanId, workPlanStatus,
    ///   seq, nodes: [ { id, kind, subtaskId?, status, label, agent?, model?, childRunId?, phase?,
    ///   isolation? } ], edges: [ { from, to } ], emittedAt }.
    /// Delta payload: { version, kind:"delta", coordinatorRunId, workPlanId, workPlanStatus, seq,
    ///   changed: [ { id, kind, subtaskId?, status, agent?, model?, childRunId? } ], emittedAt }.
    /// An edge { from, to } means node 'from' must reach assemble_ready/completed before node 'to'
    /// is dispatched (from = dependency, to = dependent). Edges never change after the snapshot.
    /// </summary>
    public const string CoordinatorTopology = "coordinator.topology";

    /// <summary>
    /// Unified coordinator graph in the shared <c>GraphDescriptor</c> contract (variant
    /// <c>"coordinator"</c>, graph_id <c>coordinator:{coordinatorRunId}</c>), emitted on the
    /// coordinator run stream as a FULL shape-only snapshot whenever the topology changes (e.g. a
    /// subtask child run is dispatched). Built from the work plan (no reflection). Unlike
    /// <see cref="CoordinatorTopology"/>, runtime status is NOT baked into the descriptor — it is
    /// shape only, consistent with the per-run <see cref="WorkflowGraph"/>; status is projected
    /// separately via the <c>subtask.*</c> / <c>coordinator.topology</c> streams. Nodes: the
    /// <c>coordinator</c> node, one <c>plan:subtask-{id}</c> node per subtask (node_type=subtask,
    /// child_graph_ref=<c>run:{childRunId}</c> once dispatched, carrying agent/model/phase/
    /// isolation/child_run_id), and the PLANNED collective-assembly chain
    /// (<c>planned:assembly-rai</c> -> <c>planned:assembly-review</c> -> <c>planned:assembly-merge</c>
    /// -> <c>planned:assembly-scribe</c>). Payload: a <c>GraphDescriptor</c>.
    /// </summary>
    public const string CoordinatorGraph = "coordinator.graph";

    /// <summary>
    /// Emitted on the COORDINATOR run's stream whenever a human steering directive
    /// (Feature 008 Phase 2) changes state, so the topology view can surface steering. Phase 2
    /// supports <c>stop</c>, <c>redirect</c>, and <c>amend</c> only (<c>pause</c> is descoped).
    /// Honest semantics: only <c>stop</c> reaches a child mid-turn (hard cancel, status jumps
    /// straight to <c>applied</c>); <c>redirect</c>/<c>amend</c> are <c>queued</c> and take effect
    /// at the target child's NEXT TURN BOUNDARY (queued -&gt; relayed -&gt; applied), never mid-turn.
    /// Payload: { directiveId: int, kind: "stop"|"redirect"|"amend", targetChildRunId: string|null
    ///   (null = broadcast to all active children), status: "pending"|"queued"|"relayed"|"applied",
    ///   instruction: string }.
    /// </summary>
    public const string CoordinatorSteering = "coordinator.steering";

    /// <summary>
    /// Emitted on the COORDINATOR run's stream once EVERY child subtask has reached a terminal
    /// state (completed / assemble_ready / failed) and the dispatch + observe loop has drained.
    /// The coordinator run now awaits Phase 3 collective assembly (merge), which is NOT yet built —
    /// this event is the explicit, non-hanging signal that dispatch is done so the UI stops showing
    /// the run as in-flight.
    /// Payload: { workPlanId, completed, assembleReady, failed, total }.
    /// </summary>
    public const string CoordinatorChildrenComplete = "coordinator.children_complete";

    // -----------------------------------------------------------------------
    // Coordinator collective assembly (Feature 008 Phase 3). Emitted on the COORDINATOR run's
    // stream once every child subtask is terminal and the work plan enters collective assembly:
    // ONE pipeline (integration branch -> collective RAI -> ONE human review -> ONE merge -> ONE
    // scribe) over the COMBINED output of all children. These are DISTINCT from the per-child
    // subtask.* / rai events — they describe the single collective stage. snake_case types,
    // monotonic seq, emitted through the same coordinator event-emit path as coordinator.graph /
    // coordinator.topology.
    // -----------------------------------------------------------------------

    /// <summary>Collective assembly claimed the work plan (awaiting_assembly -> assembling).
    /// Payload: { workPlanId, integrationBranch }.</summary>
    public const string CoordinatorAssemblyStarted = "coordinator.assembly_started";

    /// <summary>Collective RAI review of the aggregate diff began. Payload: { workPlanId }.</summary>
    public const string CoordinatorAssemblyRaiStarted = "coordinator.assembly_rai_started";

    /// <summary>Collective RAI review of the aggregate diff finished (advisory; never hard-blocks).
    /// Payload: { workPlanId, raiSafetyFlagged }.</summary>
    public const string CoordinatorAssemblyRaiCompleted = "coordinator.assembly_rai_completed";

    /// <summary>The ONE collective human-review gate was armed and is awaiting a decision.
    /// Payload: { workPlanId, integrationBranch, treeHash, raiSafetyFlagged }.</summary>
    public const string CoordinatorAssemblyReviewRequested = "coordinator.assembly_review_requested";

    /// <summary>The collective human-review gate was approved. Payload: { workPlanId, reviewer }.</summary>
    public const string CoordinatorAssemblyReviewApproved = "coordinator.assembly_review_approved";

    /// <summary>The reviewer requested changes; selected children are re-dispatched (review flows
    /// back to the coordinator). Payload: { workPlanId, reviewer, redispatchedSubtaskIds, inferredFiles }.</summary>
    public const string CoordinatorAssemblyChangesRequested = "coordinator.assembly_changes_requested";

    /// <summary>The single collective merge of the integration branch into origin began.
    /// Payload: { workPlanId, integrationBranch }.</summary>
    public const string CoordinatorAssemblyMergeStarted = "coordinator.assembly_merge_started";

    /// <summary>The single collective merge succeeded. Payload: { workPlanId, commitHash }.</summary>
    public const string CoordinatorAssemblyMergeCompleted = "coordinator.assembly_merge_completed";

    /// <summary>The single collective merge failed (conflict/error). Payload: { workPlanId, reason,
    /// conflictingFiles }.</summary>
    public const string CoordinatorAssemblyMergeFailed = "coordinator.assembly_merge_failed";

    /// <summary>The single collective scribe pass began. Payload: { workPlanId }.</summary>
    public const string CoordinatorAssemblyScribeStarted = "coordinator.assembly_scribe_started";

    /// <summary>The single collective scribe pass finished. Payload: { workPlanId }.</summary>
    public const string CoordinatorAssemblyScribeCompleted = "coordinator.assembly_scribe_completed";

    /// <summary>Collective assembly completed end-to-end (merged + scribed); work plan is complete.
    /// Payload: { workPlanId, commitHash }.</summary>
    public const string CoordinatorAssemblyCompleted = "coordinator.assembly_completed";

    /// <summary>Collective assembly was blocked and stopped with NO partial assembly: either a
    /// subtask was not assembly-eligible, or merging child branches into the integration branch
    /// conflicted. Payload: { workPlanId, reason, ineligibleSubtaskIds?, conflictingFiles? }.</summary>
    public const string CoordinatorAssemblyBlocked = "coordinator.assembly_blocked";
}
