# Feature Specification: Workflow + Review Policy Composition (Stage 2)

**Feature Branch**: `013-workflow-policy-composition`

**Created**: 2026-06-22

**Status**: Clarified — Option B (composition-as-identity) adopted 2026-06-23

**Input**: User description: "Capture and frame the unresolved Stage-2 (wf-maf-stage2) design decision: Agentweaver has TWO overlapping mechanisms that both govern how a run is gated before it merges — the default workflow graph (apps/Agentweaver.Api/Workflows/, the linear `agent -> RAI -> human review -> merge -> scribe -> terminal` with gates baked into the workflow definition) and the default review policy (apps/Agentweaver.Api/ReviewPolicies/, which runs RAI + Rubberduck with human review as opt-in). These two models were designed independently and now CONFLICT when composed for a single run: (1) RAI is double-gated (baked-in workflow step AND review-policy check), (2) the policy injects a Rubberduck step that has no executor in the workflow graph, (3) the workflow's baked-in human-review gate is orphaned/ambiguous relative to the policy's opt-in human review. Two options were identified — Option A (policy authoritative; workflow drops its baked-in gates; behavior change needing sign-off) and Option B (composition-as-identity, recommended/parity-safe; default policy composed with default workflow is a no-op that yields current Stage-1 behavior, with a golden parity test). Specify the decision itself and surface it for clarification — do not silently pick one."

## Overview

Agentweaver gates every run before it merges. Stage 1 (Feature 010) introduced two mechanisms that each independently express "what gating a run must pass" — and they now overlap on the default path for a single run.

The **first mechanism** is the **default workflow graph** (`apps/Agentweaver.Api/Workflows/`). The canonical default workflow (`DefaultWorkflowTemplate.cs`, resolved through `WorkflowRegistry.cs` / `BuiltInWorkflows.cs`) is a linear graph — `agent -> rai -> review (human) -> merge -> scribe -> terminal` — whose gates are **baked into the workflow definition itself**. `RunWorkflowGraphBinder.cs` binds exactly those five logical nodes (`agent`, `rai`, `review`, `merge`, `scribe`) onto the live Microsoft Agent Framework executors and **throws on any unmapped edge or node** as a drift guard.

The **second mechanism** is the **default review policy** (`apps/Agentweaver.Api/ReviewPolicies/`). The canonical default policy (`DefaultReviewPolicyTemplate.cs`) declares an ordered list of review steps — `rai` then `rubberduck` — with human review as an **opt-in** step that is deliberately *not* in the default. `ReviewPolicyComposer.cs` is a pure graph transform that **injects a policy's review steps as new gate nodes immediately before the workflow's merge node**, re-pointing every edge that fed `merge` so it now enters the first injected gate.

These two models were designed independently, and composing the default policy onto the default workflow for one run now produces three concrete conflicts:

1. **Double-gated RAI.** The default workflow already contains a baked-in `rai` gate, and the default policy also declares a `rai` step. Composition injects a *second* RAI gate (`policy-rai`) before `merge`, so a single run is gated by RAI twice — once as the workflow's baked-in `rai` node and again as the policy-injected gate.
2. **Executor-less Rubberduck step.** The default policy's `rubberduck` step is injected as a `policy-rubberduck` gate node, but `RunWorkflowGraphBinder` has **no executor binding** for it. Per the binder's drift guard, an injected `rubberduck` node produces an edge with "no MAF binding," which throws at build time — the step exists in the composed definition but cannot execute.
3. **Orphaned / ambiguous human gate.** The workflow's baked-in `review` (human) gate sits in the graph, while the policy treats human review as an opt-in step that the default omits. It is undefined whether the default run's human gate comes from the workflow node, the policy, both, or neither — so the human-accountability gate's source of truth is ambiguous.

This feature **frames and resolves the design decision** that fixes the overlap. It began as a decision specification stating the problem and two candidate resolutions; the decision is now **resolved (Session 2026-06-23): Option B is adopted**. The two options are retained below for traceability, followed by the resolved Composition Contract.

Two options were identified:

