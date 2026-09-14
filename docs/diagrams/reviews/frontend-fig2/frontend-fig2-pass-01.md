# Routes: global and project scope — pass-01

Audience: Agentweaver implementers and operators.

Takeaway: App.tsx declares routes; AppShell supplies shared context, not a route registry.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig2.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig2-pass-01.png at enlarged export resolution and frontend-fig2-pass-01-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. The expanded hierarchy is legible at A5 and enlarged. Native Azure identity and database symbols render correctly; accents remain 5 units. Directed arrowheads are visible after spacing/label corrections. The route declaration and ownership diagrams have the specific correction-only handoffs recorded below; remaining diagrams have no observed orientation, overlap or arrow defect.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| router: App.tsx Routes | native:flowchart (process) | apps/web/src/App.tsx:80-100 |
| shell: AppShell | native:flowchart (process) | apps/web/src/components/shell/AppShell.tsx:134-182 |
| operator: Operator destinations | native:flowchart (process) | apps/web/src/App.tsx:93-100,130-135 |
| project: Project route family | native:flowchart (process) | apps/web/src/App.tsx:104-126 |
| admin: Platform settings | native:flowchart (hexagon) | apps/web/src/App.tsx:87-92 |
| assets: Project resources | native:flowchart (process) | apps/web/src/App.tsx:110-117 |
| global-observe: Global observability | native:flowchart (process) | apps/web/src/App.tsx:99-101 |
| operations: Project operations | native:flowchart (process) | apps/web/src/App.tsx:118-125 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| shell-to-router | shell | router | wraps | apps/web/src/App.tsx:80-127 |
| router-to-operator | router | operator | declares | apps/web/src/App.tsx:93-100 |
| router-to-project | router | project | declares | apps/web/src/App.tsx:104-126 |
| router-to-assets | router | assets | declares | apps/web/src/App.tsx:110-117 |
| router-to-operations | router | operations | declares | apps/web/src/App.tsx:118-125 |

Correction-only handoff: move router-to-assets label away from the neighboring declaration crossing. Relationship itself is correct.


## Growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2172,
  "result_meaningful_xml": 22976,
  "baseline_visible_structures": 8,
  "result_visible_structures": 84,
  "growth_ratio": 10.578269,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\frontend-fig2\\frontend-fig2-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\frontend-fig2\\frontend-fig2-pass-01.drawio"
}
```

Expansion is eight distinct source-backed nodes, tier surfaces, title/subtitle/detail/metadata/pill hierarchy, native symbols and relationship-specific orthogonal arrows. No invisible objects, off-page content, duplicate cells, comments, embedded images or metadata padding are used.
