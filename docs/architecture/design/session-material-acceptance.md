# Session-material acceptance scenario

The P1 harness records a bounded session-material scenario beside its selected
API, MCP, or UI journey. The surface result, exact-source evidence, controlled
source tests, and deployed runtime outcome remain separate evidence classes.
The scenario is part of the existing P1 result; it does not add another
acceptance program or change the native probe envelope.

## Evidence and boundaries

The scenario carries the checked deployment source SHA and tree, service
readbacks, provider-catalog receipt, and AgentHost image measurement into the
redacted JSONL result. It also records the authorized actor and tenant selectors,
the runner-owned project/run/session fence, invocation and fixture IDs, and
available selection revisions. It never retains bearer credentials.

The current authorized P1 client discovers and invokes Gateway operations.
Session-material writes and reads are internal Events owner routes and require
runtime authorization, while the supplied P1 identity provider issues a
run-bound user token. The runner-owned fixture is scoped to its selected P1
journey and has no approved session-material creation or cleanup boundary. The
current authorized acceptance path also exposes neither native Host replacement
and recovery nor fault hooks for owner revocation, stale registration, retry
conflicts, or missing required turn material.

These boundaries leave the deployed session-material scenario **blocked**.
The result records the intended typed turn route as not sent, its result as not
observed, and event ID, committed position, cursor, and runtime registration
revision as unavailable. It does not infer successful execution from a session
identifier, source receipt, or controlled fixture. The full P1 result cannot be
success-shaped while this required scenario is blocked; the selected surface
status remains independent.

## Required scenario when the authorized boundary exists

An authorized implementation must use the current owner routes and native
runtime path, bound to the same exact source, image, actor, tenant, project,
run, session, invocation, and current execution fence:

1. Submit one typed turn and retain redacted request and response evidence,
   the current runtime instance and registration revision, the owner
   acknowledgment, and the committed event position/cursor.
2. Replace the runtime through the supported owner lifecycle and prove that a
   compatible native cache restores the recorded session without another
   model turn.
3. Independently exercise absent and expired optional SDK cache. Both may
   rebuild from retained turns. Verify that rebuilding invokes no model turn
   and repeats no tool or external effect.
4. Separately prove that absent or expired required turn material fails the
   turn; it is not an optional cache miss.
5. Exercise current-authority revocation, stale fence/registration, exact
   duplicate retry and changed-payload retry, and the required-material
   failure through supported owner boundaries. If an authorized fault or
   restart boundary is unavailable, preserve the exact gap as blocked.
6. Clean up only invocation-owned disposable fixtures through their recorded
   owner path. Keep every cleanup result visible; do not sweep containers or
   remove historical objects.

## Controlled evidence

`RuntimeRegistrationOwnerIntegrationTests`,
`RuntimeSessionRecoveryTests`, and `SessionMaterialPostgresTests` provide
controlled owner, runtime-recovery, and material-store source evidence. They
are not deployed acceptance. The P1 scenario records these source references
as not executed; it does not convert their existence into a live result.
The 0.x checkpoint-store and Host round-trip tests inform the fresh-instance
recovery and controlled-turn pattern, but their APIs and outcomes are not
v1 evidence.

The JSONL scenario records non-attempted operations explicitly, distinguishes
optional-cache recovery from required-turn failure, and preserves the precise
authorized-boundary blockers. It performs no live operation and creates no
additional fixture, identity, or cleanup work.
