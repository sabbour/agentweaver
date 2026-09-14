# Generation preferences, not authority — pitch

Audience: Agentweaver implementers and operators.

Takeaway: Three project preferences select models; execution admission separately authorizes use.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/project-generation-model-settings-fig1.png. Target documentation: docs/deep-dive/project-generation-model-settings.md.

## Actual PNG inspection

Opened project-generation-model-settings-fig1-pitch.png at enlarged export resolution and project-generation-model-settings-fig1-pitch-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Two scope anchors and native icons are legible at both views. The deliberately coarse association is not a full implementation flow; pass 1 must expand the subject-specific branches and authority boundaries.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| settings: Project settings | native:flowchart (process) | apps/web/src/pages/ProjectSettingsPage.tsx:617-648 |
| blueprint: Blueprint generation | native:flowchart (process) | apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:119-141 |
| record: Project record | native:database (cylinder3) | apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:815-817 |
| workflow: Fallback workflow generation | native:flowchart (process) | apps/Agentweaver.Api/Blueprints/BlueprintService.cs:765-773 |
| resolve: Generation model resolver | native:flowchart (process) | apps/Agentweaver.Api/Generation/GenerationModelOptions.cs:37-75 |
| outcome: Coordinator spec drafter | native:flowchart (process) | apps/Agentweaver.Api/Coordinator/CopilotCoordinatorSpecDrafter.cs:145 |
| caller: Caller + project authority | native:uml (umlActor) | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-330,384-402 |
| invoke: Admitted model invocation | native:flowchart (process) | apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:119-141 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| settings-to-record | settings | record | save | apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:815-817 |
| record-to-resolve | record | resolve | preferences | apps/Agentweaver.Api/Generation/GenerationModelOptions.cs:37-75 |
| resolve-to-blueprint | resolve | blueprint | model | apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:140-141 |
| resolve-to-workflow | resolve | workflow | model | apps/Agentweaver.Api/Blueprints/BlueprintService.cs:765-773 |
| resolve-to-outcome | resolve | outcome | model | apps/Agentweaver.Api/Coordinator/CopilotCoordinatorSpecDrafter.cs:145 |
| caller-to-invoke | caller | invoke | authorize | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-330,384-402 |

## Handoff

Coarse two-anchor scope association (no directional arrowhead) intentionally defers implementation detail to pass 1. Known risks: substantial unused pitch area, long scope headings, and scope anchors not yet sufficient for documentation. Upgrade into the source-backed hierarchy and exact relationships above; do not publish this pitch. Review authority/scope distinctions rather than treating the two anchors as a full sequence.
