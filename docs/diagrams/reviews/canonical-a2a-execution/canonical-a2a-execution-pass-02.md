# canonical-a2a-execution: pass-02

**Takeaway:** A2A moves leaf turns into sandbox pods, not orchestration or durable state ownership.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [canonical-a2a-execution/research-execution.md](../canonical-a2a-execution/research-execution.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Correction-only review

No composition, content, or visual-language changes. Wrapped long connector labels, separated ambiguous ports/routes, moved revision returns into outer gutters, and tightened the one crowded metadata label where needed. No new semantic objects or relationships. Saved and re-exported a distinct source/PNG pair.

Reading direction, card/boundary separation, title/detail fit, label association and arrow direction reviewed at print size and enlarged. Correction handoff: RunEvents label collides with the separate record route at its crossing.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Workflow graph: `custom:agentweaver`.
- RemoteAgentProxy: `custom:agentweaver`.
- Event recorder: `native:flowchart`.
- Sandbox pod: `native:kubernetes`.
- Durable state: `native:database`.
- Web timeline: `native:flowchart`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Workflow graph -> RemoteAgentProxy: invoke leaf. `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:36-63`. Trace pending correction passes.
- e1: RemoteAgentProxy -> Sandbox pod: A2A call. `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:38-53`. Trace pending correction passes.
- e2: Sandbox pod -> Event recorder: RunEvents. `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:58-63`. Trace pending correction passes.
- e3: Event recorder -> Durable state: record. `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:68-87`. Trace pending correction passes.
- e4: Durable state -> Web timeline: API / SSE. `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:144-167`. Trace pending correction passes.
