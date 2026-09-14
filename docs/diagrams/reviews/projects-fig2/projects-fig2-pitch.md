# Project, run and execution ownership — pitch

Audience: Agentweaver implementers and operators.

Takeaway: A project base checkout is not a run worktree, shared Git index or sandbox.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/projects-fig2.png. Target documentation: docs/deep-dive/projects.md.

## Actual PNG inspection

Opened projects-fig2-pitch.png at enlarged export resolution and projects-fig2-pitch-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Two scope anchors and native icons are legible at both views. The deliberately coarse association is not a full implementation flow; pass 1 must expand the subject-specific branches and authority boundaries.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| project: Project | native:flowchart (process) | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| run: Run | native:flowchart (process) | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:194-245 |
| checkout: Stable base checkout | native:uml (folder) | apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75 |
| worktree: Run worktree + branch | native:uml (folder) | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317 |
| team: Team + workflow files | native:uml (folder) | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| sandbox: Sandbox / AgentHost | native:flowchart (process) | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317 |
| provider: Workspace provider | native:flowchart (process) | apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62 |
| child: Child run contribution | native:flowchart (process) | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| run-to-project | run | project | belongs to | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:194-245 |
| project-to-checkout | project | checkout | owns | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| project-to-team | project | team | configures | apps/Agentweaver.Api/Projects/ProjectService.cs:47-111 |
| provider-to-checkout | provider | checkout | provisions | apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62 |
| run-to-worktree | run | worktree | owns | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317 |
| sandbox-to-worktree | sandbox | worktree | executes in | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317 |

## Handoff

Coarse two-anchor scope association (no directional arrowhead) intentionally defers implementation detail to pass 1. Known risks: substantial unused pitch area, long scope headings, and scope anchors not yet sufficient for documentation. Upgrade into the source-backed hierarchy and exact relationships above; do not publish this pitch. Review authority/scope distinctions rather than treating the two anchors as a full sequence.
