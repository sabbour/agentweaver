# experience-review-workspace-merge-fig1: pitch

**Takeaway:** Approval identifies a candidate; guarded local merge can also include target-side changes.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-review-workspace-merge-fig1/research-orchestration.md](../experience-review-workspace-merge-fig1/research-orchestration.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Pitch handoff

Two-concept relationship sketch establishes the reading direction. Known issue: deliberately collapsed responsibilities and outcome qualifications need source-backed decomposition in pass 1; not publication-ready. Long connector labels crowd endpoints in the team, session, consent and review sketches; the visual-upgrade pass must separate those labels and expose their arrowheads. Handed to the locally read docs-diagram-iterate workflow.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Run candidate: `native:flowchart`.
- Review decision: `native:flowchart`.
- Guarded merge: `native:flowchart`.
- Revised candidate: `custom:agentweaver`.
- Declined / blocked: `native:flowchart`.
- Merged history: `native:database`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Run candidate -> Review decision: inspect. `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:372-386`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Review decision -> Guarded merge: approve. `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:925-968`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Review decision -> Declined / blocked: decline. `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:978-980`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Review decision -> Revised candidate: changes. `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1367-1437`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Guarded merge -> Declined / blocked: blocked. `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:139-160`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e5: Guarded merge -> Merged history: guards pass. `apps/Agentweaver.Api/Git/WorktreeManager.cs:1902-2059`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e6: Revised candidate -> Run candidate: re-review. `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1367-1437`. Endpoint-attached, correctly directed, gutter-routed; clean.
