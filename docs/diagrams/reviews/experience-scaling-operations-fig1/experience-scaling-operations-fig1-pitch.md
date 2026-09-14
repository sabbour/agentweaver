# experience-scaling-operations-fig1: pitch

**Takeaway:** Web serves requests; workers execute; Postgres coordinates durable state; pods run leaves.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [canonical-a2a-execution/research-execution.md](../canonical-a2a-execution/research-execution.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Pitch handoff

Two-concept relationship sketch establishes the reading direction. Known issue: deliberately collapsed responsibilities and outcome qualifications need source-backed decomposition in pass 1; not publication-ready. Long connector labels crowd endpoints in the team, session, consent and review sketches; the visual-upgrade pass must separate those labels and expose their arrowheads. Handed to the locally read docs-diagram-iterate workflow.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Browser: `native:c4`.
- Web tier: `native:flowchart`.
- Worker tier: `native:flowchart`.
- Current boundary: `native:flowchart`.
- Postgres: `native:database`.
- Sandbox pods: `native:kubernetes`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Browser -> Web tier: requests. `apps/web/src/api/sse.ts:237-246`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Web tier -> Postgres: state / events. `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:144-167`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Worker tier -> Postgres: persist. `apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-95`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Worker tier -> Sandbox pods: execute. `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:38-63`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Sandbox pods -> Worker tier: results. `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:58-63`. Endpoint-attached, correctly directed, gutter-routed; clean.