- **Option A — Policy authoritative.** The workflow drops its baked-in gates (RAI, human review, merge gating) and the **review policy becomes the single source of truth** for what gating a run receives. This is a **behavior change** to existing runs needing explicit human sign-off: runs that previously always had a human gate would only have one if the policy opts in. **Considered and rejected.**
- **Option B — Composition-as-identity (adopted, parity-safe).** Treat the default policy's composition with the default workflow as a **no-op / identity** for the default case — keep the workflow's human gate as-is and define composition so that applying the default policy to the default workflow yields **exactly current Stage-1 behavior** (parity holds, provable by a golden parity test: default-in equals default-out). The composition binder is generalized so non-default policies can still layer additional behavior, but the default path is provably unchanged.

**Decision: Option B is adopted** because it preserves existing run semantics and is provable with a golden parity test. The resolved, implementation-level rules — precedence order, gate-kind dedupe, the step-kind/executor binding table, the default-identity guarantee, and migration — are specified in the **Composition Contract** and **Migration & Compatibility** sections below.

Consistent with the constitution, whatever is chosen MUST keep this an **API-first** capability with **MCP + Web parity** (Principles III, IV): the effective, composed gating of a run MUST be observable identically from both clients. It MUST use **no mocks/fakes/placeholders** — the binder binds to the real executors and every gate is a live check (Principle VII). It MUST keep every run **human-accountable** with the human-approval gate for irreversible actions intact (Principles IX, X), and contain **no emojis** in any shipped surface (Principle VIII).

## Clarifications

### Session 2026-06-23

- Q: Option A (policy authoritative) or Option B (composition-as-identity)? → A: **Option B is adopted.** The workflow graph is the structural source of truth for which gates exist and execute; the review policy is an **additive, deduplicated overlay**. Composing the default policy onto the default workflow is an **identity (no-op)** that preserves current Stage-1 run behavior, proven by a golden parity test. Option A is recorded (FR-006) but not selected — it would remove the always-on human gate from existing runs, which is a behavior change the team chose not to take.
- Q: What is the single source of truth for the human-review gate (the orphaned-gate conflict)? → A: **The workflow's `review` (human) node is the single human gate.** A policy `human-review` step is **absorbed onto** an existing workflow human gate (deduped, never duplicated); it is **injected only** when the target workflow has no human gate. The default workflow's baked-in `review` node is retained unchanged and is never orphaned.
- Q: How is the executor-less Rubberduck step resolved (and any future step kind with no executor)? → A: Stage 2 adds a **real Rubberduck executor binding** to `RunWorkflowGraphBinder`, so every supported step kind (RAI, Rubberduck, Human-review) resolves to a real executor. Because Stage-1 runs never executed rubberduck (no binding existed), **rubberduck is removed from the *default* policy** so the default composition stays identity; rubberduck remains available to **non-default / opt-in** policies, where it now composes into a real, executable gate. Any **unsupported** step kind MUST **fail validation** with a message naming the kind and MUST NOT be injected as an unbound node.
- Q: How is the default policy reconciled so default-on-default is provably identity? → A: The canonical default policy (`DefaultReviewPolicyTemplate`) is **realigned to `[rai, human-review]`**, exactly mirroring the default workflow's baked-in `rai` + `review` gates. Both steps are then absorbed by the workflow's existing gates, so the overlay is empty and the composed default equals the Stage-1 default. (Parity is defined as preserving actual Stage-1 *run* behavior — which always ran RAI then the human review gate and never ran rubberduck.)
- Q: If Option A were chosen, who signs off and is migration required? → A: **Moot** — Option A is not adopted.

## Resolution: Composition Contract (Option B)

This is the resolved, implementation-level contract. It supersedes the "to be selected at clarify" framing above.

### Precedence order (highest authority first)

1. **Runtime governance guarantees** (Microsoft Agent Framework, .NET 10): sandbox boundaries, step/time limits, the human-approval gate for irreversible actions, and the audit trail. **No workflow or policy may relax these** (Principles X, XI).
2. **Workflow structural backbone** (`WorkflowDefinition` bound by `RunWorkflowGraphBinder`): defines which gate nodes physically exist, their order, their edges, and their executors. The workflow is **authoritative** for graph shape and for any gate it bakes in.
3. **Review policy overlay** (`ReviewPolicyComposer`): **additive-only**. It MAY inject gates the workflow does **not already provide**; it MAY **never** remove, reorder, or duplicate a workflow gate.

