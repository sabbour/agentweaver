# API host pitch

Audience: engineers reading `docs/deep-dive/api-core.md`. Takeaway: client intent reaches API authority; startup is not part of a request.

Stable target: `docs/diagrams/canonical-api-host.png`. Editable target: `docs/diagrams/src/canonical-api-host.drawio`.

The initial bare concept was rejected for missing Fluent hierarchy; its `-concept` artifacts are retained. The reviewed pitch has warm neutral cards, shadows, semantic accents, native actor/process symbols, title/subtitle/metadata and teal badges. The template working canvas was normalized to actual A5 landscape, 827 x 583 draw.io units at 100 dpi. The 5 px accents remain 5 px.

Exactly three separately launched GPT-6 Astra research agents completed before drawing: `astra-core-boundaries`, `astra-core-flows`, and `astra-core-assurance`. Their bounded questions/results and file-line evidence are in `content-model.json`, with the full flow/assurance results saved beside this record. Runtime code/config/tests outrank the audit and all legacy diagrams.

Node and connector evidence: `Program.cs:1274-1295` establishes API request authority; `Agentweaver.Mcp/AgentweaverApiClient.cs:359-379` establishes MCP broker forwarding. Every proposed expanded node and connector has an evidence entry in `content-model.json`.

Visual references: repository `fluent-template.drawio`, `fluent-library.xml`, `design-system.json`, and React `CoordinatorTopologyGraph.tsx` semantic badges/shadows/typography. Native actor is `native:uml`; process is `native:flowchart`. No third-party raster logos. Native symbols come from the bundled diagrams.net libraries (Apache-2.0 project); Agentweaver chrome follows repository assets.

Export: official draw.io Desktop 31.4.5, PNG, border 16, scale 2. Opened and inspected the actual `-pitch.png` enlarged and `-pitch-print.png` as a 794 x 559 A5/96-dpi screen proof. This is screen inspection, not a physical calibrated print.

Handoff: expand startup/request separation, endpoint-classified auth, resource roles, provider-selected stores and response directions. The pitch is deliberately a coarse contract, not the publishable implementation diagram. Do not publish before the four-pass workflow.
