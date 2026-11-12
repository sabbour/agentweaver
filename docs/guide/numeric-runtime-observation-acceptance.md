# Check numeric runtime evidence

Use this case to inspect a workflow-bound child session that already has a
numeric tool-call denial. The case reads status, usage, and runtime-observation
data through the native CLI. It does not create a run, send a message, or call a
model.

This case checks source behavior through a configured non-production target.
It does not prove that the target is deployed or ready for release.

## Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit you want to check. Use only a non-production target that the
target owner has approved. Creating a run or asking a model to call a tool can
change data and incur provider charges. Get separate approval for model and
spend use before you create the test run.

Prepare a workflow-bound run with `maxToolCalls` set to `1`. Use a native
workflow child session that has one admitted tool reservation and a later
tool-call denial. The tool must be read-only. The case checks the existing
child session; it does not create this workflow or attempt.

The CLI identity must have current authority to read the selected project and
run. Sign in through the supported CLI flow before you run the case. The case
does not launch a browser or accept a bearer token.

## Prepare the case input

Copy `scripts/acceptance/api/examples/numeric-runtime-observation.json` to a
new local JSON file. Replace each example value with the approved case data:

- `sourceCommit`: the full 40-character source commit for the apphost.
- `targetCommit`: the full source commit recorded for the target.
- `apphostSha256`: the SHA-256 hash of the exact apphost file.
- `approvedNonProduction`: keep `true` only after the target owner approves
  this test.
- `expectedActorId`: the actor authorized for this case.
- `expectedMaxToolCalls`: the accepted run limit. This case expects `1`.
- `target`: the Gateway origin, Broker issuer, client ID, API audience, tenant,
  project, run, and workflow child session IDs.

Do not add credentials, tokens, prompts, tool arguments, or tool results. The
input rejects fields that are not in the example. Set `sessionId` to the child
session with the recorded tool rejection, not the root session.

Use `agentweaver api operations` and `agentweaver api describe` to confirm the
current Gateway command schemas. The relevant operations are
`getProjectConfiguration`, `createProjectRun`, `startProjectRun`,
`sendProjectRunMessage`, `readRunStatus`, `readProjectUsage`, and
`readRuntimeObservationV2`. Use the normal configuration and run commands to
prepare the approved test run. Do not use the removed `agentweaver test`
command or an internal route.

## Validate and run the case

1. Set `AGENTWEAVER_CLI_PATH` to the absolute path of the exact native
   apphost. Keep the case input and evidence outside the repository. Set the
   evidence path to a new directory.

   ```powershell
   $env:AGENTWEAVER_CLI_PATH = (Resolve-Path .\artifacts\cli\win-x64\publish\agentweaver.exe).Path
   $caseInput = 'C:\path\to\numeric-runtime-observation.json'
   $evidence = 'C:\path\to\new-numeric-runtime-observation-evidence'
   ```

2. Record the source commit and apphost hash in the case input.

   ```powershell
   git rev-parse HEAD
   (Get-FileHash $env:AGENTWEAVER_CLI_PATH -Algorithm SHA256).Hash.ToLowerInvariant()
   & $env:AGENTWEAVER_CLI_PATH --version
   ```

   Keep the target commit separate from the source commit.

3. Validate the input without starting the apphost or contacting the Gateway.

   ```powershell
   node scripts\acceptance\api\numeric-runtime-observation.mjs --input $caseInput --output $evidence --dry-run
   ```

   A successful dry run reports `validated-not-run`. It does not create the
   evidence directory or use the network. It checks the input, apphost path
   and hash, and that the evidence path is new.

4. Run the case only after the test owner approves the target and the actor is
   signed in.

   ```powershell
   node scripts\acceptance\api\numeric-runtime-observation.mjs --input $caseInput --output $evidence
   ```

   The script uses `auth status`, `api operations`, `api describe`, `api
   invoke`, and `rest`. It reads run status and project usage as context, pages
   the complete runtime-observation snapshot, and reads the denied attempt
   through both `api invoke readRuntimeObservationV2` and `rest`. It does not
   send a message, change configuration, or start a model call.

## Verify the result

The script writes `commands.jsonl` and `result.json` to the new evidence
directory. A `passed` result means the actor and tenant match the input; the
child session is bound to the selected run and current execution fence; the
accepted tool-call limit and reservation count match the input; and the
complete history contains an admitted tool reservation and a numeric
`ToolPermissionRejected` fact. The normal CLI and `rest` exact lookups must
return the same fact.

The rejection fact means the runtime recorded a rejection decision after the
owner confirmed the numeric denial. It does not prove that the SDK received
the decision or that the tool did not run. The script does not treat an
unavailable no-send or tool-execution section as proof that no tool ran.
Usage totals are context only; they do not prove that a tool did not run.

An incomplete or missing history, stale binding, missing denial, or failed
command is not a pass. Review the redacted command records before you retry.
The script does not create or clean up the run. Use the normal owner commands
to finish or cancel only a run that you own. Do not repeat a command if its
write outcome is uncertain.

## Next step

Use the [Testing guide](./testing.md) for local source tests and their limits.
See [runtime observation contracts](../reference/contracts.md#runtime-observation-v2)
for the evidence fields and their meaning.
