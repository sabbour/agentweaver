# Execute static workflow branches durably

**Issue:** [#1418](https://github.com/sabbour/agentweaver/issues/1418)  
**Area:** Workflows & automation

## User story

As a workflow author, I want independent agent steps to run as durable branches and rejoin once, so that parallel work survives retries and service restarts without duplicating child work.

## Context / problem

The workflow schema recognizes `fan_out` and `fan_in`. Agentweaver executes a validated static
region through durable child-run correlation, ordered branch records, continuation fencing,
cancellation, and restart reconciliation.

## Scope

### In

- One durable child coordinator run and work plan per parent run and workflow node
- One ordered durable subtask per declared static branch
- Parent continuation persistence before dispatch
- Exactly-once resume preparation through existing pending-delivery fencing
- Restart reconciliation and parent-cancellation propagation
- Fail-closed validation for one non-nested, wait-all static fan region
- Runtime binding through the real MAF graph and request-port suspension
- Concurrent child-run dispatch with ordered wait-all join
- Existing API, events, diagnostics, MCP, and web topology projections
- Conservative direct and blueprint custom-workflow generation for prompt-only static fans
- Explicit generated-branch independence and declared-output metadata
- PM Discovery customer-signal and technical-feasibility branches before synthesis
- Provider-backed acceptance scenarios that can retain generated artifacts for post-merge proof

### Out

- Visual-editor authoring controls for fan metadata
- Dynamic branches, nested fans, quorum or partial joins
- Coordinator-composed work, integration merge, review, Scribe, or publication
- Generic code-writing parallelism before repository write-scope isolation is implemented

## Acceptance criteria

- [x] Retrying the same parent node reattaches to one child coordinator plan and does not duplicate branch subtasks.
- [x] The parent continuation is durable before child dispatch can start.
- [x] Terminal child work queues one ordered result and fenced delivery is retryable after a stale claim.
- [x] A delivered result is not delivered again.
- [x] Parent cancellation suppresses resume, prevents new dispatch, and requests cancellation of active child runs.
- [x] Persisted parent workflow and node identity do not change if the source workflow is edited or deleted.
- [x] The immutable incoming task context and current worktree branch/tree are persisted at first
  attachment and reused on reattach; branch tasks include submitted and predecessor-composed context.
- [x] SQLite and PostgreSQL enforce one correlated work plan per parent run and workflow node.
- [x] Invalid first-release fan topology fails binder validation and workflow save; only `prompt`
  branch nodes are accepted because specialized `peer_review` and `build_test` semantics are not
  implemented by the static dispatcher.
- [x] A valid static fan topology binds to the production MAF graph and suspends at a durable child-work request port.
- [x] Independent branches dispatch concurrently and the join waits for all branches.
- [x] `(coordinatorRunId, subtaskId)` is the durable branch idempotency key: launch reserves the
  child Run id before execution is observable, and recovery adopts active or terminal unlinked runs.
- [x] Restarted active branches retain their original Run rows and ids and are re-observed before
  the parent continuation is delivered exactly once.
- [x] Joined output follows persisted branch ordinal, never child completion or database id order.
- [x] Failed, blocked, cancelled, or RAI-flagged branches cannot yield joined success.
- [x] Static fan completion bypasses integration Git branches, collective merge/review/publication, and Scribe.
- [x] Parent/branch/join state is visible through work-plan, child, event, diagnostics, MCP, and web projections.
- [x] Embedded coordinators have durable streams and canonical `run:{childRunId}` graph references,
  and their graph omits collective assembly stages.
- [x] Child-work suspension is classified as `workflow_child_work`; REST rejects `/review` and MCP/UI
  do not advertise human-review actions for that wait.
- [x] Direct and blueprint custom-workflow generation share one mechanical fan-safety policy.
- [x] Generated fan branches require `independent: true` plus exact, pairwise-disjoint
  `declared_output_paths`; unknown, dynamic, broad, shared, dependent, or overlapping scopes are
  deterministically linearized in declaration order.
- [x] Windows-style path normalization is case-insensitive and rejects exact and file/directory
  prefix overlap, manifests, migrations, and generated shared artifacts.
- [x] Generated prompt-only fans remain runtime-bindable, while `serial` and
  `coordinator_composed` remain unsupported.
- [x] PM Discovery runs customer-signal and technical-feasibility research as two ordered branches,
  joins them, then continues through synthesis and review.
- [x] The API harness can distinguish safe generated fans from conservative sequential output and
  retain provider-generated workflows for post-merge runtime evidence without simulating completion.

## Notable edge cases

- A crash after plan persistence but before continuation arming leaves the plan non-dispatchable and safe to reattach.
- A child plan that becomes terminal before resume preparation is reconciled into the same pending delivery.
- Stale `delivering` claims can be reclaimed, while `delivered` rows are no-ops.
- Branch order comes from persisted ordinals, not database-generated subtask ids or edited workflow YAML.
- A crash after all branches become terminal but before parent delivery reuses the same prepared
  result and fenced request id.
