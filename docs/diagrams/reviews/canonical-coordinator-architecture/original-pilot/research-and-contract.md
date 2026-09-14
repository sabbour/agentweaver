# Canonical coordinator architecture: grounded pilot contract

Author/model: GPT-6 Astra (`gpt-6-astra`), session f6a87a42-fc18-4c23-a5f5-3e1c5c41d367.
Repository: `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`.
Research baseline HEAD: `f10738018240f47865b98fc7023113a5dc2e8754`.

## Scope and inventory

Audience: contributors reading system overview, orchestration, and frontend internals.
Takeaway: model-assisted planning hands a durable work plan to service-driven dispatch;
child execution returns outputs for collective review, not independent child merges.

One A5 landscape sheet, 827 x 583 draw.io units (100 units/inch; rounded A5 210 x
148 mm). Landscape accommodates the planning/dispatch relationship and a separate
execution/output row without reducing responsibility text to tiny type.

Stable paths, confirmed by the inventory's exact-name selector:

- Source: `docs/diagrams/src/canonical-coordinator-architecture.drawio`
- PNG: `docs/diagrams/canonical-coordinator-architecture.png`
- Stamp: `docs/diagrams/canonical-coordinator-architecture.hash.txt`
- Final review: `docs/diagrams/reviews/canonical-coordinator-architecture.md`
- Area: canonical; intended disposition: redesign.
- Markdown consumers: `docs/deep-dive/00-system-overview.md`,
  `docs/deep-dive/orchestration.md`, `docs/deep-dive/frontend.md`.

The skill loader was invoked for both named skills but could not resolve worktree-only
skills. Both complete SKILL.md files, checklists, iteration schema, and validators were
read directly from the requested worktree before authoring.

**Protocol exception / unresolved permission conflict:** the current canonical inventory
entry is `unreviewed`, with no owner. The required `--set --owner` command writes
`docs/diagrams/drawio/inventory/areas/canonical.json`, outside the user's exclusive write
scope. It was not executed. Pilot-local ownership is recorded here as this session;
intended final status is reviewed/redesigned. This does not claim to update the actual
shared inventory. Strict skill completion is blocked until the inventory claim/status
write is authorized or performed by the coordinator. Do not silently treat this local
record as satisfying the shared claim.

Existing uncommitted migration files, including this pilot's previous non-A5 source and
review, are expected task inputs. Preserve their starting versions in session artifacts;
do not revert unrelated changes. No commit. A whole-catalog audit is unnecessary for a
single explicitly selected redesign with unchanged stable references.

## Exactly three independent GPT-6 Astra research threads

All three were separately launched as `research`, with explicit `model: gpt-6-astra`,
read-only bounded prompts, no nested agents, and no public upload of repository content.

### pilot-components: components, durability, deployment

- CoordinatorRunService creates a parent run named Coordinator, with no parent/subtask:
  `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:157-200`.
- Model-assisted planning and direct-mode workflow are distinct from service-driven
  dispatch/assembly: `Coordinator/CoordinatorWorkflowFactory.cs:158-207`;
  `apps/Agentweaver.Api/Program.cs:168-180`.
- Heartbeat is process-wide, not a per-project agent daemon:
  `Coordinator/CoordinatorHeartbeatService.cs:10-28,54-78,92-121,153-178`.
- Assembly uses a database compare-and-swap claim:
  `Coordinator/CoordinatorAssemblyService.cs:63-86`;
  `Coordinator/CoordinatorAssemblyStore.cs:31-46`.
- Durable entities include spec, plan, dependencies, events, review/steering records:
  `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:17-23,53-70`.
  PostgreSQL uses shared DB checkpoints; SQLite/dev uses file checkpoints:
  `apps/Agentweaver.Api/Program.cs:1026-1075`.
- Lease loss fences former dispatch owner; recovery reads persisted states:
  `Coordinator/CoordinatorDispatchService.cs:615-651`;
  `Coordinator/CoordinatorReconciler.cs:117-133`;
  `tests/Agentweaver.Tests/Coordinator/CoordinatorLeaseHeartbeatTests.cs:74-119`.
