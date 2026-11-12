# Run a read-only CLI API case

Use this guide to check Gateway discovery and the signed-in actor's
authorization context through normal native CLI commands.

This read-only case does not test mutations, model execution, or full P1
acceptance. The API launchers run this checked-in script; they do not provide a
separate API runner.

## Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit you want to test. Use an explicitly approved non-production
Gateway. You must already be signed in through the supported CLI flow for the
actor and tenant in the case input. The script does not sign in, launch a
browser, accept a bearer token, or create fixtures.

The script uses `auth status`, `api operations`, `api describe`, and
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

The Workspace repository-read case remains blocked until the normal CLI
supports a fresh repository review selection. Track that capability in
[issue #2752](https://github.com/sabbour/agentweaver/issues/2752). Do not use
internal CLI types, extract a selection code, or bypass sensitive-header
guards.

## Next step

Use the [Testing guide](./testing.md) to choose other checks and understand
their limits. This case does not prove a live model, deployment, or full P1
acceptance.
