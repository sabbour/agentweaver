# P1 dispatch and restart checks

This source-only command reports the two required P1 checks that cannot yet run
against accepted production owner contracts. Both results are `blocked`; this is
not native runtime evidence or P1 acceptance. It does not call Azure, Kubernetes,
Orchestrator, Events, Environment, a model provider, or a database, and it creates
no resources. It does not dispatch, invoke, or ingest the native runner.
`GatewayRouteCatalog` has no matching mapping for this report.

Run the fixed report command from the repository root:

```powershell
node scripts\azure\p1-dispatch-recovery-probes\run.mjs
```

Run its controlled tests with:

```powershell
node --test scripts\azure\p1-dispatch-recovery-probes\tests\run.test.mjs
```

The result is a separate `p1-dispatch-recovery-acceptance` envelope with these
exact required slots:

- `numeric-copilot-dispatch-boundaries`
- `checkpoint-restart-duplicate-fence`

Each slot has a blocked status, structured owner/receipt/required-fact entries,
and cleanup marked `not-started`. These entries describe facts a future
owner-issued receipt must contain; they are not accepted DTOs or endpoints.
There is no input, adapter override, or caller-supplied receipt that can turn
either result into a pass. The four existing native probes retain their own
status; the full P1 result must remain incomplete while either slot is blocked.

## Missing numeric-dispatch contract

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

The existing owner path already maintains conservative whole-run priced
Copilot totals across child runs. Before admitting a native workflow turn, the
Orchestrator reads the current credit floor under the common dispatch lock;
pending or sending siblings block another admission, and a new turn at or
above the observed floor is denied. Authenticated late usage changes the next
snapshot but does not revoke a turn already admitted. This is post-paid
next-dispatch protection, not a claim that all usage is final. A genuine
acceptance receipt must identify the whole-run total, ledger count/digest/rate
identity, current snapshot and guard revision, exact dispatch intent and
checkpoint, the denial and unchanged send state, and bounded native cursor
facts for the attempt. Copilot usage totals are not native journal positions,
and a missing event alone is not proof of no send.

The fixed command cannot exercise the current production guard or obtain these
source-owned live receipts. The check must stay blocked until a fixed owner-facing
observation path can report the actual run floor, guard revision, denial/send
state, and native cursor evidence. It must also show one durable warning when a
whole-run priced snapshot first reaches the soft limit, no duplicate warning on
replay or concurrent attempts, and later priced totals advancing the floor and
ledger-event count. Keep unbound and legacy dispatches denied; do not weaken
pending/indeterminate sibling checks or claim that an already admitted turn can
be recalled.

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
`EnvironmentRuntimePlacementReader` now has an authenticated ordinary producer
for an endpoint-bound replacement and owner-bound bootstrap/readiness sources.
They pin the actor, accepted selection, source configuration and exact profile
copy. This report does not invoke or dispatch those sources, and it does not
ingest the native runner. `GatewayRouteCatalog` has no matching mapping, so no
transport or live receipt is available; both checks remain blocked. Ordinary
Host readiness reports registration, fences, placement phases, image, source
grant, and the source-derived `RuntimeSessionRecovery` mode and
`RuntimeHostRecoveryReceipt`, including native-cache acknowledgment or journal
evidence. This report does not invoke the native runner or collect a
replacement-run recovery receipt, so it has no actual cache-consumption or
journal evidence. `ResumeCache` remains specific to the excluded P2 flow.
Readiness or registration alone also cannot prove that the replacement's
native endpoint matches the receiver's expected audience and certificate DNS
name.

The ordinary P1 path needs owner receipts for accepted run/actor scope and
gate/join state; MAF checkpoint ID, parent, revision and dispatch result; old
and replacement Sandbox identities and current Environment/provider fences; a
verified replacement profile and native endpoint checked against the
receiver's expected audience and certificate DNS name; fresh registration and
bootstrap receipts with the current source grant; and the real recovery mode
plus cache consumption or authenticated journal evidence. Registration must
not conceal a missing or mismatched endpoint deployment prerequisite.

The final before/after observation must show the same logical checkpoint,
work plan and dispatch result, the preserved Core execution fence, and no
duplicate native send, journal event, or external effect. Do not infer this
from cache compatibility, startup readiness, a changed Pod UID alone, a missing
event, or a caller-supplied cache/receipt. The fixed source-only command cannot
perform this run or collect those live owner receipts, so the check remains
blocked until a supported invocation/transport path and exact gate/join evidence
exist.
