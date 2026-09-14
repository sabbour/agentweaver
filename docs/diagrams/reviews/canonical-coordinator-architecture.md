# Coordinator architecture: draw.io pilot review

**Visual result:** one editable, uncompressed A5 landscape page, exported and inspected
with draw.io Desktop **31.4.5**. **Workflow status: blocked only on the shared inventory
claim/update permission**, described below. No commit was created.

Author and all three independent research agents: **GPT-6 Astra (`gpt-6-astra`)**.
Research baseline: `f10738018240f47865b98fc7023113a5dc2e8754`, plus the current worktree
implementation/configuration/tests and written documentation. Old diagrams were legacy
visual references, not architectural evidence.

## Artifact and scope contract

- Editable source: `docs/diagrams/src/canonical-coordinator-architecture.drawio`.
- Stable image: `docs/diagrams/canonical-coordinator-architecture.png`.
- Export stamp: `docs/diagrams/canonical-coordinator-architecture.hash.txt`.
- Page: 827 x 583 draw.io units, A5 landscape at 100 units/inch, rounded to whole units.
- PNG: 1664 x 1172, repository recipe `--border 16 --scale 2`.
- Consumers unchanged: `docs/deep-dive/00-system-overview.md`,
  `docs/deep-dive/orchestration.md`, `docs/deep-dive/frontend.md`.

Existing Markdown image paths, alt text and already-accurate draw.io provenance comments
are preserved. No product UI, runtime, workflow definition, pipeline, skill, unrelated
diagram or inventory-wide report was edited. A single-diagram review is sufficient
because the canonical identity and all three consumers remain unchanged.

The subject is logical responsibility, not a deployment map: model-assisted planning
hands persisted work to service-driven dispatch, child outputs are integrated through
dispatch, and collective gates own the combined outcome. Direct mode still coordinates;
children have separate worktrees; merged success is not guaranteed; no leaf-host-to-DB
connection is implied.

## Grounding and final every-arrow trace

The three bounded research threads covered (1) components/durability/deployment,
(2) ingress/execution/result/revision direction, and (3) complete React visual-language
extraction plus native draw.io notation and rights. All were read-only, separately
launched GPT-6 Astra agents.

Paths in the following table are relative to `apps/Agentweaver.Api/`.
Every listed arrow was traced source-to-target on the final PNG. All are **clean**:
correct direction/head, card attachment rather than group attachment, readable label,
unobstructed orthogonal route, and correct crossing/junction semantics.

| Arrow | Relationship | Implementation evidence |
| --- | --- | --- |
| e-start | Entry points -> Coordinator: start parent run | `Endpoints/ProjectEndpoints.cs:1473-1494`; `Coordinator/CoordinatorPickupService.cs:186-246` |
| e-handoff | Coordinator -> Dispatch: persisted WorkPlan handoff | `Coordinator/CoordinatorRunService.cs:1172-1188,1241-1248` |
| e-launch | Dispatch -> Child runs: launch dependency-ready work | `Coordinator/CoordinatorDispatchService.cs:384-427,870-906` |
| e-output | Child runs -> Collective assembly: usable outputs via dispatch | `Runs/RunWorkflowFactory.cs:791-816`; `Coordinator/CoordinatorDispatchService.cs:977-989` |
| e-plan-state | Coordinator -> Durable state: persist spec/plan | `Coordinator/CoordinatorWorkflowFactory.cs:103-116`; `Coordinator/CoordinatorOrchestratorExecutor.cs:1860-1911` |
| e-review-state | Collective assembly -> Durable state: record aggregate review | `Coordinator/CoordinatorAssemblyService.cs:1195-1248` |
| e-dispatch-state | Dispatch -> Durable state: persist execution status | `Coordinator/CoordinatorDispatchService.cs:977-989` |
| e-state-recovery | Durable state -> Dispatch: recover persisted plan | `Coordinator/CoordinatorReconciler.cs:117-133`; `Coordinator/CoordinatorDispatchService.cs:615-651` |
| e-revision | Collective assembly -> Coordinator: review feedback enters steering | `Coordinator/CoordinatorAssemblyService.cs:2320-2368` |

Additional node/boundary checks: `Runs/RunOrchestrator.cs:301-329` (per-child worktrees);
`Sandbox/RemoteWorkflowAgentFactory.cs:9-24` (remote leaf versus application-owned graph/
checkpoints); `Program.cs:703-715,1026-1075` (configurable remote execution and persistence);
`Coordinator/CoordinatorWorkflowFactory.cs:249-308` (Direct mode);
`Coordinator/CollectiveAssemblyPipeline.cs:439-509` (merge attempt and Scribe).
Test evidence includes `tests/Agentweaver.Tests/Coordinator/CoordinatorOutcomeSpecTests.cs:398-422`,
`CoordinatorLeaseHeartbeatTests.cs:74-119`, and `CoordinatorEventPersistenceTests.cs:48-105`
in that same tests directory.

## Visual language and native-symbol credits

Preserved warm Fluent paper/card surfaces and ink/strokes, Segoe UI, Consolas metadata,
16 px rounded cards, 5 px semantic accents, restrained shadows, icon/title/subtitle/
metadata/pill hierarchy, fixed lavender/teal/green/marigold/neutral pairs, tiered grouping,
orthogonal rounded routing, label backgrounds and packed lanes. The state-lane crossing
uses a true native arc bridge; no junction dots are invented. Only review/steering
revision uses the dashed marigold outer rail.

