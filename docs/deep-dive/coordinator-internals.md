# Coordinator Internals — Conceptual Deep Dive

## Purpose and scope

The orchestration overview explains how a goal moves through Agentweaver at a system level. The focus here is the coordinator itself: the subsystem that turns one broad request into a confirmed intent contract, a dependency-aware work plan, multiple child runs, and one collective result.

The coordinator is best understood as a **durable team manager**. It does not merely ask a model to "do the task." It records what success means, decides how to divide responsibility, starts child workers only when their prerequisites are ready, watches their outcomes, assembles their branches into one candidate result, and routes failure back into retry or terminal states.

Primary scope:

- outcome-spec drafting and confirmation;
- workflow selection and work-plan decomposition;
- subtask dispatch, observation, bubbling, and steering;
- collective assembly, review, merge, and scribe;
- restart recovery and retry semantics.

For the high-level relationship between coordinator orchestration and run workflow orchestration, see [Orchestration Engine — Conceptual Deep Dive](orchestration.md). The sections below assume that overview and go deeper into the coordinator's own internal logic.

## The coordinator mental model

A coordinator run has two personalities:

1. **Model-assisted planner.** It drafts an outcome spec, optionally revises it, selects a workflow shape, and decomposes the confirmed intent into subtasks.
2. **Service-driven supervisor.** After planning, background services drive dispatch and assembly from persisted state. They do not need the planner workflow to stay alive.

That split is the key to rebuilding the subsystem. The model helps create structured intent and plan data. Durable services then advance that data through deterministic state machines.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TB
    Goal(["Goal or Ready backlog task"])

    subgraph P1["Phase 1 — MAF spec workflow (checkpointed)"]
        Draft(["coordinator-draft"])
        CGate{{"RequestPort: coordinator-confirmation-gate"}}
        Revise(["coordinator-revise"])
        Finalize(["coordinator-finalize"])
    end

    Spec[("OutcomeSpec")]

    subgraph P2["Phase 2 — Service-driven engine (D3: not a MAF graph)"]
        Orchestrate(["CoordinatorOrchestratorExecutor"])
        Plan[("WorkPlan + Subtask DAG")]
        Dispatch(["CoordinatorDispatchService"])
        Children(["Child MAF runs"])
        Assembly(["CoordinatorAssemblyService<br/>CollectiveAssemblyPipeline"])
        AGate{{"AssemblyReviewGate"}}
        Result(["Assembled result: merge + scribe"])
    end

    Stream(["Coordinator stream (SSE)"])

    Goal --> Draft --> CGate
    CGate -- revise --> Revise --> Draft
    CGate -- confirm --> Finalize
    Draft -. persists .-> Spec
    Finalize --> Orchestrate --> Plan --> Dispatch
    Dispatch -- ready frontier --> Children
    Children -- assemble_ready --> Dispatch
    Dispatch -- all terminal --> Assembly --> AGate
    AGate -- approve --> Result
    AGate -- request changes --> Dispatch
    Draft -. events .-> Stream
    Dispatch -. events .-> Stream

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;

    class Goal client;
    class Draft,Revise,Finalize,CGate,Children runtime;
    class Spec,Plan data;
    class Orchestrate,Assembly,AGate,Result svc;
    class Dispatch core;
    class Stream evt;
