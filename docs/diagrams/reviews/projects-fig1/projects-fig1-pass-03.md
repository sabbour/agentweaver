# Project workspace provisioning — pass-03

Audience: Agentweaver implementers and operators.

Takeaway: Provider paths differ; only a healthy, initialized workspace becomes a persisted project.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/projects-fig1.png. Target documentation: docs/deep-dive/projects.md.

## Actual PNG inspection

Opened projects-fig1-pass-03.png at enlarged export resolution and projects-fig1-pass-03-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Independent no-change correction-only export. A5 and enlarged outputs were opened again: text and badges remain contained, the two pass-2 fixes are stable, no reversed connectors or hidden arrowheads were found, and crossing arcs remain distinguishable from junctions. No permitted defect remains.

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
