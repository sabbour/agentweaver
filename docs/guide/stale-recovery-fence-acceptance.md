# Check stale recovery request rejection

Use this case to check that two normal CLI requests with a prior recovery
fence receive the owner error `execution_recovery_stale`. The case also checks
that the observable run, root, recovery, runtime, gate, child, and session
journal bindings stay unchanged.

The case sends two POST requests to the same approved Gateway: one through
`api invoke recoverRun`, then one through `rest`. These requests need separate
permission to attempt an execution mutation. The expected path creates no
fixture and sends no model request.

## Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit under test. Use an explicitly approved non-production Gateway.
Sign in as the expected actor with the target tenant, project, and run binding.
Do not pass a token or credential in the input.

Confirm that the actor is allowed to attempt the two stale recovery requests.
Set `approvedExecutionPermission` to `true` only after that approval. The field
records approval; it does not grant a role or bypass Gateway authorization.

The run must have a settled recovery in `ready` state. Use its current
`checkpointRecovery.operationId` as `expectedRecoveryOperationId`. The case
reads the previous fence from authenticated recovery status and requires it to
be lower than the current fence. Provide the idempotency key from the successful
recovery as `originalRecoveryIdempotencyKey`; the case rejects either new key
if it matches that value.

## Prepare the input

Copy `scripts/acceptance/api/examples/stale-recovery-fence.json` to a new local
JSON file. Set every placeholder:

- `sourceCommit`: full source commit for the apphost.
- `targetCommit`: full source commit recorded for the target.
- `apphostSha256`: SHA-256 hash of the exact apphost file.
- `approvedNonProduction`: `true` after you confirm the target is approved.
- `approvedExecutionPermission`: `true` after you confirm permission for both
  rejected POST requests.
- `expectedActorId`: actor authorized for this case.
- `expectedRecoveryOperationId`: current ready recovery operation UUID.
- `originalRecoveryIdempotencyKey`: key used by the successful recovery.
- `caseInvocationId`: a new identifier for this case attempt.
- `target`: approved Gateway, Broker, client, audience, tenant, project, run,
  and root-session values.

The script derives and saves different idempotency keys for the `api invoke`
and `rest` requests before it sends either request. It uses a fixed body with
the previous fence, current run state version, fixed cause code, and case
reference. It omits `checkpointRecovery`.

## Validate and run the case

Keep the input and evidence outside the repository. Set the exact apphost and
copy the example input to a local file that you will edit. Choose a new
evidence directory:

```powershell
$env:AGENTWEAVER_CLI_PATH = (Resolve-Path .\artifacts\cli\win-x64\publish\agentweaver.exe).Path
$caseInput = Join-Path $HOME 'stale-recovery-fence.json'
Copy-Item .\scripts\acceptance\api\examples\stale-recovery-fence.json $caseInput
$runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N'))
$evidence = Join-Path $HOME "stale-recovery-fence-$runName"
```

Check the source and apphost identities, then validate the input without
starting the apphost or contacting the Gateway:

```powershell
git rev-parse HEAD
(Get-FileHash $env:AGENTWEAVER_CLI_PATH -Algorithm SHA256).Hash.ToLowerInvariant()
& $env:AGENTWEAVER_CLI_PATH --version
node scripts\acceptance\api\stale-recovery-fence.mjs --input $caseInput --output $evidence --dry-run
```

A successful dry run reports `validated-not-run`. It creates no evidence and
sends no command or network request. Run the case only after you confirm the
input, exact apphost, approved target, and permission for both POST attempts:

```powershell
node scripts\acceptance\api\stale-recovery-fence.mjs --input $caseInput --output $evidence
```

The case checks sign-in and API discovery, reads the current run and root
status, decisions, tree, and typed session-journal pages, then sends the first
stale request. It rechecks all bindings before it sends the second request.
It stops after an uncertain delivery, an unexpected response, or any change
to the observed state. It does not retry, generate a replacement key, or
perform cleanup.

## Read the result

The evidence directory contains `case-input.json`, `request-keys.json`,
`commands.jsonl`, and `result.json`. Command records are redacted.

`passed` with exit code `0` means both requests returned HTTP 409 with
`execution_recovery_stale`, and the observable bindings and journal events
stayed unchanged. The public API exposes no projection for Orchestrator
operation and outbox contents, so those inventory results remain blocked. The
session journal reports event history; it does not report those owner tables.
This case reports public request rejection and observable bindings. Separate
evidence is required for native no-send and physical-effect results.

`failed` with exit code `1` means a stale request was accepted. The result marks
cleanup as blocked. Stop and reconcile the run before any further request.
`blocked` with exit code `2` means the case lacked required permission or
evidence, delivery was uncertain, the owner returned another response, or
observed state changed. Preserve the evidence. Do not retry an uncertain
request or treat a blocked comparison as a rejection pass.