```

The durable artifacts are:

- **Coordinator run** — the parent run visible to clients.
- **OutcomeSpec** — the intent contract.
- **WorkPlan** — the execution contract for the confirmed outcome.
- **Subtask rows and dependency edges** — the dispatch DAG.
- **Child runs** — worker executions tagged with parent run id and subtask id.
- **Coordinator stream events** — the live and replayable explanation of what changed.

## Core invariants

- **Intent is confirmed before execution.** The coordinator can draft and revise, but decomposition is authoritative only after confirmation or unattended confirmation.
- **Plan before dispatch.** Child runs are launched from a persisted WorkPlan, never from transient model text.
- **One parent owns the combined outcome.** Children do agent work and safety review; the parent owns collective review, merge, and scribe.
- **The dependency graph is the hard ordering rule.** A subtask can run only when every dependency is satisfied.
- **`assemble_ready` and `completed` satisfy dependencies.** `failed` and `rai_flagged` do not; their dependents are blocked or failed.
- **Isolation is advisory.** Child subtasks share the orchestration worktree. File-scope declarations and conservative conflict checks are what reduce clobbering.
- **Dispatch is single-writer.** The dispatch loop owns subtask status mutation while active.
- **Assembly is exactly-once by database compare-and-swap.** In-memory guards are helpful but not authoritative.
- **Recovery starts from persisted state.** Restart logic routes by WorkPlan status, not by reconstructing chat history.
- **Provider choice is not dynamic on the live path.** The live coordinator path directly builds Copilot-backed agents; the Foundry dispatcher seam is plumbed but not active here.

## Coordinator state machine

There are two overlapping state machines: the parent run status and the WorkPlan status. The WorkPlan is the more precise coordinator-internal state after planning.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
stateDiagram-v2
    [*] --> SpecDrafting
    SpecDrafting --> AwaitingConfirmation: outcome spec persisted
    AwaitingConfirmation --> SpecDrafting: revise
    AwaitingConfirmation --> Declined: decline
    AwaitingConfirmation --> Planned: confirm + work plan persisted

    Planned --> Dispatching: dispatch loop starts
    Dispatching --> AwaitingAssembly: all subtasks terminal
    Dispatching --> Dispatching: child completes and unlocks frontier
    Dispatching --> Dispatching: steering revision or re-dispatch

    AwaitingAssembly --> Assembling: assembly CAS claim
    Assembling --> RaiBlocked: collective RAI flagged
    Assembling --> NeedsResolution: integration or merge conflict
    Assembling --> InReview: aggregate candidate ready
    InReview --> Dispatching: review requests changes
    InReview --> AssemblyDeclined: review declines
    InReview --> Assembling: review approves
    Assembling --> Complete: merge + scribe done
    Assembling --> AssemblyFailed: unexpected or terminal merge failure

    Complete --> [*]
    Declined --> [*]
    RaiBlocked --> [*]
    NeedsResolution --> [*]
    AssemblyDeclined --> [*]
    AssemblyFailed --> [*]
```

`RaiBlocked` and `NeedsResolution` are parked or terminal states. Operators can recover some parked states through steering or full run retry, but the coordinator does not silently continue past them.

## OutcomeSpec drafting logic

### Why the OutcomeSpec exists

The OutcomeSpec prevents every worker from independently interpreting the user's broad request. It turns a goal into a stable contract:

- desired outcome;
- scope and exclusions;
- assumptions;
- material clarifying questions;
- current status: awaiting confirmation, confirmed, or declined.

This contract is stored before the work is decomposed. From that point forward, child workers should treat the spec and their subtask as source of truth rather than reinterpreting the original request.

### How drafting works

The first coordinator phase is a Microsoft Agents Framework workflow:

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart LR
    Draft[coordinator-draft]
    Gate[await-confirmation RequestPort]
    Finalize[finalize spec]
    Revise[revise input]
    Orchestrate[orchestrate confirmed spec]

    Draft --> Gate
    Gate -- revise --> Revise --> Draft
    Gate -- confirm / decline --> Finalize --> Orchestrate

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;

    class Draft,Gate,Finalize,Revise runtime;
    class Orchestrate svc;
