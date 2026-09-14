# Skills: catalog to safe delivery — pass-01

Audience: Agentweaver implementers and operators.

Takeaway: Only successful shared-filesystem writes produce pointers; all other delivery is inline.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/project-skills-fig1.png. Target documentation: docs/deep-dive/project-skills.md.

## Actual PNG inspection

Opened project-skills-fig1-pass-01.png at enlarged export resolution and project-skills-fig1-pass-01-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. The expanded hierarchy is legible at A5 and enlarged. Native Azure identity and database symbols render correctly; accents remain 5 units. Directed arrowheads are visible after spacing/label corrections. The route declaration and ownership diagrams have the specific correction-only handoffs recorded below; remaining diagrams have no observed orientation, overlap or arrow defect.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| sources: Skill sources | native:uml (folder) | apps/Agentweaver.Api/Skills/SkillCatalogService.cs:335-372 |
| active: Active assignments | native:flowchart (process) | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:45-81 |
| catalog: Project skill catalog | native:database (cylinder3) | apps/Agentweaver.Api/Skills/SkillCatalogService.cs:1045-1156 |
| shared: Shared worktree available | native:uml (folder) | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:63-106 |
| assign: Explicit agent assignment | native:flowchart (process) | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:49-54 |
| pointer: Successful write only | native:uml (folder) | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:91-98,145 |
| defaults: Defaults preview / apply | native:flowchart (hexagon) | apps/Agentweaver.Api/Skills/SkillDefaultsService.cs:31-52,231-250 |
| inline: Inline full instructions | native:flowchart (process) | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:99-160 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| sources-to-catalog | sources | catalog | validate | apps/Agentweaver.Api/Skills/SkillCatalogService.cs:1045-1156 |
| catalog-to-assign | catalog | assign | assign | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:49-54 |
| assign-to-active | assign | active | lookup | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:49-54 |
| active-to-shared | active | shared | shared FS | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:63-106 |
| shared-to-pointer | shared | pointer | write succeeds | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:91-98 |
| active-to-inline | active | inline | no FS | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:108-123 |
| shared-to-inline | shared | inline | write fail | apps/Agentweaver.Api/Skills/SkillPromptComposer.cs:99-106 |

## Growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2182,
  "result_meaningful_xml": 23954,
  "baseline_visible_structures": 8,
  "result_visible_structures": 86,
  "growth_ratio": 10.978002,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\project-skills-fig1\\project-skills-fig1-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\project-skills-fig1\\project-skills-fig1-pass-01.drawio"
}
```

Expansion is eight distinct source-backed nodes, tier surfaces, title/subtitle/detail/metadata/pill hierarchy, native symbols and relationship-specific orthogonal arrows. No invisible objects, off-page content, duplicate cells, comments, embedded images or metadata padding are used.
