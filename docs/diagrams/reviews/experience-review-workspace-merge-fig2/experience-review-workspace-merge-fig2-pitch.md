# experience-review-workspace-merge-fig2: pitch

**Takeaway:** Select an allowed ref, inspect its tree, and read content through read-only operations.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-review-workspace-merge-fig1/research-orchestration.md](../experience-review-workspace-merge-fig1/research-orchestration.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Pitch handoff

Two-concept relationship sketch establishes the reading direction. Known issue: deliberately collapsed responsibilities and outcome qualifications need source-backed decomposition in pass 1; not publication-ready. Long connector labels crowd endpoints in the team, session, consent and review sketches; the visual-upgrade pass must separate those labels and expose their arrowheads. Handed to the locally read docs-diagram-iterate workflow.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Workspace reader: `native:c4`.
- Available refs: `native:flowchart`.
- Selected ref: `native:flowchart`.
- Read-only content: `native:flowchart`.
- Selected path: `native:flowchart`.
- File tree: `native:flowchart`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Workspace reader -> Available refs: list refs. `apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:13-24`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Available refs -> Selected ref: choose. `apps/web/src/pages/WorkspacePage.tsx:277-282`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Selected ref -> File tree: list tree. `apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:29-42`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: File tree -> Selected path: select. `apps/web/src/pages/WorkspacePage.tsx:297`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Selected path -> Read-only content: read. `apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:48-63`. Endpoint-attached, correctly directed, gutter-routed; clean.
