# Project, run and execution ownership — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: A project base checkout is not a run worktree, shared Git index or sandbox.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/projects-fig2.png. Target documentation: docs/deep-dive/projects.md.

## Actual PNG inspection

Opened projects-fig2-pass-04.png at enlarged export resolution and projects-fig2-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

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
| provider-to-checkout | provider | checkout | path | apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62 |
| run-to-worktree | run | worktree | owns | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317 |
| sandbox-to-worktree | sandbox | worktree | executes in | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317 |

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| run-to-project: run → project | (0, 0.5) → (1, 0.5) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| project-to-checkout: project → checkout | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| project-to-team: project → team | (1, 0.82) → (1, 0.5) | (397, 173.44) → (397, 364) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| provider-to-checkout: provider → checkout | (0, 0.5) → (0, 0.5) | (18, 474) → (18, 254) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| run-to-worktree: run → worktree | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| sandbox-to-worktree: sandbox → worktree | (0.5, 0) → (0.5, 1) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
