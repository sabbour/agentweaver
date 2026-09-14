# Test coverage · independent layers, distinct proof — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: Each layer proves a different seam; this is a coverage map, not a sequential execution pipeline.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/testing-strategy-fig1.drawio; publication: docs/diagrams/testing-strategy-fig1.png.

## Actual image review

Opened testing-strategy-fig1-pitch.png enlarged and testing-strategy-fig1-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
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

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
