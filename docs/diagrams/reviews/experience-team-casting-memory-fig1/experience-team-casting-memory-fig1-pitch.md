# experience-team-casting-memory-fig1: pitch

**Takeaway:** Only eligible database records feed future context; exports are inspectable mirrors.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-00-overview-fig1/research-identity.md](../experience-00-overview-fig1/research-identity.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Pitch handoff

Two-concept relationship sketch establishes the reading direction. Known issue: deliberately collapsed responsibilities and outcome qualifications need source-backed decomposition in pass 1; not publication-ready. Long connector labels crowd endpoints in the team, session, consent and review sketches; the visual-upgrade pass must separate those labels and expose their arrowheads. Handed to the locally read docs-diagram-iterate workflow.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Cast proposal: `custom:agentweaver`.
- Named team: `custom:agentweaver`.
- Review knowledge: `native:flowchart`.
- Future context: `custom:agentweaver`.
- Eligibility: `native:flowchart`.
- Knowledge DB: `native:database`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Cast proposal -> Named team: confirm. `apps/Agentweaver.Api/Casting/CastingService.cs:899-999`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Named team -> Review knowledge: record. `apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:150-167`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Review knowledge -> Knowledge DB: persist. `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:211-219`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Knowledge DB -> Eligibility: select. `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:57-105`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Eligibility -> Future context: compile. `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:159-226`. Endpoint-attached, correctly directed, gutter-routed; clean.
