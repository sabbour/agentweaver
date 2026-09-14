# Response delivery is not process restoration — pass-04

Audience: technical readers of the deep-dive documentation.
Takeaway: Live response, another-replica delivery and process recovery are distinct control paths.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/agent-framework-fig3.drawio; publication: docs/diagrams/agent-framework-fig3.png.

## Actual image review

Opened agent-framework-fig3-pass-04.png enlarged and agent-framework-fig3-pass-04-print.png as an A5/96-dpi screen proof. Each directed connector was followed from its source card to its target arrowhead against the implementation evidence below. All intended endpoints, directions, arrowheads, orthogonal lanes and native crossing arcs are visible; no fake junctions or bridges. The corrected boundary arrow starts at the real host and ends at planning; the corrected hosting arrow starts at controlled execution and ends at real state. The arrowless coverage map remains independent layers. Each PNG and its A5 screen proof was opened individually. Titles, subtitles, metadata and pills fit; no clipped text, unintended card/label overlap or orientation defects remain. Unchanged diagrams were independently saved and exported.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **checks** — Reviewer / API checks; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964,1052-1060.
- **live** — Existing StreamingRun; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964,1052-1060.
- **defer** — Durable deferred decision; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964.
- **poll** — Owning watch loop; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227.
- **checkpoint** — CheckpointManager; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- **recover** — Recovery service; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- **graph** — Rebuild full / child graph; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- **resume** — ResumeStreamingAsync; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.

## Relationships

- `checks-to-live`: checks → live: local instance. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964,1052-1060.
- `checks-to-defer`: checks → defer: other replica. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964.
- `defer-to-poll`: defer → poll: poll decision. Evidence: apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227.
- `poll-to-live`: poll → live: SendResponseAsync. Evidence: apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227.
- `checkpoint-to-recover`: checkpoint → recover: latest checkpoint. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- `recover-to-graph`: recover → graph: effective definition. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.
- `graph-to-resume`: graph → resume: rebuild then resume. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

PostgreSQL failure does not select local files. Ordinary approval does not restore a checkpoint.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Complete arrow trace

- `checks-to-live` | checks | live | local instance | apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964,1052-1060 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `checks-to-defer` | checks | defer | other replica | apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:887-964 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `defer-to-poll` | defer | poll | poll decision | apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `poll-to-live` | poll | live | SendResponseAsync | apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:164-227 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `checkpoint-to-recover` | checkpoint | recover | latest checkpoint | apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `recover-to-graph` | recover | graph | effective definition | apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `graph-to-resume` | graph | resume | rebuild then resume | apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1561-1586 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
