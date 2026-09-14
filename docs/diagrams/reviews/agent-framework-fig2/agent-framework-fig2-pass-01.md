# Coordinator · MAF hands off to services — pass-01

Audience: technical readers of the deep-dive documentation.
Takeaway: Confirmed spec can start dispatch; later collective phases are relational and service-driven.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/agent-framework-fig2.drawio; publication: docs/diagrams/agent-framework-fig2.png.

## Actual image review

Opened agent-framework-fig2-pass-01.png enlarged and agent-framework-fig2-pass-01-print.png as an A5/96-dpi screen proof. The expanded cards and source-backed distinctions are legible at A5. Correction handoff: short vertical connectors have label backgrounds that can conceal arrowheads; some center-gutter labels crowd neighboring routes. The typed-adapter return rail and generator materialization route need separation. Persistence/testing column boundaries must not enclose the shared lower-row concerns. The coverage-map note fold needs a smaller native fold size.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **outcome** — CoordinatorOutcome; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **dispatch** — StartDispatch; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **release** — Release MAF state; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **services** — Dispatch / assembly; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **records** — Work plan + child runs; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **executors** — Direct executor calls; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs:132-169,327,499.
- **restart** — Restart recovery; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **gate** — Assembly review gate; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/AssemblyReviewGate.cs:6-44.

## Relationships

- `outcome-to-dispatch`: outcome → dispatch: eligible handoff. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `outcome-to-release`: outcome → release: release after handoff. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `dispatch-to-services`: dispatch → services: start services. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `services-to-records`: services → records: persist phase. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `services-to-executors`: services → executors: invoke directly. Evidence: apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs:132-169,327,499.
- `records-to-restart`: records → restart: persisted phase. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `executors-to-gate`: executors → gate: service review. Evidence: apps/Agentweaver.Api/Coordinator/AssemblyReviewGate.cs:6-44.
- `restart-to-services`: restart → services: re-arm driver. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

No checkpointed MAF assembly graph. Operator history/session handling is a separate exception.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Meaningful growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2377,
  "result_meaningful_xml": 24434,
  "baseline_visible_structures": 9,
  "result_visible_structures": 87,
  "growth_ratio": 10.279344,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\agent-framework-fig2\\agent-framework-fig2-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\agent-framework-fig2\\agent-framework-fig2-pass-01.drawio"
}

```
Visible upgrade: eight distinct grounded contracts; native symbols; separate title/subtitle/detail/metadata/pills; tiered group surfaces; explicit scope; source-backed connectors where relationships exist. No hidden/off-page objects, duplicate nodes, embedded images, padding, or invented facts count toward growth.
