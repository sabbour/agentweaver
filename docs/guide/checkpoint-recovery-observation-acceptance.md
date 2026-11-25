# Observe checkpoint recovery

Use this read-only case to observe a pending gate, saved child work, or an
OpenPullRequest receipt after Host recovery.
The case does not start recovery, answer the gate, send a message, or call a
model.

A `passed` result applies to the selected observation. A gate remains pending;
saved child identities remain pending or have a recorded checkpoint result;
or the original OpenPullRequest operation has a recorded receipt. After
observation also checks retained Orchestrator recovery events for one selected
operation. A passing event result proves that the owner recorded recovery
readiness.

## Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit you want to check. Use an approved non-production Gateway. Sign
in through the supported CLI flow as the expected actor with `api.read` and
`projects.orchestrator` scopes. The script does not sign in, launch a browser,
accept a bearer token, or create fixtures.

Prepare a root session with the gate or child work to observe. For
`committed-effect`, also select a running OpenPullRequest intent without a
checkpoint receipt. Run before-capture before recovery. Recover the Host through
the normal owner operation outside this case. Then use the returned recovery
operation ID and the before-capture file for after-observation.

## Prepare the input

Copy the example for your observation to a local JSON file outside the repository:

- Gate: `scripts/acceptance/api/examples/checkpoint-recovery-observation.json`
- Child: `scripts/acceptance/api/examples/checkpoint-recovery-child-join-observation.json`
- Pull request receipt: `scripts/acceptance/api/examples/checkpoint-recovery-committed-effect-observation.json`

Set these fields:

- `sourceCommit`: the full 40-character source commit for the apphost.
- `targetCommit`: the full source commit recorded for the Gateway target.
- `apphostSha256`: the SHA-256 hash of the exact apphost file.
- `approvedNonProduction`: keep `true` only for an approved test target.
- `expectedActorId`: the actor authorized for this case.
- `expectedRecoveryOperationId`: a valid UUID. The before-capture mode does
  not use this value. Set it to the recovery operation ID for after-observation.
- `expectedGateRequestId`: the pending gate request for `pending-gate`.
- `expectedEffectOperationId`: the OpenPullRequest operation UUID for
  `committed-effect`. This is distinct from the recovery operation UUID.
- For child and effect cases, operation UUIDs must not be all zero.
- `target`: the Gateway origin, Broker issuer, client ID, API audience, tenant,
  project, run, and root session.

The input rejects credentials, unknown fields, arbitrary commands and routes,
provider URLs, and unsafe target identifiers.

## Capture the state before recovery

Set `AGENTWEAVER_CLI_PATH` to the absolute path of the exact native apphost.
Use a new evidence directory for this capture.

```powershell
$env:AGENTWEAVER_CLI_PATH = (Resolve-Path .\artifacts\cli\win-x64\publish\agentweaver.exe).Path
$caseInput = 'C:\path\to\checkpoint-recovery-observation.json'
$beforeEvidence = 'C:\path\to\new-before-capture-evidence'
node scripts\acceptance\api\checkpoint-recovery-observation.mjs --input $caseInput --output $beforeEvidence
```

Keep the input in `before-capture` mode. The script records the apphost,
script, and input hashes; actor and target; status and decision reads; and all
pages from the run's session journal. The gate case records the request, actor,
choices, and freeform setting. The child cases record visible pending
association, child-session, and message identities from the checkpoint
projection and the original session tree. They also call `readExecutionPlan`
immediately before and after journal paging. Both reads must carry version-1
saved-plan fingerprints and bind to the same actor, project, run, root,
accepted selection hash, fence, decision version, plan, and checkpoint. New
child and effect captures use format version 2. Historical captures retain
their original format and exact script hash.

The result reports `capturePath` and `captureSha256`. Keep `capture.json`,
`case-input.json`, and `commands.jsonl` together in the before-capture
directory. Use the capture hash in the after-observation input. The script
checks the retained input and command records against the capture.

## Observe recovery

Run the normal Host recovery operation outside this case. Keep its returned
operation ID. Change the input to `after-observation`, set
`expectedRecoveryOperationId` to that ID, and set `beforeCaptureSha256` to the
hash reported by the before-capture result.

Use a new evidence directory and pass the retained `capture.json`:

```powershell
$afterEvidence = 'C:\path\to\new-after-observation-evidence'
node scripts\acceptance\api\checkpoint-recovery-observation.mjs --input $caseInput --output $afterEvidence --before-evidence (Join-Path $beforeEvidence 'capture.json')
```