```

The drafting executor compiles team memory and active decisions, resolves the Coordinator charter, and runs a real Copilot coordinator turn. The prompt asks for one JSON object with `desired_outcome`, `scope`, `assumptions`, and `clarifying_questions`.

Important details:

- The human goal and revision feedback are fenced as untrusted data. The model is instructed to restate intent, not obey prompt-injection text inside the goal.
- Drafting streams onto the coordinator run timeline so the UI does not show an empty run while planning happens.
- The parser tolerates extra prose by extracting the first JSON object, but required fields must exist.
- If the model is unavailable or the draft is unparseable, the coordinator run fails visibly. It does **not** fabricate a boilerplate spec.
- Revision overwrites the existing draft in place and re-arms it for confirmation.

### Confirmation paths

Interactive runs suspend at the confirmation gate until a human confirms, revises, or declines.

Backlog pickup runs also go through the same gate, but a bounded unattended confirmation loop confirms the reversible plan on behalf of the accountable human captured on the backlog item. This is not Autopilot bypassing safety. Autopilot answers child clarifying questions only; it does not grant tool approvals, skip confirmation for interactive runs, or skip collective human review.

There is a small ordering race between "the spec was persisted and emitted" and "the framework request port is armed." The resume seam handles this by waiting briefly for the pending gate while the spec remains `awaiting_confirmation`, preserving double-submit protection without rejecting a fast confirm.

## Workflow selection and WorkPlan decomposition

### Workflow selection as shape guidance

After confirmation, the coordinator selects the workflow shape the work should follow. This is `CoordinatorOrchestratorExecutor.SelectWorkflowAsync`, and its defining property is **deterministic-first**: hard, cheap rules collapse the candidate space, and an LLM is consulted only when more than one workflow genuinely fits and no human has already named one.

1. `WorkflowRegistry.ResolveDefault(project)` resolves the project default first. It is both the selector's deterministic fallback (placed first in the candidate list) and the explicit value `SelectWorkflowAsync` returns if any step throws.
2. `WorkflowRegistry.GetOrLoad(project).Available` is ordered default-first, then by id.
3. `ResolveInvocationKindAsync` maps the run's origin to a `WorkflowInvocationKind`: `RunOrigin.BacklogPickup` becomes `Heartbeat`; everything else (and any lookup failure) becomes `Manual`.
4. A backlog `WorkflowOverrideId` short-circuits selection, but only when the workflow exists **and** `WorkflowTriggerEvaluator.IsEligible` accepts it for the invocation; otherwise the mismatch is logged and selection continues.
5. `WorkflowTriggerEvaluator.IsEligible` filters the candidates by trigger. This is a hard boundary applied **before** any model call — a manual run never selects a heartbeat/event workflow and a heartbeat pickup never selects a manual-only one.
6. Zero eligible candidates → return the project default rather than a trigger-mismatched workflow.
7. Exactly one eligible candidate → use it directly, with **no model call and no selection event** (the common, single-workflow project case stays silent and free).
8. Two or more eligible candidates → build a `WorkflowSelectionContext` and resolve the pick. An explicit `use {workflow-id}` in the revise feedback (`WorkflowSelector.TryParseOverride`) wins outright; otherwise the Copilot-backed `WorkflowSelector.SelectAsync` chooses by process fit.

The LLM is therefore consulted in exactly one situation: **2+ trigger-eligible workflows and no explicit override**. `WorkflowSelector.SelectAsync` itself is conservative — it short-circuits to the default when only one workflow is present, and any model failure, unparseable JSON, or unknown id (`CopilotWorkflowSelectionModel` returns `null` on failure) falls back to the first candidate, the project default. Failures are never silently swallowed: every multi-candidate resolution emits a `coordinator.workflow_selected` event (`EmitWorkflowSelectedEvent`) carrying the chosen id, a rationale, `wasAutoSelected`, an `overrideHint`, and the available set; and a thrown `SelectWorkflowAsync` logs a warning and returns the resolved default so the caller always knows what it is planning against.

The selected workflow is not just recorded for display. It becomes prompt context for decomposition so the resulting subtask graph mirrors the intended process shape. The run workflow factory resolves the effective workflow again at graph-build time, so a stale planning pick can never become unchecked execution.

### Decomposition strategy

The decomposition turn asks for the **minimum set of independently dispatchable subtasks**. Each subtask must include:

- title;
- exact scope, including files or outputs it owns;
- role id, preferably from the active roster;
- optional bespoke charter when no roster/catalog role fits;
- complexity;
- phase;
- advisory isolation hint;
- 1-based dependency indices.

The prompt pushes the model toward few, bounded subtasks and explicit dependency edges. It also asks parallel file-producing subtasks to write unique outputs, then add a consolidation subtask when parallel research needs synthesis.

The decomposition turn is grounded in:

- the confirmed OutcomeSpec;
- the selected workflow summary;
- active roster roles;
- relevant architectural and scope decisions;
- coordinator memory/session context.

The same prompt-injection rule applies: spec fields are fenced and treated as data.

### Defensive parsing and normalization

The WorkPlan builder assumes model output can be malformed and normalizes aggressively:

- extract the first JSON array;
- try a trailing-comma repair;
- skip invalid items rather than failing the whole array;
- require title and scope;
- rebase dependencies after skipped items;
- drop self-references and out-of-range dependencies;
- normalize complexity to low/medium/high;
- normalize phase to none/planning/execution/validation;
- default isolation to worktree;
- default role to core implementer;
- trim optional bespoke charters.

If no valid decomposition is available, the coordinator falls back to one deterministic execution subtask covering the whole confirmed outcome. This keeps the pipeline operational offline, unlike OutcomeSpec drafting where unparseable output fails visibly.

### DAG repair

The dependency graph must be acyclic. If the model creates a cycle, the coordinator traverses dependencies in stable order and drops the back-edge that closes the cycle. It records a note in the plan's isolation summary rather than dispatching a deadlocked graph.

This is a pragmatic trade-off: it preserves progress for most accidental cycles while treating the removed edge as lower confidence than the rest of the ordering constraint. A stricter rebuild can fail and ask for clarification instead.

### Assignment and model selection

Subtasks are assigned to active, dispatchable roster members. Built-in infrastructure agents such as Scribe and RAI are excluded. Role matching scores exact role/title matches, token overlap across capabilities and responsibilities, and phase affinity.

Model selection is fixed to GitHub Copilot on this path:

1. high-complexity subtasks can use the coordinator run's explicit model override;
2. otherwise use the assigned role's default model;
3. otherwise use a catalog role default;
4. otherwise use the run override;
5. otherwise use the configured Copilot default.

The persisted WorkPlan starts as `planned`, with subtasks in `pending` and dependency edges persisted by database ids.

## Dispatch and child tracking

### Ready frontier

Dispatch repeatedly computes the ready frontier:

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TD
    Load[(Load WorkPlan)]
    Status[Read subtask statuses]
    Ready{Pending and all dependencies satisfied?}
    Conflict{Conflicts with in-flight work?}
    Launch[Launch child run]
    Observe[Observe child stream]
    Terminal{Child terminal?}
    Update[Update subtask status]
    More{More ready or in-flight?}
    Assembly[Awaiting assembly]

    Load --> Status --> Ready
    Ready -- no --> More
    Ready -- yes --> Conflict
    Conflict -- yes, defer --> More
    Conflict -- no --> Launch --> Observe --> Terminal --> Update --> More
    More -- yes --> Status
    More -- no --> Assembly

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;

    class Load data;
    class Status,Ready,Conflict,Observe,Terminal,Update,More,Assembly svc;
    class Launch runtime;
```

