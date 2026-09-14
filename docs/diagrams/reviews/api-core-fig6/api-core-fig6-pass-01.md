# System diagnostics · a narrow live check map — pass-01

Audience: technical readers of the deep-dive documentation.
Takeaway: The protected system snapshot combines checks and counts; it is not the detailed-health surface.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/api-core-fig6.drawio; publication: docs/diagrams/api-core-fig6.png.

## Actual image review

Opened api-core-fig6-pass-01.png enlarged and api-core-fig6-pass-01-print.png as an A5/96-dpi screen proof. The expanded cards and source-backed distinctions are legible at A5. Correction handoff: short vertical connectors have label backgrounds that can conceal arrowheads; some center-gutter labels crowd neighboring routes. The typed-adapter return rail and generator materialization route need separation. Persistence/testing column boundaries must not enclose the shared lower-row concerns. The coverage-map note fold needs a smaller native fold size.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **request** — Diagnostics request; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:52.
- **service** — Diagnostics service; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:52.
- **storage** — SQLite + data directory; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- **builtins** — Built-in definitions; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- **heartbeat** — Heartbeat + project store; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- **github** — GitHub CLI / auth; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- **counts** — Counts + pod quota; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:105-112.
- **dto** — SystemDiagnosticsDto; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:116-134.

## Relationships

- `request-to-service`: request → service: collect. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:52.
- `service-to-storage`: service → storage: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- `service-to-builtins`: service → builtins: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- `service-to-heartbeat`: service → heartbeat: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- `service-to-github`: service → github: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- `service-to-counts`: service → counts: read counts. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:105-112.
- `github-to-dto`: github → dto: check results. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:116-134.
- `counts-to-dto`: counts → dto: summary. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:105-134.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Detailed health separately checks PostgreSQL, Key Vault, warm pool and Kubernetes; probes are cheaper.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Meaningful growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2383,
  "result_meaningful_xml": 24438,
  "baseline_visible_structures": 9,
  "result_visible_structures": 87,
  "growth_ratio": 10.255141,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\api-core-fig6\\api-core-fig6-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\api-core-fig6\\api-core-fig6-pass-01.drawio"
}

```
Visible upgrade: eight distinct grounded contracts; native symbols; separate title/subtitle/detail/metadata/pills; tiered group surfaces; explicit scope; source-backed connectors where relationships exist. No hidden/off-page objects, duplicate nodes, embedded images, padding, or invented facts count toward growth.
