# Testing boundary · choose what must stay real — pass-01

Audience: technical readers of the deep-dive documentation.
Takeaway: Replace nondeterministic dependencies without replacing the behavior under test.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/canonical-testing-boundary.drawio; publication: docs/diagrams/canonical-testing-boundary.png.

## Actual image review

Opened canonical-testing-boundary-pass-01.png enlarged and canonical-testing-boundary-pass-01-print.png as an A5/96-dpi screen proof. The expanded cards and source-backed distinctions are legible at A5. Correction handoff: short vertical connectors have label backgrounds that can conceal arrowheads; some center-gutter labels crowd neighboring routes. The typed-adapter return rail and generator materialization route need separation. Persistence/testing column boundaries must not enclose the shared lower-row concerns. The coverage-map note fold needs a smaller native fold size.
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

## Meaningful growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2394,
  "result_meaningful_xml": 22031,
  "baseline_visible_structures": 9,
  "result_visible_structures": 81,
  "growth_ratio": 9.20259,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\canonical-testing-boundary\\canonical-testing-boundary-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\canonical-testing-boundary\\canonical-testing-boundary-pass-01.drawio"
}

```
Visible upgrade: eight distinct grounded contracts; native symbols; separate title/subtitle/detail/metadata/pills; tiered group surfaces; explicit scope; source-backed connectors where relationships exist. No hidden/off-page objects, duplicate nodes, embedded images, padding, or invented facts count toward growth.
