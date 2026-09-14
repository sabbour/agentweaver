# Full single-agent run · representative lifecycle — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: Safety, review and merge outcomes branch; coordinator children use a trimmed graph.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/00-system-overview-fig2.drawio; publication: docs/diagrams/00-system-overview-fig2.png.

## Actual image review

Opened 00-system-overview-fig2-pitch.png enlarged and 00-system-overview-fig2-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **launch** — Accepted run; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- **agent** — Agent turn; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- **rai** — Rai safety; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **review** — Human review; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **empty** — Empty diff result; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **merge** — Merge attempt; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **scribe** — Scribe; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- **terminal** — Watch loop / terminal; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.

## Relationships

- `launch-to-agent`: launch → agent: launch. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- `agent-to-rai`: agent → rai: turn output. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `rai-to-review`: rai → review: nonempty, cleared. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `rai-to-empty`: rai → empty: empty diff. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `review-to-merge`: review → merge: approve. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `empty-to-scribe`: empty → scribe: unflagged. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `merge-to-scribe`: merge → scribe: nonblocked. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.
- `scribe-to-terminal`: scribe → terminal: output. Evidence: apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:335-426.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Branch labels inside cards are explicit exits, not hidden arrows. POST /api/runs is retired (410).
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
