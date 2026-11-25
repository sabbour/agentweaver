# Run native CLI API cases

Use this guide to run targeted public API checks through the native CLI. Each
case lists its target, required identity, side effects, and evidence.

The read-only case checks Gateway discovery and the signed-in actor's
authorization context. It does not test mutations, model execution, or full P1
acceptance. The API launchers run checked-in case scripts through the native
apphost; they do not provide a separate API runner.

## Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit you want to test. Use an explicitly approved non-production
Gateway. You must already be signed in through the supported CLI flow for the
actor and tenant in the case input. The script does not sign in, launch a
browser, accept a bearer token, or create fixtures.

The read-only script uses `auth status`, `api operations`, `api describe`, and
`rest --method GET` with the same Gateway target. It makes no resource changes
and sends no model request. The `rest` command still uses normal CLI
authentication and Gateway authorization.

## Prepare the case input

Copy `scripts/acceptance/api/examples/read-only-gateway.json` to a new local
JSON file. Replace every example value with the approved case data:

- `sourceCommit`: the full 40-character source commit for the apphost.
- `targetCommit`: the full 40-character source commit recorded for the target.
- `apphostSha256`: the SHA-256 hash of the exact apphost file.
- `approvedNonProduction`: set to `true` only after you confirm the target is
  approved for this test. This field records your approval; the script cannot
  verify the target's deployment status.
- `expectedActorId`: the actor ID authorized for this case.
- `target`: the Gateway origin, Broker issuer, client ID, API audience, and
  tenant ID for that actor.

Do not add credentials, tokens, private payloads, project IDs, or run IDs. The
input accepts only the fields in the example. It rejects unknown fields and
legacy runner fields.

## Validate and run the case

1. Set `AGENTWEAVER_CLI_PATH` to the absolute path of the exact native
   apphost. Keep the case input and evidence outside the repository. Set
   `$caseRoot` to a new local directory, then copy the example input there.
   Set `$evidence` to a new directory below `$caseRoot`.

   ```powershell
   $env:AGENTWEAVER_CLI_PATH = (Resolve-Path .\artifacts\cli\win-x64\publish\agentweaver.exe).Path
   $caseRoot = Join-Path $HOME 'agentweaver-acceptance'
   New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
   $caseInput = Join-Path $caseRoot 'read-only-gateway.json'
   Copy-Item .\scripts\acceptance\api\examples\read-only-gateway.json $caseInput
   $runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N'))
   $evidence = Join-Path $caseRoot "read-only-gateway-$runName"
   ```

2. Check the source and apphost identities before you run the case.

   ```powershell
   git rev-parse HEAD
   (Get-FileHash $env:AGENTWEAVER_CLI_PATH -Algorithm SHA256).Hash.ToLowerInvariant()
   & $env:AGENTWEAVER_CLI_PATH --version
   ```

   Put the full source commit and SHA-256 result in the input. The script checks
   the apphost hash again before it starts a CLI command. Keep the target
   commit separate from the source commit.

3. Validate the input without starting the apphost or contacting the Gateway.

   ```powershell
   node scripts\acceptance\api\read-only-gateway.mjs --input $caseInput --output $evidence --dry-run
   ```

   A successful dry run reports `validated-not-run`. It does not create the
   evidence directory. It checks the input, the absolute apphost path and name,
   the apphost file and its SHA-256 hash, and that the evidence path is new.
   It does not execute the apphost or contact the Gateway.

4. If the actor is not signed in, use the normal `agentweaver auth login`
   command for the same target and `api.read` scope. Do this only when browser
   sign-in is authorized. The script never starts sign-in. Pass the same
   `--gateway-url`, `--broker-issuer`, `--client-id`, `--api-audience`, and
   `--tenant-id` values that appear in the case input.

5. Run the case with a new evidence directory.

   ```powershell
   node scripts\acceptance\api\read-only-gateway.mjs --input $caseInput --output $evidence
   ```

   The script checks the apphost hash, captures its version, then runs the
   normal CLI commands as separate processes. It records the apphost and case
   script hashes, redacted command arguments, output, status, timestamps, and
   the final result. It does not overwrite an existing evidence directory.