Only `assemble_ready` and `completed` satisfy dependencies. A failed or RAI-flagged dependency fails its still-pending dependents with recovery guidance, because serial dependents cannot safely proceed from a bad prerequisite.

### Shared worktree conflict control

Child subtasks share one orchestration worktree. `IsolationStrategy` helps communicate intent, but it is not an enforced sandbox.

The dispatcher therefore adds two conservative safeguards:

- If multiple subtasks declare the same output file token, it serializes them by adding dependency edges.
- While a child is in flight, another ready subtask is deferred if their declared file tokens overlap. If either side declares no file tokens, they are assumed to conflict.

This favors correctness over maximum parallelism. A poorly scoped subtask may reduce parallelism, but it is less likely to clobber sibling work.

### Child run construction

For each dispatched subtask, the coordinator creates a child run with:

- `ParentRunId` set to the coordinator run;
- `SubtaskId` set to the subtask id;
- assigned agent and selected model from the WorkPlan;
- `ModelSource = GitHubCopilot`;
- inherited run options such as auto-approve-tools and Autopilot;
- scoped approval inheritance for that project/run/subtask.

The child task includes the subtask title/scope, any recovery guidance, the parent OutcomeSpec, dependency summaries, and completed sibling outputs. That gives workers enough local context without asking them to rediscover the entire plan.

Child runs use the trimmed child workflow. They produce work and pass child-level safety checks, then stop at the assemble-ready boundary. They do not each perform human review, merge, or scribe.

### Observation and bubbling

The dispatch loop observes child runs through the durable run event stream. It replays the events already recorded for each child run and then tails new ones as they arrive, so the coordinator sees a complete, ordered history regardless of when it begins observing. This replay-then-tail model means observation is resumable: a coordinator that restarts can reconstruct child progress from the stream rather than depending on in-memory state.

Terminal child events map to coordinator outcomes:

- `run.assemble_ready` maps to `assemble_ready`, unless safety was flagged;
- content-safety failures map to `rai_flagged`;
- completed no-change runs map to `completed`;
- failed, declined, cancelled, and merge-failed child states map to `failed`.

