# Generate a draft, not a saved workflow - pass-01

One correction attempt reuses loader and binder validation; saving is a separate action.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | User description: Request workflow generation; not a save command | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n1 | Server prompt + model: Constrained generation request; authored draft response | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n2 | Clean draft + ID: Normalize returned content; built-in edit: new ID | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| n3 | Loader validation: Parse workflow definition; same validation pipeline | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| n4 | Binder validation: Check runtime bindability; not syntax alone | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| n5 | Valid draft response: Return YAML to caller; not persisted/applied | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n6 | Error + one retry: Ask model to correct error; exactly one correction | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n7 | Validate correction: Same cleanup/loader/binder; second pass only | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-112` |
| n8 | Generation exception: Correction still invalid; explicit failure | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |

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
  "baseline_meaningful_xml": 1546,
  "result_meaningful_xml": 23814,
  "baseline_visible_structures": 6,
  "result_visible_structures": 82,
  "growth_ratio": 15.403622,
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
| e0 | n0 -> n1 | Description starts bounded draft generation | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| e1 | n1 -> n2 | Normalize model output and ensure valid draft ID | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| e2 | n2 -> n3 | Validate cleaned draft with workflow loader | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| e3 | n3 -> n4 | Validate runtime bindability after loading | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| e4 | n4 -> n5 | Return a valid draft without saving | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| e5 | n3 -> n6 | First validation error supplies one correction attempt | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| e6 | n4 -> n6 | First binding error supplies one correction attempt | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| e7 | n6 -> n7 | Corrected draft uses the same validation pipeline | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-112` |
| e8 | n7 -> n5 | Valid corrected draft returns to caller | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| e9 | n7 -> n8 | Second invalid result throws generation exception | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