The script uses `auth status`, `api operations`, `api describe`, authorization
context through `rest`, `readRunStatus`, `readSessionStatus`, `readDecisions`,
`readSessionTree`, paged `replayRunEvents`, and, for child and effect cases,
`readExecutionPlan`. After observation discovers and describes
`readCheckpointRecoveryEvents` through the CLI, then reads its pages for the
selected project, run, root session, and recovery operation. Before capture
does not discover or read recovery history. The script brackets journal and
recovery-event pages with status reads. It compares run and root-session
status through `api invoke` and `rest`.

The selected operation must be `ready` on run and root-session status, use a
different runtime and the next source fence, and report `nativeCache` or
`journalRebuild` material. The previous and replacement runtime registrations
have their own revision values; the case binds each value exactly and does not
compare the two numeric values. Both status surfaces
must retain the same operation, source checkpoint ID and revision, previous
and current execution fences, previous and replacement runtime IDs,
registration revisions, and material mode before and after event paging. The
gate case requires the same request, actor, choices, and freeform setting. A
gate event binds to the source checkpoint reported by current status; the
case does not compare that checkpoint to the before capture.

The event read must return the current paging envelope and a stable watermark.
The script follows each cursor without changing it, accepts sequence gaps,
and rejects duplicate IDs, non-increasing or unsafe sequences, cursor loops,
and changed page bindings. It requires a pending admission event for the
selected boundary and one version-1
`orchestrator.run.checkpoint_recovery_ready` witness. The witness must match
the stable status binding. The result retains the witness event ID and
sequence.

The child cases also require a released dispatch hold, a stable authorization
context, and the same source checkpoint captured before recovery. A later
checkpoint is accepted when the decision version is unchanged and the observed
child or receipt state is explained. If recovery used a different source
checkpoint, take a new before capture before a later recovery attempt.

For `child-join`, each captured child remains pending with the same association,
session, and message, or the original child is complete with a recorded
checkpoint result. For `committed-effect`, a matching
`committedAwaitingReceipt` / `reconcileCommitted` operation is `blocked`
until reconciliation records its receipt. A matching
`terminalReceiptRecorded` operation and succeeded step pass the receipt
observation. `intentOnly` / `mayRepeat` does not establish a Source Control
commit, so that after state is `blocked`.

When both version-1 fingerprints are stable, bound, and equal across recovery,
the child and receipt results mark full-plan content and dependency-graph
proof as `passed`. A different stable fingerprint after recovery fails with
`The saved plan changed during recovery.` A missing, malformed, unsupported,
unbound, or changing plan read blocks the observation. Completed-child proof
passes when at least one captured child completes and both post-recovery plan
reads link every captured child that completed to its original message and the
same receipt hash. Missing or changing links block this proof. A stable link
to a different message fails the observation. Use the same observer script
for the before capture and the after observation. The receipt result also leaves
physical no-duplicate proof and the `checkpoint-restart-duplicate-fence` P1
slot `blocked` because native-send and external-effect evidence is not in the
public reads. A readiness witness can pass while the effect receipt remains
blocked. Missing runtime identity blocks; equal old and new runtime IDs fail.

## Validate and read the result

Use `--dry-run` to check the input, apphost hash, before-capture hash, and
evidence path without starting the apphost or contacting the Gateway. A
successful dry run writes no evidence.

The script writes `case-input.json`, `commands.jsonl`, and `result.json` to
each new evidence directory. The before-capture directory also contains
`capture.json`.

| Result | Meaning |
| --- | --- |
| `captured` | The script retained the selected before-recovery observation and journal. |
| `passed` | The selected gate, child, or receipt observation meets its checks. |
| `blocked` | The sign-in, target, before capture, status, material, or journal evidence is missing or incomplete. For child and effect cases, changing or unbound API and REST reads are also blocked. |
| `failed` | The recovery, runtime, fence, gate, or a well-formed event for the selected operation contradicts the stable bound status. Differing API and REST reads fail for the pending-gate case. For child and effect cases, they fail when each source repeats the same different body with complete, stable binding. |
| `validated-not-run` | Dry-run validation passed; the script used no network and wrote no evidence. |

Exit codes are `0` for `captured`, `passed`, or `validated-not-run`, `1` for
`failed`, and `2` for `blocked`. Event discovery, transport, denial, and parse
problems block only committed recovery-event proof and leave the base result
unchanged. A bound contradiction fails the case with exit `1` while the
script retains the other proof results. Review the redacted command records
before you run after observation again. The witness proves only that the owner
recorded readiness; it does not prove journal delivery, run completion, or
physical effect uniqueness.
