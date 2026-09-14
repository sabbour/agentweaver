# Typed MAF adapters · state completes the contract — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: Review decisions carry approval; saved AgentTurnOutput supplies the merge data.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/agent-framework-fig1.drawio; publication: docs/diagrams/agent-framework-fig1.png.

## Actual image review

Opened agent-framework-fig1-pitch.png enlarged and agent-framework-fig1-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **output** — AgentTurnOutput; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- **adapter** — Review adapter; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- **state** — Workflow state; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- **port** — RequestPort; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- **decision** — Approved decision; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- **mergeadapter** — Merge adapter; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- **blocked** — Blocked adapter; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:532-545.
- **merge** — Merge executor; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.

## Relationships

- `output-to-adapter`: output → adapter: turn output. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- `adapter-to-state`: adapter → state: save output. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- `adapter-to-port`: adapter → port: request. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- `port-to-decision`: port → decision: matching response. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- `state-to-mergeadapter`: state → mergeadapter: read saved output. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- `decision-to-mergeadapter`: decision → mergeadapter: approved. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- `mergeadapter-to-merge`: mergeadapter → merge: MergeInput. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:409-440.
- `merge-to-blocked`: merge → blocked: blocked output. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:532-545.
- `blocked-to-port`: blocked → port: same review port. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:532-545.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Representative full-run adapters only. Collective assembly and Operator conversations are different paths.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