Product frames are `custom:agentweaver`. Embedded notation is `native:c4` person,
`native:uml` component/folder, `native:flowchart` document/decision/predefined process,
`native:database` cylinder, and `native:cloud` optional remote host. Native glyphs are
editable library shapes, not raster recreations or inferred product-resource identities.

Visual references: `docs/diagrams/drawio/design-system.json`, `fluent-template.drawio`,
`fluent-library.xml`, and legacy React `theme.ts`, `nodes.tsx`, `edges.tsx`,
`DiagramCanvas.tsx` at the research baseline. The supplied template/library were loaded;
sample cells and its invisible metadata vertex were removed.

Official notation sources are draw.io's `Sidebar-C4.js`, `Sidebar-UML25.js`,
`Sidebar-Flowchart.js`, and built-in shape definitions. Draw.io code is Apache-2.0;
shape/stencil assets have separate terms with an exception for end-user diagram exports.
See [draw.io licensing](https://github.com/jgraph/drawio/blob/f3abfe0f082c18f7b4fee8a34c2d07b1987687fd/LICENSE)
and [stencil terms](https://github.com/jgraph/drawio/blob/f3abfe0f082c18f7b4fee8a34c2d07b1987687fd/src/main/webapp/stencils/LICENSE).
No external logos, raster assets or stencil collections were downloaded or redistributed.

## Iteration evidence

| Stage | Actual exported PNG inspection and changes |
| --- | --- |
| Pitch | Three source-grounded responsibility units; actual PNG opened at print scale and enlarged. |
| Pass 1 | Complete visual upgrade: **3,162 -> 28,462**, **9.001265x**, metric `visible-semantic-canonical-xml-v1`; 8 -> 96 visible structures. Zero invisible/off-page/duplicate/padding findings. |
| Pass 2 | Correction only: restore clipped native subprocess sidebars on assembly/children icons. No content or composition change. |
| Pass 3 | Correction-only inspection found no remaining defect; distinct source and fresh PNG saved and opened. |
| Pass 4 | Correction-only inspection plus complete **9/9 arrow trace**; distinct source and fresh PNG saved and opened. Zero remaining orientation, overlap or arrow defects. |

Each pass includes independently saved draw.io, PNG, A5-size inspection raster and change
record. The failed pass-1 preflight export is retained, not misreported as passing.
The final two no-change passes are source- and pixel-identical to the corrected pass 2.

Session review artifact directory:

```text
C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\diagram-pilot\reviews\canonical-coordinator-architecture
```

It contains `research-and-contract.md`, the pitch and all numbered pass triples,
`iteration-manifest.json`, `validation-audit.json`, and the full pass-4 route trace.
Iteration artifacts remain outside the repository by request.

## Validation and protocol status

Focused pipeline tests: **18 passed**. Pitch/iteration validator regression tests:
**19 passed**. JSON Schema Draft 2020-12 validation and the skill's iteration-manifest
validator pass. XML checks verify one A5 page, unique IDs, visible in-page structures,
growth, all expected source/target pairs, and revision-only dashed styling. Selective
render uses the pinned Desktop executable and writes the standard source/XML/PNG stamp.
`npm run docs:check-diagrams -- --spec canonical-coordinator-architecture` passes.
The published source matches pass 4 byte-for-byte, and the selectively re-exported PNG
matches its inspected pixels exactly. `npm run docs:build` passed in 69.87 seconds
(non-blocking dependency `"use client"`, chunk-size and `promql` highlighting warnings).
Inventory integrity validation passes for all 149 discovered entries without writes.

The inventory exact-name lookup confirms every stable path. Its canonical-area shard
still records `unreviewed` and no owner. The required `--set --owner`/final-disposition
write would modify `docs/diagrams/drawio/inventory/areas/canonical.json`, outside the
explicit exclusive write paths. That shared file was deliberately not changed.
Pilot-local owner: session `f6a87a42-fc18-4c23-a5f5-3e1c5c41d367`;
intended disposition: `redesign`, reviewed. This local record is not a substitute
for the shared claim. Therefore the visual/technical deliverable is complete, but
strict inventory-protocol completion remains blocked on that scoped write.

## Catalog completion addendum

The later sole-agent catalog-completion request authorized the shared inventory
reconciliation and narrow docs-only pilot compatibility correction. The inventory
ownership blocker above is resolved; the historical account is retained unchanged.
The original session evidence has also been copied intact to
`canonical-coordinator-architecture/original-pilot/` for durable catalog validation.

The public source now has explicit roles, scaled A5 metadata and nine
`classicThin`, size-8 arrow markers. All cell copy, geometry, endpoints and routes
are unchanged. An immutable, diagram-specific source fingerprint protects the
approved optical layout; regression tests reject mutations and attempts to reuse
the profile elsewhere. No runtime behavior changed.

The original and corrected exports were individually inspected and all nine
arrows traced again. The transparent marker re-export changes the content crop
from 1664 x 1172 to 1668 x 1172. Card interiors have maximum RGB channel difference
2/255, not exact pixel identity. The measured result, immutable originals and
corrected inspected PNG are in `catalog-integration/pilot-compatibility/`.
Full-catalog render and drift now pass for 121 public diagrams. The authoritative
completion evidence is `catalog-integration/integration-result.json`.