Mid-run child questions and tool approval requests are re-emitted on the coordinator stream with child run id, subtask id, and request id. Autopilot may answer bubbled **questions** by running a one-shot Copilot coordinator turn grounded in the OutcomeSpec and subtask. Tool approvals remain separate and are not auto-granted by Autopilot.

Observation includes stall handling. If a child emits no events within the configured stall timeout, the coordinator persists any partial output checkpoint it saw, fails the subtask with recovery guidance, increments the recovery-attempt counter, and propagates failure to dependents.

## Collective assembly

When all subtasks settle, dispatch moves the WorkPlan to `awaiting_assembly` and hands off to the assembly service. Assembly is service-driven rather than a MAF workflow because it starts from already-produced git state, has a coordinator-owned review gate, and routes review changes back to re-dispatch rather than back to one model turn.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TD
    Claim{CAS awaiting_assembly<br/>to assembling}
    Eligible{All subtasks eligible?}
    Order[Topological branch order]
    Integration[Build integration branch]
    Rai[Collective RAI over aggregate diff]
    RaiFlag{RAI flagged?}
    Review[One collective human review]
    Decision{Decision}
    Merge[Merge integration branch]
    Scribe[Collective scribe]
    Complete[Complete parent run]
    RequestChanges[Infer affected subtasks<br/>reset to pending]
    AlreadyClaimed[Already claimed<br/>skip]
    Blocked[Blocked / needs resolution / failed]

    Claim -- lost --> AlreadyClaimed
    Claim -- won --> Eligible
    Eligible -- no --> Blocked
    Eligible -- yes --> Order --> Integration
    Integration -- conflict --> Blocked
    Integration -- ok --> Rai --> RaiFlag
    RaiFlag -- yes --> Blocked
    RaiFlag -- no --> Review --> Decision
    Decision -- approve --> Merge
    Decision -- request changes --> RequestChanges
    Decision -- decline --> Blocked
    Merge -- conflict or failure --> Blocked
    Merge -- merged --> Scribe --> Complete
    RequestChanges --> Dispatching[Back to dispatch]

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;

    class Claim,Eligible,Order,RaiFlag,Review,Decision,Complete,RequestChanges,AlreadyClaimed,Blocked svc;
    class Integration,Rai,Merge,Scribe runtime;
    class Dispatching core;
