# Skills: catalog to safe delivery — pass-02

Audience: Agentweaver implementers and operators.

Takeaway: Only successful shared-filesystem writes produce pointers; all other delivery is inline.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/project-skills-fig1.png. Target documentation: docs/deep-dive/project-skills.md.

## Actual PNG inspection

Opened project-skills-fig1-pass-02.png at enlarged export resolution and project-skills-fig1-pass-02-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Corrections only: route declaration label no longer obscures its neighboring crossing; project-to-team now leaves a separate port from run-to-project. Those affected neighbors were rechecked. Other assets are independently exported no-change passes. All actual outputs remain legible at A5 and enlarged; orientation, label bounds, endpoints and arrowheads are clean.

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
