# How work enters the coordinator - pass-04

Origin produces work; it does not filter the set of selectable workflows.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Manual request: Start coordinator directly; submitting user | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:204-207` |
| n1 | Verified Repo App event: HMAC before JSON parsing; configured signing keys | `apps/Agentweaver.Api/Endpoints/GitHubWebhookEndpoints.cs:14-118` |
| n2 | Due schedule: Claim scheduled occurrence; recover outstanding work | `apps/Agentweaver.Api/Workflows/WorkflowScheduleTriggerService.cs:145-199` |
| n3 | Trigger + authorization: Match and claim invocation; rejected: no work | `apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:69-116` |
| n4 | Pinned Ready task: Durable workflow choice; automation work item | `apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:69-116` |
| n5 | Atomic pickup: Claim and reserve a run; start reserved coordinator | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:187-240` |
| n6 | Coordinator run: Manual or backlog origin; RunOrigin recorded | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:339-372` |
| n7 | Approval policy: Autopilot controls unattended; not origin alone | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:403-408` |
| n8 | Available workflows: No invocation-kind filter; valid supplied candidates | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:288-300` |

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
| e0 | n0 -> n6 | Manual request starts a coordinator run | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:204-207` |
| e1 | n1 -> n3 | Verified event matches authorized workflow triggers | `apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:69-116` |
| e2 | n2 -> n3 | Due schedule obtains authorized durable invocation | `apps/Agentweaver.Api/Workflows/WorkflowScheduleTriggerService.cs:145-199` |
| e3 | n3 -> n4 | Authorized invocation publishes a pinned Ready task | `apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:69-116` |
| e4 | n4 -> n5 | Pickup atomically claims work and reserves run | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:187-240` |
| e5 | n5 -> n6 | Start the reserved coordinator run | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:240` |
| e6 | n6 -> n7 | Unattended confirmation follows approval/autopilot policy | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:370-408` |
| e7 | n7 -> n8 | Selection uses normally available definitions without origin filtering | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:288-300` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
