# Routes: global and project scope — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: App.tsx declares routes; AppShell supplies shared context, not a route registry.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig2.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig2-pass-04.png at enlarged export resolution and frontend-fig2-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

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

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| shell-to-router: shell → router | (0, 0.12) → (1, 0.12) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| router-to-operator: router → operator | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| router-to-project: router → project | (1, 0.35) → (0, 0.5) | (385, 130.2) → (385, 254) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| router-to-assets: router → assets | (1, 0.6) → (0, 0.35) | (410, 153.2) → (410, 350.2) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| router-to-operations: router → operations | (1, 0.85) → (0, 0.5) | (435, 176.2) → (435, 474) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
