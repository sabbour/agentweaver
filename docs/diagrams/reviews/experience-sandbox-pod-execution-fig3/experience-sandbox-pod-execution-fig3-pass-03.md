# experience-sandbox-pod-execution-fig3: pass-03

**Takeaway:** A review wait may release compute; preview and assembly retention are explicit exceptions.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [canonical-a2a-execution/research-execution.md](../canonical-a2a-execution/research-execution.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Correction-only review

No composition, content, or visual-language changes. Orientation, overlap, arrow endpoint/direction/routing checks found no permitted defect. Saved and re-exported a distinct source/PNG pair.

Reading direction, card/boundary separation, title/detail fit, label association and arrow direction reviewed at print size and enlarged. Zero remaining defects.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Active leaf: `native:kubernetes`.
- Worker gate: `custom:agentweaver`.
- Release decision: `native:flowchart`.
- Resumed work: `native:kubernetes`.
- Durable checkpoint: `native:database`.
- Release or retain: `native:flowchart`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Active leaf -> Worker gate: reach gate. `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:394-408`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Worker gate -> Release decision: evaluate. `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:518-536`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Release decision -> Release or retain: apply. `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:956-984`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Worker gate -> Durable checkpoint: checkpoint. `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:394-408`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Durable checkpoint -> Resumed work: resume. `apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:118-140`. Endpoint-attached, correctly directed, gutter-routed; clean.
