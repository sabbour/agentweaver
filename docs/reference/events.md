# Events reference

See Outcome, DAG, redispatch and collective assembly event families for the shared visual model.

See Durable replay, nonterminal waits and child-human round trips for the shared visual model.

Every run event uses the same envelope:

| Field | Type | Notes |
| --- | --- | --- |
| `runId` | string | Run identifier |
| `sequence` | integer | Per-run monotonic ordering key |
| `type` | string | Event type from the fixed taxonomy |
| `timestamp` | ISO 8601 string | Informational only |
| `payload` | object | Type-specific data |
| `callId` | string | A field of the `payload` on tool events (`tool.call`, `tool.result`, `tool.error`); pairs a tool outcome with its call |

Clients should order and deduplicate events by `sequence`.

## Event taxonomy

| Type | When it fires | Payload fields |
| --- | --- | --- |
| `agent.turn.start` | When the model begins a turn | `turnId` |
| `agent.message.delta` | When the model streams a chunk of visible text from the GitHub Copilot SDK runner | `delta`, `messageId` |
| `agent.turn.end` | When the model finishes a turn (closes the turn bubble in the frontend) | `turnId` |
| `agent.intent` | When the agent calls `report_intent` before a major step | `intent` |
| `agent.system_prompt` | At run start, after the system prompt is set | `provider`, `prompt` (full text), `note` (optional) |
| `agent.tools` | At run start, listing the tools registered for this run | `tools` (string array of tool names) |
| `agent.runtime_context` | Once for each provider agent turn after the prompt and provider tool declarations are assembled | `provider`, `runId`, `projectId`, `baseCharacters`, `runContextCharacters`, `skillCharacters`, `separatorCharacters`, `taskCharacters`, `toolDeclarationCharacters`, `skillDeliveryMode` (`none`, `file`, `inline`, `mixed`), `totalCharacters`, `estimatedTokens` |
| `memory.context_composition` | After the structured memory context is selected for a run or coordinator decomposition | `included`, `omittedMemoryCount`, `omittedSessionCount`, `omissionCauses`; no prompt text, records, identifiers, or size measurements |
| `tool.call` | Before the runtime evaluates a tool invocation against the sandbox policy | `callId`, `toolName`, `arguments` |
| `tool.result` | After an approved tool runs successfully | `callId`, `content` |
| `tool.error` | After a tool is denied by the sandbox policy, or fails for any other reason such as a missing file or I/O failure | `callId`, `errorMessage` |
| `tool.approval_required` | When a tool call is paused awaiting human approval | `request_id`, `tool_name`, `url` (optional), `intention` (optional) |
| `tool.approval_pending` | Heartbeat re-emitted every ~20s while a tool call is blocked on a human-approval gate; keeps the run's stream flowing so the buffered `tool.approval_required` is delivered/persisted and the coordinator stall timer is reset. Non-terminal; consumers may ignore it | `requestId`, `displayId`, `toolName` |
| `tool.auto_approved` | When an explicit policy auto-grants a repository-defined safe tool instead of waiting for a human; audit-only (the tool then runs). For `start_preview`, no approval card, notification, or waiter is created | `decisionId`, `toolName`, `approvalSource`, `policySnapshotId` (run-policy decisions), `previewTarget` and `targetPort` (`start_preview`), `url` (optional for other tools) |
| `agent.question_asked` | When an agent calls `ask_question` to bubble a clarifying question or permission request; the run suspends inside the tool call until answered or timed out | `requestId`, `question` |
| `agent.question_answered` | When a pending `ask_question` request is answered (or resolved by timeout) and the agent resumes | `requestId`, `answer`, `timedOut` |
| `run.completed` | When the watch loop determines the run is terminal with no file changes (watch-loop only; never emitted by the runner) | `result` |
| `run.outcome` | Agent self-assessment of task completion, emitted just before `run.completed` | `achieved` (bool), `reason` |
| `run.failed` | When the runtime, provider, or content-safety flow ends the run in failure | `message`, `errorCode`, `retryable` — bounded normalized public contract |
| `run.bounded` | When the run hits a step-count or wall-clock bound | `limit_type`, `step_count` |
| `run.cancelled` | When an in-progress run is cancelled because its project was deleted | *(none)* |
| `run.approval_policy_selected` | When a coordinator run persists its immutable launch approval policy | `autoApproveTools`, `autopilot`, `source` (`direct`, `backlog_pickup`, or `retry`), `capturedAt`, `settingsUpdatedAt` (heartbeat-derived policies), `inheritedFromRunId` (retries), `safeTools` |
| `run.error` | When an operation fails but the run is reverted to a retryable state (e.g. back to AwaitingReview after a merge internal error); **non-terminal** — the stream stays open | `reason` |
| `run.degraded` | When the sandbox blocks at least one tool call during a run; **non-terminal** — the run continues with a degraded outcome | `toolName`, `reason` |
| `rai.verdict` | The RAI reviewer's verdict for a run; written to both the parent run stream and the `{runId}-rai` sub-stream | `verdict` (`green` / `yellow` / `red` / `revise`), `runId`, `rationale` |
| `workflow.step` | When each workflow executor stage transitions (start/complete/fail/skip), for every node in both the full and child pipelines | `step`, `status`, `label`, `timestamp_utc`, `agent_name` (agent step only), `reviewer` (review step only), `message` (optional) |
| `run.workflow_graph` | Once at run start, carrying the full workflow graph descriptor for rendering the run topology | `GraphDescriptor` (see below) |
| `sandbox.provisioning_pending` | Heartbeat re-emitted about every 20s on a coordinator **child** run's own stream while its AgentHost `SandboxClaim` is unbound (the pod is still being scheduled by Kubernetes — a node may be freeing up or the pool autoscaling). Keeps the child stream flowing so the parent coordinator's subtask-stall timer resets during a legitimate provisioning wait instead of firing `agent_stall_timeout` (issue #217). Non-terminal and idempotent; consumers may ignore it | `claimName`, `timestamp_utc` |
| `coordinator.child_provisioning_pending` | Coordinator-scoped projection emitted once when a child enters the Kubernetes scheduling wait. Repeated child heartbeats are suppressed until another child event clears the state | `childRunId`, `subtaskId`, `claimName`, `timestamp_utc` |
| `sandbox.preview_applicability` | Before assembly Build & Test approval is evaluated, records whether the assembled artifact needs a preview or is skipped as not applicable | `run_id`, `work_plan_id`, `tree_hash`, `state`, `reason`, `evidence` |
| `sandbox.preview_start_requested` | When the deterministic preview step resolves a run command and starts the platform-owned preview attempt | `run_id`, `work_plan_id`, `tree_hash`, `source`, `command_source` |
| `sandbox.preview_pending` | When AgentPreviewGate is waiting for approval to expose the Build & Test preview port, including a fresh retry attempt | `run_id`, `work_plan_id`, `tree_hash`, `target_port`, `approval`, `request_id`, `expires_at`, `timeout_minutes`; optional `retry_of_request_id` |
| `sandbox.preview_ready` | When Gateway preview provisioning succeeds for the run | `run_id`, `work_plan_id`, `tree_hash`, `target_port`, `pod_name`, `session_id`, `preview_runner_session_id`, `preview_url`, `keepalive_url`, `started_at` |
| `coordinator.preview_ready` | Coordinator-scoped mirror of `sandbox.preview_ready` for the assembly preview | Same as `sandbox.preview_ready` |
| `sandbox.preview_failed` | When preview approval, app readiness, forwarder reachability, Gateway provisioning, or the approval-time outcome guard fails | `run_id`, `work_plan_id`, `tree_hash`, `source`, `reason`, `message`; optional `target_port`, `approval_request_id`, `retry_available`, `expired_at`, `preview_runner_session_id` |
| `sandbox.preview_skipped_not_applicable` | Final preview outcome for non-previewable assembled work or unavailable preview infrastructure | `run_id`, `work_plan_id`, `tree_hash`, `source`, `reason`, `message` or `evidence` |
| `review.requested` | After the worktree is committed and the review tree hash is stored | `tree_hash`, `request_id` |
| `review.approved` | When the owner approves the run and the merge proceeds | *(none)* |
| `review.declined` | When the owner declines the run | *(none)* |
| `review.changes_requested` | When the human reviewer requests a revision | `comment` |
| `revision.started` | When a request-changes revision cycle begins | `revision`, `message` |
| `merge.started` | When an approved merge begins execution | `tree_hash` |
| `merge.completed` | After an approved run merges cleanly into the originating branch | `merged_commit_hash`; `previous_head_sha` (direct path only) |
| `merge.failed` | After an approved run cannot merge back cleanly | `reason` |
| `coordinator.started` | When a coordinator run begins drafting an OutcomeSpec from the user's goal | `goal` |
| `coordinator.recovered` | When an interrupted coordinator run is resumed after a process restart and its dispatch / collective-assembly engine is re-armed from the persisted work plan | `status` (the work-plan status it resumed from) |
| `coordinator.outcome_spec` | When the coordinator has drafted an OutcomeSpec and suspended at the await-confirmation gate | `specId`, `status`, `desiredOutcome`, `scope`, `assumptions`, `clarifyingQuestions` |
| `coordinator.outcome_spec.confirmed` | When the drafted OutcomeSpec is confirmed through the normal seam, interactively or by launch Autopilot | `specId`, `confirmedBy` |
| `coordinator.work_plan` | When the coordinator has decomposed the confirmed spec into a persisted work plan | `workPlanId`, `status`, `subtasks`, `dependencies` |
| `coordinator.workflow_selected` | When the coordinator selects which workflow to run from a project's multi-workflow set (skipped silently when the project carries only one workflow) | `selectedId`, `selectedName`, `rationale`, `wasAutoSelected`, `overrideHint`, `available` (`[{ id, name }]`) |
| `coordinator.child_stall_detected` | When a child run emits no new events past the configured stall timeout and the coordinator marks that path as stalled | `childRunId`, `subtaskId`, `staleSinceUtc`, `stallTimeoutMinutes`, `lastEventSequence` |
| `coordinator.subtask_redispatched` | When a stalled subtask still has recovery budget and is reset to `pending` for a fresh child (on a fresh pod) instead of dead-ending the run | `subtaskId`, `priorChildRunId`, `attempt`, `maxAttempts`, `reason` (`stall_redispatch`), `timestamp_utc` |
| `coordinator.topology` | When the orchestration graph is first dispatched (snapshot) and on every subsequent subtask lifecycle transition (delta) | `version`, `kind`, `seq`, `nodes` (snapshot) / `changed` (delta), `edges` (snapshot) |
| `coordinator.graph` | When the unified coordinator graph shape changes (a subtask child run is dispatched, or the plan reaches its terminal snapshot) | a state-bearing `GraphDescriptor` (variant `coordinator`) |
| `subtask.dispatched` / `subtask.running` / `subtask.assemble_ready` / `subtask.rai_flagged` / `subtask.completed` / `subtask.failed` | As a subtask's child run advances through its lifecycle | `subtaskId`, `childRunId`, `assignedAgent`, `selectedModelId`, `status` |
| `run.assemble_ready` | On a coordinator CHILD run's own stream when the child finishes its trimmed agent pipeline and is ready to be collected/assembled | `runId`, `subtaskId`, `parentRunId`, `worktreeBranch`, `treeHash`, `hasChanges`, `stepCount`, `raiSafetyFlagged` |
| `run.no_changes_produced` | On a coordinator CHILD run when it reaches assemble-ready with no committed changes (the worker wrote no files) | `runId`, `subtaskId`, `parentRunId`, `message` |
| `coordinator.steering` | When a steering directive is created or changes state | `directiveId`, `kind`, `targetChildRunId`, `status`, `instruction` |
| `coordinator.steering_received` | When a steering signal from any source is persisted and queued for the coordinator | `directiveId`, `source`, `severity`, `verb`, `targetScope`, `feedback`, `treeHash` |
| `coordinator.steering_decision` | When the coordinator records its steering decision before executing the effect | `directiveId`, `decision`, `rationale`, `subtaskIds`, `attempt` |
| `coordinator.children_complete` | When every child subtask has reached a terminal status and the work plan moves to `awaiting_assembly` | `workPlanId` |
| `coordinator.assembly_started` | When the collective-assembly pipeline claims the plan (`awaiting_assembly → assembling`, exactly-once) | `workPlanId`, `integrationBranch`, `subtaskCount` |
| `coordinator.integration_conflict_auto_resolved` | When the integration-branch build auto-resolves a child conflict by accepting the child's version and continues | `workPlanId`, `conflictingBranch`, `conflictingFiles`, `strategy: "accept_child"` |
| `coordinator.assembly_blocked` | When assembly stops with NO partial work — an ineligible subtask set, or when retryable Build & Test infrastructure is unavailable. Non-terminal for stream subscribers; the plan can be re-armed/recovered and emit more events. | `workPlanId`, `reason`; `ineligibleSubtaskIds`, `ineligibleSubtasks` for subtask eligibility blocks; `detail`, `exceptionMessage`, `innerExceptionMessage`, `innerExceptionType`, `infrastructureReason`, `retryable` for retryable Build & Test infrastructure blocks |
| `coordinator.assembly_rai_started` / `coordinator.assembly_rai_completed` | The ONE collective RAI pass over the aggregate diff | `workPlanId`, `integrationBranch`, `gateId` / `raiSafetyFlagged` |
| `coordinator.assembly_review_requested` | When an assembly gate starts. Emitted for automated Build & Test (`gateKind: "build-test"`), automated Rubberduck critique (`"rubberduck"`), and actionable human review (`"human-review"`; legacy events may omit `gateKind`) | `workPlanId`, `integrationBranch`, `treeHash`, `gateId`, `gateKind`, `hasChanges`; `includedSubtaskIds` on human review |
| `coordinator.assembly_review_approved` | When a gate approves or passes the combined output | `workPlanId`, `reviewer` |
| `coordinator.assembly_changes_requested` | When a gate requests changes; the coordinator re-dispatches the reviewer-implicated subtasks and their transitive dependents | `workPlanId`, `redispatchSubtaskIds`, `redispatchedSubtaskIds`, `implicatedSubtaskIds`, `dependentSubtaskIds`, `feedback` |
| `coordinator.assembly_implicated_scope_fallback` | When the #223 implicated-subtask scoping reverts to the broad all-contributors set because the reviewer's structured `TARGET_FILES:` hint was missing or reverse-mapped to nothing (fail-safe, made observable) | `workPlanId`, `source`, `reviewer`, `reason` (`no_target_files_field` \| `target_files_matched_nothing`), `namedFiles`, `touchedFiles`, `contributorIds` |
| `coordinator.assembly_merge_started` / `coordinator.assembly_merge_completed` / `coordinator.assembly_merge_failed` | The ONE collective merge of the integration branch into the originating branch | `workPlanId`, `integrationBranch` / `commitHash` / `reason`, `conflictingFiles` |
| `coordinator.assembly_scribe_started` / `coordinator.assembly_scribe_completed` | The ONE collective scribe pass after a successful merge (best-effort) | `workPlanId` |
| `coordinator.assembly_completed` | When collective assembly finishes and the work plan reaches `complete` | `workPlanId`, `integrationBranch`, `commitHash` |
| `coordinator.assembly_declined` | When the reviewer declines the combined output (terminal); the coordinator run ends `declined` | `workPlanId`, `reason`, `reviewer` |
| `coordinator.assembly_failed` | When the assembly background task hits an UNEXPECTED fault, or when Build & Test infrastructure fails with a non-retryable configuration error; the work plan moves to `assembly_failed` and the coordinator run ends with a human-readable reason. This is a stream terminal. | `workPlanId`, `reason`, `phase` for unexpected faults; `detail`, `exceptionMessage`, `innerExceptionMessage`, `innerExceptionType`, `infrastructureReason` for Build & Test infrastructure failures |
| `coordinator.child_question` | When a coordinator child run bubbles a question via `ask_question`; re-projected onto the coordinator stream so the operator can answer the child run | `childRunId`, `subtaskId`, `requestId`, `question` |
| `coordinator.child_approval_required` | When a coordinator child run pauses on a tool-approval gate; re-projected onto the coordinator stream so the operator can grant/deny on the child run | `childRunId`, `subtaskId`, `requestId`, `toolName`, `url` (optional), `message` (optional) |
| `coordinator.autopilot_answered` | When the coordinator's Autopilot option is ON and the coordinator model auto-answers a clarifying question (its own or one bubbled from a child); the answer is also resolved on the child's question gate, so the normal `agent.question_answered` still surfaces | `runId`, `childRunId` (optional), `requestId`, `question`, `answer` |

## Tool event pairing

Each `tool.call` carries a `callId` in its payload. The matching `tool.result` or `tool.error` repeats that same `callId`, so clients can pair tool outcomes with calls without relying on adjacent events. A policy denial is reported as a `tool.error`, not a separate event type.

## Provider parity

The GitHub Copilot SDK runner streams text as `agent.message.delta` events. Each
delta carries a `delta` chunk and the `messageId` it belongs to, and
`agent.turn.end` closes the final turn bubble.

For each tool the agent runs, the stream carries a `tool.call`, followed by a
`tool.result` for an approved tool (with its real content) or a `tool.error` for a
denial or failure. The Copilot SDK supplies these through lifecycle events that flow
inline through the streaming response.

SDK-internal tools (`report_outcome`, `glob`) are suppressed from the event stream. `report_intent` is translated into an `agent.intent` event rather than suppressed — the raw tool call is hidden, but the intent text surfaces as a first-class event. `agent.tools` is a synthetic event emitted by the runtime, not an SDK tool.

## Event details

### `agent.runtime_context`

Both GitHub Copilot execution paths emit the same bounded composition record once per agent turn.
It contains only scalar character counts, the stable run/project correlation, provider name, and the
fixed delivery-mode token; it never contains prompt or task text, skill names or content, tool names,
tool arguments or declarations, credentials, or exception text. This is separate from
`memory.context_composition`, which reports #1241 structured-memory selection and omission facts.

The counts measure the exact assembled fragments. `separatorCharacters` includes the base-to-context
and run-context-to-skill separators. Provider tool declarations are measured from the declaration list
passed to the SDK. The invariant is:

`totalCharacters = baseCharacters + runContextCharacters + skillCharacters + separatorCharacters + taskCharacters + toolDeclarationCharacters`

`estimatedTokens = ceil(totalCharacters / 4)` is a stable planning estimate, not provider-reported token usage.


### `rai.verdict`

The RAI executor emits one structured verdict event when a Responsible AI review completes. The payload is:

| Field | Type | Notes |
| --- | --- | --- |
| `verdict` | `"green"` \| `"yellow"` \| `"red"` \| `"revise"` | Machine-readable token. There is no emoji field in the event payload. |
| `runId` | string | The reviewed run. |
| `rationale` | string | Human-readable rationale extracted from the RAI response, or a fallback failure rationale. |

The event is written to both the parent run stream and the `{runId}-rai` sub-stream (`packages/Agentweaver.Domain/EventTypes.cs:104`, `packages/Agentweaver.AgentRuntime/Workflow/RaiTurnExecutor.cs:373`). The web session panel reads the token and maps it locally to a traffic-light presentation with the rationale (`apps/web/src/components/AgentSessionPanel.tsx:1200`, `:1216`).

### `tool.call`

This event fires before the runtime evaluates the request against the sandbox policy. `toolName` is the tool the model invoked, and `arguments` is the argument object it passed (for file tools, this includes the requested `path`).

### `tool.result`

This event records a successful tool execution. `content` carries the result the tool returned, such as the text of an approved file read.

### `tool.error`

This event records every tool outcome that is not a success. It covers sandbox policy denials — an absolute path, `..` traversal, or a symlink escape — as well as non-policy failures such as a missing file or an I/O error. `errorMessage` explains what went wrong. It never carries the contents of a file outside the sandbox, because a denied tool never runs.

### `tool.execution_pending`

This event is an output-free heartbeat while a sandboxed `run_command` invocation is still active.
It is correlated with the matching `tool.call` by `toolCallId`, and it stops when the command
returns, fails, times out, is cancelled, or the sandbox tears down. The payload is limited to timing
and correlation fields:

| Field | Type | Notes |
| --- | --- | --- |
| `runId` | string | The run that owns the command. |
| `toolCallId` | string | Matches the `tool.call`/`tool.result`/`tool.error` correlation id. |
| `toolName` | string | Always `run_command`. |
| `startedAtUtc` | string | UTC time the active command slot opened. |
| `deadlineUtc` | string \| null | Watchdog deadline when one is armed. |
| `elapsedSeconds` | number | Wall-clock seconds observed when the heartbeat was emitted. |

The heartbeat never carries command text, stdout, stderr, exit code, working directory, or
environment data. Browser clients consume it from the existing run stream; they must not add
separate polling.

`run_command` is bounded by its effective execution budget (30 minutes by default, configurable via
`AGENTWEAVER_RUN_COMMAND_DEFAULT_TIMEOUT_SECONDS` or the tool-call `timeout_ms`). If that deadline
expires, the tool stops emitting heartbeats, cancels the sandbox process, returns `timed_out: true`
guidance to the model, and emits `run.degraded` with the same actionable guidance.

### `run.completed`

This event is emitted exclusively by the watch loop (`RunWatchLoopService`) when the
workflow reaches a terminal state with no file changes. The `result` field is
`"no_changes"`. The GitHub Copilot SDK runner emits `agent.turn.end` to close its
final turn and lets the watch loop determine terminal state. When the agent produces
changes, `run.completed` is not emitted; the run transitions to `review.requested`
instead.

### `run.failed`

This event marks a terminal failure. Public REST and SSE consumers receive only the bounded normalized `{ message, errorCode, retryable }` payload. The legacy `reason` and `detail` fields are deprecated and never serialized publicly. Authorized full consumers can request the separately redacted [`GET /api/runs/{id}/terminal-diagnostic`](../guide/runs.md#failed-run-diagnostics) projection. Content-safety, executor, infrastructure, and watch-loop causes are represented by the allowlisted `errorCode`, not untrusted failure text.

### `run.bounded`

This event marks a run that exceeded enforced limits. `limit_type` is `step-count` or `wall-clock`.

### `run.cancelled`

This event marks a run that was cancelled because its project was deleted. The run transitions to a terminal state immediately and no further events are emitted. It carries no payload fields. The originating branch and any worktree state are cleaned up as part of the project deletion.

### `review.requested`

This event anchors the review gate. `tree_hash` identifies the committed worktree state that the human reviews and that the merge step later verifies. `request_id` is an informational correlation id for the underlying workflow review request; it is not required for the review decision.

### `review.approved`

This event records that an approve decision was accepted. It carries no payload fields. It is followed immediately by either `merge.completed` or `merge.failed`. A blocked (retriable) approve does not emit this event — the run stays at the review gate and `review.requested` remains the last event on the stream.

### `review.declined`

This event records a decline decision. It carries no payload fields. The originating branch remains unchanged.

### `merge.started`

This event fires immediately before the merge operation begins, bridging the gap between the approve action and the terminal `merge.completed` or `merge.failed` event. `tree_hash` identifies the committed worktree state being merged — the same value recorded by `review.requested`.

### `merge.completed`

This event records a successful merge. `merged_commit_hash` is always present. In the primary workflow path, the field contains the full result string in the format `merged:{commitHash}` (e.g., `"merged:34c09ee..."`). In the direct fallback path (post-restart recovery with no checkpoint), the field contains just the commit hash, and `previous_head_sha` is also present — the SHA the originating branch pointed to before the merge, useful for auditing and rollback.

### `merge.failed`

This event records why an approved run could not merge. Terminal reasons are branch divergence with unresolvable conflicts, and a tree-hash mismatch (the worktree branch changed after the run was reviewed). A checked-out originating branch is not a terminal reason — if the branch is checked out but the working tree is dirty, the approve is blocked retriably and no event is emitted until the condition is resolved.

### `agent.intent`

Emitted when the agent calls the `report_intent` internal tool before a major step. Not shown as a tool call card in the frontend — rendered as a lightweight lifecycle card with the intent text. The `intent` field is always a non-empty string.

### `agent.system_prompt`

Emitted once at run start when the system prompt is set. `provider` identifies which model provider is active. `prompt` carries the full system prompt text. In the frontend, this renders as a collapsible card showing a 120-character preview with character count; clicking expands the full text.

### `agent.tools`

Emitted once at run start listing the tool names registered for this run. Varies by sandbox policy (`run_command` is absent when shell execution is disabled). Rendered in the frontend as a row of `<Badge>` components.

### `revision.started`

Emitted when a request-changes revision cycle begins. `revision` is a 1-based counter. `message` is the reviewer's comment passed to the agent.

### `run.outcome`

The agent's self-assessment of task completion. `achieved: true` when the agent reports the task was completed; `false` when a critical step failed or was blocked (for example, a required tool call was denied by the sandbox). Emitted by the `report_outcome` internal tool and kept out of normal tool activity. If the agent never calls `report_outcome`, consumers must rely on the terminal run event.

### `review.changes_requested`

Emitted when the human reviewer calls `POST /api/runs/{id}/request-changes`. `comment` is the reviewer's feedback passed to the agent for the revision cycle.

### Build & Test infrastructure reasons

Automated assembly Build & Test reports sandbox/A2A infrastructure separately from authored code feedback. Retryable failures emit `coordinator.assembly_blocked` and set `reason` to `build_test_infra_{reason}`; non-retryable configuration errors emit `coordinator.assembly_failed`. Current typed reasons are:

| Reason suffix | Meaning |
| --- | --- |
| `agenthost_capacity_pending` | **Legacy / no longer produced.** Kubernetes now owns admission, so the assembly Build & Test launch waits for the pod to schedule instead of pre-checking capacity (issue #217); still matched for back-compat recovery of pre-upgrade records. |
| `agenthost_launch_failed` | AgentHost pod launch failed for a retryable, non-specific launch error. |
| `agenthost_ip_not_ready` | The claim is bound but the pod has no IP yet. |
| `a2a_endpoint_unavailable` | No A2A endpoint could be resolved for the run-bound AgentHost pod. |
| `a2a_transport_failure` | The A2A turn transport failed after endpoint setup. |

The full event reason is prefixed, for example `build_test_infra_a2a_endpoint_unavailable`. These values come from `WorkflowAgentInfrastructureException` and the assembly parking path (`packages/Agentweaver.AgentRuntime/Workflow/WorkflowAgentInfrastructureException.cs:7`, `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1567`).

For infrastructure reasons, the payload also carries operator diagnostics: `detail` (outer message plus the
innermost exception when different), `exceptionMessage`, `innerExceptionMessage`, `innerExceptionType`, and
`infrastructureReason`. Retryable `assembly_blocked` includes `retryable: true`; terminal
`assembly_failed` omits that flag and closes the stream after replay has drained persisted rows
(`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1590`, `:1604`, `:1648`).

### `tool.approval_required`

Emitted when a tool call is paused waiting for human approval. `request_id` identifies the request and is used by `POST /api/runs/{id}/tool-approvals` and `POST /api/runs/{id}/tool-denials`. `tool_name` is the tool being called. `url` is the resource being accessed (for `web_fetch` and similar tools). `intention` is an optional human-readable description of what the agent intends to do. The run is paused until the human approves or denies; `tool.result` or `tool.error` follows once settled.

### `tool.approval_pending`

A lightweight **heartbeat** emitted repeatedly on the child run's stream while a tool call is blocked on a human-approval gate (issue #212). It fires every ~20 seconds (`ApprovalHeartbeatInterval`) from when the gate arms until it resolves, carrying the same `requestId` as the `tool.approval_required` card plus its short `displayId` and the `toolName`. Its purpose is operational, not a state change: it keeps the pod's outbound A2A/SSE stream flushing so the buffered `tool.approval_required` frame is delivered and durably persisted promptly, and it resets the parent coordinator's subtask-stall timer so a human-paced wait is not misclassified as `agent_stall_timeout`. The frame is non-terminal and idempotent — clients that already render the approval card may ignore it — and a prompt approval emits none. Emitted by both the pod runtime (`CopilotAIAgent`) and the in-API runner (`GitHubCopilotAgentRunner`). See the [Tool Approval SSE Contract](../tool-approval-sse-contract.md#tool-approval-pending-heartbeat-issue-212) for the full frame and the coordinator stall-guard behavior.

### `sandbox.provisioning_pending`

A **heartbeat** emitted repeatedly on a coordinator **child** run's own stream while its AgentHost `SandboxClaim` is still being provisioned — i.e. the claim is not yet bound because Kubernetes is still scheduling the pod (a node may need to free up or the pool may need to autoscale). It fires about every 20 seconds (`SandboxProvisioningHeartbeatInterval`) from `KubernetesSandboxExecutor` while the claim is unbound, carrying the `claimName` and a `timestamp_utc`. Like `tool.approval_pending` (issue #212), its purpose is operational: it keeps the child stream moving so the parent coordinator's subtask-stall timer resets during the (Kubernetes-paced) provisioning wait instead of false-firing `agent_stall_timeout` (issue #217). A **Pending** pod is therefore a legitimate wait, not a failure — the platform no longer pre-flights namespace capacity before launching. The frame is non-terminal and idempotent; consumers that do not care may ignore it, and the coordinator's exemption self-heals as soon as any other real event (the pod binding, agent output, or a terminal event) arrives. The emit is best-effort: a stream-append failure is logged and swallowed so it can never fail a launch Kubernetes would otherwise admit.

The coordinator projects the first heartbeat in each uninterrupted wait as
`coordinator.child_provisioning_pending`, adding `childRunId` and `subtaskId`. Repeated heartbeats
remain on the child stream for stall protection but are not copied repeatedly to the coordinator
stream. Any other child event clears that projection state, so a later scheduling wait is visible
again.

### `workflow.step`

Emitted by each workflow executor stage when it starts, completes, fails, or is skipped — for every executor node in BOTH the full and the trimmed coordinator-child pipelines, so the graph updates live (no node stays "Pending" while it is actually running). The `step` field identifies the stage:

| Step | Executor | Emitter |
|------|----------|---------|
| `agent` | AI agent turn (task execution) | executor self-emit |
| `rai` | RAI safety review | executor self-emit |
| `review` | Human review gate | HITL (`review.requested` / decision endpoints) |
| `merge` | Branch merge | executor self-emit |
| `scribe` | Session logger | executor self-emit |
| `assemble-ready` | Coordinator child assemble-ready terminal | watch loop (MAF executor-lifecycle translation) |
| `preview` | Build & Test live-preview stage | coordinator / preview endpoint |

Lifecycle (`started` / `completed` / `failed`) is translated from the MAF `ExecutorInvoked` / `ExecutorCompleted` / `ExecutorFailed` events by the run watch loop for nodes that do not already self-emit (currently the child `assemble-ready` terminal); the agent/rai/merge/scribe executors self-emit their own lifecycle plus the richer branch statuses.

Possible `status` values: `started`, `completed`, `failed`, `skipped`, `revise` (RAI step only).

The `step` value always equals the descriptor node id the frontend keys on (`run.workflow_graph` `nodes[].id`). The `label` field is a short human-readable description (e.g. `"Agent turn"`, `"RAI review"`, `"Assemble-ready"`). `timestamp_utc` is an ISO 8601 (`"O"`) timestamp; the `timestamp_utc` on the `started` event drives the per-node live elapsed timer. The `agent` step includes `agent_name` (the team member running the turn). The `review` step includes `reviewer` (GitHub username) when a human review decision is recorded. An optional `message` field carries a short human-readable status note when available (e.g. on the assemble-ready node); consumers must tolerate its absence.

The web UI uses `workflow.step` events to drive the workflow diagram — each card in the Agent → Rai → Review → Merge → Scribe pipeline (or Agent → Assemble-ready for a child) updates live as these events arrive. The preview stage uses the same event with `step: "preview"` and statuses such as `started`, `pending`, `completed`, `failed`, or `skipped`; see [Live-preview provisioning](./live-preview-provisioning.md).

### `run.workflow_graph`

Emitted once at run start, carrying a full snapshot of the run's workflow topology as a `GraphDescriptor`. It is built from the same code that wires the MAF workflow (no runtime reflection), so the rendered graph never drifts from the executors that actually run. The descriptor is persisted with the run's other events, so it is also available for terminal runs via replay and through `GET /api/runs/{id}/graph`. Child runs (`parent_run_id != null`) carry the `child` variant; all others carry the `full` variant.

`GraphDescriptor` shape (snake_case JSON):

```json
{
  "graph_id": "agentweaver-workflow-full",
  "variant": "full",
  "start_node_id": "agent",
  "nodes": [
    { "id": "agent", "label": "Agent", "role": "agent", "kind": "live", "node_type": "agent", "child_graph_ref": null }
  ],
  "edges": [
    { "from": "agent", "to": "rai", "cardinality": "direct", "loopback": false }
  ]
}
```

- `variant`: `"full"` | `"child"` | `"coordinator"`.
- `nodes[].id`: the logical node id (equals the `step` key in `workflow.step` events for live business nodes). `kind`: `"live"` | `"planned"`. `child_graph_ref`: optional reference to a nested graph.
- `nodes[].node_type`: self-declared category that drives the frontend's rendered shape — one of `"agent"` (an AI agent turn), `"action"` (a deterministic system op), `"gate"` (a human-in-the-loop decision/approval), `"terminal"` (a workflow endpoint/checkpoint), or `"subtask"` (a coordinator fan-out child reference). Required on every node. In the `full` variant: `agent`/`rai`/`scribe` are `agent`, `review` is `gate`, `merge` is `action`; in the `child` variant `assemble-ready` is `terminal`.
- `edges[].cardinality`: `"direct"` | `"fanout"` | `"fanin"`. `loopback`: `true` for revision-cycle back-edges (the target is an ancestor of the source).

Plumbing executors (input storers, adapters, terminals) are collapsed or hidden: hidden nodes are dropped and their edges are transitively re-stitched, and the scribe-path executors collapse into the single `scribe` node. The `full` variant nodes are `agent`, `rai`, `review`, `merge`, `scribe`; the `child` variant nodes are `agent`, `assemble-ready`.

### `coordinator.started`

Emitted once when a coordinator run begins. A coordinator run is a Run with `ParentRunId == null` driven by the built-in Coordinator agent. `goal` carries the user's submitted goal text. The run reads the project's Feature 006 memories and decision-inbox entries as grounding context, then drafts an OutcomeSpec.

### `coordinator.recovered`

Emitted when an interrupted coordinator run is resumed after an API process restart. A coordinator run stays `InProgress` across the (non-MAF) dispatch + collective-assembly window, so a restart would otherwise strand it. On startup — after the generic restart sweep has failed any stranded child runs — `CoordinatorRunService.RecoverInterruptedRunsAsync` reconstructs the orchestration entirely from the persisted work plan and re-arms the correct engine: the dispatch loop re-launches in-flight subtasks (reset to `pending`), or the collective-assembly pipeline rebuilds the integration branch and re-arms the human-review gate. (The pre-confirm spec phase is a checkpointed MAF workflow and is resumed from its checkpoint instead, with no `coordinator.recovered` event.) `status` is the work-plan status the run resumed from (`planned`, `dispatching`, `awaiting_assembly`, `assembling`, or `in_review`). The re-armed engine then re-emits its topology / assembly snapshots so the live view renders without client-side computation.

### `coordinator.outcome_spec`

Emitted when the coordinator has drafted an OutcomeSpec and suspended at the await-confirmation gate (`coordinator-confirmation-gate`). The OutcomeSpec is persisted to the memory store with status `awaiting_confirmation` before this event fires. `specId` is the persisted row id; `status` is `awaiting_confirmation`. `desiredOutcome`, `scope`, and `assumptions` are the drafted strings; `clarifyingQuestions` is an optional array. Interactive defineOutcome waits here for confirm/revise; launch Autopilot can confirm unattended. Direct skips this drafted-spec gate.

Outcome-definition and review waits are nonterminal. A connection may close at a gate; use run/spec state and reconnect with the last per-run sequence. Do not require `done` immediately after `coordinator.outcome_spec`: local SSE closure checks review-requested state, whereas durable subscribers use terminal events.

### `coordinator.outcome_spec.confirmed`

The spec becomes confirmed and `confirmedBy` identifies the accountable confirmer, including unattended normal-seam confirmation. Confirmation advances orchestration rather than inherently emitting `run.completed`; Direct skips the drafted-spec gate.

### `coordinator.work_plan`

Emitted when the coordinator decomposes a confirmed OutcomeSpec into a work plan and persists it. `workPlanId` is the persisted plan id; `status` begins at `planned`. `subtasks` is the array of decomposed units of work, each carrying its `subtaskId`, `title`, `scope`, `assignedAgent`, `selectedModelId`, `phase`, `isolation`, and `status`. `dependencies` is the array of `{ subtaskId, dependsOnSubtaskId }` edges that constrain dispatch order. The work plan is the durable artifact behind the live `coordinator.topology` graph; subagents read from it once dispatched.

### `coordinator.topology`

Emitted to describe the orchestration graph as it executes. The payload is versioned (`version: 1`) and comes in two `kind`s, ordered by a per-coordinator `seq` counter:

- A `snapshot` (`kind: "snapshot"`, `seq: 0`) fires once when dispatch begins. It carries the complete `nodes` array and the `edges` array. There is one `coordinator` node (`id: "coordinator"`) plus one node per subtask (`id: "subtask-{id}"`). Each node carries `id`, `kind` (`coordinator` or `subtask`), `subtaskId`, `status`, `label`, `agent`, `model`, `childRunId`, `phase`, and `isolation`. Each edge is `{ from, to }`, meaning the `from` node (a dependency) must reach `assemble_ready`/`completed` before the `to` node (its dependent) is dispatched.
- A `delta` (`kind: "delta"`, `seq > 0`) fires on every subtask lifecycle transition. It carries a `changed` array of the node(s) whose state moved (replace by `id`); `edges` never change after the snapshot, so deltas omit them. A delta may carry the `coordinator` node when the work plan's own status transitions.

Clients render directly from these events and never compute topology themselves. Edge direction is always dependency to dependent.

### `coordinator.graph`

The unified coordinator view in the shared `GraphDescriptor` contract (the same shape returned by `GET /api/runs/{id}/graph` and emitted per-run as `run.workflow_graph`), so the frontend's generic renderer draws the coordinator, its fan-out subtask children, and the PLANNED Phase 3 collective-assembly stage with one code path. Emitted on the coordinator stream as a FULL, state-bearing snapshot whenever the topology shape changes (a subtask child run is dispatched, or the plan reaches its terminal snapshot). It is built from the work plan (no reflection).

The descriptor includes optional persisted status/reason/terminal-stage fields. It is a `GraphDescriptor` with `variant: coordinator`, a Coordinator start node and `coordinator:{coordinatorRunId}` graph ID. Topology snapshot/delta events remain a separate projection.

- Node `coordinator` (`node_type: "agent"`, `role: "coordinator"`, `kind: "live"`).
- One `plan:subtask-{id}` node per subtask (`node_type: "subtask"`, `kind: "live"`) carrying optional `agent`, `model`, `phase`, `isolation`, `child_run_id` fields (omitted when null) and a `child_graph_ref` of `run:{childRunId}` once dispatched (null until then) so the child's own graph can be expanded via `GET /api/runs/{childRunId}/graph`.
- Selected-workflow assembly gates precede merge and Scribe. Their stable `planned:assembly-*` IDs become live as stages execute, with persisted status/reason/terminal-stage fields.
- Coordinator connects to roots; prerequisite subtasks connect to dependents; leaves connect to the first selected gate (or merge). Every selected gate has a Coordinator loopback excluded from forward-degree/cardinality calculations. `coordinator.topology` remains available alongside the unified descriptor.

### `subtask.*`

The `subtask.dispatched`, `subtask.running`, `subtask.assemble_ready`, `subtask.rai_flagged`, `subtask.completed`, and `subtask.failed` events track a single subtask's child run through its lifecycle on the coordinator stream. Each carries `subtaskId`, `childRunId`, `assignedAgent`, `selectedModelId`, and `status`. The subtask status advances `pending -> dispatched -> running -> {assemble_ready | rai_flagged | completed | failed}`. Each `subtask.*` event is paired with a `coordinator.topology` delta for the changed node, so observers can choose either the granular per-subtask signal or the graph view.

A `subtask.pending_capacity` event (subtask status `pending_capacity`) is **legacy / historical**: it was emitted when the dispatcher parked a subtask because the namespace had no AgentHost pod capacity. Kubernetes now owns pod admission and scheduling, so new runs never emit it (issue #217) — a pod that is still being scheduled is surfaced instead by `sandbox.provisioning_pending` heartbeats on the child run's own stream, and a pre-upgrade subtask stranded in `pending_capacity` is recovered to `pending`. It is documented only so old records still render.

### `coordinator.subtask_redispatched`

Emitted on the coordinator stream when a child subtask **stalls** (its child run made no progress past the stall TTL) but the subtask still has recovery budget, so the coordinator redispatches it instead of dead-ending the run. Before this change a single stalled subtask blocked the whole run at the eligibility gate (`coordinator.assembly_blocked: ineligible_subtasks` → `coordinator.assembly_failed`); now the subtask is reset to `pending` for a fresh child on a fresh pod, and it only becomes a genuine terminal `failed` once the budget is exhausted.

The redispatch is bounded by `CoordinatorSteeringService.MaxRecoveryAttempts` (**3**). `RecoveryAttempts` is incremented **monotonically** and never reset, so at most three stall-redispatches occur per subtask before the stall dead-ends. The **prior** stalled child run is still terminalized (`agent_stall_timeout`) and its AgentHost pod released — only the *subtask* is revived; the old child stays failed.

Payload:

- `subtaskId` — the subtask being redispatched.
- `priorChildRunId` — the stalled child run, recorded on the subtask's `PriorChildRunId` so the fresh child builds on the prior branch through the existing handoff bundle.
- `attempt` — the new `RecoveryAttempts` value (`N` of `maxAttempts`).
- `maxAttempts` — `MaxRecoveryAttempts` (3).
- `reason` — always `"stall_redispatch"`.
- `timestamp_utc` — ISO-8601 emit time.

Grounded in `CoordinatorDispatchService.TryRedispatchStalledSubtaskAsync` (`apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1235`), wired into the `ChildOutcome.Stalled` branch of the dispatch loop (`:399`). See the coordinator deep-dive [stall redispatch before dead-end](/deep-dive/coordinator-internals#stall-redispatch-before-dead-end).

### `coordinator.steering`

Emitted when a steering directive is created through `POST /api/runs/{id}/steer` and as it changes state. `directiveId` identifies the directive. `kind` is `stop`, `redirect`, `amend`, or a recovery verb such as `recover`. `targetChildRunId` selects a child; null broadcasts to active children. `instruction` is required for `redirect` and `amend`, but optional for `stop` and recovery directives. `status` advances `pending -> queued -> relayed -> applied`. A `stop` applies immediately. Other directives apply at a child turn boundary. Pause is not supported.

Unified autonomous steering adds two source-agnostic events around this legacy directive lifecycle:

- `coordinator.steering_received` is emitted when feedback from any source is persisted and queued for the coordinator. `source` is `human-review`, `rai`, `rubberduck`, `build-test`, `agent`, `coordinator`, or `step`; `severity` is `advisory`, `request-changes`, or `blocking`.
- `coordinator.steering_decision` is emitted before the effect executes. `decision` is `in_place_steer`, `dispatch_fresh`, `proceed`, or `advisory`; `rationale` explains why. Fresh dispatch is therefore visible before any reset/re-dispatch. See [Unified autonomous steering](./unified-steering.md).

### `coordinator.children_complete` and `coordinator.assembly_*`

Phase 3 collective assembly runs ONE pipeline over the COMBINED output of all child runs, then flows back to the coordinator. Child output is git state, not in-memory text: each child commits to its own worktree branch. When every child subtask reaches a terminal status, the coordinator emits `coordinator.children_complete` and moves the work plan to `awaiting_assembly`.

A single background pipeline then drives the collective stages, each emitting a paired `coordinator.graph` so its planned assembly node flips to `kind: "live"`. Authored assembly gates are ordered by happy-path traversal of the workflow graph, not YAML node declaration order:

1. **Exactly-once claim** — a DB compare-and-swap transitions `awaiting_assembly → assembling`; only the winner proceeds. `coordinator.assembly_started` carries the `integrationBranch` name (`agentweaver/integration/{coordinatorRunId}`) and `subtaskCount`.
2. **Eligibility gate (no partial assembly)** — every subtask must be assembly-eligible (`assemble_ready`, or `completed` with no changes). If any is failed / rai_flagged / pending / blocked, the pipeline emits `coordinator.assembly_blocked` and STOPS — no RAI, no merge. The payload includes `ineligibleSubtaskIds` plus enriched `ineligibleSubtasks`.
3. **Integration branch** — the eligible child branches are merged in dependency (topological) order off the coordinator's originating branch, producing one aggregate diff + tree hash. If a child conflicts while being added, the coordinator accepts that child's version for each conflicting path, emits `coordinator.integration_conflict_auto_resolved`, and continues.
4. **Collective RAI** (`coordinator.assembly_rai_started` → `coordinator.assembly_rai_completed`) — one RAI pass over the aggregate diff. A safety flag hard-stops the plan before later gates.
5. **Automated assembly review gates** (`coordinator.assembly_review_requested` with `gateKind: "rubberduck"` or `"build-test"`) — Rubberduck critique may request changes; Build & Test creates a detached integration-branch worktree and runs the build/test verdict. The deterministic preview step then runs after Build & Test for approved or request-changes verdicts, producing `sandbox.preview_ready`, `sandbox.preview_failed`, or `sandbox.preview_skipped_not_applicable` without changing the verdict. Automated gate request-changes feedback routes through unified steering: `coordinator.steering_received` records the source, and `coordinator.steering_decision` records whether the coordinator chose in-place steering, fresh dispatch, proceed/terminal, or advisory no-op. Fresh dispatch is therefore visible before any reset.
6. **One human review gate** (`coordinator.assembly_review_requested` with `gateKind: "human-review"`, carrying `treeHash` and `includedSubtaskIds` so the UI can render the assembled tree and which subtasks it covers) — the pipeline suspends until a decision arrives via `POST /api/runs/{coordinatorRunId}/assembly/review`. The POST can land on any API replica; if it lands away from the owner pipeline while the work plan is durably `in_review`, the decision is deferred in shared state and the owner consumes it at most once. Approve → `coordinator.assembly_review_approved`. Request changes → `coordinator.assembly_changes_requested` (the coordinator scopes the re-dispatch to the reviewer's **implicated** subtasks — reverse-mapped from the reviewer's structured `TARGET_FILES:` hint via `AssemblyPlanning.ScopeImplicatedSubtasks`, never prose-scraped — plus their transitive dependents: `implicatedSubtaskIds`, `dependentSubtaskIds`, `redispatchSubtaskIds`; if no hint is present or it matches nothing it falls back to all contributors and emits `coordinator.assembly_implicated_scope_fallback`), resets those subtasks to `pending`, returns the plan to `dispatching`, and re-dispatches. A pure decline is the terminal `assembly_declined` status.
7. **One merge** (`coordinator.assembly_merge_started` → `coordinator.assembly_merge_completed` with `commitHash`, or `coordinator.assembly_merge_failed` with `reason`/`conflictingFiles`).
8. **One scribe** (`coordinator.assembly_scribe_started` → `coordinator.assembly_scribe_completed`) — best-effort; a scribe failure does not fail the already-merged assembly.
9. **Completion** — `coordinator.assembly_completed` with the `integrationBranch` and `commitHash`; the work plan reaches `complete`.

Work-plan status and run terminal status differ. `assembly_blocked` can park a recoverable assembly and does not inherently terminate SSE. Actual terminal outcomes emit terminal events; durable replay drains its loaded batch before closing. Budget exhaustion escalates to `in_review` at human review, not terminal steering-budget exhaustion.

Durable replay drains the full persisted batch before terminal handling. A `coordinator.assembly_failed`
row is terminal, but if a diagnostic row was persisted just after it in the same replay batch, subscribers
still receive that row before `/api/runs/{id}/stream` completes. `coordinator.assembly_blocked` is
intentionally not terminal: it represents a retryable park, so SSE subscribers stay attached until the plan
recovers or reaches a real terminal event (`apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs:153`,
`apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:111`).

## Model-assisted casting

Creating a casting proposal in `free_text` or `analysis` mode starts a MAF run on GitHub Copilot. That run emits events under the same event model as a regular agentweaver run — the same envelope, the same event types, and the same SSE endpoint.

The `run_id` returned in the `POST /api/projects/{id}/casting/proposals` response identifies that run. Stream its events from `GET /api/runs/{run_id}/stream` to observe the model's reasoning as it builds the proposal.

The casting wizard in the web UI streams these events during the review step using the same timeline rendering as the watch screen. MCP clients receive them as progress notifications during a `team_cast` tool call.

No `.squad/` files are written during this run. The run produces a proposal, not a commit. Files are only written after the user confirms the proposal through `POST /api/projects/{id}/casting/proposals/{pid}/confirm`.

## ask_question bubbling

Any agent (a worker, or the coordinator during decomposition) can call the `ask_question` tool to surface a clarifying question or permission request instead of silently guessing. On invoke the tool emits `agent.question_asked` (carrying a generated `requestId` and the `question`) on the run's own stream, then suspends inside the tool call on the per-run question gate.

The wait is resolved by `POST /api/runs/{id}/questions/{requestId}/answer` with body `{ "answer": "..." }`. When an answer arrives (or the wait times out after 30 minutes), the tool emits `agent.question_answered` (`requestId`, `answer`, `timedOut`) and returns the answer text to the model so it can continue. On timeout the model receives a proceed-with-best-judgement instruction rather than hanging. The question gate is cleared on run completion alongside the tool-approval gate; any still-pending wait resolves to a proceed instruction.

For a coordinator CHILD run, the coordinator's child watcher (`CoordinatorDispatchService.ObserveChildAsync`) re-projects the child's `agent.question_asked` onto the COORDINATOR stream as `coordinator.child_question`, and the child's `tool.approval_required` as `coordinator.child_approval_required`, each carrying `childRunId` + `subtaskId` + `requestId`. The answer/approval flows back to the CHILD run: answer via `POST /api/runs/{childRunId}/questions/{requestId}/answer`, approval via the existing `POST /api/runs/{childRunId}/tool-approvals` / `tool-denials`. Re-projection does not affect terminal-event mapping.

Scenario-mode proposals resolve without a model run. The `run_id` field in their proposal response is `null`.

Coordinator graph descriptors combine work-plan topology with persisted status. Nodes may include `status`, `status_reason`, and `terminal_stage`; subtask state also arrives through `coordinator.topology`. Selected-workflow assembly gates become `kind: "live"` when reached, even though their stable IDs start with `planned:assembly-`. Failure projection uses the terminal stage so failure-scribe does not mark never-run gates as executed. Delegated plans leave skipped nodes planned with delegated status.

Leaf subtasks connect to the first selected gate (or merge if none); the gates form a chain followed by merge and Scribe. Each selected gate has a coordinator loopback, excluded from forward degree calculations. Fixed gate lists are examples for a particular workflow, not a universal RAI-only pipeline.

The SSE envelope sequence is the per-run replay cursor. The `seq` inside `coordinator.topology` is a separate topology snapshot/delta counter, not a substitute for `Last-Event-ID`.

<details id="diagram-context-canonical-durable-event-stream">
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Postgres is the event relay</td></tr>
<tr><td>subtitle</td><td>Any API replica can serve a cursor over durable RunEvents—no sticky session required.</td></tr>
<tr><td>group-title0</td><td>Write path · replica A</td></tr>
<tr><td>group-title1</td><td>Read path · replica B</td></tr>
<tr><td>Run producer</td><td>Run producer</td></tr>
<tr><td>Run producer</td><td>Append a structured event</td></tr>
<tr><td>Run producer</td><td>runId + type + payload</td></tr>
<tr><td>EF event stream</td><td>EF event stream</td></tr>
<tr><td>EF event stream</td><td>Serialize writes per run</td></tr>
<tr><td>EF event stream</td><td>pg_advisory_xact_lock</td></tr>
<tr><td>RunEvents</td><td>RunEvents</td></tr>
<tr><td>RunEvents</td><td>Shared PostgreSQL table</td></tr>
<tr><td>RunEvents</td><td>(RunId, Sequence)</td></tr>
<tr><td>Web / MCP watcher</td><td>Web / MCP watcher</td></tr>
<tr><td>Web / MCP watcher</td><td>Consume ordered events</td></tr>
<tr><td>Web / MCP watcher</td><td>last delivered cursor</td></tr>
<tr><td>SSE endpoint</td><td>SSE endpoint</td></tr>
<tr><td>SSE endpoint</td><td>Emit id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>ordered response frames</td></tr>
<tr><td>EF subscriber</td><td>EF subscriber</td></tr>
<tr><td>EF subscriber</td><td>Read Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>idle poll: 250 ms</td></tr>
<tr><td>e1</td><td>append</td></tr>
<tr><td>e2</td><td>commit</td></tr>
<tr><td>e3</td><td>ordered batch</td></tr>
<tr><td>e4</td><td>yield</td></tr>
<tr><td>e5</td><td>SSE frames</td></tr>
<tr><td>assurance-title</td><td>POSTGRES LANE ONLY</td></tr>
<tr><td>assurance-line1</td><td>SQLite register-channel / replay / tail is a separate implementation—not this architecture.</td></tr>
<tr><td>assurance-line2</td><td>Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>Run producer</td><td>Input</td></tr>
<tr><td>Run producer</td><td>RunStreamEntry</td></tr>
<tr><td>Run producer</td><td>Identity</td></tr>
<tr><td>Run producer</td><td>runId + event type</td></tr>
<tr><td>Run producer</td><td>Body</td></tr>
<tr><td>Run producer</td><td>Structured payload</td></tr>
<tr><td>Run producer</td><td>Ack</td></tr>
<tr><td>Run producer</td><td>After durable commit</td></tr>
<tr><td>EF event stream</td><td>Lock</td></tr>
<tr><td>EF event stream</td><td>Per-run advisory lock</td></tr>
<tr><td>EF event stream</td><td>Next</td></tr>
<tr><td>EF event stream</td><td>MAX(Sequence) + 1</td></tr>
<tr><td>EF event stream</td><td>Write</td></tr>
<tr><td>EF event stream</td><td>Save transaction</td></tr>
<tr><td>EF event stream</td><td>Commit</td></tr>
<tr><td>EF event stream</td><td>Before acknowledgement</td></tr>
<tr><td>RunEvents</td><td>Table</td></tr>
<tr><td>RunEvents</td><td>Key</td></tr>
<tr><td>RunEvents</td><td>RunId + Sequence</td></tr>
<tr><td>RunEvents</td><td>Order</td></tr>
<tr><td>RunEvents</td><td>Ascending sequence</td></tr>
<tr><td>RunEvents</td><td>Reuse</td></tr>
<tr><td>RunEvents</td><td>Same type / payload</td></tr>
<tr><td>Web / MCP watcher</td><td>Client</td></tr>
<tr><td>Web / MCP watcher</td><td>Web or MCP</td></tr>
<tr><td>Web / MCP watcher</td><td>Resume</td></tr>
<tr><td>Web / MCP watcher</td><td>Last delivered cursor</td></tr>
<tr><td>Web / MCP watcher</td><td>Replica</td></tr>
<tr><td>Web / MCP watcher</td><td>No sticky requirement</td></tr>
<tr><td>Web / MCP watcher</td><td>History</td></tr>
<tr><td>Web / MCP watcher</td><td>Durable ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Frame</td></tr>
<tr><td>SSE endpoint</td><td>id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>Cursor</td></tr>
<tr><td>SSE endpoint</td><td>Last-Event-ID</td></tr>
<tr><td>SSE endpoint</td><td>Delivery</td></tr>
<tr><td>SSE endpoint</td><td>Yield ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Close</td></tr>
<tr><td>SSE endpoint</td><td>After batch is drained</td></tr>
<tr><td>EF subscriber</td><td>Query</td></tr>
<tr><td>EF subscriber</td><td>Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>Idle</td></tr>
<tr><td>EF subscriber</td><td>Poll after 250 ms</td></tr>
<tr><td>EF subscriber</td><td>State</td></tr>
<tr><td>EF subscriber</td><td>Shared durable table</td></tr>
<tr><td>EF subscriber</td><td>Blocked</td></tr>
<tr><td>EF subscriber</td><td>Retryable: keep open</td></tr>
<tr><td>producer</td><td>Coordinator or run execution; Acknowledgement follows commit</td></tr>
<tr><td>append</td><td>Allocate MAX(Sequence) + 1; Save and commit transaction</td></tr>
<tr><td>store</td><td>Cross-replica ordered history; Explicit duplicates must match payload</td></tr>
<tr><td>client</td><td>Reconnect from the cursor; No local channel dependency</td></tr>
<tr><td>sse</td><td>Cursor advances after delivery; Drain batch before terminal close</td></tr>
<tr><td>reader</td><td>Query the shared durable table; Retryable assembly_blocked stays open</td></tr>
<tr><td>notes</td><td>POSTGRES LANE ONLY; SQLite register-channel / replay / tail is a separate implementation—not this architecture.; Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>groups</td><td>Write path · replica A; Read path · replica B</td></tr>
</tbody></table>
</details>

<details id="diagram-context-resilient-assembly-review-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Rejected work keeps useful context</td></tr>
<tr><td>takeaway</td><td>A steering decision chooses the effect; rejection does not always rotate the author.</td></tr>
<tr><td>group-title-0</td><td>FEEDBACK AND SCOPE</td></tr>
<tr><td>group-title-1</td><td>BOUNDED DIRECTION</td></tr>
<tr><td>group-title-2</td><td>AUTHOR CONTINUITY AND HUMAN ESCALATION</td></tr>
<tr><td>Gate request-changes</td><td>Gate request-changes</td></tr>
<tr><td>Gate request-changes</td><td>Structured target-file hints</td></tr>
<tr><td>Gate request-changes</td><td>not prose-inferred blame</td></tr>
<tr><td>Implicated + dependent</td><td>Implicated + dependent</td></tr>
<tr><td>Implicated + dependent</td><td>Rebuild closure without blame</td></tr>
<tr><td>Implicated + dependent</td><td>structured TARGET_FILES</td></tr>
<tr><td>Signal + decision</td><td>Signal + decision</td></tr>
<tr><td>Signal + decision</td><td>Persist explicit direction</td></tr>
<tr><td>Signal + decision</td><td>accumulated context</td></tr>
<tr><td>In-place revision</td><td>In-place revision</td></tr>
<tr><td>In-place revision</td><td>Same author and session</td></tr>
<tr><td>In-place revision</td><td>no reset-to-pending</td></tr>
<tr><td>Fresh dispatch</td><td>Fresh dispatch</td></tr>
<tr><td>Fresh dispatch</td><td>Scoped author selection</td></tr>
<tr><td>Fresh dispatch</td><td>handoff with context</td></tr>
<tr><td>No alternate author</td><td>No alternate author</td></tr>
<tr><td>No alternate author</td><td>Context permits same author</td></tr>
<tr><td>No alternate author</td><td>bounded conscious fallback</td></tr>
<tr><td>Human escalation</td><td>Human escalation</td></tr>
<tr><td>Human escalation</td><td>No context or budget left</td></tr>
<tr><td>Human escalation</td><td>durable review request</td></tr>
<tr><td>Human decision</td><td>Human decision</td></tr>
<tr><td>Human decision</td><td>Approve, change or decline</td></tr>
<tr><td>Human decision</td><td>no wall-clock timeout</td></tr>
<tr><td>Fresh autonomous budget</td><td>Fresh autonomous budget</td></tr>
<tr><td>Fresh autonomous budget</td><td>Only human changes reset it</td></tr>
<tr><td>Fresh autonomous budget</td><td>no human-round-trip cap</td></tr>
<tr><td>e0</td><td>scope</td></tr>
<tr><td>e1</td><td>signal</td></tr>
<tr><td>e2</td><td>resume</td></tr>
<tr><td>e3</td><td>fresh</td></tr>
<tr><td>e4</td><td>no alt</td></tr>
<tr><td>e5</td><td>context</td></tr>
<tr><td>e6</td><td>no context</td></tr>
<tr><td>e7</td><td>Proceed</td></tr>
<tr><td>e8</td><td>await</td></tr>
<tr><td>e9</td><td>changes</td></tr>
<tr><td>e10</td><td>retry</td></tr>
<tr><td>groups</td><td>FEEDBACK AND SCOPE; BOUNDED DIRECTION; AUTHOR CONTINUITY AND HUMAN ESCALATION</td></tr>
</tbody></table>
</details>
