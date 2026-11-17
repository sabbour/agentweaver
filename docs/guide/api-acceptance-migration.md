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
evidence. The job and draft remain stored after the case.

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
   accepted selection. It then uses `api invoke` to submit the generation
   request, poll job status, and read the completed result. It cancels the
   disposable run and verifies its status. The command log redacts request
   bodies and headers; the result records the generated artifact ID and
   definition hash. The command log also contains the returned draft and target
   IDs. Store the evidence with the project's access controls.

### Verify the result

The evidence directory contains `commands.jsonl` and `result.json`. A `passed`
result (exit code `0`) requires a completed generation, matching workflow
definition hash and artifact ID, valid native completion evidence, and a
verified cancelled run. The result records generation and cleanup separately.

A missing sign-in, missing operation, or caller/selection mismatch is
`blocked` (exit code `2`). A generation error, timeout, response mismatch, or
unverified cleanup is `failed` (exit code `1`). A failed or blocked case is
not a pass.

This route-specific case checks workflow generation. It does not replace full
P1 API acceptance.

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

## Next step

Use the [Testing guide](./testing.md) to choose other checks and understand
their limits. The read-only case does not prove a live model; the
workflow-generation case exercises one model-backed Gateway flow but does not
prove full P1 acceptance. To inspect an existing numeric tool-denial, root
model-turn, or hard-credit observation through normal CLI commands and `rest`, use the
[numeric runtime observation case](./numeric-runtime-observation-acceptance.md).
