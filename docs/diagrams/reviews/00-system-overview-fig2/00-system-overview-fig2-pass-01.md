# Full single-agent run · representative lifecycle — pass-01

Audience: technical readers of the deep-dive documentation.
Takeaway: Safety, review and merge outcomes branch; coordinator children use a trimmed graph.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/00-system-overview-fig2.drawio; publication: docs/diagrams/00-system-overview-fig2.png.

## Actual image review

Opened 00-system-overview-fig2-pass-01.png enlarged and 00-system-overview-fig2-pass-01-print.png as an A5/96-dpi screen proof. The expanded cards and source-backed distinctions are legible at A5. Correction handoff: short vertical connectors have label backgrounds that can conceal arrowheads; some center-gutter labels crowd neighboring routes. The typed-adapter return rail and generator materialization route need separation. Persistence/testing column boundaries must not enclose the shared lower-row concerns. The coverage-map note fold needs a smaller native fold size.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **launch** — Accepted run; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- **agent** — Agent turn; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- **rai** — Rai safety; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **review** — Human review; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **empty** — Empty diff result; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **merge** — Merge attempt; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **scribe** — Scribe; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **terminal** — Watch loop / terminal; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.

## Relationships

- `launch-to-agent`: launch → agent: launch. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- `agent-to-rai`: agent → rai: turn output. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `rai-to-review`: rai → review: nonempty, cleared. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `rai-to-empty`: rai → empty: empty diff. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `review-to-merge`: review → merge: approve. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `empty-to-scribe`: empty → scribe: unflagged. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `merge-to-scribe`: merge → scribe: nonblocked. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `scribe-to-terminal`: scribe → terminal: output. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Branch labels inside cards are explicit exits, not hidden arrows. POST /api/runs is retired (410).
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Meaningful growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2364,
  "result_meaningful_xml": 24296,
  "baseline_visible_structures": 9,
  "result_visible_structures": 87,
  "growth_ratio": 10.277496,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\00-system-overview-fig2\\00-system-overview-fig2-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\00-system-overview-fig2\\00-system-overview-fig2-pass-01.drawio"
}

```
Visible upgrade: eight distinct grounded contracts; native symbols; separate title/subtitle/detail/metadata/pills; tiered group surfaces; explicit scope; source-backed connectors where relationships exist. No hidden/off-page objects, duplicate nodes, embedded images, padding, or invented facts count toward growth.
