# Workflow authoring: pass 04 on true A5

Corrections only: move Generate candidate and Sync titles 12 logical units right to clear the native UML glyph overhang. Actual output inspected at both sizes. Full 13-arrow trace completed; no remaining orientation, overlap, endpoint, label or routing defect.

Source is uncompressed editable XML on one 583x827 A5 portrait page. Export: draw.io Desktop 31.4.5, border 16, scale 2. Export DPI is not page size. Rounded warm cards, semantic accents, native flowchart/UML symbols, badges, Segoe UI and monospaced metadata are retained. No new content after pass 1.

The original larger-page triples remain at the review root. The first rescaling probe in a5-corrected omitted HTML font scaling and visibly failed; it is not a pass in this accepted lineage. All five a5-final images were subsequently exported and opened individually at print and enlarged size before this record was generated.

Grounding: original research is preserved in ../canonical-provider-admission/research-thread-01.md, research-thread-02.md and research-thread-03.md. The root pitch and pass-04 records retain the detailed node evidence and native-symbol credits.

## Final every-arrow trace

| ID | Source -> target | Relationship | Evidence | Result |
| --- | --- | --- | --- | --- |
| e01 | request -> generator | permitted | apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:593-625,662-675 | clean |
| e02 | context -> generator | grounds prompt | apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:124-230; apps/Agentweaver.Api/Generation/GenerationModelOptions.cs:36-40,74-83 | clean |
| e03 | generator -> validation | candidate YAML | apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:63-77,90-118 | clean |
| e04 | validation -> review | valid; unsaved | apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:677-690; tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs:622-650 | clean |
| e05 | validation -> repair | first invalid | apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:69-75; tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs:328-338 | clean |
| e06 | repair -> generator | one repair | apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:73-77; tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs:328-350 | clean |
| e07 | validation -> generation-error | invalid again | apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:80-82; apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:692-695 | clean |
| e08 | review -> save-validation | explicit Save | docs/guide/workflows.md:94-99; apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:438-456 | clean |
| e09 | save-validation -> save-error | invalid | apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:457-506 | clean |
| e10 | save-validation -> write | checks pass | apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:494-524 | clean |
| e11 | write -> write-error | failure | apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:521-529 | clean |
| e12 | write -> registry | write succeeded | apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:524-549 | clean |
| e13 | registry -> reload-error | failure | apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:550-582 | clean |

Traced e01 down request-to-generator, e02 left/down/left input-to-generator, e03 down candidate-to-validation, e04 down valid-to-review, e05 right first-invalid-to-repair, e06 outer right/up/left one-repair return, e07 right/down/right second-invalid-to-error, e08 down explicit Save across the responsibility boundary, e09 right save rejection, e10 down successful validation-to-write, e11 right write failure, e12 down write-to-registry, and e13 right reload failure. Heads and endpoints attach to the correct cards, not icons/groups; labels stay in gutters. No crossings require a bridge; no fake junctions. Only the semantic repair return is dashed marigold.
