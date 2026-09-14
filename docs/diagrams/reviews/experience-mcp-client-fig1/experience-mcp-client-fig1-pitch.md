# experience-mcp-client-fig1: pitch

**Takeaway:** Choose one intake path, inspect the result, and preserve explicit human decisions.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-00-overview-fig1/research-identity.md](../experience-00-overview-fig1/research-identity.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Pitch handoff

Two-concept relationship sketch establishes the reading direction. Known issue: deliberately collapsed responsibilities and outcome qualifications need source-backed decomposition in pass 1; not publication-ready. Long connector labels crowd endpoints in the team, session, consent and review sketches; the visual-upgrade pass must separate those labels and expose their arrowheads. Handed to the locally read docs-diagram-iterate workflow.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Human + assistant: `native:c4`.
- Project and team: `custom:agentweaver`.
- Choose intake: `native:flowchart`.
- Human review: `native:flowchart`.
- State and artifacts: `native:flowchart`.
- Coordinator: `custom:agentweaver`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Human + assistant -> Project and team: prepare. `apps/Agentweaver.Api/Casting/CastingService.cs:899-927`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Project and team -> Choose intake: choose. `apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-34`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Choose intake -> Coordinator: start once. `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:186-249`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Coordinator -> State and artifacts: observe. `apps/Agentweaver.Mcp/Tools/RunTools.cs:229-265`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: State and artifacts -> Human review: inspect. `apps/Agentweaver.Mcp/Tools/RunTools.cs:276-284`. Endpoint-attached, correctly directed, gutter-routed; clean.
