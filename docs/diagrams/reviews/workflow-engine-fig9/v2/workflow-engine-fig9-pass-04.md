# Bind contracts, not just node names - pass-04

Typed executor, start and transition contracts can reject otherwise parseable YAML.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Parsed definition: Loader-valid graph data; not execution proof | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| n1 | Type/gate classifier: Known node kinds; renamed IDs still work | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-105` |
| n2 | Unsupported kind: No concrete runtime contract; fail closed | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:122-151` |
| n3 | Executor bindings: Concrete executor instances; factory integrations | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:818-850` |
| n4 | Transition adapters: Typed predicates and messages; not arbitrary arrows | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| n5 | Terminal outputs: Known result contracts; typed completion | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| n6 | Start + edge checks: Dry-run bindability; verdict start may fail | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:156-205` |
| n7 | MAF graph: Validated executable graph; start executor resolved | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| n8 | WorkflowBindException: Unsupported contract reported; no silent fallback graph | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:122-205` |

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
| e0 | n0 -> n1 | Classify node types/gates independently of arbitrary IDs | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-105` |
| e1 | n1 -> n2 | Unsupported node kinds fail closed | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:122-151` |
| e2 | n1 -> n3 | Bind supported classified nodes to executors | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:818-850` |
| e3 | n3 -> n4 | Wire typed transition predicates and adapters | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| e4 | n3 -> n5 | Associate terminal output contracts | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| e5 | n4 -> n6 | Validate start and edge message compatibility | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:156-205` |
| e6 | n5 -> n6 | Validate terminal and start contracts | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:156-205` |
| e7 | n6 -> n7 | Return validated executable graph | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| e8 | n6 -> n8 | Invalid start/edge contract raises bind exception | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:156-205` |
| e9 | n2 -> n8 | Unsupported node classification reports binding failure | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:122-151` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
