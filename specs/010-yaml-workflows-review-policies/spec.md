# Feature Specification: YAML-Authored Workflows & Per-Project Review Policies

**Feature Branch**: `010-yaml-workflows-review-policies`

**Created**: 2026-06-22

**Status**: Draft

**Input**: User description: "I want to be able to define workflows in YAML and have them loaded from .scaffolders/workflows/ . Team templates should come with pre-defined workflows that align with the work to be done. Start by converting the current template we have into a predefined workflow. The template semantics should allow for dynamic composition by the coordinator, prompt, peer-review, fan out/fan in, serial execution, checks, and different triggers like manual, heartbeat schedule, event. RAI step, Rubber-duck step, Human-review step, are all part of a concept of 'Review Policies' that apply per project."

## Overview

Today the work a run performs is a **graph wired in C# code**. The per-run pipeline (`agent-input-storer -> agent -> rai -> review-gate -> merge -> scribe -> terminal`, with an RAI revision loop and a human-review request gate) is constructed imperatively in `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` (`BuildWorkflow(bool isChild)`, lines 203-604). The coordinator's planning graph (`draft -> confirmation gate -> finalize | revise-loop`) is likewise hardcoded in `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs` (lines 105-171). The canonical stage order `agent, rai, review, merge, scribe` is a hardcoded array in `packages/Agentweaver.AgentRuntime/Workflow/WorkflowStepEvents.cs:20`. Changing what a team's runs do therefore requires a code change and redeploy.

This feature makes the workflow **data-defined**. A workflow is authored in a YAML document, discovered and loaded from a project-local `.scaffolders/workflows/` directory, validated, and executed by the runtime as the project's effective run workflow. The YAML schema expresses the building blocks the system already has as executors and seams — **prompt** steps (an agent turn, `AgentTurnExecutor`), **peer-review** steps, **checks/gates** (the `RequestPort` human-review gate and RAI verdict gate), **fan-out / fan-in** (the coordinator's subtask `SubtaskFrontier` dispatch and `AssemblyPlanning` join), **serial execution**, **dynamic composition by the coordinator** (decomposition into subtasks), and **triggers** (manual pickup, heartbeat schedule, and event).

The **first deliverable** is a faithful, behavior-preserving conversion: the current hardcoded run workflow is expressed as a predefined YAML workflow that ships with team templates, so an out-of-the-box project runs exactly as it does today, but now from a declarative definition rather than from code.

A second, orthogonal concept introduced here is the **Review Policy**: RAI review, rubber-duck review, and human review are pulled out of being implicitly hardcoded into every workflow and modeled as named **review steps** that compose into a **Review Policy attached per project**. A project selects/configures a Review Policy; its review steps inject into the project's workflow runs at defined points. This maps onto the real seams that already exist: the RAI gate (`RaiTurnExecutor`, wired at `RunWorkflowFactory.cs:337-345`), the human-review `RequestPort` gate (`RunWorkflowFactory.cs:224-225`), the request-changes ("rubber-duck") loop back to the agent (`RunWorkflowFactory.cs:587-590`), and the collective assembly review/RAI/merge gates (`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs`, `AssemblyPlanning.cs`, `CollectiveAssemblyPipeline.cs`).

Consistent with the constitution, this is an API-first capability. Authoring/loading workflows, selecting/configuring review policies, and viewing the effective workflow MUST be reachable identically from the MCP server and the Web UI, with no business logic in either client (Principles III, IV). Workflow definitions govern how runs execute, so the runtime/governance layer (Microsoft Agent Framework, .NET 10) remains the enforcement point for sandbox boundaries, step/time limits, human-approval gates, and audit (Principles X, XI). No mocks, fakes, or placeholders, and no emojis in any shipped surface (Principles VII, VIII).

## Clarifications

### Session 2026-06-22

- (none yet — see `[NEEDS CLARIFICATION]` markers in Requirements and Open Questions below; resolve via `/speckit.clarify`)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Convert the current built-in workflow into an equivalent predefined YAML workflow (Priority: P1)

The team's existing, hardcoded run pipeline is expressed as a predefined YAML workflow that ships with the team templates. A project created from a template that carries this workflow runs end to end exactly as it does today: an agent turn produces a change, RAI reviews it (passing through, requesting a revision up to the iteration cap, or failing on a content-safety RED), a human-review gate approves / requests changes / declines, an approved change merges, and the scribe records the outcome. No behavior changes; only the source of the definition changes from C# to YAML.