## Verify the result

The script writes `commands.jsonl` and `result.json` in the new evidence
directory. A `passed` result (exit code `0`) means only that the expected actor
and tenant were reported, the Gateway listed and described
`getAuthorizationContext`, and `rest` returned HTTP 200 with the expected
actor and tenant.

An unauthenticated actor or missing Gateway operation is `blocked` (exit code
`2`). An unexpected response, identity mismatch, timeout, or command error is
`failed` (exit code `1`). A failed or interrupted run is not a pass. Review the
redacted command records before you retry.

## Run workflow generation

The `workflow-generation` case uses normal `agentweaver api invoke` commands
through the exact native apphost. It checks the current actor and accepted
selection, creates or reattaches a durable generation job, polls its status,
and verifies the generated draft, artifact hash, and native completion
evidence. It checks the stored assistant reply against the workflow completion
receipt without keeping the reply text. The job and draft remain stored after
the case.

The case sends a model request and cancels the named run after it confirms the
run identity. Use an explicitly approved non-production Gateway and a
disposable run with a bounded model budget. The case is complete only when it
verifies that the run reached `cancelled`. It does not launch a browser or
delete the job or draft.

### Before you begin

You need Node.js 24, the self-contained native apphost for the source commit,
and an approved non-production Gateway. Prepare a disposable run whose accepted
root session and model selection match the case. The same actor and tenant
need two CLI identities:

- A run-bound `projects.orchestrator` identity for the exact project and run.
- An unbound `projects.admin` identity with no project or run selector.

The case checks both identities before it submits the generation request. If
you need to sign in, use the normal `agentweaver auth login` flow for each
identity. Browser sign-in needs explicit authorization.

### Prepare the case input

Copy `scripts/acceptance/api/examples/workflow-generation.json` to a new local
JSON file. Replace its example values with the approved case data:

- `sourceCommit`: the full 40-character source commit for the apphost.
- `targetCommit`: the full 40-character source commit recorded for the target.
- `apphostSha256`: the SHA-256 hash of the exact apphost file.
- `approvedNonProduction` and `approvedDisposableRun`: set both to `true`
  after you approve the target and disposable run.
- `expectedActorId`: the actor ID authorized for the project and run.
- `expectedDecisionStateVersion`: the current accepted decision-state version.
- `expectedSelection`: the exact project configuration revision, model
  selection reference, and run limits returned by the accepted selection.
- `prompt`: the prompt for the draft workflow.
- `target`: the Gateway origin, Broker issuer, client ID, API audience, tenant,
  project, run, and accepted root session IDs.

Do not add credentials, tokens, or private prompt data. The input accepts only
the fields in the example and rejects unknown fields.

### Validate and run the case

1. Set `AGENTWEAVER_CLI_PATH` to the absolute path of the exact native
   apphost. Keep the input and evidence outside the repository. Use a new
   evidence directory:

   ```powershell
   $env:AGENTWEAVER_CLI_PATH = (Resolve-Path .\artifacts\cli\win-x64\publish\agentweaver.exe).Path
   $caseRoot = Join-Path $HOME 'agentweaver-acceptance'
   New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
   $caseInput = Join-Path $caseRoot 'workflow-generation.json'
   Copy-Item .\scripts\acceptance\api\examples\workflow-generation.json $caseInput
   $runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N'))
   $evidence = Join-Path $caseRoot "workflow-generation-$runName"
   ```

2. Record the apphost source and hash in the input:

   ```powershell
   git rev-parse HEAD
   (Get-FileHash $env:AGENTWEAVER_CLI_PATH -Algorithm SHA256).Hash.ToLowerInvariant()
   & $env:AGENTWEAVER_CLI_PATH --version
   ```

3. Validate the input without starting the apphost or contacting the Gateway:

   ```powershell
   node scripts\acceptance\api\workflow-generation.mjs --input $caseInput --output $evidence --dry-run
   ```

   A successful dry run reports `validated-not-run`. It checks the input,
   apphost path and hash, and evidence path. It does not execute the apphost,
   contact the Gateway, or create evidence.

