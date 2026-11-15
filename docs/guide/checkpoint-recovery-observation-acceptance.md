# Observe pending-gate recovery

Use this read-only case to check that Host recovery restored a pending gate
without changing its request, authorized actor, choices, or freeform setting.
The case does not start recovery, answer the gate, send a message, or call a
model.

A `passed` result confirms the selected durable recovery operation is ready,
uses restored runtime material, and keeps the captured gate pending. The
committed recovery-event proof remains **Blocked** because the public session
journal has no recovery-progress projection.

## Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit you want to check. Use an approved non-production Gateway. Sign
in through the supported CLI flow as the expected actor with `api.read` and
`projects.orchestrator` scopes. The script does not sign in, launch a browser,
accept a bearer token, or create fixtures.

Prepare a root session with the expected gate pending. Run the before-capture
mode before recovery. Recover the Host through the normal owner operation
outside this case. Then run after-observation mode with the returned recovery
operation ID and the before-capture file.

## Prepare the input

Copy `scripts/acceptance/api/examples/checkpoint-recovery-observation.json`
to a local JSON file outside the repository. Set these fields:

- `sourceCommit`: the full 40-character source commit for the apphost.
- `targetCommit`: the full source commit recorded for the Gateway target.
- `apphostSha256`: the SHA-256 hash of the exact apphost file.
- `approvedNonProduction`: keep `true` only for an approved test target.
- `expectedActorId`: the actor authorized for this case.
- `expectedRecoveryOperationId`: a valid UUID. The before-capture mode does
  not use this value. Set it to the recovery operation ID for after-observation.
- `expectedGateRequestId`: the pending gate request to preserve.
- `target`: the Gateway origin, Broker issuer, client ID, API audience, tenant,
  project, run, and root session.

The input rejects credentials, unknown fields, arbitrary commands and routes,
provider URLs, and unsafe target identifiers.

## Capture the gate before recovery

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
pages from the run's session journal. The capture records the pending gate
request, authorized actor, choices, and freeform setting from both decision
and session-status surfaces.

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
`readSessionTree`, and paged `replayRunEvents`. It compares run and root-session
status through `api invoke` and `rest`. It reads status and decision bindings
again after the journal pages. The script does not use caller-selected
operation status or an Orchestrator outbox event.

The case passes only when the selected operation is `ready`, the new runtime
differs from the prior runtime, the recovery fence is the next source fence,
and the run, root, and decisions report that fence. The case also requires
`nativeCache` or `journalRebuild` recovery material and the same pending gate
request, actor, choices, and freeform setting.

## Validate and read the result

Use `--dry-run` to check the input, apphost hash, before-capture hash, and
evidence path without starting the apphost or contacting the Gateway. A
successful dry run writes no evidence.

The script writes `case-input.json`, `commands.jsonl`, and `result.json` to
each new evidence directory. The before-capture directory also contains
`capture.json`.

| Result | Meaning |
| --- | --- |
| `captured` | The script retained a complete before-recovery gate and journal observation. |
| `passed` | The selected recovery is ready and the captured gate remains pending. |
| `blocked` | The sign-in, target, before capture, status, material, or journal evidence is missing or incomplete. |
| `failed` | The recovery, runtime, fence, gate, or API/REST responses contradict the expected state. |
| `validated-not-run` | Dry-run validation passed; the script used no network and wrote no evidence. |

Exit codes are `0` for `captured`, `passed`, or `validated-not-run`, `1` for
`failed`, and `2` for `blocked`. Review the redacted command records before
you retry. A missing recovery-progress event in the session journal does not
fail the recovery and does not prove a committed recovery event.