- API and worker hosts both register coordinator background services; do NOT depict
  exclusive worker ownership: `apps/Agentweaver.Api/Program.cs:518-528,1255-1273`;
  `k8s/base/api-deployment.yaml:87-98`; `k8s/base/worker-deployment.yaml:87-116`.
- Remote leaf-agent execution is configurable, and not a durable workflow engine:
  `apps/Agentweaver.Api/Program.cs:703-715`; `k8s/base/sandbox-template-agenthost.yaml:70-73,151-158`.

### pilot-relationships: ingress, execution, result and revision

- UI submits orchestration; MCP posts to the same API:
  `apps/web/src/components/StartOrchestrationDialog.tsx:87-110`;
  `apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-44`;
  `apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:1473-1494`.
- Ready backlog pickup claims/reserves before activation:
  `Coordinator/CoordinatorPickupService.cs:186-204,231-246`.
- Interactive drafting and confirmation/revision:
  `Coordinator/CoordinatorWorkflowFactory.cs:103-168`.
  Direct mode creates prompt-backed confirmed spec and uses the same planner:
  `Coordinator/CoordinatorWorkflowFactory.cs:249-308`;
  `tests/Agentweaver.Tests/Coordinator/CoordinatorOutcomeSpecTests.cs:398-422`.
- Planner selects workflow, decomposes, assigns actual team members/models and persists
  plan/subtasks/dependencies:
  `Coordinator/CoordinatorOrchestratorExecutor.cs:118-240,1860-1911`;
  `tests/Agentweaver.Tests/Coordinator/CoordinatorOrchestratorTests.cs:80-104`.
- Dispatch evaluates dependency readiness and overlapping/undeclared scopes, then
  starts assigned child runs:
  `Coordinator/CoordinatorDispatchService.cs:384-427,870-906`.
- Each child gets its own git worktree:
  `apps/Agentweaver.Api/Runs/RunOrchestrator.cs:301-329`.
- Agent runtime mediates tools, approvals, network/shell policy:
  `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:439-488`.
- Child terminates at assemble-ready or turn-failed; parent observes output and builds
  dependency integration base:
  `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:791-816`;
  `Coordinator/CoordinatorDispatchService.cs:977-989,1827-1857`.
- Aggregate human review is persisted; collective merge locks repository and may fail:
  `Coordinator/CoordinatorAssemblyService.cs:1195-1248`;
  `Coordinator/CollectiveAssemblyPipeline.cs:439-469`.
- Scribe runs for terminal outcomes, but memory curation is not a guarantee:
  `Coordinator/CollectiveAssemblyPipeline.cs:473-509`;
  `packages/Agentweaver.AgentRuntime/Workflow/ScribeTurnExecutor.cs:144-170,207-215`.
- Request-changes enters steering decider, not unconditional plan reset:
  `Coordinator/CoordinatorAssemblyService.cs:2320-2368`.
- Durable progress events:
  `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:183-204`;
  `tests/Agentweaver.Tests/Coordinator/CoordinatorEventPersistenceTests.cs:48-105`.
- Remote pods have no DB connection; graph/checkpoints remain in application runtime:
  `apps/Agentweaver.Api/Sandbox/RemoteWorkflowAgentFactory.cs:9-24`.

Paths beginning `Coordinator/` above are beneath `apps/Agentweaver.Api/`.

### pilot-visual-language: full legacy style and native symbols

Legacy React styling was read at HEAD because renderer files are deleted by the current
migration; it is a visual reference, never factual architecture evidence.

- `docs/diagrams/drawio/design-system.json:7-42`: warm canvas `#efeae7`;
  outer surface `#f8f4f1`; cards `#fdfbf8`; strokes `#ece7e3` and `#e2ddd9`;
  inks `#272320`, `#3f3935`, `#635c57`, `#746d68`.
- Semantic pairs: lavender `#d2ccf8/#3f3682`; teal `#a6e9ed/#00666d`;
  green `#9fd89f/#0e700e`; marigold `#f9e2ae/#835b00`;
  neutral `#e7e1dc/#635c57`.
- Legacy `docs/diagram-renderer/src/theme.ts:50-54`, `nodes.tsx:17-30,44-59,124-193`:
  Segoe UI/system sans, Cascadia Code/Consolas metadata; 16 px radius, 5 px accent,
  restrained shadows, icon/title/subtitle/metadata/pill hierarchy, tiered group titles.