4. If either identity is not signed in, use `agentweaver auth login` with the
   matching scope and selectors. Sign in to `projects.orchestrator` with the
   project and run selectors. Sign in to `projects.admin` without either
   selector. Use the same actor, tenant, and target options. The case does not
   start sign-in.

5. Run the case:

   ```powershell
   node scripts\acceptance\api\workflow-generation.mjs --input $caseInput --output $evidence
   ```

   The script checks the exact apphost hash, sign-in state, caller, and
   accepted selection. Before submission, it checks that the apphost and
   Gateway support the turn digest read. It then uses `api invoke` to submit
   the generation request, poll job status, and read the completed result.
   It reads the stored reply's hash before cancelling the disposable run
   and verifying its status. If the first submission returns
   HTTP 503 with `workflow_generation_queue_full`, the case sends the same
   request with the same `Idempotency-Key` once more. It does not retry other
   submission failures or timeouts.

   The command log records submission attempts and turn digest reads without
   their arguments or output. It redacts request bodies and headers for other
   commands. The log contains the draft returned by the result read and the
   target IDs. The result records the artifact ID, definition hash, and the verified reply's
   project, run, session, and event IDs and hash. Store the evidence with the
   project's access controls.

### Verify the result

The evidence directory contains `commands.jsonl` and `result.json`. A `passed`
result (exit code `0`) requires a completed generation, matching workflow
definition hash and artifact ID, valid native completion evidence, a matching
stored reply hash, and verified run cancellation. The result records generation
and cancellation separately. This case checks that the run reached `cancelled`.
It does not check whether runtime cleanup finished; to verify owned runtime
cleanup after cancellation, check `readRunStatus.runtimeCleanup` for matching
run identity, a positive total, all tracked cleanup complete, and zero pending.

A missing sign-in, missing turn digest support, missing operation, or
caller or selection mismatch is `blocked` (exit code `2`). A generation
error, timeout, response mismatch, or unverified cancellation is `failed`
(exit code `1`). A failed or blocked case is
not a pass.

This route-specific case checks workflow generation. It does not replace full
P1 API acceptance.

## Verify zero-credit workflow generation and cleanup

The `workflow-generation-credit-zero` case checks a zero-credit
workflow-generation denial and verified disposable-run cleanup through the
native CLI. It requires a newly provisioned disposable run for each attempt.

### Before you begin

Prepare a disposable run on an approved non-production Gateway with:

- `modelSelection.sourceMode` set to `"hostedCopilot"`.
- `runLimits.copilotHardCreditLimit` set to numeric `0`.
- A ready root coordinator session in a nonterminal run (`runtimeStartup.state: "ready"`).
- Initial decision state (`stateVersion: 1`) matching the session `executionFence`.

Sign in with both the run-bound `projects.orchestrator` identity (for reads)
and the unbound `projects.admin` identity (for cancellation).

### Prepare and run the case

Copy `scripts/acceptance/api/examples/workflow-generation-credit-zero.json` to
a new local JSON file outside the repository and replace the example values.
Set `expectedDecisionStateVersion` to `1` and
`expectedSelection.runLimits.copilotHardCreditLimit` to `0`.

Validate the input with `--dry-run`, then run the case with a new evidence
directory:

```powershell
node scripts\acceptance\api\workflow-generation.mjs --input $caseInput --output $evidence --dry-run
node scripts\acceptance\api\workflow-generation.mjs --input $caseInput --output $evidence
```

### How the case checks the proof and cleanup

1. Preflight checks the apphost hash, both sign-ins, caller context, accepted
   selection (including `hostedCopilot` and an explicit JSON `0` credit limit),
   ready root session status, and initial decision state (`stateVersion: 1`).
   A failed preflight exits without submitting generation or cancelling the run.
2. Before submitting generation, the script saves `cancellation-request.json`
   (the target, cancellation `Idempotency-Key`, and request body) in the
   evidence directory. It calls `generateProjectWorkflow` once and never
   retries generation, even on a timeout or `workflow_generation_queue_full`.