Where the workflow and a policy both express the same gate, the **workflow wins** (the policy step is absorbed, not injected). Where a policy expresses a gate the workflow lacks, the **policy adds** it pre-merge in declared order.

### Gate-kind dedupe / overlay rule

- Every workflow gate node and every policy step carries a canonical **gate-kind key** (`rai`, `human-review`, `rubberduck`, ...). Gate-kind is an explicit attribute on the node/step, not inferred from labels.
- For each policy step, in declared order:
  - If the workflow already contains a **pre-merge gate with the same gate-kind key** → **ABSORB** (no node injected; the existing workflow gate stands as the single instance of that gate).
  - Else → **INJECT** a new gate of that kind immediately before the merge node, after any previously-injected steps, preserving policy order. The injected kind MUST have a real executor binding (else validation error, see below).
- There is **exactly one** gate per gate-kind on any composed pre-merge path: RAI is never double-gated; the human gate is never duplicated.

### Step-kind to executor binding (binder generalization)

`RunWorkflowGraphBinder` MUST map every supported policy step kind to a real executor, in addition to the workflow's baked-in logical nodes:

| Step kind | Executor | Notes |
| --- | --- | --- |
| `rai` | RAI content-safety executor (existing) | Same executor the workflow `rai` node binds to. |
| `human-review` | Human review / approval executor (existing `review` binding) | Same executor the workflow `review` node binds to; this is the single human gate. |
| `rubberduck` | Rubber-duck critique executor (**NEW in Stage 2**) | Added so non-default policies that include rubberduck are executable. |
| any other | (none) | MUST fail validation with a message naming the unsupported kind; MUST NOT inject an unbound node. |

The binder's drift guard (throw on an unmapped edge/node) is **retained**; the resolution makes the mapping table complete for the supported set rather than weakening the guard.

### Default-identity guarantee

- The canonical default review policy is **realigned to `[rai, human-review]`** to mirror the default workflow's baked-in `rai` + `review` gates.
- Composing the (realigned) default policy onto the default workflow therefore **absorbs both steps** and **injects nothing**: the effective workflow equals the Stage-1 default (`agent -> rai -> review -> merge -> scribe -> terminal`) node-for-node, edge-for-edge, executor-for-executor.
- A **golden parity test** asserts this identity and runs in CI as a drift guard (FR-009 to FR-012).

## Migration & Compatibility

- **Default-policy template change**: the in-code canonical `DefaultReviewPolicyTemplate` changes from `[rai, rubberduck]` to `[rai, human-review]`. This is the policy projection of what Stage-1 runs actually did; it does not change run behavior for default projects (parity).
- **Existing materialized policy files are never clobbered**: `TryMaterialize` already refuses to overwrite an existing `.agentweaver/review-policies/default.yaml`. A **one-time, idempotent normalizer** MUST reconcile any project whose materialized default policy still equals the **old** canonical `[rai, rubberduck]` *and* is bound as the project default, rewriting it to the new canonical `[rai, human-review]`. User-customized policies (anything not byte-equal to the old canonical) are **left untouched**.
- **Previously-broken default+rubberduck runs are fixed, not regressed**: before Stage 2, composing `[rai, rubberduck]` onto the default workflow **threw at build time** (executor-less `rubberduck`). After Stage 2 such a project either (a) is normalized to identity, or (b) keeps rubberduck and now gets a **real, executable** rubberduck gate. Both outcomes are strictly better than the prior build failure.
- **Custom user workflows are unaffected**: the overlay is additive-and-deduped, so a custom workflow keeps all its own gates; a policy can only add gates the workflow lacks. A workflow with **no merge node** composes as a no-op (already true in `ReviewPolicyComposer`).
- **Non-default policies that intentionally use rubberduck**: now compose into an executable rubberduck gate (new capability), with no double-gating of RAI or human review.
- **No project migration of workflows is required**: existing projects without materialized `.agentweaver/workflows/` continue to resolve the in-code default; the binder change only adds bindings, it removes none.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A default run is gated exactly once, unambiguously (Priority: P1)

