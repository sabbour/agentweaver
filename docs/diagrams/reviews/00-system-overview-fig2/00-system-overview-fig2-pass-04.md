# Full single-agent run · representative lifecycle — pass-04

Audience: technical readers of the deep-dive documentation.
Takeaway: Safety, review and merge outcomes branch; coordinator children use a trimmed graph.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/00-system-overview-fig2.drawio; publication: docs/diagrams/00-system-overview-fig2.png.

## Actual image review

Opened 00-system-overview-fig2-pass-04.png enlarged and 00-system-overview-fig2-pass-04-print.png as an A5/96-dpi screen proof. All eight connector endpoints and directions were traced, and the enlarged PNG and A5 proof are geometrically clear. Code tracing found an ambiguous review condition: safety clears could imply no flagged nonempty diff reaches review, but the actual condition is no further Rai revision. Queue the label/subtitle clarification and replace the terminal card child-graph citation with the actual watch-loop terminal handler in pass 5. Pass 4 is not the publication candidate.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **launch** — Accepted run; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- **agent** — Agent turn; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- **rai** — Rai safety; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **review** — Human review; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:372-379.
- **empty** — Empty diff result; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **merge** — Merge attempt; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **scribe** — Scribe; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **terminal** — Watch loop / terminal; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:595-681; apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:390-426.

## Relationships

- `launch-to-agent`: launch → agent: launch. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- `agent-to-rai`: agent → rai: turn output. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `rai-to-review`: rai → review: no revision. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:372-379.
- `rai-to-empty`: rai → empty: empty diff. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `review-to-merge`: review → merge: approve. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `empty-to-scribe`: empty → scribe: unflagged. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `merge-to-scribe`: merge → scribe: nonblocked. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `scribe-to-terminal`: scribe → terminal: output. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:422-426; apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:595-681.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Branch labels inside cards are explicit exits, not hidden arrows. POST /api/runs is retired (410).
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Complete arrow trace

- `launch-to-agent` | launch | agent | launch | apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `agent-to-rai` | agent | rai | turn output | apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `rai-to-review` | rai | review | no revision | apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:372-379 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | endpoint clean; “cleared” label ambiguous, correction queued for pass-05
- `rai-to-empty` | rai | empty | empty diff | apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `review-to-merge` | review | merge | approve | apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `empty-to-scribe` | empty | scribe | unflagged | apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `merge-to-scribe` | merge | scribe | nonblocked | apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `scribe-to-terminal` | scribe | terminal | output | apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:422-426; apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:595-681 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