3. It polls `readProjectWorkflowGenerationJob` and saves the redacted
   `zeroCreditNoSend` proof to `zero-credit-proof.json` before cancelling the
   run. Any completed job, non-null artifact, or mismatched proof fails the
   case. It never fetches a workflow draft.
4. It calls `cancelProjectRun` and polls `readRunStatus` up to a 90-second
   deadline, allowing at most two same-key cancellation replays at least five
   seconds apart after a lost response, HTTP 503 `runtime_start_queue_full`,
   or pending cleanup. It stops on authority denial, conflict, or mismatched
   output.

### Verify the result

The case passes (exit code `0`) only when preflight passes, `zeroCreditNoSend`
matches the ready session registration and fence, `readRunStatus` reports
`executionState: "cancelled"`, and `runtimeCleanup` reports `state: "complete"`,
`total > 0`, `pending: 0`, and `complete === total`. Passing this dedicated
case promotes the `credit-zero` row in `numericMatrix` to `passed`.

A missing job ID or missing proof at the deadline leaves `credit-zero`
`blocked` (exit code `2`) when cleanup completes. If cleanup remains pending,
the case fails (exit code `1`) and retains `cancellation-request.json` for
authorized follow-up.

## Run task model completion

The `task-model-completion` case dispatches an already claimed task through the
public Gateway. It checks the accepted model selection and hard budget,
owner-derived task completion, the matching child receipt, the stored assistant
reply, priced usage, and disposable-run cleanup.

The case sends a model request and cancels the named run after it confirms the
run identity. Use an approved non-production Gateway and a disposable run.

### Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit you want to test. Prepare a disposable run with:

- An accepted root session and a claimed task.
- A confirmed one-item WorkPlan for that task.
- A BYOK model selection pinned to the expected model.
- A run-level hard budget for `byok.tokens` in `USD`.
- Run limits that match the case input.

Sign in as the expected actor and tenant with two CLI identities:

- A run-bound `projects.orchestrator` identity for the exact project and run.
- An unbound `projects.admin` identity, with no project or run selector, to
  cancel the disposable run.

The case does not sign in or launch a browser. Use the supported CLI login
flow before running it. Browser authorization needs explicit approval.

### Prepare the case input

Copy `scripts/acceptance/api/examples/task-model-completion.json` to a new
local JSON file outside the repository. Replace the example values:

- `sourceCommit`: the full 40-character source commit for the apphost.
- `targetCommit`: the full source commit deployed to the Gateway target.
- `apphostSha256`: the SHA-256 hash of the exact apphost file.
- `approvedNonProduction` and `approvedDisposableRun`: keep both `true` after
  you approve the target and disposable run. These values record your approval;
  the script cannot determine the target's deployment status.
- `expectedActorId`: the actor authorized for the project and run.
- `expectedDecisionStateVersion`: the current decision-state version for the
  confirmed plan.
- `expectedSelection`: the exact project configuration revision, model
  selection reference, model ID, maximum USD spend, and run limits.
- `expectedTask`: the task ID and revision, claim ID, WorkPlan ID, workflow ID
  and revision, and the task's WorkPlan association ID.
- `target`: the Gateway origin, Broker issuer, client ID, API audience, tenant,
  project, run, and root session IDs.

Set `expectedSelection.maxRunUsd` to the approved spending cap, up to USD 5.
The accepted selection must contain one run-scoped `byok.tokens` budget in
`USD` with a positive hard limit that does not exceed `maxRunUsd`. The
`reservePerDispatch` value must also be positive. The case input accepts only
its documented fields. Do not add credentials, tokens, prompts, or task content.

### Validate and run the case

1. Set the native apphost path and create a new input and evidence path outside
   the repository:

   ```powershell
   $env:AGENTWEAVER_CLI_PATH = (Resolve-Path .\artifacts\cli\win-x64\publish\agentweaver.exe).Path
   $caseRoot = Join-Path $HOME 'agentweaver-acceptance'
   New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
   $caseInput = Join-Path $caseRoot 'task-model-completion.json'
   Copy-Item .\scripts\acceptance\api\examples\task-model-completion.json $caseInput
   $runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N'))
   $evidence = Join-Path $caseRoot "task-model-completion-$runName"
   ```

