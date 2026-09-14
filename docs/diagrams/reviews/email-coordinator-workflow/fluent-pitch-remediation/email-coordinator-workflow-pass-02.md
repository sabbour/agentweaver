# email-coordinator-workflow — pass-02



Takeaway: A sequence of bounded handoffs and one collective review—not a process-map substitute

Audience: Agentweaver contributors and operators. Orientation: true A5 landscape, 827 × 583 draw.io units, pageScale 1.

Export: draw.io Desktop 31.4.5, PNG, border 16, scale 2. Export scaling is not page sizing.



## Grounding and visual references

apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:65-82 and assembly workflow construction; apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-57; apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:81-96; coordinator-provided completed assurance research summary

Independent GPT-6 Astra research is preserved unchanged in ../canonical-provider-admission/research-thread-01.md, research-thread-02.md and research-thread-03.md (paths relative to reviews). The user explicitly required reuse, not new research delegation.

Current implementation/configuration/tests were read directly: WorkflowStageProjector.cs state mappings; EfRunEventStream.cs durable subscriber and 250 ms loop; CollectiveAssemblyPipeline.cs merge/Scribe; EfRunEventStreamTests.cs terminal-batch cases. Existing final graphics are composition references only, never evidence for facts.

Design sources: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json; canonical-workflow-authoring/a5-final pitch was read as a hierarchy example. No external logo downloads. Native draw.io symbols come from the pinned bundled libraries (jgraph/drawio, Apache-2.0); Azure artwork, when present, is bundled Microsoft architecture-icon artwork, used only to identify the represented service.



## Actual raster inspection

Opened the actual pass-02.png enlarged and its separately resampled pass-02-print.png in a 2×2 print-size sheet. Inspected title/subtitle, card hierarchy, symbols, metadata, badges, group boundaries, contrast, orientation, label spacing, and connector attachment.

Correction-only inspection. Moved the FINISH context note and its three children down four units, clearing the preceding human activation. Message endpoints and participant order are unchanged. Inspected the preceding PNG, saved a fresh source copy, exported a fresh PNG, and opened both print and enlarged output. No remaining orientation, overlap, label/endpoint or routing defect was found.



## Every-arrow trace

ID | XML source | XML target | Relationship | direction / endpoints / route | result

---|---|---|---|---|---

message1 | activation0-human | activation0-coord | 1  submit goal; draft and persist OutcomeSpec | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message2 | activation1-coord | activation1-human | 2  request confirmation / revision | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message3 | activation2-human | activation2-coord | 3  confirm; persist WorkPlan DAG | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message4 | activation3-coord | activation3-children | 4  dispatch dependency-ready children | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message5 | activation4-children | activation4-coord | 5  report settled results; block failed dependents | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message6 | activation5-coord | activation5-assembly | 6  integrate branches; run configured gates | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message7 | activation6-assembly | activation6-human | 7  request one collective review | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message8 | activation7-human | activation7-assembly | 8  approve reviewed integration | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

message9 | activation8-assembly | activation8-scribe | 9  MergeWorktree → Scribe | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean


All traced arrows use the grounding cited above. Activation-center attachment is valid UML attachment, not an unattached endpoint. Dashed marigold means semantic return/revision; gray dashed ProjectReference dependencies retain UML semantics. No decorative false junction dots.
