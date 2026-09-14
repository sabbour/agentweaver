# experience-scaling-operations-fig1: pass-01

**Takeaway:** Web serves requests; workers execute; Postgres coordinates durable state; pods run leaves.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [canonical-a2a-execution/research-execution.md](../canonical-a2a-execution/research-execution.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Visual upgrade

Metric: visible-semantic-canonical-xml-v1. Baseline 1031; result 17948; ratio 17.408341x.

Growth decomposes the two conceptual endpoints into six distinct evidenced responsibilities or outcomes, with tiered boundaries, native icons, titles, subtitles, metadata, badges, visible qualifications and endpoint-attached connectors. No padding, invisible objects, off-page cells, duplicate cells, comments or image bytes contribute.

Reading direction, card/boundary separation, title/detail fit, label association and arrow direction reviewed at print size and enlarged. Correction handoff: Web and worker database connectors appear to share a terminal segment without a logical junction.

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

- e0: Browser -> Web tier: requests. `apps/web/src/api/sse.ts:237-246`. Trace pending correction passes.
- e1: Web tier -> Postgres: state / events. `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:144-167`. Trace pending correction passes.
- e2: Worker tier -> Postgres: persist. `apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-95`. Trace pending correction passes.
- e3: Worker tier -> Sandbox pods: execute. `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:38-63`. Trace pending correction passes.
- e4: Sandbox pods -> Worker tier: results. `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:58-63`. Trace pending correction passes.
