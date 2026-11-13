# Specification: Recover a committed pull request effect

**Status:** Proposed. Focused local design review complete.

This specification covers PR2b3 of [#2748](https://github.com/sabbour/agentweaver/issues/2748).
It targets Agentweaver v1. The source baseline is `origin/v1` at
`90da05454e71044e3aa450e27f0b41e1b6762ed5`. This is a source design, not
live-acceptance, release, or deployment evidence.

## Problem and goals

Child-join recovery rejects every OpenPullRequest intent. This also rejects a
run where the Source Control owner committed the original pull request, but the
checkpoint did not record its receipt before the Host stopped.

Allow that recovery only when the existing Source Control operation proves the
same pull request was committed. Keep the operation ID, request, provider pin,
and result fingerprint. Reconcile the original result under current run
authority. Do not create a second pull request.

## Non-goals

- Do not add a new recovery boundary or public recovery route.
- Do not recover OpenPullRequest when there is no child-join history.
- Do not recover BuildTest, Merge, Scribe, review, RubberDuck, generic tool, or
  other external-effect history.
- Do not retry an unresolved or uncertain operation.
- Do not add a fault-injection seam, live-acceptance case, or new storage
  format.
- Do not change #2604 delivery requirements, #2714 release acceptance, or
  other v1.1 milestone work.

## Source-backed baseline

At the source SHA above:

- `CoordinationOwnerStore.RequireNoChildJoinPlatformHistory` rejects all
  OpenPullRequest intents and receipts. `RequireGateCheckpointBoundary` also
  rejects platform effects.
- `MafOpenPullRequestExecutor` saves an intent before the owner call. It stores
  the terminal receipt in a later checkpoint transaction.
- `SourceControlOwnerStore.BeginOpenPullRequestOperationAsync` stores a stable
  operation ID, request fingerprint, effect fingerprint, original
  authorization fingerprint, phase, pull request number, and result
  fingerprint. It commits the pending claim before the provider call.
- `SourceControlEndpoints.CreateOrRecoverOpenPullRequestAsync` reads the
  recorded pull request number on exact retries. When an operation has no
  result, it sets `OwnerMayCreate` to false for a retry.
- `MafOpenPullRequestContract.EffectRequestFingerprint` excludes the fence and
  decision version. It includes the operation, source pin, workspace, and
  pull-request payload. The owner-request and original-authorization
  fingerprints include the authorization binding. Therefore a recovered
  request has a new owner-request fingerprint even though it has the same
  effect fingerprint.
- `SourceControlOwnerStore.BeginOpenPullRequestOperationAsync` currently
  rejects a changed owner-request or authorization fingerprint. The endpoint
  also calls the operation-completion update after it reads a saved result.
  Recovery must change both behaviors for the committed-result path.
- The stored Source Control pin is bound to the old fence. Normal pin reads
  reject that pin after recovery advances the fence. Reconciliation therefore
  needs a narrow read-only path that uses the old pin under current run
  authority; it must not replace the stored pin or permit a new operation.
- `MafExecutionCheckpointStore.AppendRecoveryCheckpointInTransactionAsync`
  preserves checkpoint work and records the source checkpoint hash and
  recovery lineage while it advances the state revision and decision version.
- `MafExecutionRecoveryProjector` currently reports an intent without a
  terminal receipt as `mayRepeat`.

The matching v0.x `OpenPullRequestWorkflowTests` cover ordinary request
handling and provider failure. They do not prove durable commit-before-ack
recovery. The v1 operation ledger and checkpoint tests are the relevant prior
art. PR2b2 parts A, B, C, D1, D2, and D3 are already merged in #2771–#2776 and
are not part of this change.

## Intended behavior and requirements

The existing child-join recovery path may proceed only when all normal
child-join checks pass, the checkpoint has exactly one platform-effect history
item (one OpenPullRequest intent and no receipt), and its step is `Running`.
The matching `source_control_pull_request_operations` row must:

- match the saved operation ID, identity, pin, request fingerprint, effect
  fingerprint, and original authorization fingerprint;
- have state `committed`, a pull request number, a result fingerprint, and a
  completion time; and
- have no changed request or result hash.

Admission checks this evidence before it advances the run fence. Any missing,
pending, uncertain, mismatched, or unsupported effect keeps the existing
fail-closed behavior. It does not create a recovery operation or advance the
fence.

After recovery, dispatch verifies that the checkpoint's recovery lineage points
to the saved checkpoint and that the current selection, pin, plan, step, and
effect still match the saved intent. It creates a transient owner request from
that intent. The request keeps the original operation ID, selection, pin,
workspace, and pull-request payload. It replaces only the decision version and
execution fence with their current values. The effect fingerprint must remain
identical.

The Source Control owner validates the transient request and current runtime
authority. It accepts a changed owner-request and authorization fingerprint
only when the existing operation is `committed` and the effect fingerprint
matches exactly. It does not change the stored request, authorization, or
result fingerprints. It reads the stored pull request number; it does not call
the create path. It checks the returned result against the stored result
fingerprint. It does not run the operation-completion update for a result that
is already committed.

The Source Control pin remains bound to its original fence and keeps its
original pin ID. For this replay only, the owner validates the old pin's
selection, actor, tenant, and provider binding against current run authority,
then uses a transient pin binding with the current fence. This path is allowed
only for an existing committed operation with the exact effect fingerprint.
It does not update the stored pin or create a missing operation.

The dispatch executor skips its separate workspace-diff request for this
replay because that route requires a current pin. The OpenPullRequest owner
route still checks the exact workspace incarnation and base/head revisions
before and after reading the committed pull request, using the transient pin
binding.

The executor validates the result against the original saved request. It then
writes the original terminal receipt under the current checkpoint lock and
compare-and-swap. Concurrent retries return the same result. Only one retry
records the receipt and terminal checkpoint transition.

The public execution-recovery projection reports this saved case as
`reconcileCommitted`, not `complete`. It uses the persisted recovery lineage
that was written only after the committed operation passed admission. Recovery
readiness does not mean the workflow step or run is complete. A later exact
dispatch retry must record the terminal receipt. A stale Host cannot use the
old run binding.

## Contracts and boundaries

The Source Control operation row remains the authority for the original
effect. Do not copy it into a second effect ledger. The checkpoint recovery
lineage and current run authorization permit only a read and import of that
same result. They do not authorize a new create.

No public route or request schema changes are planned. Existing recovery,
execution-recovery status, and ordinary dispatch operations remain the user
surface. The changed component is `Agentweaver.Orchestrator`.

## Failure and recovery

- A missing or unresolved operation blocks admission before the fence changes.
- A changed pin, selection, workspace, request, or effect fingerprint is a
  conflict.
- A changed or unverifiable provider result does not produce a terminal
  receipt. It does not authorize another create.
- Exact retries retain the original operation ID and read the original result.
- Other effect types remain unsupported and do not become safe through
  conversation or journal restoration.

## Acceptance and validation

| Criterion | How to verify | State or evidence |
| --- | --- | --- |
| Admit only a committed, exact OpenPullRequest operation | Controlled checkpoint and Source Control owner regressions | PostgreSQL regression added and compiled; hosted PostgreSQL CI is required to run it. |
| Reject missing, pending, indeterminate, changed, and unsupported effect history before fence advance | Focused recovery regressions | PostgreSQL rejection regressions added and compiled. Local boundary tests passed; hosted PostgreSQL CI is required. |
| Reauthorize with transient current request and pin bindings without changing the saved effect, pin, or original owner-request and authorization fingerprints | Owner operation and dispatch regressions | PostgreSQL owner and dispatch regressions added and compiled; hosted PostgreSQL CI is required. |
| Concurrent retries read one result and record one terminal receipt | Controlled executor/checkpoint regression and hosted PostgreSQL CI | Concurrent replay regression added and compiled; hosted PostgreSQL CI is required. |
| Keep other platform effects blocked and report the committed effect as pending reconciliation | Recovery projector and boundary regressions | The focused non-PostgreSQL recovery tests passed locally. |
| Update recovery guidance and the maintained recovery diagram | Documentation build, links, diagram checks | Documentation build, link check, diagram checks, and diagram tests passed locally. |

No live acceptance, real-model call, deployment, package publication, or
production/cluster operation is in scope.

## Rollout and compatibility

This change needs no database migration. It reuses the existing Source Control
operation record and checkpoint recovery lineage. Existing clients use the
same recovery and dispatch operations. No release or deployment action is
included.

## Open questions and decisions

This slice supports only one committed OpenPullRequest effect at the existing
child-join boundary. Other effect owners need their own reviewed recovery
contract before they can be added.
