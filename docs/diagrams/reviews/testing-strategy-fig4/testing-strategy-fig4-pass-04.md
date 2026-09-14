# API test hosting · configure, substitute, request — pass-04

Audience: technical readers of the deep-dive documentation.
Takeaway: Specialized factories keep Program wiring real while selecting isolated state and controlled seams.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/testing-strategy-fig4.drawio; publication: docs/diagrams/testing-strategy-fig4.png.

## Actual image review

Opened testing-strategy-fig4-pass-04.png enlarged and testing-strategy-fig4-pass-04-print.png as an A5/96-dpi screen proof. Each directed connector was followed from its source card to its target arrowhead against the implementation evidence below. All intended endpoints, directions, arrowheads, orthogonal lanes and native crossing arcs are visible; no fake junctions or bridges. The corrected boundary arrow starts at the real host and ends at planning; the corrected hosting arrow starts at controlled execution and ends at real state. The arrowless coverage map remains independent layers. Each PNG and its A5 screen proof was opened individually. Titles, subtitles, metadata and pills fit; no clipped text, unintended card/label overlap or orientation defects remain. Unchanged diagrams were independently saved and exported.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **test** — Test case; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-60.
- **config** — Fixture configuration; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-60.
- **substitute** — Service replacement; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82.
- **host** — Real Program host; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-82.
- **state** — Isolated real state; native:database inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:24-38.
- **request** — Test HTTP client; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/WorkflowIntegrationTests.cs:20-31.
- **seam** — Controlled execution; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/Helpers/TestFileEditAgentRunner.cs:44-80.
- **assert** — Assertions + cleanup; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: tests/Agentweaver.Tests/WorkflowIntegrationTests.cs:20-31.

## Relationships

- `test-to-config`: test → config: configure fixture. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-60.
- `test-to-substitute`: test → substitute: replace registrations. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82.
- `config-to-host`: config → host: build host. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-82.
- `substitute-to-host`: substitute → host: configure services. Evidence: tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82.
- `request-to-host`: request → host: HTTP request. Evidence: tests/Agentweaver.Tests/WorkflowIntegrationTests.cs:20-31.
- `seam-to-state`: seam → state: real file operations. Evidence: tests/Agentweaver.Tests/Helpers/TestFileEditAgentRunner.cs:44-80.
- `request-to-assert`: request → assert: assert result. Evidence: tests/Agentweaver.Tests/WorkflowIntegrationTests.cs:20-31.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Fixture legacy auth settings are not production architecture. The retired-route test proves no full workflow.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Complete arrow trace

- `test-to-config` | test | config | configure fixture | tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-60 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `test-to-substitute` | test | substitute | replace registrations | tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `config-to-host` | config | host | build host | tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:34-82 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `substitute-to-host` | substitute | host | configure services | tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs:61-82 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `request-to-host` | request | host | HTTP request | tests/Agentweaver.Tests/WorkflowIntegrationTests.cs:20-31 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `seam-to-state` | seam | state | real file operations | tests/Agentweaver.Tests/Helpers/TestFileEditAgentRunner.cs:44-80 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
- `request-to-assert` | request | assert | assert result | tests/Agentweaver.Tests/WorkflowIntegrationTests.cs:20-31 | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | clean
