# canonical-agent-communication-handoff — pitch



Takeaway: The Coordinator owns the dependency frontier and assembles child results.

Audience: Agentweaver contributors and operators. Orientation: true A5 landscape, 827 × 583 draw.io units, pageScale 1.

Export: draw.io Desktop 31.4.5, PNG, border 16, scale 2. Export scaling is not page sizing.



## Grounding and visual references

apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-57,76,116-124,407,477,778; apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:81-96; docs/deep-dive/agent-communication.md:126-200

Independent GPT-6 Astra research is preserved unchanged in ../canonical-provider-admission/research-thread-01.md, research-thread-02.md and research-thread-03.md (paths relative to reviews). The user explicitly required reuse, not new research delegation.

Current implementation/configuration/tests were read directly: WorkflowStageProjector.cs state mappings; EfRunEventStream.cs durable subscriber and 250 ms loop; CollectiveAssemblyPipeline.cs merge/Scribe; EfRunEventStreamTests.cs terminal-batch cases. Existing final graphics are composition references only, never evidence for facts.

Design sources: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json; canonical-workflow-authoring/a5-final pitch was read as a hierarchy example. No external logo downloads. Native draw.io symbols come from the pinned bundled libraries (jgraph/drawio, Apache-2.0); Azure artwork, when present, is bundled Microsoft architecture-icon artwork, used only to identify the represented service.



## Actual raster inspection

Opened the actual pitch.png enlarged and its separately resampled pitch-print.png in a 2×2 print-size sheet. Inspected title/subtitle, card hierarchy, symbols, metadata, badges, group boundaries, contrast, orientation, label spacing, and connector attachment.

This new initial pitch supersedes the old sparse concept strip. Two source-backed responsibilities are fully articulated, not arbitrary boxes selected solely to lower the baseline. Native symbols, warm surfaces, rounded 16-unit cards, five-unit accents, restrained shadows, Segoe UI, metadata and semantic badges are already present in the initial pitch.

The sequence pitches remain temporal interactions with native UML lifelines, visible activation bars, ordered request/return messages; the architecture pitch is a workload-boundary view, the component pitch is a ProjectReference dependency, and the board pitch is a persisted-state projection.

Handoff to pass 1: expand the compact responsibility model into its separately evidenced actors/stores/stages, explicit relationships and assurance details. No known initial-pitch overlap or missing-native-symbol defect remains.

## Initial symbol inventory

- Coordinator: `native:uml` (`shape=component`); Agentweaver Fluent card chrome.

- Bounded child run: `native:uml` (`shape=component`); Agentweaver Fluent card chrome.



## Every-arrow trace

ID | XML source | XML target | Relationship | direction / endpoints / route | result

---|---|---|---|---|---

handoff | role0 | role1 | dispatch subtask | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean

result | role1 | role0 | results | evidence direction; intended shape attachment; orthogonal gutter or horizontal activation-to-activation; crossing bridges where required | clean


All traced arrows use the grounding cited above. Activation-center attachment is valid UML attachment, not an unattached endpoint. Dashed marigold means semantic return/revision; gray dashed ProjectReference dependencies retain UML semantics. No decorative false junction dots.