A user submits work to a project that uses the default workflow and the default review policy (the out-of-the-box configuration). The run executes and is gated before merge. The user observes that RAI runs exactly once, that no step in the run lacks an executor, and that there is exactly one unambiguous human-review gate (or a clearly defined absence of one) before any irreversible action.

**Why this priority**: This is the core defect the feature exists to resolve. The default configuration is what every new project gets; today, composing the default policy onto the default workflow double-gates RAI, injects an unexecutable Rubberduck step, and leaves the human gate ambiguous. A default run that is gated once, fully executable, and unambiguous is the smallest slice that delivers value and is the precondition for everything else.

**Independent Test**: Create a project with the default workflow and default review policy, submit a run, and inspect the effective (composed) gating: confirm RAI appears exactly once, every node in the executed graph resolves to a real executor (no build-time "no MAF binding" throw), and the human-review gate's presence/source is unambiguous and matches the chosen option's defined behavior.

**Acceptance Scenarios**:

1. **Given** a project on the default workflow and default review policy, **When** a run is composed and built, **Then** the build succeeds with no unmapped-edge / "no MAF binding" error, and the executed graph contains exactly one RAI gate.
2. **Given** the same default run, **When** the effective gating is inspected, **Then** there is exactly one human-review gate whose source (workflow node vs. policy step) is unambiguous and consistent with the resolved design option.
3. **Given** the same default run, **When** every node in the executed graph is enumerated, **Then** each node resolves to a real executor binding (no orphaned, executor-less node such as today's `policy-rubberduck`).
4. **Given** both clients, **When** the effective gating of the default run is read from the MCP server and from the Web UI, **Then** both present the same gates in the same order (Principle IV).

---

### User Story 2 - The default composition preserves current Stage-1 behavior (parity) (Priority: P1)

A maintainer needs assurance that resolving the overlap does not silently change how existing runs behave. They run a golden parity check that composes the default review policy onto the default workflow and compares the result against the known-good Stage-1 default run pipeline. Under the adopted Option B the two are identical (zero delta); any drift fails the check.

**Why this priority**: The parity constraint is the safety net for the whole change. The conflict sits on the default path that every project inherits, so the resolution MUST be provably understood relative to today's behavior before planning. This is P1 because without a parity/delta proof the change cannot be merged safely (Principle IX human-accountable change; Principle VII no fakes — the check runs against the real composition).

**Independent Test**: Execute the golden parity test that composes `DefaultReviewPolicyTemplate` onto `DefaultWorkflowTemplate` and diffs the effective workflow (and its bound executor graph) against the captured Stage-1 default. Confirm the result is either provably identical (Option B) or a documented, explicitly approved delta (Option A).

**Acceptance Scenarios**:

1. **Given** the default workflow and default policy, **When** they are composed, **Then** a golden parity test asserts the effective definition (nodes, edges, ordering, verdict routing) against the captured Stage-1 default and reports identical-or-delta.
2. **Given** Option B is chosen, **When** the parity test runs, **Then** the composed default equals the Stage-1 default with zero behavioral delta (default-in equals default-out).
3. **Given** Option A is chosen, **When** the parity test runs, **Then** the behavioral delta (specifically the loss of an always-on human gate when the policy does not opt in) is captured, surfaced, and gated on explicit human sign-off before it can ship.
4. **Given** the default workflow or default policy changes later, **When** the parity test runs in CI, **Then** any drift from the captured baseline fails loudly rather than silently re-gating existing runs.

---

### User Story 3 - A non-default policy still layers additional gating onto a workflow (Priority: P2)

A user configures a project with a non-default review policy (for example, one that adds a human-review step a workflow does not bake in, or an extra review step). When a run is composed, the additional gating from the policy is layered onto the workflow correctly, each layered step has a real executor, and no gate is duplicated.

**Why this priority**: The reason the two mechanisms coexist is to let policies extend a workflow's gating without re-authoring the workflow. The resolution MUST keep that generalization working for non-default policies, not only fix the default. It is P2 because the default path (US1/US2) is the active defect and must be correct first; the generalization is the forward-looking capability built on the same binder.

**Independent Test**: Configure a non-default policy that adds a gating step beyond the workflow's baked-in gates, compose a run, and confirm the extra gate is present and executable, that no gate (e.g., RAI) is duplicated, and that the workflow's existing gates are not dropped or orphaned.

**Acceptance Scenarios**:

1. **Given** a workflow and a non-default policy that adds a gating step the workflow does not already contain, **When** a run is composed, **Then** the added gate is present, ordered before merge, and bound to a real executor.
2. **Given** a policy step that duplicates a gate the workflow already bakes in (e.g., RAI), **When** composition runs, **Then** the gate is **not** duplicated (no double-gating) and the run is gated by that check exactly once.
3. **Given** a policy that injects a step kind for which no executor exists, **When** composition or build runs, **Then** the system fails with a clear, actionable error naming the unbindable step kind rather than producing a silently broken or unexecutable graph.

---

### User Story 4 - Composition follows the resolved contract: dedupe, identity, executable overlay (Priority: P1)

A maintainer exercises the resolved Option B contract end to end across the four canonical cases — default-on-default (identity), an RAI duplicate (dedupe), a human-review duplicate (dedupe onto the workflow node), and a non-default rubberduck policy (executable injection) — and confirms each produces the contract-defined effective graph.

**Why this priority**: These are the concrete, testable obligations the resolution creates. They are the acceptance surface an implementer codes against and the regression surface CI guards. P1 because they operationalize US1/US2 into precise, checkable outcomes.

**Independent Test**: Run the four canonical compositions and assert each effective graph against its expected shape (identity, deduped RAI, deduped human gate, injected executable rubberduck), with every node bound to a real executor.

**Acceptance Scenarios**:

1. **Given** the default workflow and the realigned default policy `[rai, human-review]`, **When** composed, **Then** both steps are absorbed, no node is injected, and the effective graph equals the Stage-1 default node-for-node, edge-for-edge, executor-for-executor.
2. **Given** a workflow that bakes in an `rai` gate and a policy whose first step is `rai`, **When** composed, **Then** the policy `rai` is absorbed (not injected) and the run is gated by RAI exactly once.
3. **Given** a workflow that bakes in a human `review` gate and a policy with a `human-review` step, **When** composed, **Then** the policy step is absorbed onto the workflow `review` node, which remains the single human gate (no second human gate).
4. **Given** a workflow with no rubberduck gate and a non-default policy containing a `rubberduck` step, **When** composed, **Then** a rubberduck gate is injected pre-merge, in declared order, bound to the new real Rubberduck executor, and the build succeeds.
5. **Given** a policy containing an unsupported step kind, **When** composed or validated, **Then** validation fails with a clear message naming the unsupported kind and no unbound node is injected.

---

### Edge Cases

- **Workflow with no merge node**: A workflow with no irreversible-action (merge) node has nothing to gate; composition MUST be a no-op (the current composer already returns the workflow unchanged with an empty injection set), and no review steps may be injected.
- **Empty policy (zero steps)**: Composing a policy with no steps onto any workflow MUST leave the workflow's baked-in gating unchanged (identity), never stripping the workflow's own gates.
- **Policy step kind with no executor**: A policy step whose kind has no MAF executor binding MUST **fail validation** with a clear message naming the kind, never silently injected as an unexecutable node that throws at build time. (Rubberduck is no longer such a case — Stage 2 binds it; the rule governs any future unsupported kind.)
- **Duplicate gate (RAI in both workflow and policy)**: When a policy step names a gate the workflow already contains, composition MUST **dedupe to the single workflow gate** (absorb the policy step) — never double-gate.
- **Human gate present in workflow but absent from policy**: The workflow `review` node is the single source of truth for the human gate; a policy that omits `human-review` MUST NOT remove the workflow's human gate (overlay is additive-only).
- **Existing project still materialized with `[rai, rubberduck]`**: The one-time normalizer reconciles a still-default-equal policy to `[rai, human-review]`; an intentionally customized policy is left untouched and now composes its rubberduck into a real executable gate (previously a build failure).
- **Drift between definition and executor wiring**: If the default workflow definition diverges from the executor bindings, the build MUST fail loudly (preserving today's drift-guard behavior in `RunWorkflowGraphBinder`), never silently mis-wire.

## Requirements *(mandatory)*

### Functional Requirements

**Problem framing & the decision (the core of this spec)**

- **FR-001**: The system MUST resolve the overlap between the **default workflow graph** (`apps/Agentweaver.Api/Workflows/`, gates baked into the definition) and the **default review policy** (`apps/Agentweaver.Api/ReviewPolicies/`, RAI + Rubberduck with opt-in human review) so that a single run has **one coherent gating model** rather than two independently-authored ones.
- **FR-002**: The resolution MUST eliminate **double-gated RAI**: a run that uses the default workflow and default policy MUST be gated by RAI **exactly once**, never both as a baked-in workflow node and as a policy-injected gate.
- **FR-003**: The resolution MUST ensure **every node in a composed, executed workflow has a real executor binding**: no policy step (notably today's `rubberduck`) may be injected as a node that `RunWorkflowGraphBinder` cannot bind, which currently throws "no MAF binding" at build time.
- **FR-004**: **RESOLVED (Option B).** The **workflow's `review` (human) node is the single human gate** and the single source of truth for human accountability on the default path. A policy `human-review` step is **absorbed onto** an existing workflow human gate (deduped, never duplicated) and is **injected only** when the target workflow has no human gate. The default workflow's baked-in `review` node is retained unchanged and is never orphaned.
- **FR-005**: **RESOLVED.** **Option B (composition-as-identity) is adopted** (Clarifications, Session 2026-06-23). Option A is recorded (FR-006) but not selected. The binding contract, precedence order, dedupe rule, and default-identity guarantee are specified in the **Composition Contract** section above.

**The two options (to be selected at clarify)**

- **FR-006**: **RECORDED, NOT ADOPTED.** Option A (policy authoritative) — the default workflow drops its baked-in gates and the review policy becomes the single source of truth — was considered and **rejected** because it would remove the always-on human gate from existing runs (a behavior change). It is retained here for traceability; its sign-off/migration question is therefore moot.
- **FR-007**: **ADOPTED.** Under **Option B (composition-as-identity)**, composing the default policy onto the default workflow MUST be a **provable no-op / identity** that yields exactly current Stage-1 behavior — the workflow's human gate is kept as-is, and `default-in` equals `default-out` — while the composition binder is **generalized** so non-default policies can still layer additional behavior on top of any workflow (see Composition Contract).
- **FR-008**: Whichever option is chosen, **non-default review policies MUST still be able to layer additional gating** onto a workflow (the generalization that justified having both mechanisms), with each layered step bound to a real executor and no gate duplicated (US3).

**Parity & verification**

- **FR-009**: The composition of the **default workflow + default review policy** MUST be **testable**, and the system MUST include a **golden parity test** that composes `DefaultReviewPolicyTemplate` onto `DefaultWorkflowTemplate` and compares the effective definition (and its bound executor graph) against the captured Stage-1 default run pipeline.
- **FR-010**: The golden parity test MUST assert **zero behavioral delta** for the default case (the composed default is semantically identical to the Stage-1 default — same nodes, edges, ordering, verdict routing, and executor bindings), reflecting the adopted Option B.
- **FR-011**: The golden parity test MUST run in CI as a **drift guard**: any later change to the default workflow or default policy that alters the composed default MUST fail the build loudly rather than silently re-gating existing runs (preserving the spirit of the existing `RunWorkflowGraphBinder` drift guard).
- **FR-012**: The verification MUST use the **real composition and real executors** (no mocks/fakes/placeholders, Principle VII): the parity assertion is over the actual `ReviewPolicyComposer` output and the actual binder wiring, not a stand-in.

**Binder generalization & safety (cross-cutting)**

- **FR-013**: **RESOLVED.** The composition binder MUST be **generalized** so the mapping from a review-policy step kind to an executor is explicit and complete (see the Composition Contract binding table): `rai`, `human-review`, and `rubberduck` each MUST resolve to a real executor — Stage 2 adds the **Rubberduck executor binding** that was missing. `rubberduck` is **removed from the default policy** (it never ran in Stage-1) so the default composition stays identity, while remaining available to non-default policies. Any **unsupported** step kind MUST produce a clear, actionable **validation error naming the kind** and MUST NOT be injected as an unexecutable node.
- **FR-014**: The resolution MUST NOT weaken safety guarantees: the **human-approval gate for irreversible actions**, sandbox boundaries, step/time limits, and the audit trail remain enforced by the runtime/governance layer (Microsoft Agent Framework, .NET 10) regardless of which option is chosen (Principles X, XI). Every run MUST remain attributable to an accountable human (Principle IX).
- **FR-015**: The effective (composed) gating of a run MUST be **observable identically from the MCP server and the Web UI** (Principles III, IV), with all composition performed server-side and no business logic in either client.
- **FR-016**: No shipped surface produced by this feature (composed definitions, gating displays, validation messages, logs, UI) may contain emojis (Principle VIII).

**Composition contract (resolved — precedence, dedupe, identity)**

- **FR-017**: Composition MUST honor the **precedence order**: (1) runtime governance guarantees, (2) workflow structural backbone, (3) review-policy overlay. A policy overlay MUST be **additive-only** — it MAY inject gates the workflow lacks but MUST NEVER remove, reorder, or duplicate a workflow gate.
- **FR-018**: Composition MUST deduplicate by **gate-kind key**: every workflow gate node and every policy step carries an explicit gate-kind key (`rai`, `human-review`, `rubberduck`, ...). For each policy step, if the workflow already has a pre-merge gate of that kind the step MUST be **absorbed** (no injection); otherwise it MUST be **injected** pre-merge in declared order. There MUST be **exactly one** gate per gate-kind on any composed pre-merge path.
- **FR-019**: The canonical default review policy MUST be **realigned to `[rai, human-review]`** so that, composed onto the default workflow, both steps are absorbed and the effective workflow equals the Stage-1 default node-for-node, edge-for-edge, executor-for-executor (the default-identity guarantee).
- **FR-020**: A **one-time, idempotent migration normalizer** MUST reconcile any existing project whose materialized default policy still equals the **old** canonical `[rai, rubberduck]` and is bound as the project default, rewriting it to the new canonical `[rai, human-review]`; user-customized policies MUST be left untouched, and materialized files MUST NOT be clobbered outside this explicit normalization.
- **FR-021**: Custom (non-default) workflows MUST be unaffected by the overlay beyond additive, deduplicated gate injection; a workflow with no merge node MUST compose as a no-op, and the binder's drift guard (throw on an unmapped edge/node) MUST be retained.

### Key Entities *(include if feature involves data)*

- **Default Workflow**: The canonical, code-embedded run graph (`apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs`, resolved via `WorkflowRegistry.cs` / `BuiltInWorkflows.cs`): linear `agent -> rai -> review (human) -> merge -> scribe -> terminal` with gating baked into the definition. Bound onto live executors by `RunWorkflowGraphBinder.cs`, which recognizes exactly the five logical nodes and throws on any unmapped edge/node.
- **Default Review Policy**: The canonical, code-embedded review policy (`apps/Agentweaver.Api/ReviewPolicies/DefaultReviewPolicyTemplate.cs`): an ordered step list `rai` then `rubberduck`, with human review as an opt-in step deliberately excluded from the default.
- **Review Policy Composer**: The pure graph transform (`apps/Agentweaver.Api/ReviewPolicies/ReviewPolicyComposer.cs`) that injects a policy's review steps as new gate nodes immediately before a workflow's merge node, re-pointing edges that fed `merge`. Today this is the locus of the three conflicts when the default policy is composed onto the default workflow.
- **Composition (effective workflow)**: The result of composing a policy onto a workflow — the effective gating a run actually receives. Must be single-gated, fully executable, unambiguous about the human gate, and (under Option B) identical to the Stage-1 default for the default case.
- **Golden Parity Test**: The verification artifact that composes the default policy onto the default workflow and asserts the result is identical to the captured Stage-1 default (zero delta under the adopted Option B). Runs in CI as a drift guard.
- **Run Workflow Graph Binder**: The component (`RunWorkflowGraphBinder.cs`) that maps a workflow definition's logical nodes/edges onto real MAF executors. Its drift guard (throw on unmapped edge/node) is why an executor-less injected step currently fails the build; its generalization is part of this feature.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For the default workflow + default review policy, a composed run is gated by RAI **exactly once** — 0 double-gated RAI occurrences across all tested default runs.
- **SC-002**: 100% of nodes in any composed, executed workflow resolve to a real executor — **0** build-time "no MAF binding" failures caused by an injected policy step (today's executor-less `rubberduck`).
- **SC-003**: For every composed run, the human-review gate's presence and single source of truth are unambiguous and observable — 0 runs where the human gate's origin is undefined.
- **SC-004**: A golden parity test for the default composition exists and passes, asserting **zero** behavioral delta against the Stage-1 default (adopted Option B) — verified in CI on every build.
- **SC-005**: Non-default review policies can layer additional gating onto a workflow with each layered step executable and no gate duplicated — demonstrated end to end for at least one non-default policy without re-authoring the workflow.
- **SC-006**: The effective composed gating of a run is reported identically from the MCP server and the Web UI — 0 discrepancies between the two clients (Principle IV).
- **SC-007**: No safety guarantee is weakened by the resolution: in 100% of tested compositions the human-approval gate for irreversible actions and the audit trail remain enforced regardless of the chosen option (Principles IX, X, XI).

## Assumptions

- "Default workflow" is the code-embedded `DefaultWorkflowTemplate` resolved through `WorkflowRegistry` / `BuiltInWorkflows`; "default review policy" is the code-embedded `DefaultReviewPolicyTemplate` (`rai` then `rubberduck`, human review opt-in). These are the Stage-1 (Feature 010) artifacts this Stage-2 work composes.
- The composition mechanism is the existing `ReviewPolicyComposer.Compose`, which injects policy steps before the merge node; this feature generalizes/corrects how that composition interacts with a workflow that already bakes in gates, rather than introducing a new composition engine.
- The executor binding is the existing `RunWorkflowGraphBinder`, whose drift guard throws on unmapped edges/nodes; the executor-less `rubberduck` step is a real consequence of that guard, not a hypothetical.
- "Stage-1 behavior" is the current default run pipeline (`agent -> rai -> review -> merge -> scribe -> terminal`) as bound today; the adopted Option B guarantees the default composition equals this exactly, provable via a golden parity test (`default-in == default-out`).
- Parity is defined as preserving actual Stage-1 **run** behavior: Stage-1 runs always executed RAI then the human `review` gate and **never** executed rubberduck (no executor existed), so the parity-correct default effective gating excludes rubberduck and includes the human gate.
- Workflows, review policies, and their materialization are owned by Feature 010 (`specs/010-yaml-workflows-review-policies`); this feature composes/corrects their interaction and does not re-specify their internals.
- The runtime/governance layer (Microsoft Agent Framework, .NET 10) remains the enforcement point for sandbox, limits, human-approval, and audit; no workflow or policy may relax those guarantees (Principles X, XI).

## Resolved Decisions

All four open questions are resolved (Clarifications, Session 2026-06-23) and reflected in the Composition Contract, Migration & Compatibility, and the updated requirements:

1. **Option A vs Option B (FR-005)** — RESOLVED: **Option B (composition-as-identity) adopted.** Workflow is the structural source of truth; the review policy is an additive, deduplicated overlay; default-on-default composes to identity, proven by a golden parity test.
2. **Option A sign-off & migration (FR-006)** — MOOT: Option A not adopted. Migration for the adopted path is specified in **Migration & Compatibility** (idempotent normalizer; no clobbering; previously-broken default+rubberduck runs are fixed).
3. **Rubberduck / unsupported step kinds (FR-003 / FR-013)** — RESOLVED: Stage 2 adds a **real Rubberduck executor**; rubberduck is **removed from the default policy** (kept available to non-default policies); any unsupported kind **fails validation** naming the kind and is never injected unbound.
4. **Human-gate single source of truth (FR-004)** — RESOLVED: the **workflow `review` node is the single human gate**; a policy `human-review` step is absorbed onto it (deduped) and injected only when the workflow has no human gate.
