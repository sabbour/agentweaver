# canonical-coordinator-journey — pass-01



Takeaway: Confirm intent, dispatch bounded work, then integrate and review the whole result.

Audience: Agentweaver contributors and operators. Orientation: true A5 landscape, 827 × 583 draw.io units, pageScale 1.

Export: draw.io Desktop 31.4.5, PNG, border 16, scale 2. Export scaling is not page sizing.



## Grounding and visual references

apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:65-82 and assembly workflow construction; apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-57; apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:81-96; coordinator-provided completed assurance research summary

Independent GPT-6 Astra research is preserved unchanged in ../canonical-provider-admission/research-thread-01.md, research-thread-02.md and research-thread-03.md (paths relative to reviews). The user explicitly required reuse, not new research delegation.

Current implementation/configuration/tests were read directly: WorkflowStageProjector.cs state mappings; EfRunEventStream.cs durable subscriber and 250 ms loop; CollectiveAssemblyPipeline.cs merge/Scribe; EfRunEventStreamTests.cs terminal-batch cases. Existing final graphics are composition references only, never evidence for facts.

Design sources: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json; canonical-workflow-authoring/a5-final pitch was read as a hierarchy example. No external logo downloads. Native draw.io symbols come from the pinned bundled libraries (jgraph/drawio, Apache-2.0); Azure artwork, when present, is bundled Microsoft architecture-icon artwork, used only to identify the represented service.



## Actual raster inspection

Opened the actual pass-01.png enlarged and its separately resampled pass-01-print.png in a 2×2 print-size sheet. Inspected title/subtitle, card hierarchy, symbols, metadata, badges, group boundaries, contrast, orientation, label spacing, and connector attachment.

## Meaningful growth
Metric: visible-semantic-canonical-xml-v1; baseline 3267; result 36540; ratio 11.184573×.

The added structures are visible sourced detail: differentiated roles and stores, readable responsibility tables, field-specific values, distinct state mappings, real native symbols, source-backed assurance notes and explicit relationships. Table rules separate actual property rows; there are no invisible/off-page cells, metadata padding, decorative duplicate rows or invented nodes.

This was the only visual-upgrade pass. Header icon overlap found during the initial export was corrected before completing pass 1. Sequence participant responsibilities and native lifelines preserve the temporal notation, with semantic return messages distinguished in marigold.

Pass-1 handoff: two long labels (confirmed, approved) crowd their short connector arrowheads. Pass 2 must shorten these labels without changing the relationship.



## Every-arrow trace

ID | XML source | XML target | Relationship | direction / endpoints / route | result

---|---|---|---|---|---

e1 | intent | plan | confirmed | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

e2 | plan | dispatch | dispatch | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

e3 | dispatch | integrate | settled work | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

e4 | integrate | review | request review | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

e5 | review | finish | approved | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean


All traced arrows use the grounding cited above. Activation-center attachment is valid UML attachment, not an unattached endpoint. Dashed marigold means semantic return/revision; gray dashed ProjectReference dependencies retain UML semantics. No decorative false junction dots.
