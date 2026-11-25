# Check numeric runtime evidence

Use this case to inspect a workflow-bound child session that already has a
numeric tool-call or hard-credit denial. The case reads status, usage, and
runtime-observation data through the native CLI. It does not create a run, send
a message, or call a model. To check root model-turn or hard-credit evidence, see
[Check root model-turn and hard-credit no-send evidence](#check-root-model-turn-and-hard-credit-no-send-evidence).

This case checks source behavior through a configured non-production target.
It does not prove that the target is deployed or ready for release.

## Before you begin

You need Node.js 24 and the self-contained native Agentweaver apphost for the
source commit you want to check. For the child hard-credit mode, install the
locked root dependency with this command:

```powershell
npm ci --ignore-scripts
```

Use only a non-production target that the target owner has approved. Creating a run or asking a model to call a tool can
change data and incur provider charges. Get separate approval for model and
spend use before you create the test run.

The example keeps `maxToolCalls` set to `1`. To prove the tool-below-cap
matrix row, prepare an existing workflow-bound run with `maxToolCalls` set to
`2`. Use a native workflow child session with two distinct, recorded, read-only
tool reservations: one at count `1` and one at count `2`. Record a later
tool-call denial in the same child session. A reservation records a slot; it
does not prove that the tool executed. The case checks the existing child
session; it does not create this workflow or attempt.

The CLI identity must have current authority to read the selected project and
run. Use the `api.read` and `projects.orchestrator` scopes with that
project/run binding. The case uses this same bound identity for its
authorization-context, status, usage, and observation reads. Sign in through
the supported CLI flow before you run the case. The case does not launch a
browser or accept a bearer token.

## Prepare the case input

Copy `scripts/acceptance/api/examples/numeric-runtime-observation.json` to a
new local JSON file. Replace each example value with the approved case data:

- `sourceCommit`: the full 40-character source commit for the apphost.
- `targetCommit`: the full source commit recorded for the target.
- `apphostSha256`: the SHA-256 hash of the exact apphost file.
- `approvedNonProduction`: keep `true` only after the target owner approves
  this test.
- `expectedActorId`: the actor authorized for this case.
- `expectedMaxToolCalls`: the accepted run limit. The checked-in one-slot
  example uses `1`. Set it to `2` for an existing accepted two-slot run whose
  child session has both reservations and the later denial.
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
CLI authorization context matches the selected actor, tenant, project, and run;
the child session is bound to the selected run and current execution fence; the
accepted tool-call limit and reservation count match the input; and the
complete history contains an admitted tool reservation and a numeric
`toolPermissionRejected` fact. The reservation and rejection must match the
child session's current execution fence and accepted selection. Every history
page and both exact responses must carry the selected project, run, session,
fence, and selection. The normal CLI and `rest` exact lookups must return the
same current-authority fact.

For `tool-below-cap`, the matrix checks two separate reservations under the
current binding. Their valid attempt IDs must differ. One reservation must
show positive count `maxToolCalls - 1`; the next must show positive count
`maxToolCalls`. Both must equal `reservedCount` and carry the exact accepted
limit, unit `count`, and scope `copilotRun`. Their sequences must be positive
safe integers no greater than the shared snapshot high-watermark. The count
`maxToolCalls - 1` record must come first in page traversal and sequence
order. Intervening sequence gaps are valid because history pages can omit
events from other sessions. This row remains `blocked` for a one-slot run
because a positive count cannot represent `0`.

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

## Check a child hard-credit denial

The `numeric-child-hard-credit-denial` mode reads one existing child
preparation attempt that the owner denied at the accepted Copilot hard-credit
limit. It does not prove that a native model call sent nothing. Child
preparation happens before dispatch insertion, so the native no-send witness
is explicitly unavailable.

Copy `scripts/acceptance/api/examples/numeric-child-hard-credit-denial.json`.
Set the existing target fields, `attemptId`, and `expectedRelation`. The attempt
ID must identify the exact denied child attempt. Use `equal` when observed
credits equal the accepted limit or `overshoot` when they are greater than the
accepted limit. Do not run one input for both relations.

The dry run validates the input, native apphost path and hash, and new evidence
path. It starts no CLI process and writes no evidence. In a live read, the case:

1. Pages the complete observation history under one snapshot and high-water
   mark.
2. Selects exactly one current-fence `denied` fact for the pinned attempt.
3. Requires a `childPreparation` owner decision with a receipt ID and payload
   hash.
4. Reads the same attempt through `api invoke` and `rest`.
5. Compares the accepted limit, configured value, and observed value as exact
   .NET decimal values.

All three reads must identify the same project, run, child/root relationship,
current fence, accepted selection, registration, revision, denial, receipt, and
payload hash. Missing, stale, malformed, incomplete, or conflicting data
returns `blocked`.

The parser rejects duplicate JSON names, YAML-only syntax, quoted credit
numbers, values outside .NET `decimal`, excessive numeric exponents, and
conflicting evidence. It retains the original selected number tokens in
`result.json` and in redacted command records. Saved command arguments and
responses replace continuation cursors. Observation command records do not
retain raw standard output or standard error.

The result stores these selected strings in `creditNumberSources`:
`copilotHardCreditLimit`, `configuredValue`, and `observedValue`.

## Check root model-turn and hard-credit no-send evidence

The same script has two additional no-send cases. Each case reads the **root**
session of an existing run that already reached a limit. The script does not
drive the run to the limit. Use the same procedure as above, with these
changes:

| Case | Example input | Case-specific input |
| --- | --- | --- |
| `numeric-model-turn-no-send` | `scripts/acceptance/api/examples/numeric-model-turn-no-send.json` | `expectedMaxModelTurns`: the accepted `maxModelTurns` value, from `1` to `1000`. |
| `numeric-hard-credit-no-send` | `scripts/acceptance/api/examples/numeric-hard-credit-no-send.json` | `expectedSoftWarning`: `true` if the run must show a soft-credit warning; otherwise `false`. |

Set `target.sessionId` to the root session of the run. The input does not
include `expectedMaxToolCalls` or a credit value. The hard-credit case reads
the configured limit from `acceptedLimits.copilotHardCreditLimit`.

### Current-authority rule

A root-session history contains facts from all sessions, execution fences,
and accepted selections of the run. The script uses a fact as evidence only
when all of these conditions are true:

- The fact `sessionId` is the selected root session.
- The fact `executionFence` is the binding `currentExecutionFence`.
- The fact `acceptedSelectionHash` is the binding `acceptedSelectionHash`.

The script applies this rule to each denial, native no-send, model-turn
reservation, and credit-guard fact. It also applies the rule to the facts that
the exact `api invoke` and `rest` lookups return. If no fact matches, the
result is `blocked`, and the reason names the mismatch.

The tool case applies the same rule to the child-session rejection and admitted
reservation. Each history page and exact response must also match the selected
project, run, session, execution fence, and accepted selection. Observation
times do not grant execution authority.

### Model-turn result

A `passed` result for `numeric-model-turn-no-send` means:

- The binding is the root session at its current execution fence.
- `acceptedLimits.maxModelTurns` equals `expectedMaxModelTurns`.
- The run-wide `totals.modelSlotsReserved` value is available and equals
  `expectedMaxModelTurns`. A larger value is over-admission and does not pass.
- The history has at least `expectedMaxModelTurns` `modelTurnReserved` facts
  that match the current-authority rule.
- A `denied` fact has `limitKind` `modelTurns`, `stage` `rootAuthorization`,
  `reasonCode` `limit_reached`, and an available owner decision.
- A `nativeNoSend` fact for the same attempt has an available native no-send
  section with a complete range and zero events.
- The exact `api invoke` and `rest` lookups return the same `nativeNoSend`
  fact.

This result does not cover child sessions or `maxPromptTokens`. A child
model-turn denial has no native no-send witness.

### Hard-credit result

A `passed` result for `numeric-hard-credit-no-send` means:

- The binding is the root session at its current execution fence.
- `acceptedLimits.copilotHardCreditLimit` is set.
- A `denied` fact has `limitKind` `copilotHardCredits`, `stage`
  `nativeTurnBegin`, and `reasonCode` `hard_credit_limit_reached`. Its
  `configuredValue` equals the accepted limit, and its `observedValue` is not
  less than that limit. Both values use the unit `nanoAiu`. A denial with a
  different configured value is from an old configuration and does not pass.
- A `nativeNoSend` fact for the same dispatch has the same configured value
  and an available native no-send section with a complete range and zero
  events.
- At least one `creditGuardChanged` fact shows `hardCapExhausted`. The
  observed credit floor does not decrease in sequence order.
- If `expectedSoftWarning` is `true`, at least one `creditGuardChanged` fact
  shows `softWarningEmitted`.
- The separate `numericMatrix.credit-warning-floor` row passes only when
  current guard facts show a warning transition from below the accepted soft
  credit limit to at or above it, followed by a higher floor, ledger-event
  count, and owner revision. The facts must use `nanoAiu` and `copilotRun`.
- The exact `api invoke` and `rest` lookups return the same `nativeNoSend`
  fact.

This result does not prove the exact spend of other model turns.

For all three cases, each result that is not a pass is `blocked`, and the
script exits with code `2`. Read the `reason` field to see the failed check.
An unavailable or unknown section is never treated as proof that nothing was
sent.

## Read numeric boundary coverage

Each result includes `numericMatrix`. The rows in
`scripts/acceptance/api/numeric-cases.json` compare reservations and denials
with the accepted limit. Equality and overshoot are separate predicates.
`numericMatrix.complete` is `true` only when every required row passes. A
legacy case can still pass while the matrix remains blocked or incomplete;
the matrix does not change the existing case status or exit code. A malformed
or incomplete reservation pair blocks only `tool-below-cap`; it does not
change the existing case, at-cap, denial, or replay rows.

The matrix marks exact reservation replay in the tool case as blocked because
its exact lookup returns the rejection, not the reservation. It also keeps
zero-credit receipts, prompt capacity, full native enforcement, and child
native no-send proof blocked while the public observation route lacks those
facts. The admitted-completion case replays the same generation request with
the same idempotency key. It checks that the Gateway returns the same
completion and that the reservation identity and total stay the same before
and after the replay.

## Check an admitted root completion

The admitted-completion case calls `formulateCastingProposal` through the
native CLI, then replays the same request with the same idempotency key. The
Gateway returns the original completion for that replay. The first request
starts a model turn, creates a pending casting proposal, and can incur model
charges. The case rejects the proposal after it checks the completion
evidence.

Use an existing, approved non-production run. Get separate approval for the
model call, its expected spend, and proposal cleanup. Set
`approvedNonProduction`, `approvedModelAndSpend`, and
`approvedProposalCleanup` to `true` in
`scripts/acceptance/api/examples/numeric-admitted-completion.json`. Set
`expectedConfigurationRevision` to the current project revision and
`expectedMaxModelTurns` to the accepted run limit. Set the target session to
the run's root session.

Use the same native apphost and target fields as the read-only cases. Keep the
input and evidence outside the repository. The script reads the approved
configuration revision and casting-role catalog to build the prompt that the
Gateway accepts. It also checks that the cleanup identity is an unbound
project writer. Validate the input first:

```powershell
node scripts\acceptance\api\numeric-admitted-completion.mjs --input $caseInput --output $evidence --dry-run
```

The dry run checks the input, apphost hash, and new evidence path. It starts no
process, uses no network, and writes no evidence. Run the case after the
target, model-spend, and cleanup approvals are in place:

```powershell
node scripts\acceptance\api\numeric-admitted-completion.mjs --input $caseInput --output $evidence
```

The script saves the operation key in `operation-key.json` before it sends the
generation request. After the first reservation read, it replays the exact
request with the same key and checks the completion and reservation again. If
`result.json` reports `mutationOutcome: "uncertain"`, keep the evidence and
operation key. Do not start another request with a new key. Check the proposal
through the normal owner flow. If cleanup reports `blocked`, reject the
proposal through that flow.

A `passed` result means the response includes native completion events,
registration and selection bindings, cursor hashes, usage IDs, and typed
material acknowledgments. The script joins that receipt to one current
root-session reservation and checks the accepted model-turn total through
`api invoke` and `rest`. The exact reads before and after the same-key replay
return the same reservation and total. The script rejects the generated
proposal, then reads it back to confirm the rejected state.
`numericMatrix` stays `blocked` while any required boundary or unavailable
proof row lacks evidence. This case does not drive a denial or complete N1
coverage.

## Next step

Use the [Testing guide](./testing.md) for local source tests and their limits.
See [runtime observation contracts](../reference/contracts.md#runtime-observation-v2)
for the evidence fields and their meaning.