**Why this priority**: This is the foundational, lowest-risk slice and the explicit starting point requested. It proves the YAML schema and interpreter can express the full existing pipeline (the strongest correctness test of the schema) and gives every other story a real, working baseline workflow to build on. Until the current behavior is reproducible from YAML, nothing else can safely replace the hardcoded graph.

**Independent Test**: Take a project whose effective workflow is the converted predefined YAML workflow and run it; verify the run passes through the same logical stages in the same order, makes the same decisions at the RAI gate, human-review gate, and merge, emits the same `workflow.step` stage stream (`agent, rai, review, merge, scribe`), and reaches the same terminal states as the current code-defined workflow for the same inputs.

**Acceptance Scenarios**:

1. **Given** a team template that ships a predefined YAML workflow equivalent to today's pipeline, **When** a project is created from it, **Then** the project's effective workflow is that YAML workflow and a run executes the same logical stages, in the same order, as the current hardcoded `RunWorkflowFactory` graph.
2. **Given** an agent turn that produces no changes, **When** the YAML workflow runs, **Then** it terminates on the same no-op path the current workflow uses (no review, no merge), with an equivalent terminal state.
3. **Given** an RAI verdict that requests a revision, **When** the YAML workflow runs, **Then** it loops back to the agent up to the same iteration cap and otherwise behaves identically to today.
4. **Given** an RAI content-safety RED verdict, **When** the YAML workflow runs, **Then** it fails safe to the same content-safety-failed terminal state as today, without proceeding to review or merge.
5. **Given** a human reviewer who requests changes, approves, or declines at the review gate, **When** the YAML workflow runs, **Then** each decision routes to the same next step (back to agent / to merge / to a declined terminal) as the current graph.
6. **Given** the converted workflow, **When** its stage stream is observed, **Then** the live `workflow.step` events and the per-run graph descriptor present the same logical nodes and order as the current build.

---

### User Story 2 - Author and load a workflow from `.scaffolders/workflows/` (Priority: P1)

A team member authors a workflow as a YAML file and places it in the project-local `.scaffolders/workflows/` directory. The system discovers it, validates it against the workflow schema, and makes it available as a selectable/effective workflow for the project. A malformed or invalid file is reported with a clear, actionable error and does not crash the project, take down other valid workflows, or silently run a partial/wrong graph.

**Why this priority**: Loading from `.scaffolders/workflows/` is the core capability the feature exists to provide — it is what lets a team change run behavior without a code change. It is independently valuable the moment one valid workflow can be discovered, validated, and selected, even before advanced node types are added.

**Independent Test**: Drop a valid workflow YAML into a project's `.scaffolders/workflows/`; confirm it is discovered, validated, and listed as available for the project. Drop a malformed YAML and a schema-invalid YAML; confirm each is reported with a clear error, is excluded from the available set, and does not prevent other valid workflows from loading.

**Acceptance Scenarios**:

1. **Given** a valid workflow YAML in `.scaffolders/workflows/`, **When** the project's workflows are loaded, **Then** the workflow is discovered, validated, and made available as a selectable workflow for that project.
2. **Given** a workflow YAML with a syntax error (unparseable YAML), **When** loading occurs, **Then** the system surfaces a clear error identifying the file and the problem, excludes that file, and continues loading the remaining valid workflows.
3. **Given** a workflow YAML that parses but violates the schema (unknown node type, missing required field, dangling edge, duplicate id), **When** loading occurs, **Then** validation fails with a specific, actionable message and the workflow is not made available.
4. **Given** a project with no `.scaffolders/workflows/` directory or an empty one, **When** workflows are loaded, **Then** the project falls back to its template-provided predefined workflow (or the built-in default) and a run can still execute.
5. **Given** both clients, **When** the available workflows for a project are listed from the MCP server and from the Web UI, **Then** both present the same set with the same validation status.

---

### User Story 3 - Compose the building blocks: prompt, peer-review, checks, serial, fan-out/fan-in, dynamic composition (Priority: P2)