- Legacy `edges.tsx:38,97-104,375-403,442-453`, `DiagramCanvas.tsx:38-67`:
  orthogonal rounded lines; label backgrounds; separated packed lanes; real arc bridges
  for crossings; dots only at true junctions; dashed marigold only revision/return.
- Current template/library loaded as XML starting assets. Replace sample payload and
  remove the template's invisible design-system cell: invisible content is forbidden.
- Native C4 person: `mxgraph.c4.person2`; UML component: `module`; document:
  `mxgraph.flowchart.document2`; subroutine: `process`; database: `cylinder3`;
  cloud: `cloud`. Do not use nonexistent inferred `mxgraph.c4.container`.

Official symbol sources: draw.io `Sidebar-C4.js:17-29`, `Sidebar-UML25.js:223-250`,
`Sidebar-Flowchart.js:11-45`, under
https://github.com/jgraph/drawio/tree/f3abfe0f082c18f7b4fee8a34c2d07b1987687fd/src/main/webapp
(public source checked by visual thread; pinned Desktop export is final compatibility check).
Draw.io code is Apache-2.0; current shape/stencil assets have separate restrictions with
an express exception for end-user diagram exports. See repository LICENSE and
`src/main/webapp/stencils/LICENSE`. Use packaged shape identifiers only; no third-party
logos downloaded or redistributed, no logo/trademark license inferred.

## Reconciled content model, before XML

Facts override stale coordinator-internals prose: per-child worktrees, provider-neutral
planning, configurable confirmation, no leaf pod-to-database arrow.
This is a logical responsibility diagram, NOT a deployment diagram. WorkPlan and
OutcomeSpec are product data, not independently active database cylinders.

| Node | Visible responsibility | Classification |
| --- | --- | --- |
| Entry points | Web/MCP through API; alternative Ready pickup; human control | custom:agentweaver frame; native:c4 person; native:uml API module |
| Coordinator | Draft/confirm or Direct; select workflow; assign team; save spec/plan | custom:agentweaver; native:flowchart document glyphs for spec and plan |
| Dispatch | Dependency-ready work; safe scopes; lease/recovery | custom:agentweaver; native:uml component |
| Child runs | Assigned agents; separate worktrees; governed tools; optional remote host | custom:agentweaver; native:flowchart subprocess; native:cloud optional remote execution glyph |
| Collective assembly | Usable branches; configured collective gates; merge attempt; Scribe | custom:agentweaver; native:flowchart subprocess |
| Durable state | Specs/plans, runs/events, review/steering, provider-specific checkpoints | native:database in custom:agentweaver card chrome |

| ID | Source -> target: verb | Grounding |
| --- | --- | --- |
| e-start | Entry points -> Coordinator: start | ProjectEndpoints:1473-1494; CoordinatorPickupService:186-246 |
| e-handoff | Coordinator -> Dispatch: plan handoff | CoordinatorRunService:1172-1188,1241-1248 |
| e-launch | Dispatch -> Child runs: launch ready work | CoordinatorDispatchService:384-427,870-906 |
| e-output | Child runs -> Collective assembly: usable outputs | RunWorkflowFactory:791-816; CoordinatorDispatchService:977-989 |
| e-plan-state | Coordinator -> Durable state: persist spec/plan | CoordinatorOrchestratorExecutor:1860-1911; CoordinatorWorkflowFactory:103-116 |
| e-review-state | Collective assembly -> Durable state: record review | CoordinatorAssemblyService:1195-1248 |
| e-revision | Collective assembly -> Coordinator: feedback / steering | CoordinatorAssemblyService:2320-2368 |

Arrows describe selected logical handoffs, not every internal call. Child outputs are
observed/integrated by dispatch before assembly; the output connector explicitly says
"via dispatch". The feedback arrow targets the broader Coordinator responsibility,
not a command to re-run its completed planning graph.

No true split/merge junction or unavoidable crossing is needed; do not add dots/bridges
as decoration. Dominant direction: left-to-right top row, down execution, right-to-left
output row. Return flow follows an outer marigold rail. Shared-state persistence crosses
the center gutter, never an execution card.
