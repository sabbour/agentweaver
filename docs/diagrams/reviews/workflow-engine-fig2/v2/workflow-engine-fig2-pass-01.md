# Declarative data is not an executor - pass-01

A parsed workflow still needs structural and runtime-bindability validation.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Workflow YAML: Authored graph document; start + nodes + edges | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:58-103` |
| n1 | Start + typed nodes: Stable node identity; type / gate contracts | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:58-103` |
| n2 | Edges + metadata: Transitions and render fields; role / kind: visual | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:66-103` |
| n3 | Structural checks: References and graph shape; loader-valid definition | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| n4 | Runtime classifier: Known node execution kinds; publish -> agent kind | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-100` |
| n5 | Executor bindings: Concrete execution contracts; not metadata inference | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-139` |
| n6 | Start/edge compatibility: Typed transitions must fit; dry-run bindability | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:156-205` |
| n7 | Executable graph: Valid concrete MAF graph; ready to execute | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| n8 | Binding error: Unsupported node/start/edge; WorkflowBindException | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:122-205` |

## Symbols and credits

Repository Fluent template/library/design-system and current React theme are visual references, not runtime evidence.
Process, decision, event and document icons are native:flowchart; cylinders native:database;
people native:uml; component symbols native:uml. Product-specific chrome is custom:agentweaver.
Built-in diagrams.net symbols use the existing Desktop Apache-2.0 distribution. No external logos or image payloads.
Warm canvas/cards, 16px rounding, 5px accents, restrained shadows, Segoe UI, Cascadia Code metadata,
fixed pill tones, tiered named groups and orthogonal rounded labeled connectors are retained.

## Meaningful expansion

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 1531,
  "result_meaningful_xml": 23385,
  "baseline_visible_structures": 6,
  "result_visible_structures": 81,
  "growth_ratio": 15.274331,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true
}
```

## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | Parse start and typed node declarations | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:58-103` |
| e1 | n0 -> n2 | Parse edge and metadata declarations | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:66-103` |
| e2 | n1 -> n3 | Validate node/start structure before graph execution | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| e3 | n2 -> n3 | Validate declarative edge references | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| e4 | n3 -> n4 | Classify runtime node/gate semantics | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-100` |
| e5 | n4 -> n5 | Choose concrete executor contracts | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-139` |
| e6 | n5 -> n6 | Validate runtime start and transition compatibility | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:156-205` |
| e7 | n6 -> n7 | Compatible bound graph is executable | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| e8 | n6 -> n8 | Unsupported runtime contracts fail explicitly | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:122-205` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