2. Record the apphost source, target commit, and hash in the case input:

   ```powershell
   git rev-parse HEAD
   (Get-FileHash $env:AGENTWEAVER_CLI_PATH -Algorithm SHA256).Hash.ToLowerInvariant()
   & $env:AGENTWEAVER_CLI_PATH --version
   ```

3. Validate the input without starting the apphost or contacting the Gateway:

   ```powershell
   node scripts\acceptance\api\task-model-completion.mjs --input $caseInput --output $evidence --dry-run
   ```

   A successful dry run reports `validated-not-run`. It checks the case input,
   apphost path and hash, and evidence path. It does not execute the apphost,
   contact the Gateway, or create evidence.

4. Sign in with both identities for the same target. Use the project and run
   selectors for `projects.orchestrator`. Do not use either selector for
   `projects.admin`. The case checks both sign-ins, caller context, accepted
   selection, and decision state before it dispatches the task.

5. Run the case:

   ```powershell
   node scripts\acceptance\api\task-model-completion.mjs --input $caseInput --output $evidence
   ```

   The case calls `dispatchCoordinatorExecution` at
   `POST /api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/dispatch`.
   The Gateway and native CLI use a ten-minute deadline for each dispatch
   request. The case retries the same request while child startup is pending.
   A terminal dispatch failure stops retries. Before dispatch, the case checks
   that the apphost supports `--turn-content-sha256`. It uses `api describe`
   to check the Gateway turn read's required inputs and HTTP 200 JSON response.
   After dispatch completes, the case reads the
   assistant turn in the child session once. The stored assistant reply is
   verified against the child receipt by matching the reply's hash.

   After it verifies task completion and run usage, the case cancels the
   named run and waits for `runtimeCleanup.state` to reach `complete` with no
   pending work. The case
   also attempts cleanup after a dispatch or turn read failure. It does not
   dispatch again after a turn read failure.

   `commands.jsonl` records command steps, timing, and exit status. It does
   not record arguments, standard output, or standard error. `result.json`
   records the selected model, task and completion hashes, verified turn IDs
   and hash, usage totals, and cleanup result.

### Verify the result

The evidence directory contains `commands.jsonl` and `result.json`. A
`passed` result (exit code `0`) requires matching task-completion evidence and
child receipt, a matching stored reply hash, fully priced BYOK usage within the
accepted hard limit, and a cancelled run with complete runtime cleanup.

A missing sign-in, missing apphost capability or operation, caller mismatch,
selection mismatch, or non-dispatchable decision is `blocked` (exit code `2`). A dispatch error,
timeout, evidence or usage mismatch, run that does not reach `cancelled`, or
runtime cleanup that remains pending is `failed` (exit code `1`). A failed or
blocked case is not a pass.

This route-specific case checks task dispatch and completion. It does not
replace full P1 API acceptance.

## Run the Workspace repository-read case

The `workspace-repository-read` case reads one file from the Repo App
repository of a project through the `agentweaver workspace` commands. It makes
no changes. It opens three or more browser approvals, so a person must approve
each one as the expected actor.

Before you begin, make sure that you have these items:

- An approved non-production target.
- A project that uses a GitHub App repository connection.
- A GitHub App installation that can see the exact `owner/name` repository.
- A sign-in as the expected actor with `agentweaver auth login --scope
  projects.admin` and the target options, without `--project-id` or
  `--run-id`.

Copy `scripts/acceptance/api/examples/workspace-repository-read.json` to a new
local JSON file. Set the same fields as the read-only case, and set the
`fixture` fields: `projectId`, `repository` (`owner/name` with the exact case),
`refKind` (`branch` or `tag`), `refName`, `commitSha`, `filePath`, `byteLength`,
and `sha256` of the file.

```powershell
$env:AGENTWEAVER_CLI_PATH = "C:\approved\agentweaver.exe"
node scripts/acceptance/api/workspace-repository-read.mjs --input .\workspace-case.json --output .\evidence\workspace-repository-read-001 --interactive
```

