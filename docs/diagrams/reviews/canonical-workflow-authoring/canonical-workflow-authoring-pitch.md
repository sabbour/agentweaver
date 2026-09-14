# Workflow authoring — initial pitch

## Artifact contract

- **Audience:** workflow authors and maintainers reading `docs/guide/workflows.md`.
- **Takeaway:** generation returns a validated, unsaved draft; only an explicit Save crosses the persistence boundary.
- **Canonical output:** `docs/diagrams/canonical-workflow-authoring.png`.
- **Canonical source:** `docs/diagrams/src/canonical-workflow-authoring.drawio`.
- **Canvas:** one uncompressed A5 portrait page, 1123 × 1587 logical units (approximately 192 dpi). Every object is within the actual page.
- **Assignment:** exclusive canonical-workflow-authoring family in `plan-shared.json`. Shared inventory and consumer pages are coordinator-owned and were not modified.

## Independent research provenance and reconciliation

The assigning coordinator reports **three completed, independent GPT-6 Astra threads**:

1. Components: `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54–112`, `WorkflowDefinitionEndpoints.cs:438–688`, `WorkflowRegistry.cs:57–82,182–194`.
2. Flow: request → provider-gated generator → candidate YAML → loader plus binder dry-run; one repair and revalidation on failure; human editing and explicit Save are separate.
3. Assurance: `tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs:309,328,341,622` establishes binder checks, one correction, bounded failure, and draft-without-save.

The original thread transcripts were supplied at inaccessible-to-this-task temporary paths. This worker did **not** read, copy, or claim to preserve those originals. Original preservation remains with the coordinator. This record preserves the supplied synthesis, independently rechecked below; it does not misrepresent the recheck as three newly launched agents. No nested agent was launched.

## Current-source evidence

All paths below are repository-relative. Legacy diagrams are not evidence.

| Fact / relationship | Current implementation and assurance |
| --- | --- |
| Owned project, required description, provider execution gate before generation | `apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:593–625` |
| Model context uses schema, roles and examples | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:124–230` |
| Project model override has precedence over per-flow/shared defaults | `apps/Agentweaver.Api/Generation/GenerationModelOptions.cs:36–40,74–83`; endpoint `:662–673` |
| Generate → validate → exactly one correction → revalidate → error | `CopilotWorkflowGenerator.cs:54–82` |
| Strip fences, ensure id, load structure, dry-run binder | `CopilotWorkflowGenerator.cs:90–118` |
| Valid result is returned without persistence | `WorkflowDefinitionEndpoints.cs:588–592,662–690`; `WorkflowGeneratorTests.cs:622–650` |
| Review and edit YAML/graph before Save | `docs/guide/workflows.md:80–99` |
| Explicit Save → validation → contained file write | `WorkflowDefinitionEndpoints.cs:438–538` |
| Allowed-set extension → registry Sync → saved definition, or explicit post-write error | `WorkflowDefinitionEndpoints.cs:532–585`; `WorkflowRegistry.cs:57–82,182–209` |
| One successful repair / two invalid attempts fail closed | `WorkflowGeneratorTests.cs:309–350` |

**Facts, not proposals:** the diagram intentionally does not choose a model name, start a run, imply automatic persistence, equate loader validation with runtime bindability, or claim rollback after a failed registry reload. The visual review is a human authoring step, not an automated approval gate.

## Pitch content model and symbols

The pitch intentionally begins at the user-facing abstraction: two operations and one explicit choice. Pass 1 will expand their internals and failure paths, not manufacture an alternative baseline.

| Node | Classification | Evidence |
| --- | --- | --- |
| `draft` — Generate & review | `native:flowchart`, built-in `process` (predefined process), in Agentweaver chrome | generator `:54–118`; endpoint `:571–685`; guide `:80–99` |
| `save` — Save & register | `native:flowchart`, built-in `document` for persisted YAML, in Agentweaver chrome | endpoint `:438–585` |
| `explicit-save`: draft → save, “Human chooses Save” | directed orthogonal control flow | guide `:94–99`; PUT endpoint `:438–585` |
| Authoring boundary, accents and badge treatment | `custom:agentweaver` presentation chrome, not additional runtime components | committed design system |

## Visual research and credits

- Started from `docs/diagrams/drawio/fluent-template.drawio`; loaded `fluent-library.xml`; adopted `design-system.json` palette, typography, badge colors, 5-unit semantic accents, warm shadows, rounded cards and group hierarchy.
- Read the current React `apps/web/src/components/WorkflowGraphPanel.tsx` styles: Fluent tokens, semantic accents and restrained card shadows. The dedicated documentation design system supplies the requested five-unit accent contract.
- Native process/document/decision shapes are draw.io built-in notation, not hand-drawn logos. Subsequent component symbols use native UML notation. Official source/rights reference: https://github.com/jgraph/drawio (Apache-2.0). No external logo or copyrighted illustration was downloaded or embedded.
- Typography is Segoe UI, with Cascadia Code for metadata, rendered through locally installed fonts; fonts are not distributed.
- Warm paper `#efeae7`, group `#f8f4f1`, card `#fdfbf8`, lavender unsaved status and teal persisted-file status.

## Actual PNG inspection and handoff

Opened **both** `canonical-workflow-authoring-pitch-print.png` (A5-fit inspection copy) and the actual full-resolution `canonical-workflow-authoring-pitch.png` (2079 × 2751).

- Print: title, takeaway, group title, native icons, card titles/subtitles/metadata and both pill badges are legible. Two-operation hierarchy is intact.
- Enlarged: connector reaches the correct cards; no clipping, overlapping labels or false junctions; native process and document notation visibly render.
- Handoff: expand the abstract operations into a vertical success spine, show loader **and** binder checks, put the single semantic repair on an outer marigold return rail, distinguish pre-write failures from possible post-write reload failures, and retain the same A5 canvas.
- Pitch is **not** publication-ready; no canonical source or image has been promoted.
