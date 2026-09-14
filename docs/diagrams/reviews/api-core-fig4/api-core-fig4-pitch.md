# API middleware · endpoint metadata is authority — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: The request pipeline classifies endpoints before authentication; handlers enforce resource roles.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/api-core-fig4.drawio; publication: docs/diagrams/api-core-fig4.png.

## Actual image review

Opened api-core-fig4-pitch.png enlarged and api-core-fig4-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **transport** — Forwarded headers; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1266-1276.
- **routing** — Routing → CORS; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1266-1276.
- **integrity** — Endpoint integrity; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1274-1277.
- **authentication** — Authentication; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1277-1278.
- **unmatched** — Unmatched endpoint; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1278-1279.
- **authorization** — Authorization; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1279-1280.
- **handler** — Endpoint handler; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1280-1295.
- **result** — Service → DTO / result; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:429-445.

## Relationships

- `transport-to-routing`: transport → routing: next. Evidence: apps/Agentweaver.Api/Program.cs:1266-1276.
- `routing-to-integrity`: routing → integrity: selected endpoint. Evidence: apps/Agentweaver.Api/Program.cs:1274-1277.
- `integrity-to-authentication`: integrity → authentication: metadata valid. Evidence: apps/Agentweaver.Api/Program.cs:1277-1278.
- `authentication-to-unmatched`: authentication → unmatched: next. Evidence: apps/Agentweaver.Api/Program.cs:1278-1279.
- `unmatched-to-authorization`: unmatched → authorization: matched route. Evidence: apps/Agentweaver.Api/Program.cs:1279-1280.
- `authorization-to-handler`: authorization → handler: policy satisfied. Evidence: apps/Agentweaver.Api/Program.cs:1280-1295.
- `handler-to-result`: handler → result: delegate / map. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:429-445.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Request lane only: startup migration/recovery is separate. Worker role exposes probes, not this app surface.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
