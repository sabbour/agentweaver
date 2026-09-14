# Response delivery is not process restoration — pass-01

Audience: technical readers of the deep-dive documentation.
Takeaway: Live response, another-replica delivery and process recovery are distinct control paths.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/agent-framework-fig3.drawio; publication: docs/diagrams/agent-framework-fig3.png.

## Actual image review

Opened agent-framework-fig3-pass-01.png enlarged and agent-framework-fig3-pass-01-print.png as an A5/96-dpi screen proof. The expanded cards and source-backed distinctions are legible at A5. Correction handoff: short vertical connectors have label backgrounds that can conceal arrowheads; some center-gutter labels crowd neighboring routes. The typed-adapter return rail and generator materialization route need separation. Persistence/testing column boundaries must not enclose the shared lower-row concerns. The coverage-map note fold needs a smaller native fold size.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **checks** — Reviewer / API checks; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964,1052-1060.
- **live** — Existing StreamingRun; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964,1052-1060.
- **defer** — Durable deferred decision; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964.
- **poll** — Owning watch loop; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227.
- **checkpoint** — CheckpointManager; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- **recover** — Recovery service; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- **graph** — Rebuild full / child graph; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- **resume** — ResumeStreamingAsync; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.

## Relationships

- `checks-to-live`: checks → live: local instance. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964,1052-1060.
- `checks-to-defer`: checks → defer: other replica. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964.
- `defer-to-poll`: defer → poll: poll decision. Evidence: apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227.
- `poll-to-live`: poll → live: SendResponseAsync. Evidence: apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227.
- `checkpoint-to-recover`: checkpoint → recover: latest checkpoint. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- `recover-to-graph`: recover → graph: effective definition. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- `graph-to-resume`: graph → resume: rebuild then resume. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

PostgreSQL failure does not select local files. Ordinary approval does not restore a checkpoint.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Meaningful growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2376,
  "result_meaningful_xml": 24053,
  "baseline_visible_structures": 9,
  "result_visible_structures": 86,
  "growth_ratio": 10.123316,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\agent-framework-fig3\\agent-framework-fig3-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\agent-framework-fig3\\agent-framework-fig3-pass-01.drawio"
}

```
Visible upgrade: eight distinct grounded contracts; native symbols; separate title/subtitle/detail/metadata/pills; tiered group surfaces; explicit scope; source-backed connectors where relationships exist. No hidden/off-page objects, duplicate nodes, embedded images, padding, or invented facts count toward growth.
