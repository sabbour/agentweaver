# Generation preferences, not authority — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: Three project preferences select models; execution admission separately authorizes use.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/project-generation-model-settings-fig1.png. Target documentation: docs/deep-dive/project-generation-model-settings.md.

## Actual PNG inspection

Opened project-generation-model-settings-fig1-pass-04.png at enlarged export resolution and project-generation-model-settings-fig1-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

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

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| settings-to-record: settings → record | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| record-to-resolve: record → resolve | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| resolve-to-blueprint: resolve → blueprint | (1, 0.22) → (0, 0.5) | (390, 338.24) → (390, 144) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| resolve-to-workflow: resolve → workflow | (1, 0.5) → (0, 0.5) | (425, 364) → (425, 254) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| resolve-to-outcome: resolve → outcome | (1, 0.82) → (0, 0.82) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| caller-to-invoke: caller → invoke | (1, 0.5) → (0, 0.5) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
