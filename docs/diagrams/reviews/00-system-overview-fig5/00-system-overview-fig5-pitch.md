# Memory · promotion before reuse — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: Only eligible, approved context returns to prompts; exported files are mirrors, not policy.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/00-system-overview-fig5.drawio; publication: docs/diagrams/00-system-overview-fig5.png.

## Actual image review

Opened 00-system-overview-fig5-pitch.png enlarged and 00-system-overview-fig5-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **agent** — Agent observation; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:125-143.
- **inbox** — Decision inbox; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:125-143.
- **scribe** — Post-run Scribe; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- **approved** — Approved active state; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- **session** — Current open session; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- **compiler** — Context compiler; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:55-160.
- **mirrors** — Workspace mirrors; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- **prompt** — Subsequent prompt; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:55-160.

## Relationships

- `agent-to-inbox`: agent → inbox: submit. Evidence: apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:125-143.
- `inbox-to-scribe`: inbox → scribe: eligible entries. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- `scribe-to-approved`: scribe → approved: low-risk only. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- `scribe-to-session`: scribe → session: append summary. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- `approved-to-compiler`: approved → compiler: approved state. Evidence: apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:55-160.
- `session-to-compiler`: session → compiler: current session. Evidence: apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:55-160.
- `session-to-mirrors`: session → mirrors: export committed. Evidence: apps/Agentweaver.Api/Runs/PostRunScribeService.cs:25-150.
- `compiler-to-prompt`: compiler → prompt: compile. Evidence: apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:55-160.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Architecture and scope proposals require coordinator review. An observation alone is never trusted policy.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
