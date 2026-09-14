# Discover and cache valid workflows - pass-02

Registry sources are categories, not a project-policy precedence chain.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Embedded default: Platform-provided definition; default retained | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:122-180` |
| n1 | Conforming catalog: Known catalog workflows; reserved IDs protected | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:122-180` |
| n2 | Project YAML: Project-defined documents; not review-policy files | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:122-180` |
| n3 | Loader + bindability: Validate usable definitions; invalid entries diagnosed | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:122-203` |
| n4 | Identity collisions: Reserved catalog conflicts; materialized default skipped | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:183-265` |
| n5 | Allowed-set filter: Filter available choices; default retained | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:211-242` |
| n6 | Invalid diagnostics: Keep errors in results; not selectable candidates | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:18-25` |
| n7 | Signature cache: Per-project refresh key; sync invalidates/refreshes | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:57-83` |
| n8 | Available candidates: Valid and allowed workflows; selection input | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:18-25` |

## Symbols and credits

Repository Fluent template/library/design-system and current React theme are visual references, not runtime evidence.
Process, decision, event and document icons are native:flowchart; cylinders native:database;
people native:uml; component symbols native:uml. Product-specific chrome is custom:agentweaver.
Built-in diagrams.net symbols use the existing Desktop Apache-2.0 distribution. No external logos or image payloads.
Warm canvas/cards, 16px rounding, 5px accents, restrained shadows, Segoe UI, Cascadia Code metadata,
fixed pill tones, tiered named groups and orthogonal rounded labeled connectors are retained.


## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n3 | Load embedded default definition | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:122-180` |
| e1 | n1 -> n3 | Load conformance-checked catalog definitions | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:122-180` |
| e2 | n2 -> n3 | Load project YAML definitions | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:122-180` |
| e3 | n3 -> n4 | Validate identity and reserved-name behavior | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:183-265` |
| e4 | n3 -> n6 | Keep invalid diagnostics in registry result | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:18-25` |
| e5 | n4 -> n5 | Apply allowed-set filter to valid definitions | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:211-242` |
| e6 | n5 -> n7 | Cache project resolution using signature | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:57-83` |
| e7 | n7 -> n8 | Expose valid available candidates | `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs:18-25` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven print sheets and fourteen original-pixel pairs. The Review API heading collision and resilient budget title wrap are corrected. Existing content and Fluent styling remain intact; residual close-lane issues are recorded per diagram.

Correction-only pass: no added content or reopened composition. Existing-label fitting, native segment-routing correction and any heading repair are recorded in corrections-pass-02.json.
