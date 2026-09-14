# Review authorizes; merge still guards - pass-01

A review-bearing standalone workflow declares its gates; approval alone does not edit Git.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Selected definition: Bind the authored graph; no injected project policy | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1495-1517` |
| n1 | Producer output: Capture tree and diff; reviewable candidate | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:927-931` |
| n2 | Declared review gate: Only when workflow includes it; not universal to all graphs | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:822-835` |
| n3 | Request changes: Return feedback to execution; revision path | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:969-975` |
| n4 | Approve: Allow workflow continuation; not direct file mutation | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:963-967` |
| n5 | Decline: Persist declined terminal; no merge authorization | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:977-980` |
| n6 | Continuation: Deliver workflow response; remaining authored nodes | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1049-1053` |
| n7 | Merge coordinator: Lock and reviewed-tree guard; CAS before Git operation | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:46-102` |
| n8 | Actual merge result: Merged, blocked or conflict; internal errors distinct | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:106-185` |

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
  "baseline_meaningful_xml": 1553,
  "result_meaningful_xml": 23353,
  "baseline_visible_structures": 6,
  "result_visible_structures": 81,
  "growth_ratio": 15.037347,
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
| e0 | n0 -> n1 | Bound review-bearing workflow produces candidate artifacts | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:822-835` |
| e1 | n1 -> n2 | Present candidate to declared review gate | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:927-931` |
| e2 | n2 -> n3 | Request-changes selects producer revision path | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:969-975` |
| e3 | n2 -> n4 | Approval authorizes continuation | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:963-967` |
| e4 | n2 -> n5 | Decline transitions the run to declined | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:977-980` |
| e5 | n3 -> n6 | Deliver request-changes response to workflow | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1049-1053` |
| e6 | n4 -> n6 | Deliver approval response to workflow | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1049-1053` |
| e7 | n6 -> n7 | When execution reaches merge, enforce shared guard | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:82-102` |
| e8 | n7 -> n8 | Classify actual guarded Git operation outcome | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:106-185` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
