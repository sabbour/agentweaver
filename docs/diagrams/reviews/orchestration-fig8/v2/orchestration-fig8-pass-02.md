# Persist the plan before dispatch - pass-02

Confirmation, selection and decomposition precede durable child dispatch.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Submitted request: Goal + caller context; manual or pickup | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:370-408` |
| n1 | Confirmation boundary: Manual or unattended policy; autopilot-dependent | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:370-408` |
| n2 | Existing plan?: Reuse persisted plan; avoid decomposing twice | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:133-150` |
| n3 | Select workflow: Available definitions; explicit choices honored | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:157-160` |
| n4 | Decompose + validate: Outcome-complete work; compatibility check | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:157-182` |
| n5 | Persist WorkPlan: Subtasks and dependencies; workflow identity | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:230-249` |
| n6 | Ready frontier: Dependency satisfaction; pending -> ready work | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-427` |
| n7 | Dispatch children: Observe classified outcomes; isolated child runs | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-537` |
| n8 | Collective handoff: After child supervision; assembly eligibility | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:798-799` |

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
| e0 | n0 -> n1 | Intent confirmation obeys approval/autopilot policy | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:370-408` |
| e1 | n1 -> n2 | Check for an existing persisted plan | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:133-150` |
| e2 | n2 -> n3 | Without a reusable plan, select workflow | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:157-160` |
| e3 | n3 -> n4 | Selection precedes decomposition and compatibility check | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:157-182` |
| e4 | n4 -> n5 | Persist selected workflow and work-plan DAG | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:230-249` |
| e5 | n2 -> n6 | Existing plan goes to durable dispatch supervision | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:133-150` |
| e6 | n5 -> n6 | Persisted subtasks feed frontier evaluation | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-427` |
| e7 | n6 -> n7 | Admitted ready tasks become child runs | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-427` |
| e8 | n7 -> n8 | Child supervision hands settled work to assembly | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:798-799` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven print sheets and fourteen original-pixel pairs. The Review API heading collision and resilient budget title wrap are corrected. Existing content and Fluent styling remain intact; residual close-lane issues are recorded per diagram.

Correction-only pass: no added content or reopened composition. Existing-label fitting, native segment-routing correction and any heading repair are recorded in corrections-pass-02.json.
