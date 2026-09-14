# Coordinator · MAF hands off to services — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: Confirmed spec can start dispatch; later collective phases are relational and service-driven.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/agent-framework-fig2.drawio; publication: docs/diagrams/agent-framework-fig2.png.

## Actual image review

Opened agent-framework-fig2-pitch.png enlarged and agent-framework-fig2-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **outcome** — CoordinatorOutcome; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **dispatch** — StartDispatch; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **release** — Release MAF state; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **services** — Dispatch / assembly; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **records** — Work plan + child runs; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **executors** — Direct executor calls; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs:132-169,327,499.
- **restart** — Restart recovery; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- **gate** — Assembly review gate; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Coordinator/AssemblyReviewGate.cs:6-44.

## Relationships

- `outcome-to-dispatch`: outcome → dispatch: eligible handoff. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `outcome-to-release`: outcome → release: release after handoff. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `dispatch-to-services`: dispatch → services: start services. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `services-to-records`: services → records: persist phase. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `services-to-executors`: services → executors: invoke directly. Evidence: apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs:132-169,327,499.
- `records-to-restart`: records → restart: persisted phase. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.
- `executors-to-gate`: executors → gate: service review. Evidence: apps/Agentweaver.Api/Coordinator/AssemblyReviewGate.cs:6-44.
- `restart-to-services`: restart → services: re-arm driver. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1172-1250.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

No checkpointed MAF assembly graph. Operator history/session handling is a separate exception.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
