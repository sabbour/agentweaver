# Pass 04 — final correction check and complete arrow trace

Input: the inspected pass-03 PNG. Inspection found no further permitted defect, so pass 4 is an explicitly **no-change correction-only pass**. It has its own source copy and a new draw.io Desktop PNG export; it is not a reused filename or a claim that an XML inspection equals image inspection.

Opened `canonical-workflow-authoring-pass-04-print.png` and the actual `canonical-workflow-authoring-pass-04.png` (2190 × 3151). Both print-fit and enlarged checks are complete.

## Final content model

Evidence abbreviations:

- **E** = `apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs`
- **G** = `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs`
- **R** = `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs`
- **C** = `apps/Agentweaver.Api/Generation/GenerationModelOptions.cs`
- **T** = `tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs`
- **D** = `docs/guide/workflows.md`

| Node | Evidence | Native classification |
| --- | --- | --- |
| request — authorize owned-project request and provider execution plan | E:593–625 | `native:flowchart`, process |
| context — roles/schema/examples, configurable model | G:124–230; C:36–40,74–83; E:662–675 | `native:flowchart`, document |
| generator — model returns candidate YAML, create/edit supported | G:54–82,120–133; E:662–690 | `native:uml`, component |
| validation — loader + binder, not just YAML parsing | G:90–118; T:309–323 | `native:flowchart`, decision |
| repair — failed YAML and error, exactly one correction | G:69–82; T:328–350 | `native:flowchart`, predefined process |
| generation-error — explicit 400 after second-invalid result | G:80–82; E:692–695; T:341–350 | `native:flowchart`, terminator |
| review — human edits unsaved YAML/graph | E:677–690; T:622–650; D:80–99 | `native:flowchart`, document |
| save-validation — owned-project PUT, syntax/structure/id/binder checks | E:438–507 | `native:flowchart`, decision |
| save-error — 400/422 validation rejection before writing | E:454–506 | `native:flowchart`, terminator |
| write — contained `.agentweaver/workflows/{id}.yaml` write | E:509–529 | `native:flowchart`, document |
| write-error — path guard or I/O failure | E:521–529 | `native:flowchart`, terminator |
| registry — extend restricted allowed set if needed, Sync, return detail | E:532–585; R:57–82,182–209 | `native:uml`, component |
| reload-error — written file may exist despite 422/500 reload failure | E:550–582 | `native:flowchart`, document |

Card chrome, accents, text hierarchy, pills and the two responsibility boundaries are `custom:agentweaver` presentation components, not invented runtime actors. Boundaries are generation/no-write and explicit-save/workspace responsibilities, not separate services or deployment/security zones.

## Every-arrow trace

All thirteen arrow IDs in the final XML were individually traced on the enlarged PNG.

| ID | Source → target / relationship | Evidence | Direction, endpoints and route | Result |
| --- | --- | --- | --- | --- |
| e01 | request → generator: permitted request | E:593–625,662–675 | Down; request bottom center to generator top center; clear vertical gutter | clean |
| e02 | context → generator: grounds prompt | G:124–230; C:36–40,74–83 | Left/down/left; context left center to generator right center; independent input gutter; no repair crossing | clean |
| e03 | generator → validation: candidate YAML | G:63–66,74–77,90–118 | Down; generator bottom center to validation top center; label beside success spine | clean |
| e04 | validation → review: valid, unsaved draft | G:66–67,76–77; E:677–690; T:622–650 | Down; validation bottom center to review top center; not the invalid ports | clean |
| e05 | validation → repair: first invalid candidate | G:69–75; T:328–338 | Right; upper-right validation port to repair left port; distinct label and short direct gutter | clean |
| e06 | repair → generator: one correction attempt | G:73–77; T:328–350 | Outer right/up/left return; repair right center to generator lower-right port; dashed marigold, arrowhead on generator; revalidation proceeds through e03 | clean |
| e07 | validation → generation-error: invalid again | G:80–82; E:692–695; T:341–350 | Right/down/right; lower-right validation port to error left center; separate from first-invalid lane; endpoint elbow removed and label in clear gutter | clean |
| e08 | review → save-validation: human explicitly chooses Save | D:94–99; E:438–456 | Down; review bottom center to Save top center; crosses responsibility boundary through empty space; not automatic persistence | clean |
| e09 | save-validation → save-error: invalid | E:457–506 | Right; center-to-center; 400/422 branch precedes file write | clean |
| e10 | save-validation → write: checks pass | E:494–524 | Down; Save bottom center to write top center; label in inter-card gutter | clean |
| e11 | write → write-error: failure | E:521–529 | Right; center-to-center; path guard/I/O failure; no arrow implying success | clean |
| e12 | write → registry: write succeeded | E:524–549 | Down; write bottom center to registry top center; allowed-set adjustment is in registry-stage subtitle | clean |
| e13 | registry → reload-error: failure | E:550–582 | Right; center-to-center; explicitly post-write, does not imply rollback | clean |

No arrow attaches to an icon, badge, text cell or group boundary. There are no remaining crossings and therefore no artificial bridge or junction decoration. The split happens on the actual decision-bearing validation card: separate valid, first-invalid and second-invalid ports. No fake junction dots were added. The only dashed marigold edge is the semantic correction return.

## Inspection conclusion

- **Print:** legible hierarchy, metadata, badge tones and decisive Save boundary; no clipped document labels.
- **Enlarged:** all native glyphs visible; no card/text collisions; error metadata stays to the left of its badge; orthogonal routes use clear gutters.
- **Remaining counts:** orientation 0, overlap 0, arrow 0.
- Four independent post-pitch exports and inspections are complete. Final pass is 4; no redesign occurred after pass 1.
- All correction passes retain the original pass-1 node IDs, labels, symbols and relationship endpoints.

Canonical promotion is permitted only after the manifest/schema, meaningful-growth, one-page/bounds, artifact-integrity and scoped renderer/stamp/drift checks succeed. Consumer pages/global inventory and the documentation build remain coordinator-owned by the assignment.

## Promotion and validation result

Completed after all gates passed:

- Promoted pass 4 byte-for-byte to `docs/diagrams/src/canonical-workflow-authoring.drawio`.
- Ran the repository renderer with **only** `--spec canonical-workflow-authoring`, pinned draw.io Desktop **31.4.5**; wrote the stable PNG and hash stamp.
- Opened the stable published PNG again, alongside the pixel-identical pass-04 print-fit image. Published pixels exactly match the inspected final pass.
- Deleted the obsolete canonical JSON and obsolete generated draw.io copy. The generated copy had previously been staged by the coordinator; the coordinator must stage its working-tree deletion when integrating.
- JSON Schema Draft 2020-12 and the skill's cross-field manifest validator pass.
- Growth remains **9.352396×**; all five XML/PNG/record triples exist; all actual vertex/waypoint bounds pass; correction content and symbols are unchanged.
- Scoped source/PNG/stamp drift check passes; `git diff --check` for this asset family is clean.
- Durable results: `validation-report.json`, `promotion-verification.json`, `scoped-drift-check.txt`.

No diagram-specific blocker remains. The global inventory listing failed on unrelated orphaned entries; no global inventory write was attempted. Original upstream research transcript preservation and global documentation integration remain coordinator-owned, as stated in the pitch record. The supplied three-thread synthesis and current-source recheck are preserved here.
