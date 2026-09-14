# experience-workflows-backlog-fig3: pitch

**Takeaway:** Ready pickup commits claim and reservation before activation; other outcomes do not launch.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-review-workspace-merge-fig1/research-orchestration.md](../experience-review-workspace-merge-fig1/research-orchestration.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Pitch handoff

Two-concept relationship sketch establishes the reading direction. Known issue: deliberately collapsed responsibilities and outcome qualifications need source-backed decomposition in pass 1; not publication-ready. Long connector labels crowd endpoints in the team, session, consent and review sketches; the visual-upgrade pass must separate those labels and expose their arrowheads. Handed to the locally read docs-diagram-iterate workflow.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Ranked Ready tasks: `custom:agentweaver`.
- Atomic transaction: `native:database`.
- Activate winner: `custom:agentweaver`.
- Lost / unavailable: `native:flowchart`.
- Claimed failed run: `native:flowchart`.
- Coordinator work: `custom:agentweaver`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Ranked Ready tasks -> Atomic transaction: attempt. `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:101-121`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Atomic transaction -> Activate winner: won. `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:231-249`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Atomic transaction -> Lost / unavailable: not won. `apps/Agentweaver.Api/Infrastructure/Ef/EfBacklogTaskStore.cs:382-417`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Activate winner -> Coordinator work: activate. `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:240-249`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Activate winner -> Claimed failed run: failure. `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:251-268`. Endpoint-attached, correctly directed, gutter-routed; clean.
