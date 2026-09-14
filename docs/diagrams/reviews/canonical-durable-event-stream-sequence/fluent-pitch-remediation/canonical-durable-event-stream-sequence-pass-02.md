# canonical-durable-event-stream-sequence — pass-02



Takeaway: EF / PostgreSQL sequence · durable delivery across API replicas

Audience: Agentweaver contributors and operators. Orientation: true A5 landscape, 827 × 583 draw.io units, pageScale 1.

Export: draw.io Desktop 31.4.5, PNG, border 16, scale 2. Export scaling is not page sizing.



## Grounding and visual references

apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15-36,66-87,145-190,220-320; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs

Independent GPT-6 Astra research is preserved unchanged in ../canonical-provider-admission/research-thread-01.md, research-thread-02.md and research-thread-03.md (paths relative to reviews). The user explicitly required reuse, not new research delegation.

Current implementation/configuration/tests were read directly: WorkflowStageProjector.cs state mappings; EfRunEventStream.cs durable subscriber and 250 ms loop; CollectiveAssemblyPipeline.cs merge/Scribe; EfRunEventStreamTests.cs terminal-batch cases. Existing final graphics are composition references only, never evidence for facts.

Design sources: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json; canonical-workflow-authoring/a5-final pitch was read as a hierarchy example. No external logo downloads. Native draw.io symbols come from the pinned bundled libraries (jgraph/drawio, Apache-2.0); Azure artwork, when present, is bundled Microsoft architecture-icon artwork, used only to identify the represented service.



## Actual raster inspection

Opened the actual pass-02.png enlarged and its separately resampled pass-02-print.png in a 2×2 print-size sheet. Inspected title/subtitle, card hierarchy, symbols, metadata, badges, group boundaries, contrast, orientation, label spacing, and connector attachment.

Correction-only inspection. No edits were necessary. Inspected the preceding PNG, saved a fresh source copy, exported a fresh PNG, and opened both print and enlarged output. No remaining orientation, overlap, label/endpoint or routing defect was found.



## Every-arrow trace

ID | XML source | XML target | Relationship | direction / endpoints / route | result

---|---|---|---|---|---

message1 | activation0-producer | activation0-stream | 1  append(runId, event) | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message2 | activation1-stream | activation1-store | 2  lock run · allocate MAX + 1 · save | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message3 | activation2-store | activation2-stream | 3  commit succeeds | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message4 | activation3-stream | activation3-producer | 4  acknowledge assigned sequence | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message5 | activation4-client | activation4-sse | 5  reconnect with last delivered cursor | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message6 | activation5-sse | activation5-store | 6  query Sequence > cursor | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message7 | activation6-store | activation6-sse | 7  return ordered durable batch | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message8 | activation7-sse | activation7-client | 8  emit id + event + data; advance cursor | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean


All traced arrows use the grounding cited above. Activation-center attachment is valid UML attachment, not an unattached endpoint. Dashed marigold means semantic return/revision; gray dashed ProjectReference dependencies retain UML semantics. No decorative false junction dots.