```

### Exactly-once claim

Assembly starts with a database compare-and-swap from `awaiting_assembly` to `assembling`, stamping the integration branch. Only the winner proceeds. This is the authoritative exactly-once guard across dispatch completion, recovery, and review-triggered re-dispatch.

### Eligibility gate

The coordinator does no partial assembly. Every subtask must be `assemble_ready` or `completed`.

- `assemble_ready` means the child produced changes to assemble.
- `completed` means the child completed with no mergeable changes; this is an eligible no-op.
- `failed`, `rai_flagged`, and still-running statuses block the whole plan.

### Integration branch

Eligible child branches are merged into one integration branch in dependency order. Completed no-op children are eligible but contribute no branch. If child branch integration conflicts, the coordinator stops before collective review or merge and records a needs-resolution style terminal/parked state.

### Collective RAI

The production pipeline reuses the existing RAI executor over the aggregate diff. A collective RAI safety flag is a hard stop: the WorkPlan is marked `rai_blocked`, the coordinator run is failed, and a human override/recovery path is required.

### One collective review

The human reviews the combined integration result once. The gate is an in-memory, owner-scoped task keyed by coordinator run id. It is at-most-once: double submissions find no armed gate after the decision is consumed.

Review decisions:

- **Approve** — proceed to one collective merge.
- **Request changes** — infer affected subtasks and re-dispatch them.
- **Decline** — mark assembly declined and terminalize the coordinator run.
- **Timeout/cancel** — leave recoverable or mark failed depending on path.

### Request-changes routing

When the reviewer requests changes, the coordinator tries to avoid redoing everything:

1. Combine explicit target files with path-like tokens parsed from feedback.
2. Match those files against files touched by each child diff.
3. Select directly matched subtasks.
4. Add every transitive dependent of those subtasks.
5. If no files can be inferred or no child matches, fall back to all subtasks.

Selected subtasks are reset to `pending` with recovery guidance containing the review feedback. Other completed subtasks remain intact. The WorkPlan returns to `dispatching`, and the dispatch loop re-runs the affected frontier. After those children finish, assembly starts again from `awaiting_assembly`.

### Merge, scribe, and decision promotion

Approval triggers one merge of the integration branch into the originating branch, serialized by the repository merge lock. Merge conflicts become needs-resolution. Non-conflict merge failures become assembly failures.

After a successful merge, the coordinator runs one collective Scribe pass. Scribe is best-effort: failure is visible but does not fail the already-merged assembly. The coordinator then promotes pending architectural and scope decisions created by the coordinator during the run, marks the WorkPlan complete, terminalizes the parent run, persists stream events, and completes the stream.

## Recovery and retry semantics

The coordinator assumes in-memory drivers can disappear. Recovery routes by durable WorkPlan state.

| Durable state | Recovery action |
|---|---|
| No WorkPlan | Resume the checkpointed spec draft/confirmation workflow. |
| WorkPlan with no subtasks | Finalize the coordinator run from the spec status. |
| `planned` or `dispatching` | Reset in-flight subtasks to pending and re-arm dispatch. |
| `awaiting_assembly` | Re-arm assembly; the CAS decides the winner. |
| `assembling` or `in_review` | Reset to `awaiting_assembly` and re-run assembly to recreate in-memory gates. |
| `complete` | Settle the coordinator run as completed if it was still in progress. |
| blocked/failed/declined assembly states | Settle the coordinator run as failed or declined with the recorded reason. |

The heartbeat also runs a reconciler. It scans for orphaned dispatching, awaiting-assembly, and assembling plans whose in-memory loop is gone, recreates the coordinator stream if needed, and re-arms the correct service. Each candidate is isolated by try/catch so one corrupt plan does not stop the sweep.

### Failure containment

Several paths intentionally convert ambiguous failure into durable, inspectable state:

- Child start failure creates a terminal failed child run before marking the subtask failed, so the child run page is not empty.
- Orphaned/stalled children are failed after the stall TTL instead of being observed forever.
- Pending dependents of a failed prerequisite are failed with recovery guidance.
- Unexpected assembly exceptions mark the WorkPlan failed, emit a human-readable assembly failure, terminalize the run, and complete/persist the stream.
- Corrupt reconciler candidates are marked failed rather than retried endlessly.
- Steering recovery is capped per subtask to avoid infinite auto-resume loops.

### Steering and operator recovery

Steering is the live control surface:

- **send** records an informational nudge and changes no state.
- **stop** cancels active child workflows and can terminalize the coordinator on broadcast stop.
- **redirect** and **amend** queue instructions for a child's next turn boundary.
- A targeted redirect can force-complete a stuck child stream so the dispatch loop reaches a boundary and applies the directive.
- For parked coordinators, redirect/amend can reset affected subtasks to pending, reopen the coordinator stream, un-terminalize the run when appropriate, and re-arm dispatch.

The semantics are deliberately "honest": there is no mid-token or mid-tool magical pause. Direction changes apply at turn boundaries or through explicit cancellation/re-dispatch.

### Retrying a pickup run

Retrying a failed backlog-pickup coordinator creates a fresh parent run with `RetriedFrom` pointing to the source run and preserves the durable backlog-pickup origin and accountable human. It does not silently re-claim or duplicate the backlog task.

## CopilotAIAgent vs AgentRunnerDispatcher

The live coordinator path is Copilot-backed in multiple places:

- outcome-spec drafting constructs `CopilotAIAgent` directly;
- workflow selection constructs `CopilotAIAgent` directly;
- decomposition constructs `CopilotAIAgent` directly;
- Autopilot question answering constructs `CopilotAIAgent` directly;
- child runs are created with `ModelSource.GitHubCopilot`;
- live workflow execution uses the Copilot workflow turn-agent path.

The provider-neutral `AgentRunnerDispatcher` can route one-shot runner calls to Foundry, but that seam is not active for the live coordinator/run workflow path. Rebuilding provider choice for coordinator execution requires adding an explicit workflow turn-agent selection point and preserving setup, event normalization, tool governance, checkpointing, and child-run semantics for the new provider.

## Common failure modes

| Failure mode | Coordinator behavior |
|---|---|
| Draft model unavailable or unparseable | Fail visibly; do not invent an OutcomeSpec. |
| Decomposition model unavailable or malformed | Fall back to one deterministic subtask. |
| Model creates dependency cycle | Drop cycle-closing edges deterministically and note it. |
| Workflow selection fails | Fall back to project default workflow. |
| Child run cannot start | Persist terminal failed child run, then fail the subtask. |
| Child safety flagged | Mark subtask `rai_flagged`; dependents do not proceed. |
| Child stream stalls | Persist partial output checkpoint when possible, fail subtask, propagate failure. |
| Assembly has ineligible subtasks | Block whole assembly; no partial merge. |
| Integration branch conflict | Mark needs resolution; do not enter review/merge. |
| Collective RAI flagged | Current behavior: mark `rai_blocked` and terminalize failed. |
| Review requests changes | Reset inferred subtasks and dependents; re-dispatch. |
| Review declines | Mark assembly declined and terminalize. |
| Merge conflict | Mark needs resolution / merge failed. |
| Scribe fails after merge | Emit failure event but keep assembly successful. |
| Process restarts mid-run | Route by persisted WorkPlan status and re-arm idempotent engines. |

## Trade-offs

### Why mix MAF workflow and service-driven loops?

The confirmation phase benefits from MAF request ports and checkpoints because it is a human-suspendable model workflow. Dispatch and assembly are better as durable service loops because they supervise many child runs, rebuild in-memory gates, and advance from relational state.

### Why fail hard for spec drafting but not decomposition?

An invalid OutcomeSpec means the system has not established intent. Fabricating one would violate the confirmation contract. An invalid decomposition happens after intent is confirmed; a one-subtask fallback preserves correctness, though with less parallelism.

### Why no partial assembly?

Partial assembly risks shipping an inconsistent subset of a team plan. The coordinator instead requires every subtask to be eligible, then assembles the whole result once.

### Why conservative file conflict rules?

All child runs share a worktree. If the coordinator cannot prove two subtasks own disjoint files, it serializes them. This may reduce parallelism but avoids silent clobbering.

### Why reset assembly after restart from `in_review`?

The review gate is in memory. After restart, the HTTP endpoint has nothing to complete. Resetting to `awaiting_assembly` rebuilds the integration branch, re-runs the needed stages, and re-arms the review gate from durable state.

## Rebuild blueprint

To rebuild the coordinator from scratch:

1. **Define durable state first.** Create parent runs, OutcomeSpecs, WorkPlans, Subtasks, dependency edges, steering directives, and run events.
2. **Implement draft/revise/confirm.** Use a checkpointed workflow or equivalent request-port mechanism; persist the spec before asking for confirmation.
3. **Fence untrusted user text.** Treat goals, spec fields, feedback, and child questions as data inside prompts.
4. **Select workflow shape conservatively.** Filter by trigger, honor safe overrides, and default deterministically.
5. **Decompose into a minimal DAG.** Parse defensively, normalize fields, repair or reject cycles, and persist before dispatch.
6. **Assign real workers.** Exclude infrastructure agents, choose roster members by role fit, and make model/provider selection explicit.
7. **Dispatch only the ready frontier.** Use satisfied dependencies and conflict checks to control parallelism.
8. **Treat child runs as fragments.** They should stop at assemble-ready; parent-level review/merge/scribe happens once.
9. **Observe by durable events.** Replay then tail where possible; map child terminals into subtask statuses.
10. **Bubble human gates.** Re-emit child questions and approvals on the parent stream with enough correlation to answer the child.
11. **Assemble exactly once.** Use a database CAS for the `awaiting_assembly → assembling` claim.
12. **Require all children to be eligible.** Build one integration branch in dependency order and review the aggregate.
13. **Route review feedback to affected subtasks.** Infer files, include dependents, reset selected subtasks with recovery guidance, and re-dispatch.
14. **Make every failure durable and explainable.** Terminalize parent and child rows with reasons; persist stream events before completing.
15. **Recover by state, not memory.** On startup and heartbeat, route by WorkPlan status and re-arm idempotent drivers.
16. **Do not assume dispatcher provider support reaches live workflows.** Add provider selection at the workflow turn-agent seam if live coordinator runs need non-Copilot providers.

## Where this lives

- `apps/Agentweaver.Api/Coordinator/`
- `apps/Agentweaver.Api/Runs/`
- `apps/Agentweaver.Api/Memory/`
- `packages/Agentweaver.AgentRuntime/Workflow/`
- `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs`
