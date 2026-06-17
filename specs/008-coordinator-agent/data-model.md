# Data Model: Squad Coordinator Agent (Spec 008)

**Branch**: `008-coordinator-agent`
**Companion to**: `plan.md`
**Date**: 2026-06-17

All entities below are added to the **existing** `MemoryDbContext`
(`apps/Scaffolder.Api/Memory/`), persisted in `scaffolder.db` via
`Microsoft.EntityFrameworkCore.Sqlite`. This is required by FR-003 / FR-004a: the
outcome spec and work plan MUST live in the team's existing memory/decision store, not a
parallel one. One EF Core migration adds all six tables. The `Run` linkage change is made in
the legacy raw run store (`Infrastructure/SqliteDb.cs`) with idempotent `ADD COLUMN` guards,
consistent with how Features 005/006 evolved that table.

Status enums are stored as strings (matching the existing `AgentMemory`/`DecisionInboxEntry`
convention of string-typed `Type`/`Status` columns).

## Run linkage (Domain + run store)

Add to `Scaffolder.Domain.Run` (both nullable; null for all legacy single-agent runs):

| Field | Type | Notes |
| --- | --- | --- |
| `ParentRunId` | `string?` | The coordinator run that launched this child run. Null for the coordinator run itself and for ordinary single-agent runs. |
| `SubtaskId` | `string?` | The `Subtask.Id` this child run executes. Null for non-orchestrated runs. |

## OutcomeSpec

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | int (PK) | |
| `ProjectId` | string | Owning project (Feature 003) |
| `CoordinatorRunId` | string | The coordinator run that owns this spec |
| `Goal` | string | The user's original plain-language goal |
| `DesiredOutcome` | string | What success looks like |
| `Scope` | string | In/out of scope |
| `Assumptions` | string | Stated assumptions |
| `ClarifyingQuestions` | string? | Scoped questions that materially affect scope (FR-007) |
| `Status` | string | `drafting` / `awaiting_confirmation` / `confirmed` / `declined` |
| `ConfirmedBy` | string? | GitHub login of the confirming human (FR-008) |
| `CreatedAt` / `UpdatedAt` | DateTimeOffset | |

Index: `(ProjectId, CoordinatorRunId)`.

## WorkPlan

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | int (PK) | |
| `OutcomeSpecId` | int (FK) | The confirmed spec this plan derives from |
| `ProjectId` | string | |
| `CoordinatorRunId` | string | |
| `IsolationSummary` | string? | Human-readable summary of the chosen isolation strategy (FR-030) |
| `IntegrationBranch` | string? | The coordinator run's integration branch/worktree holding the assembled tree that the N child worktree branches are merged into before the single collective review (N1, FR-031). Null until assembly begins. |
| `Status` | string | `planned` / `dispatching` / `assembling` / `in_review` / `complete` |
| `CreatedAt` / `UpdatedAt` | DateTimeOffset | |

Index: `(CoordinatorRunId)`.

## Subtask

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | int (PK) | |
| `WorkPlanId` | int (FK) | |
| `Title` | string | |
| `Scope` | string | The scope/context the dispatched subagent reads (FR-004a) |
| `AssignedAgent` | string | Roster member name (Feature 005) chosen by role fit (FR-011) |
| `SelectedModelId` | string | Copilot model id chosen by complexity; honors role default with runtime override (FR-012). Provider fixed to Copilot. |
| `Phase` | string | `none` / `planning` / `execution` / `validation` (FR-028) |
| `IsolationStrategy` | string | `worktree` / `shared` (FR-030) |
| `Status` | string | `pending` / `dispatched` / `running` / `rai_flagged` / `assemble_ready` / `completed` / `failed` |
| `ChildRunId` | string? | The child run executing this subtask (via the trimmed child-run pipeline: agent -> RAI -> assemble-ready terminal; no individual review/merge/scribe — B1) |
| `LockedOutAgents` | string? | CSV of agents barred from revising (reviewer-rejection lockout, FR-023) |
| `CreatedAt` / `UpdatedAt` | DateTimeOffset | |

Index: `(WorkPlanId)`.

## SubtaskDependency

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | int (PK) | |
| `SubtaskId` | int (FK) | The dependent subtask |
| `DependsOnSubtaskId` | int (FK) | The prerequisite |

Together these form the dependency DAG that decides parallel vs serial dispatch (FR-013/FR-014).
Index: `(SubtaskId)`.

## BubbleUpRequest

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | int (PK) | |
| `CoordinatorRunId` | string | The parent run surfacing the request |
| `ChildRunId` | string | The originating child run |
| `OriginatingAgent` | string | Attribution shown to the human (FR-024) |
| `Kind` | string | `clarification` / `permission` |
| `Prompt` | string | The question or the gated-action description |
| `Status` | string | `pending` / `answered` / `denied` |
| `Granted` | bool? | For `permission`: whether the human granted it (FR-025) |
| `Answer` | string? | The human's answer relayed back (FR-026) |
| `AnsweredBy` | string? | GitHub login of the responder |
| `CreatedAt` / `AnsweredAt` | DateTimeOffset | |

Index: `(CoordinatorRunId, Status)`.

## SteeringDirective

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | int (PK) | |
| `CoordinatorRunId` | string | |
| `TargetChildRunId` | string? | Null = broadcast to all affected children |
| `Kind` | string | `redirect` / `pause` / `stop` / `amend` (FR-018/FR-018a). Steering is NEW infrastructure (B2): `stop` cancels via the existing `RunWorkflowRegistry.Abandon -> Cts.Cancel()` path; `redirect`/`amend` are applied at the child's **next turn boundary** (queued, then injected as a revised task turn — there is no mid-turn interrupt today); `pause` has no current primitive and is contingent on the Phase 2 feasibility spike (hold-before-next-turn) or is descoped. |
| `Instruction` | string | The direction the coordinator relays |
| `Status` | string | `pending` / `queued` / `relayed` / `applied` (`queued` = awaiting the target child's next turn boundary) |
| `CreatedBy` | string | GitHub login of the steering human |
| `CreatedAt` / `RelayedAt` | DateTimeOffset | |

Index: `(CoordinatorRunId, Status)`.

## Lifecycle summary

1. `POST /orchestrations` creates the coordinator run -> `OutcomeSpec` (drafting).
2. Confirm -> `OutcomeSpec.Status = confirmed` -> coordinator persists `WorkPlan` + `Subtask`
   rows + `SubtaskDependency` edges.
3. Dispatch -> each `Subtask` gets a `ChildRunId`; the child `Run` carries `ParentRunId` +
   `SubtaskId` and runs the **trimmed child-run pipeline** (agent -> RAI -> assemble-ready
   terminal; no individual review/merge/scribe — B1).
4. Per-child RAI findings update `Subtask.Status` (`rai_flagged`) and may append to
   `LockedOutAgents`. A child that passes RAI terminates `assemble_ready`.
5. Bubble-ups and steering directives are created/answered out-of-band while children run.
   Steering follows the next-turn-boundary semantics above (B2).
6. Assembly -> the coordinator run merges the N child worktree branches into its
   `WorkPlan.IntegrationBranch` tree and runs **pre-gate conflict detection** (FR-031, N1) ->
   the re-wired `review-gate` + merge + scribe run **once** over the assembled tree;
   `WorkPlan.Status` advances to `complete`.
