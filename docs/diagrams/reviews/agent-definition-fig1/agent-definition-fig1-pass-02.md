# Agent definition · five generated targets — pass-02

Audience: technical readers of the deep-dive documentation.
Takeaway: One tool map is regenerated; handwritten prose stays intact and all five outputs are checked.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/agent-definition-fig1.drawio; publication: docs/diagrams/agent-definition-fig1.png.

## Actual image review

Opened agent-definition-fig1-pass-02.png enlarged and agent-definition-fig1-pass-02-print.png as an A5/96-dpi screen proof. Correction-only changes: vertical labels moved beside arrowheads; long labels shortened without changing their evidence-map relationships; outer labels that exceeded the page removed; MAF retry moved to a marigold outer rail; embedded materialization routed through a separate gutter; shared lower rows removed from provider/seam groups; native note fold corrected. A5 and enlarged review confirms the previously obscured arrowheads are visible. Remaining batch defects are the overview broker/delegate label collision and collinear opposite-direction center segments in typed adapters/service handoff; fan-out exit attachments also need distinct positions. Other layouts need no further content or composition changes.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **tools** — MCP tool sources; native:uml inside custom:agentweaver Fluent chrome. Evidence: scripts/gen-docs.mjs:243-273.
- **generator** — scripts/gen-docs.mjs; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: scripts/gen-docs.mjs:243-273.
- **reference** — Tool reference; native:uml inside custom:agentweaver Fluent chrome. Evidence: scripts/gen-docs.mjs:266-300.
- **repoagent** — Repository agent; native:uml inside custom:agentweaver Fluent chrome. Evidence: scripts/gen-docs.mjs:266-300.
- **embedded** — Embedded API template; native:uml inside custom:agentweaver Fluent chrome. Evidence: scripts/gen-docs.mjs:266-300.
- **public** — Documentation download; native:uml inside custom:agentweaver Fluent chrome. Evidence: scripts/gen-docs.mjs:266-300.
- **webcopy** — Web-host download; native:uml inside custom:agentweaver Fluent chrome. Evidence: scripts/gen-docs.mjs:266-300.
- **materialize** — New project agent file; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Projects/AgentDefinitionTemplate.cs:33-75.

## Relationships

- `tools-to-generator`: tools → generator: parse + compose. Evidence: scripts/gen-docs.mjs:243-273.
- `generator-to-reference`: generator → reference: write / check. Evidence: scripts/gen-docs.mjs:266-300.
- `generator-to-repoagent`: generator → repoagent: write / check. Evidence: scripts/gen-docs.mjs:266-300.
- `generator-to-embedded`: generator → embedded: write / check. Evidence: scripts/gen-docs.mjs:266-300.
- `generator-to-public`: generator → public: write / check. Evidence: scripts/gen-docs.mjs:266-300.
- `generator-to-webcopy`: generator → webcopy: write / check. Evidence: scripts/gen-docs.mjs:266-300.
- `embedded-to-materialize`: embedded → materialize: embedded resource. Evidence: apps/Agentweaver.Api/Projects/AgentDefinitionTemplate.cs:33-75.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Five outputs are siblings, not a copy chain. Only the embedded API copy materializes new project files.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.
