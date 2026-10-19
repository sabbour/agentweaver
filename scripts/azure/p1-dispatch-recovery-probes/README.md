# P1 dispatch and restart checks

This read-only command checks source-owned runtime receipts for the two required
P1 probes. It never sends a model turn, provisions or replaces a Host, changes
Azure or Kubernetes resources, or creates probe evidence on behalf of an owner.
Without inputs it prints a blocked report:

```powershell
node scripts\azure\p1-dispatch-recovery-probes\run.mjs
```

For live readback, provide a run-bound JSON manifest and an authorized provider
module:

```powershell
node scripts\azure\p1-dispatch-recovery-probes\run.mjs --input <run-bound-manifest.json> --provider-module <authorized-runtime-reader.mjs>
```

The provider module exports `createP1DispatchRecoveryDependencies({ input })`.
It returns optional `observationClient`, `creditDenialDependencies` with a
`hostCaller.postJson(path, body)`, and `runtimeReadinessProvider.readCurrent`
clients. The manifest may contain `observationContext`, `creditDenialInput`,
and `recoveryContext`; it must not contain bearer tokens or other credentials.
The provider supplies the already-authorized Host caller and must use the
source-owned fixed routes. The runner validates returned Host denial/no-send
receipts and the current replacement Host recovery receipt; it does not accept
prebuilt probe results from the provider.

The recovery reader must return `placementObservation.resource.resourceId`,
`placementObservation.resource.generation`, and `placementObservation.pod.uid`
from a current owner read, along with the registration and Host readiness
receipt. The registration binds to the provider resource ID; the observed Pod
UID is checked separately. The runner rejects expired or revoked registrations,
stale Observe grants, source sessions from another owner, and cache
acknowledgments whose session material does not match the current owner and
source.

Run its controlled tests with:

```powershell
node --test scripts\azure\p1-dispatch-recovery-probes\tests\run.test.mjs
```

The result is a `p1-dispatch-recovery-acceptance` envelope with a canonical
`receiptSha256` checksum and these exact required slots:

- `numeric-copilot-dispatch-boundaries`
- `checkpoint-restart-duplicate-fence`

The fixed report always leaves both slots `blocked`. A live invocation may add
validated owner, native cursor/no-send, and replacement recovery observations,
but it keeps `acceptanceEvidence` false until every required receipt has a
source-owned producer. Missing, stale, or malformed receipts remain blocked;
the runner never infers a pass from a scripted result or a missing event.
Cleanup remains `not-started` because this read-only command creates no
resources. The full P1 aggregator preserves these two probe statuses alongside
the four native probes.

## Missing numeric-dispatch evidence

`RuntimeAgentHost.ConfigureAsync` supports `MaxModelTurns` and `MaxToolCalls`
only for workflow-bound registrations, whose native sends use owner-prepared
dispatch and begin admission. `MaxPromptTokens` is preserved through owner
projection and registration but remains unsupported: `RuntimeContractValidation`
rejects it with `runtime_numeric_limits_unsupported` before SDK setup, even for
workflow-bound registrations. Unbound `MaxModelTurns` and `MaxToolCalls` are
also rejected with that code. Positive HostedCopilot hard credit limits are
supported for workflow-bound registrations through owner-prepared dispatch and
begin admission. Unbound registrations still deny positive hard limits with
`runtime_usage_affinity_unavailable`.

The owner path maintains conservative whole-run priced Copilot totals across
child runs. Before admitting a native workflow turn, the Orchestrator reads the
current credit floor under the common dispatch lock; pending or sending siblings
block another admission, and a new turn at or above the observed floor is
denied. Authenticated late usage changes the next snapshot but does not revoke a
turn already admitted. This is post-paid next-dispatch protection, not a claim
that all usage is final. A complete acceptance receipt must identify the
whole-run total, ledger count/digest/rate identity, current snapshot and guard
revision, exact dispatch intent and checkpoint, the denial and unchanged send
state, and bounded native cursor facts for the attempt. Copilot usage totals are
not native journal positions, and a missing event alone is not proof of no send.

With run-bound credit-reader pins and an authorized Host caller, the command
reads the existing fixed-zero and positive hard-credit denial routes. Those
owner responses can prove the stored denial and its complete empty native-event
range. They do not execute numeric `MaxModelTurns` or `MaxToolCalls` boundaries,
observe the whole-run soft-warning/floor advance, or supply admitted-turn
completion and usage receipts. The numeric probe therefore stays blocked until
those receipts have source-owned producers. Keep unbound and legacy dispatches
denied; do not weaken pending/indeterminate sibling checks or claim that an
already admitted turn can be recalled.

AgentHost permits `MaxModelTurns` and `MaxToolCalls` only when every constrained
send uses the prepared-dispatch, action-grant and native-begin path.
`MaxPromptTokens` remains unsupported before SDK setup. Existing no-resend,
pending-accounting and sticky exhaustion rules must remain in force. Estimated
counts, a failed dispatch response, an old pricing snapshot, or a missing
journal event is not proof of no send.

## Missing P1 replacement and recovery evidence

P1 restart proof must use the ordinary Sandbox abandon/reconcile/reprovision,
new Host registration/bootstrap, and `RuntimeSessionRecovery` path. The
manifest-based suspend/resume facade and `RuntimeResumeBootstrapRequest` are
P2 and explicitly excluded; they are not a substitute for a gate/join restart
check. No same-lease Host restart API is required.

The ordinary Environment bootstrap profile registry is immutable and maps an
Environment owner plus `ProfileId` to one exact `ProviderResourceRef`.
`EnvironmentRuntimePlacementReader` provides owner-bound replacement placement
and readiness sources. The live command can read the current replacement
Host's authenticated `/runtime/v1/readiness` receipt when supplied a
run-bound placement and reader. It does not abandon or reprovision a Host, and
one current receipt does not prove the before/after gate-join result or absence
of duplicate effects. `ResumeCache` remains specific to the excluded P2 flow.
The replacement endpoint must also match the receiver's expected audience and
certificate DNS name.

The ordinary P1 path needs owner receipts for accepted run/actor scope and
gate/join state; MAF checkpoint ID, parent, revision and dispatch result; old
and replacement Sandbox identities and current Environment/provider fences; a
verified replacement profile and native endpoint checked against the receiver
expected audience and certificate DNS name; fresh registration and bootstrap
receipts with the current source grant; and the real recovery mode plus cache
consumption or authenticated journal evidence. Registration must not conceal a
missing or mismatched endpoint deployment prerequisite.

The final before/after observation must show the same logical checkpoint,
work plan and dispatch result, the preserved Core execution fence, and no
duplicate native send, journal event, or external effect. Do not infer this
from cache compatibility, startup readiness, a changed Pod UID alone, a missing
event, or a caller-supplied cache/receipt. No source-owned reader currently
produces the complete invocation-owned gate/join and no-duplicate receipt, so
the check remains blocked even when the replacement Host recovery receipt is
available.
