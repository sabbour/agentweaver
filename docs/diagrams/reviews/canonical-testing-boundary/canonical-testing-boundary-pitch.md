# Testing boundary · choose what must stay real — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: Replace nondeterministic dependencies without replacing the behavior under test.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/canonical-testing-boundary.drawio; publication: docs/diagrams/canonical-testing-boundary.png.

## Actual image review

Opened canonical-testing-boundary-pitch.png enlarged and canonical-testing-boundary-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **host** — Real API host; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82.
- **agent** — Deterministic agent seam; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82.
- **stores** — Real SQLite stores; native:database inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/ProjectsWebApplicationFactory.cs:170-201.
- **http** — Controlled HTTP boundary; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/ProjectsWebApplicationFactory.cs:170-201.
- **git** — Real Git / policy logic; native:uml inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/CoordinatorWebApplicationFactory.cs:214-253.
- **planning** — Planning / workflow seams; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/CoordinatorWebApplicationFactory.cs:214-253.
- **postgres** — Real PostgreSQL fixture; native:database inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/PostgresIntegration/PostgresFixture.cs:9-29; tests/Agentweaver.Tests/Mcp/McpBrokerRealProcessTests.cs:235-255.
- **mcp** — Real MCP process; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/PostgresIntegration/PostgresFixture.cs:9-29; tests/Agentweaver.Tests/Mcp/McpBrokerRealProcessTests.cs:235-255.

## Relationships

- `host-to-agent`: host → agent: inject seam. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82.
- `git-to-planning`: git → planning: retain logic; replace planner. Evidence: tests/Agentweaver.Tests/Helpers/CoordinatorWebApplicationFactory.cs:214-253.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Scope varies by fixture. These tests do not establish live Entra, model-provider or Kubernetes behavior.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