A workflow author expresses real pipelines using the supported node types: **prompt** steps (an agent turn against a prompt), **peer-review** steps (one agent reviews another's output), **check** steps (gates/conditions that route based on a verdict or predicate), **serial** sequences (run steps one after another), and **fan-out / fan-in** (decompose into parallel subtasks, then join their results). The author can also mark a stage for **dynamic composition by the coordinator**, where the coordinator decomposes the work into subtasks at runtime rather than the author enumerating them statically.

**Why this priority**: These node types are what make YAML workflows expressive enough to be worth authoring beyond the converted baseline. They are P2 because Story 1's conversion already exercises prompt/check/peer-review/serial implicitly; this story makes them first-class, named, and recombinable, and adds explicit fan-out/fan-in and coordinator-driven composition on top of the existing `SubtaskFrontier`/`AssemblyPlanning` machinery.

**Independent Test**: Author a workflow that uses each node type — a prompt step, a peer-review step, a check that branches on a verdict, a serial sequence, and a fan-out into parallel subtasks followed by a fan-in join — and a stage flagged for coordinator dynamic composition. Run it and verify each node behaves per the schema: serial steps run in order, fan-out subtasks run in parallel and the fan-in waits for all of them, checks route correctly, and the coordinator-composed stage produces subtasks at runtime.

**Acceptance Scenarios**:

1. **Given** a workflow with a **prompt** node, **When** it runs, **Then** it performs an agent turn against the specified prompt and produces an output the next node consumes (mapping onto `AgentTurnExecutor`).
2. **Given** a workflow with a **serial** sequence of nodes, **When** it runs, **Then** the nodes execute strictly in declared order, each starting only after the previous one completes.
3. **Given** a workflow with a **fan-out** node listing N parallel branches/subtasks and a downstream **fan-in** node, **When** it runs, **Then** the branches are dispatched in parallel (subject to existing readiness/dependency rules) and the fan-in node does not proceed until all required branches reach an assemble-ready/eligible state, joining their results.
4. **Given** a workflow with a **peer-review** node, **When** it runs, **Then** a reviewing agent evaluates the target node's output and emits a verdict that a downstream check can route on.
5. **Given** a workflow with a **check** node, **When** the upstream verdict/predicate is evaluated, **Then** the run follows the matching outgoing branch (e.g., pass vs. request-changes vs. fail) and rejects a definition whose check has no branch for a possible verdict.
6. **Given** a stage marked for **dynamic composition by the coordinator**, **When** it runs, **Then** the coordinator decomposes the work into subtasks at runtime (reusing the existing decomposition path) rather than requiring the author to statically enumerate them, and the resulting subtasks fan out and fan in as in Scenario 3.

---

### User Story 4 - Trigger workflows manually, on a heartbeat schedule, and on events (Priority: P2)

A workflow declares how it is started. A **manual** trigger means a person (or client) explicitly starts a run, as today. A **heartbeat schedule** trigger means the coordinator picks up eligible work on its periodic heartbeat. An **event** trigger means the workflow starts in response to a defined event. The declared trigger determines when and how runs of that workflow begin, mapping onto the coordinator's existing heartbeat, pickup, and autopilot machinery.

**Why this priority**: Triggers determine when work actually starts and connect YAML workflows to the existing coordinator scheduling. P2 because the converted baseline (Story 1) already runs under the existing manual/heartbeat pathways; this story makes the trigger a declared, first-class part of the workflow rather than an implicit property of how it was launched.

**Independent Test**: Author three workflows differing only by trigger (manual, heartbeat schedule, event). Verify the manual one starts only on explicit start; the heartbeat one is picked up on a coordinator heartbeat when work is eligible; and the event one starts when its declared event occurs and not otherwise.

**Acceptance Scenarios**:

1. **Given** a workflow with a **manual** trigger, **When** no one starts it, **Then** no run begins; **When** a client explicitly starts it, **Then** exactly one run begins.
2. **Given** a workflow with a **heartbeat schedule** trigger and eligible work, **When** the coordinator heartbeat occurs, **Then** the work is picked up and a run begins on that heartbeat (mapping onto `CoordinatorHeartbeatService` / `CoordinatorPickupService`).
3. **Given** a workflow with an **event** trigger, **When** the declared event occurs, **Then** a run begins; **When** the event does not occur, **Then** no run begins.
4. **Given** a workflow whose trigger is unspecified or unsupported, **When** it is loaded, **Then** validation reports the problem and the workflow is not made available with an ambiguous trigger.

---

### User Story 5 - Configure a per-project Review Policy from RAI, rubber-duck, and human-review steps (Priority: P1)

Review behavior is no longer baked into each workflow. A project selects/configures a **Review Policy** composed of review steps — **RAI step**, **Rubber-duck step**, and **Human-review step** — and those steps inject into the project's workflow runs at the defined review point(s). Changing a project's Review Policy changes the review behavior of its runs without editing the workflow definition. The policy maps onto the seams that exist today: the RAI verdict gate, the request-changes loop, and the human-review `RequestPort` gate (and their collective-assembly equivalents).

**Why this priority**: Review is a governance- and safety-critical concern (Constitution Principles IX, X) and the user calls it out as a first-class per-project concept. Making it a selectable per-project policy — rather than hardcoded per workflow — is high-value and must be correct from the start, so it is P1 alongside the conversion and loading stories.

**Independent Test**: Configure two projects with different Review Policies (e.g., one with RAI + human-review, another adding rubber-duck) over the same workflow; run both and verify each run applies exactly the review steps in its project's policy at the correct point, that a human-review step still gates merge for irreversible actions, and that RAI still fails safe on a content-safety RED regardless of other policy choices.

**Acceptance Scenarios**:

1. **Given** a project with a Review Policy containing an RAI step, **When** a run reaches the review point, **Then** the RAI review executes (mapping onto `RaiTurnExecutor`) and its verdict routes the run (pass / request-revision / content-safety-fail) as today.
2. **Given** a project whose Review Policy includes a Human-review step, **When** a run reaches a merge or other irreversible action, **Then** a human-approval gate is required before it proceeds (mapping onto the `RequestPort` review gate), preserving the human-in-the-loop guarantee.
3. **Given** a project whose Review Policy includes a Rubber-duck step, **When** that review requests changes, **Then** the run loops back to the producing step with the feedback (mapping onto the existing request-changes-to-agent loop) rather than proceeding.
4. **Given** two projects with different Review Policies over the same workflow, **When** each runs, **Then** each applies only its own project's review steps, demonstrating that review behavior is per project and not embedded in the workflow.
5. **Given** a collective/coordinator assembly (fan-in of multiple subtasks), **When** the project's Review Policy applies, **Then** the same review steps apply at the assembly review point (mapping onto `CoordinatorAssemblyService` / `CollectiveAssemblyPipeline` RAI/review/merge gates), not only at the single-run level.
6. **Given** a project with no explicitly configured Review Policy, **When** a run executes, **Then** a safe default policy applies that at minimum preserves RAI content-safety failure and human approval for irreversible actions (no run executes with weaker safety than today's hardcoded behavior).

---

### User Story 6 - Author and select workflows and review policies identically from MCP and Web (Priority: P2)

Everything in this feature — listing available workflows, viewing a workflow's effective definition and validation status, selecting a project's effective workflow, and selecting/configuring a project's Review Policy — is reachable identically from the MCP server and the Web UI, with no business logic in either client. Both clients call the same API and present the same results.

**Why this priority**: Parity is a constitutional requirement (Principles III, IV) and applies to every capability, but it is captured as its own story so it is explicitly tested rather than assumed. P2 because it rides on the capabilities defined in the P1/P2 stories.

**Independent Test**: From each client in turn, list a project's available workflows, view a workflow definition with its validation status, select the project's effective workflow, and select/configure its Review Policy; verify both clients can perform every action and observe the same resulting state.

**Acceptance Scenarios**:

1. **Given** the API exposes workflow listing/selection and review-policy selection/configuration, **When** the same action is performed from the MCP server and from the Web UI, **Then** both succeed and yield the same resulting project state.
2. **Given** a project's effective workflow or Review Policy is changed from one client, **When** the other client views the project, **Then** it reflects the change without any client-side recomputation.
3. **Given** either client, **When** it attempts to embed workflow- or review-decision logic locally, **Then** the design MUST NOT permit it: all validation, composition, and policy resolution occur server-side and the clients only render API results.

---

### Edge Cases

- **Malformed YAML**: An unparseable workflow file MUST be reported with a clear, file-scoped error and excluded; it MUST NOT crash the project, abort loading of other valid workflows, or run a partial graph.
- **Schema-valid but semantically invalid**: A workflow that parses but is invalid (unknown node/trigger type, missing required field, duplicate node id, dangling/cyclic edge where cycles are disallowed, a check with an unhandled verdict branch) MUST fail validation with a specific message and be excluded.
- **No workflows present**: A project with no `.scaffolders/workflows/` directory (or an empty one) MUST fall back to its template-provided predefined workflow or the built-in default so runs still execute.
- **Changing a workflow definition while a run is in flight**: An in-flight run MUST complete on the workflow definition it started with; redefinition MUST NOT mutate a running graph mid-execution. `[NEEDS CLARIFICATION: are workflow files hot-reloaded on change, or loaded once at project/run start? If hot-reloaded, what is the reload trigger (file watch, explicit reload action, next heartbeat) and how are in-flight runs isolated?]`
- **Multiple workflows defined**: When `.scaffolders/workflows/` contains more than one valid workflow, how the project's *effective* workflow is chosen (single active selection, per-trigger binding, or named selection per Ready item) MUST be defined. `[NEEDS CLARIFICATION: how does a project choose its effective workflow when multiple valid workflows exist — a single per-project active selection, a binding per trigger, or a selection per work item?]`
- **Conflicting node ids across files**: Two workflow files declaring the same workflow id/name MUST be resolved deterministically (reject both, last-wins, or namespaced) rather than nondeterministically.
- **Review Policy references a review step the workflow has no point for**: If a project's Review Policy includes a review step but the effective workflow declares no compatible injection point, the system MUST resolve this predictably (inject at the default review point vs. reject) rather than silently dropping the review. `[NEEDS CLARIFICATION: where do Review Policy steps inject when the workflow does not explicitly declare review points — at a single implicit pre-merge review point, or only at author-declared points?]`
- **Trigger fires but no eligible work / no coordinator**: A heartbeat or event trigger that fires when there is no eligible work or no active coordinator MUST be a no-op, not an error or a dropped/duplicated run.
- **Event trigger semantics**: The set of events that may start a workflow, and their payloads, MUST be bounded and defined rather than arbitrary. `[NEEDS CLARIFICATION: which events are valid event triggers (e.g., run-completed, task-added-to-Ready, external webhook) and what is their payload contract?]`
- **Dynamic composition produces zero subtasks**: A coordinator-composed stage that decomposes into zero subtasks MUST resolve to a defined terminal/no-op outcome, not hang at the fan-in.
- **Sandbox / limits under YAML control**: A YAML workflow MUST NOT be able to weaken sandbox boundaries, step/time limits, human-approval-for-irreversible-action gates, or audit; the runtime/governance layer remains authoritative (Principles X, XI).
- **Workflow file outside the project sandbox**: Loading MUST only read workflow definitions from the project's own `.scaffolders/workflows/` location and MUST NOT follow references that escape the project sandbox.

## Requirements *(mandatory)*

### Functional Requirements

**Authoring, discovery & loading**

- **FR-001**: Workflows MUST be authorable as YAML documents and discovered from a project-local `.scaffolders/workflows/` directory.
- **FR-002**: The system MUST validate each discovered workflow against a defined workflow schema before making it available, and MUST make available only workflows that pass validation.
- **FR-003**: On a malformed (unparseable) workflow file, the system MUST surface a clear, file-scoped error, exclude that file, and continue loading the remaining valid workflows without crashing the project.
- **FR-004**: On a schema-invalid workflow (unknown node/trigger type, missing required field, duplicate id, dangling edge, disallowed cycle, unhandled check verdict), the system MUST fail validation with a specific, actionable message and exclude the workflow.
- **FR-005**: When a project has no `.scaffolders/workflows/` directory or it is empty, the project MUST fall back to its template-provided predefined workflow or the built-in default so runs still execute.
- **FR-006**: The loading behavior MUST be defined with respect to hot-reload vs. load-on-start, and an in-flight run MUST complete on the workflow definition it started with regardless of redefinition. `[NEEDS CLARIFICATION: hot-reload (file-watch / explicit reload / next heartbeat) vs. load-on-start, and the isolation contract for in-flight runs.]`
- **FR-007**: Workflow loading MUST read only from the project's own `.scaffolders/workflows/` location and MUST NOT follow references that escape the project sandbox (Principle X).

**Predefined workflows & template alignment (first deliverable)**

- **FR-008**: The current hardcoded run workflow (the `RunWorkflowFactory.BuildWorkflow` graph: agent turn, RAI gate with revision loop and content-safety fail, human-review gate, merge, scribe, and their terminal/no-op paths) MUST be expressible as, and converted into, an equivalent predefined YAML workflow that preserves today's behavior.
- **FR-009**: The converted predefined workflow MUST reproduce the current logical stage order and decisions, including the same `workflow.step` stage stream (`agent, rai, review, merge, scribe`, per `WorkflowStepEvents.cs:20`) and the same per-run graph descriptor nodes.
- **FR-010**: Team templates MUST be able to ship one or more predefined workflows aligned to the kind of work the template represents, and a project created from a template MUST adopt that template's predefined workflow(s) as available/effective.
- **FR-011**: An out-of-the-box project (using the converted predefined workflow with the default Review Policy) MUST run end to end with behavior equivalent to today's hardcoded pipeline.

**Workflow semantics (node/step & composition)**

- **FR-012**: The schema MUST define a **prompt** node that performs an agent turn against a specified prompt, mapping onto the existing `AgentTurnExecutor` agent-turn executor.
- **FR-013**: The schema MUST define a **serial** composition where nodes execute strictly in declared order, each starting only after the previous completes.
- **FR-014**: The schema MUST define **fan-out** (dispatch multiple parallel branches/subtasks) and **fan-in** (join that waits for all required branches to reach an assemble-ready/eligible state before proceeding), mapping onto the existing `SubtaskFrontier` dispatch and `AssemblyPlanning` join.
- **FR-015**: The schema MUST define a **peer-review** node in which a reviewing agent evaluates another node's output and emits a verdict.
- **FR-016**: The schema MUST define a **check** node (gate/condition) that routes the run along the branch matching an upstream verdict/predicate, and validation MUST reject a check that lacks a branch for a possible verdict.
- **FR-017**: The schema MUST allow a stage to be marked for **dynamic composition by the coordinator**, where the coordinator decomposes the work into subtasks at runtime (reusing the existing decomposition path) instead of the author statically enumerating them; the resulting subtasks MUST fan out and fan in per FR-014.
- **FR-018**: A coordinator-composed stage that yields zero subtasks MUST resolve to a defined terminal/no-op outcome rather than hanging at fan-in.
- **FR-019**: Node identifiers within a workflow MUST be unique, and edges MUST reference existing nodes; violations MUST fail validation (FR-004).

**Triggers**

- **FR-020**: The schema MUST support a **manual** trigger; a manually triggered workflow MUST begin a run only on an explicit start action and MUST NOT self-start.
- **FR-021**: The schema MUST support a **heartbeat schedule** trigger; eligible work for such a workflow MUST be picked up on the coordinator heartbeat, mapping onto `CoordinatorHeartbeatService` / `CoordinatorPickupService`.
- **FR-022**: The schema MUST support an **event** trigger; a run MUST begin when the declared event occurs and not otherwise. The set of valid events and their payload contract MUST be bounded and defined. `[NEEDS CLARIFICATION: enumerate the valid event types and their payload contracts.]`
- **FR-023**: A trigger that fires when there is no eligible work or no active coordinator MUST be a no-op (no error, no dropped or duplicated run).
- **FR-024**: A workflow with an unspecified or unsupported trigger MUST fail validation and MUST NOT be made available with an ambiguous trigger.

**Review Policies (per project)**

- **FR-025**: Review Policy MUST be a first-class, per-project concept composed of review steps, distinct from and not embedded in any individual workflow definition.
- **FR-026**: The supported review steps MUST include an **RAI step**, a **Rubber-duck step**, and a **Human-review step**, mapping respectively onto the RAI verdict gate (`RaiTurnExecutor`), the request-changes-to-producer loop, and the human-approval `RequestPort` gate.
- **FR-027**: A project MUST be able to select/configure its Review Policy, and changing it MUST change the review behavior of that project's subsequent runs without editing any workflow definition.
- **FR-028**: The review steps in a project's Review Policy MUST inject into the project's workflow runs at the defined review point(s), and the injection behavior when the workflow declares no explicit review point MUST be defined. `[NEEDS CLARIFICATION: implicit single pre-merge review point vs. author-declared review points only.]`
- **FR-029**: A Human-review step MUST gate merge and other irreversible actions on explicit human approval, preserving the human-in-the-loop guarantee for consequential actions (Principles IX, X).
- **FR-030**: An RAI step MUST preserve fail-safe behavior: a content-safety RED verdict MUST stop the run on the content-safety-failed path regardless of other policy configuration.
- **FR-031**: Review Policies MUST apply at the collective/coordinator assembly review point (fan-in of multiple subtasks), not only at the single-run level, mapping onto `CoordinatorAssemblyService` / `CollectiveAssemblyPipeline` review/RAI/merge gates.
- **FR-032**: A project with no explicitly configured Review Policy MUST receive a safe default policy that is no weaker than today's hardcoded behavior (at minimum RAI content-safety failure and human approval for irreversible actions).
- **FR-033**: How a project selects/stores its Review Policy (where the policy is defined and bound to the project) MUST be defined. `[NEEDS CLARIFICATION: is the Review Policy authored in `.scaffolders/` (e.g. `.scaffolders/review-policies/`), stored as a project setting via the API, or both, and is it selected by name or inlined per project?]`

**Governance, safety & parity (cross-cutting)**

- **FR-034**: A YAML workflow or Review Policy MUST NOT be able to weaken sandbox boundaries, step/time limits, the human-approval-for-irreversible-action gate, or the audit trail; these remain enforced by the runtime/governance layer (Microsoft Agent Framework, .NET 10) (Principles X, XI).
- **FR-035**: Runs executed from a YAML workflow MUST preserve run-step streaming and the auditable event log (agent messages, tool calls, tool results) exactly as today (Principles V, IX, X).
- **FR-036**: All capabilities in this feature — listing available workflows, viewing a workflow's effective definition and validation status, selecting a project's effective workflow, and selecting/configuring a project's Review Policy — MUST be reachable identically from the MCP server and the Web UI, with all validation, composition, and policy resolution performed server-side and no business logic in either client (Principles III, IV).
- **FR-037**: The effective workflow used to render derived surfaces (e.g., the Kanban board columns of Feature 009) MUST be the resolved YAML/predefined workflow, so those surfaces stay consistent with what actually executes.
- **FR-038**: No shipped surface produced by this feature (definitions, generated docs, validation messages, logs, UI) may contain emojis (Principle VIII), and no part of the implementation may use mocks, fakes, stubs, or placeholders (Principle VII).

### Key Entities *(include if feature involves data)*

- **Workflow Definition**: A declarative, YAML-authored description of a run pipeline, identified by a stable id/name, discovered from `.scaffolders/workflows/`. Composed of typed **nodes/steps** (prompt, peer-review, check, serial sequence, fan-out, fan-in, coordinator-composed stage) connected by edges, plus a declared **trigger**. Validated before use; the source of the project's effective run graph.
- **Workflow Node / Step**: A typed unit within a Workflow Definition. Types: **prompt** (agent turn → `AgentTurnExecutor`), **peer-review** (reviewing agent emits a verdict), **check** (gate/condition routing on a verdict/predicate), **fan-out** (parallel dispatch → `SubtaskFrontier`), **fan-in** (join → `AssemblyPlanning`), **coordinator-composed** (runtime decomposition into subtasks). Carries render metadata equivalent to today's `IWorkflowNodeMeta` (logical id, label, role, node type, kind).
- **Trigger**: The declared start condition of a workflow — **manual** (explicit start), **heartbeat schedule** (coordinator heartbeat pickup → `CoordinatorHeartbeatService`/`CoordinatorPickupService`), or **event** (defined event with a bounded payload contract).
- **Predefined Workflow**: A Workflow Definition shipped with a team template, aligned to the template's kind of work. The first one is the behavior-preserving conversion of today's hardcoded `RunWorkflowFactory` graph.
- **Review Policy**: A first-class, per-project configuration composed of **review steps** — **RAI step** (`RaiTurnExecutor`), **Rubber-duck step** (request-changes loop), **Human-review step** (`RequestPort` human-approval gate). Bound to a project (not to a workflow); injected into the project's runs at defined review points, including the collective-assembly review point. A safe default applies when none is configured.
- **Effective Workflow (per project)**: The resolved Workflow Definition a project actually runs — selected from its available workflows (template-provided and `.scaffolders/workflows/`), with the project's Review Policy injected. The single source of truth for run execution and for derived views (e.g., Feature 009's board columns).
- **Validation Result**: The per-file outcome of discovering and validating a workflow (valid / invalid with a specific, actionable, file-scoped message), surfaced identically to both clients.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For identical inputs, a run executed from the converted predefined YAML workflow reaches the same terminal states and emits the same logical stage stream and decisions as the current hardcoded pipeline in 100% of the covered scenarios (no-op, RAI revision loop, content-safety fail, request-changes, approve-and-merge, decline).
- **SC-002**: A valid workflow dropped into `.scaffolders/workflows/` becomes available for the project, and a malformed or schema-invalid one is reported with a clear file-scoped error and excluded, without affecting other valid workflows — verified for 100% of the malformed/invalid cases tested.
- **SC-003**: A team can change what a project's runs do by editing/selecting a YAML workflow with no source-code change and no redeploy, demonstrated by changing the effective workflow and observing the run behavior change accordingly.
- **SC-004**: Changing a project's Review Policy changes that project's review behavior (which review steps run and where) without editing any workflow definition, while two projects with different policies over the same workflow each apply only their own policy.
- **SC-005**: In 100% of tested runs, RAI content-safety RED still fails safe and merge/irreversible actions still require human approval, regardless of workflow or policy configuration (no run executes with weaker safety than today).
- **SC-006**: Every workflow/review-policy capability is performed successfully and yields identical resulting state from both the MCP server and the Web UI (0 capabilities reachable from only one client).
- **SC-007**: A workflow using serial, fan-out/fan-in, prompt, peer-review, check, and coordinator-composed nodes executes with the declared semantics — serial steps strictly ordered, fan-out parallel, fan-in waits for all required branches — verified by observing the run's step stream and graph descriptor.
- **SC-008**: Each trigger type behaves as declared: manual workflows start only on explicit start, heartbeat workflows are picked up on a heartbeat when work is eligible, and event workflows start only on their declared event — 0 spurious or duplicated runs across the tested cases.

## Assumptions

- "The current template/workflow" to convert (FR-008/FR-009) refers to the per-run graph built in `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` (`BuildWorkflow`, lines 203-604), whose canonical stage order is `agent, rai, review, merge, scribe` (`packages/Agentweaver.AgentRuntime/Workflow/WorkflowStepEvents.cs:20`). The coordinator's planning graph (`CoordinatorWorkflowFactory.cs:105-171`) is related but secondary; its conversion is in scope only insofar as fan-out/fan-in and dynamic composition (Story 3) build on the existing `SubtaskFrontier`/`AssemblyPlanning` machinery.
- The node types map onto existing executors and seams rather than introducing a parallel runtime: prompt → `AgentTurnExecutor`; peer-review/RAI → `RaiTurnExecutor`; check/human-review → the `RequestPort` review gate; fan-out/fan-in → `SubtaskFrontier` + `AssemblyPlanning`; merge/scribe → `MergeExecutor`/`ScribeTurnExecutor`. The Microsoft Agent Framework (.NET 10) remains the runtime and governance layer (Principles I, XI).
- "Project" is the Feature 003 project container; `.scaffolders/workflows/` is project-local, analogous to the existing project-local `.agentweaver/settings.yml` sandbox-policy file (`apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs`).
- "Team templates" are the existing catalog team templates loaded by `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`; this feature lets such templates carry one or more predefined workflows. No `.scaffolders/` directory exists in the repo today, so the loader is new.
- The model provider remains fixed to GitHub Copilot (Principle II); YAML may influence prompts/roles/composition but MUST NOT select a different provider.
- The accountable human for a run is unchanged (Principle IX); YAML workflows and Review Policies do not remove human accountability or the human-approval gate for irreversible actions.
- This feature defines how workflows are authored, loaded, composed, triggered, and reviewed; it does not redefine how individual executors (agent turn, RAI, merge, scribe) do their internal work — those are referenced, not respecified.

## Open Questions (for `/speckit.clarify`)

These correspond to the `[NEEDS CLARIFICATION]` markers above:

1. **Load-on-start vs. hot-reload** (FR-006, Edge Cases): Are workflow files loaded once at project/run start, or hot-reloaded on change? If hot-reloaded, what triggers reload (file watch, explicit reload action, next heartbeat), and how are in-flight runs isolated?
2. **Effective-workflow selection with multiple files** (Edge Cases): When `.scaffolders/workflows/` holds several valid workflows, how is the project's effective workflow chosen — a single per-project active selection, a binding per trigger, or a selection per work item?
3. **Event-trigger catalog** (FR-022): Which events are valid event triggers (e.g., run-completed, task-added-to-Ready, external webhook), and what is each event's payload contract?
4. **Review-step injection points** (FR-028, Edge Cases): Where do Review Policy steps inject when the workflow declares no explicit review points — at a single implicit pre-merge review point, or only at author-declared points?
5. **Review Policy storage/binding** (FR-033): Is a Review Policy authored under `.scaffolders/` (e.g., `.scaffolders/review-policies/`), stored as a project setting via the API, or both — and is it selected by name or inlined per project?
