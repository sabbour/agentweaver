# Test coverage · independent layers, distinct proof — pass-02

Audience: technical readers of the deep-dive documentation.
Takeaway: Each layer proves a different seam; this is a coverage map, not a sequential execution pipeline.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/testing-strategy-fig1.drawio; publication: docs/diagrams/testing-strategy-fig1.png.

## Actual image review

Opened testing-strategy-fig1-pass-02.png enlarged and testing-strategy-fig1-pass-02-print.png as an A5/96-dpi screen proof. Correction-only changes: vertical labels moved beside arrowheads; long labels shortened without changing their evidence-map relationships; outer labels that exceeded the page removed; MAF retry moved to a marigold outer rail; embedded materialization routed through a separate gutter; shared lower rows removed from provider/seam groups; native note fold corrected. A5 and enlarged review confirms the previously obscured arrowheads are visible. Remaining batch defects are the overview broker/delegate label collision and collinear opposite-direction center segments in typed adapters/service handoff; fan-out exit attachments also need distinct positions. Other layouts need no further content or composition changes.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **unit** — Unit / component; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-82.
- **vitest** — Frontend Vitest; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/../../apps/web/package.json:11.
- **api** — In-process API; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-82.
- **workflow** — Workflow / Git / policy; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-82.
- **postgres** — PostgreSQL integration; native:database inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/PostgresIntegration/PostgresFixture.cs:9-29.
- **mcp** — Real MCP process; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Mcp/McpBrokerRealProcessTests.cs:235-255.
- **oauth** — OAuth server tests; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Auth/OpenIddictAuthorizationServerTests.cs:196-239.
- **e2e** — Opt-in / staging browser; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/../e2e/playwright.config.ts:11-15.

## Relationships

No arrows: this diagram is an independent coverage taxonomy, not an execution sequence.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

No arrows: layers run independently. Source presence is evidence of coverage, not a passing test run.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.