The script runs `--version`, `api operations`, `auth status`, and `rest` for the
authorization context. If the actor or tenant is not the expected one, the case
fails before any approval. Then the script runs `workspace refs` until it finds
the ref, `workspace tree`, and `workspace file`. It compares the ref, the commit,
the file size, the SHA-256 hash, and the Git blob SHA. It never writes the file
content to the evidence. Each workspace command can wait 11 minutes for the
browser approvals.

A missing sign-in, a Broker error, or an approval that does not complete in time
is `blocked` (exit code `2`). A mismatch or an unexpected response is `failed`
(exit code `1`). Until a live run with approved input passes, treat this case as
blocked. Do not use internal CLI types, extract a selection code, or bypass
sensitive-header guards.

## Read back a pending BYOK provider host request

The `runtime-byok-provider-readback` case checks one known, pending
bring-your-own-key (BYOK) model-provider host request through
`listPlatformEgressDestinations` using the native CLI. It does not create,
approve, or change any host request.

### Before you begin

You need Node.js 24, the self-contained native apphost for the source commit,
an approved non-production Gateway, and an existing pending BYOK model-provider
host request. Sign in as the expected actor with an unbound `projects.admin`
identity that holds `readPlatformRuntimeDefaults` on `platform/default`:

```powershell
agentweaver auth login --scope projects.admin --gateway-url <GATEWAY_URL> --broker-issuer <BROKER_ISSUER> --client-id <CLIENT_ID> --api-audience <API_AUDIENCE> --tenant-id <TENANT_ID>
```

Do not pass `--project-id` or `--run-id`.

### Prepare and run the case

1. Copy `scripts/acceptance/api/examples/runtime-byok-provider-readback.json`
   to a local JSON file outside the repository. Replace the example values for
   `sourceCommit`, `targetCommit`, `apphostSha256`, `approvedNonProduction`,
   `expectedActorId`, `target`, and `expectedRequest` (`requestId`, `tenantId`,
   `scope`, `projectId`, `providerId`, `providerRevision`, `host`, and `port`).
2. Set `AGENTWEAVER_CLI_PATH` to the absolute path of the native apphost and
   run a dry run first to check the local input and apphost hash:

   ```powershell
   $env:AGENTWEAVER_CLI_PATH = (Resolve-Path .\artifacts\cli\win-x64\publish\agentweaver.exe).Path
   node scripts\acceptance\api\runtime-byok-provider-readback.mjs --input .\provider-readback.json --output .\evidence\provider-readback-001 --dry-run
   ```

   A dry run starts no CLI process and creates no evidence directory.

3. Run the case with a new evidence directory:

   ```powershell
   node scripts\acceptance\api\runtime-byok-provider-readback.mjs --input .\provider-readback.json --output .\evidence\provider-readback-001
   ```

   The script removes ambient target, project, and run environment variables,
   verifies the unbound `projects.admin` authorization context, and pages
   through `listPlatformEgressDestinations` with `state=requested`. It processes
   list pages in memory and writes only allowlisted command status, timing
   summaries, static error codes, and the matched destination fields to
   `commands.jsonl` and `result.json`. If a run fails, start a new run from
   page 1 with a new evidence directory.

### Verify the result

A `passed` result (exit code `0`) confirms that the unbound actor matched and
that `listPlatformEgressDestinations` returned the exact `expectedRequest` with
`state: "requested"` and `origin: "modelProvider"`. A missing sign-in or missing
Gateway operation returns `blocked` (exit code `2`). Any identity, binding,
pagination, matching, HTTP, parsing, or timeout error returns `failed` (exit
code `1`) with a static allowlisted `errorCode`.

## Next step

Use the [Testing guide](./testing.md) to choose other checks and understand
their limits. The read-only case does not prove a live model; the
workflow-generation case exercises one model-backed Gateway flow but does not
prove full P1 acceptance. To inspect an existing numeric tool-denial, root
model-turn, or hard-credit observation through normal CLI commands and `rest`, use the
[numeric runtime observation case](./numeric-runtime-observation-acceptance.md).
