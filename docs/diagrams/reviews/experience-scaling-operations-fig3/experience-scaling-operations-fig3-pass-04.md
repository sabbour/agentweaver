# experience-scaling-operations-fig3: pass-04

**Takeaway:** An expired owner can be replaced; continuation depends on the persisted run state.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [canonical-a2a-execution/research-execution.md](../canonical-a2a-execution/research-execution.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Correction-only review

No composition, content, or visual-language changes. Orientation, overlap, arrow endpoint/direction/routing checks found no permitted defect. Saved and re-exported a distinct source/PNG pair.

Reading direction, card/boundary separation, title/detail fit, label association and arrow direction reviewed at print size and enlarged. Zero remaining defects.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Worker A: `native:flowchart`.
- Lease expires: `native:database`.
- Worker B: `native:flowchart`.
- Visible failure: `native:flowchart`.
- Inspect run state: `native:flowchart`.
- Durable recovery: `native:database`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map and complete final arrow trace

- e0: Worker A -> Lease expires: renewals stop. `apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:54-95`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Lease expires -> Worker B: next claim. `apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-51`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Worker B -> Inspect run state: inspect. `apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:57-140`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Inspect run state -> Visible failure: stranded turn. `apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:57-90`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Inspect run state -> Durable recovery: recoverable. `apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:118-140`. Endpoint-attached, correctly directed, gutter-routed; clean.
