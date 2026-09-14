# Project workspace provisioning — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: Provider paths differ; only a healthy, initialized workspace becomes a persisted project.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/projects-fig1.png. Target documentation: docs/deep-dive/projects.md.

## Actual PNG inspection

Opened projects-fig1-pass-04.png at enlarged export resolution and projects-fig1-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| create: Create project request | native:uml (umlActor) | apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:1232-1283 |
| probe: Create directory + probe | native:flowchart (process) | apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62 |
| provider: Configured workspace provider | native:flowchart (hexagon) | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| git: Initialize or clone Git | native:uml (folder) | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| local: Local filesystem policy | native:uml (folder) | apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62 |
| persist: Scaffold and persist project | native:database (cylinder3) | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| volume: Persistent-volume policy | native:uml (folder) | apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75 |
| failure: Provisioning failure | native:flowchart (hexagon) | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| create-to-provider | create | provider | create | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| provider-to-local | provider | local | local | apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62 |
| provider-to-volume | provider | volume | volume | apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75 |
| local-to-probe | local | probe | local path | apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62 |
| volume-to-probe | volume | probe | PV path | apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75 |
| probe-to-git | probe | git | healthy | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| git-to-persist | git | persist | scaffold | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| persist-to-failure | persist | failure | if create fails | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| create-to-provider: create → provider | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| provider-to-local: provider → local | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| provider-to-volume: provider → volume | (0, 0.5) → (0, 0.5) | (19, 254) → (19, 474) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| local-to-probe: local → probe | (1, 0.5) → (0, 0.3) | (395, 364) → (395, 125.6) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| volume-to-probe: volume → probe | (1, 0.5) → (0, 0.7) | (433, 474) → (433, 162.39999999999998) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| probe-to-git: probe → git | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| git-to-persist: git → persist | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| persist-to-failure: persist → failure | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
