# How work enters the coordinator - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
