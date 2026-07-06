# Trinity coordinator-only frontend cleanup

- Date: 2026-07-06T01:15:00-07:00
- Agent: Trinity
- Scope: apps/web frontend cleanup for coordinator-only UX

## Decision
Remove the dead single-run frontend path and the separate Review Policy settings layer. Overview attention links now target valid routes, and RAI/Scribe assembly execution opens the in-context Agent Sessions panel instead of the legacy Runs workflow page.

## Rationale
Agentweaver is coordinator-only: start-work flows should stay on StartOrchestration and coordinator run surfaces. Review gates are becoming workflow steps, so keeping a standalone Review Policy settings UI/API client creates dead frontend surface area. RAI/Scribe work is easier to understand in the coordinator session stream than in a separate Runs page.

## Validation
- npm --prefix apps/web run build
- npm --prefix apps/web test -- --run
