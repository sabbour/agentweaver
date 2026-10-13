# P1 dispatch and restart checks

This source-only command reports the two required P1 checks that cannot yet run
against accepted production owner contracts. Both results are `blocked`; this is
not native runtime evidence or P1 acceptance. It does not call Azure, Kubernetes,
Orchestrator, Events, Environment, a model provider, or a database, and it creates
no resources.

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

`RuntimeAgentHost.ConfigureAsync` permits `MaxModelTurns` only for
workflow-bound registrations, whose native sends use owner-prepared dispatch
and begin admission. An unbound `MaxModelTurns`, any `MaxToolCalls`, or any
`MaxPromptTokens` still fails before SDK setup with
`runtime_numeric_limits_unsupported`. A positive HostedCopilot hard credit
limit is separately denied with `runtime_usage_affinity_unavailable` until
actual native usage can be bound to its platform turn. The Orchestrator's
existing prepared-dispatch reservation and private credit reconciliation
methods do not make the missing source-observed boundary receipts available.

The smallest safe production repair is conditional support, not removal of the
guard. Orchestrator must expose a current-authorized owner observation for the
accepted selection/fence, exact checkpoint ID/parent/revision, registration and
source identity, dispatch ID/request hash/state/revision, and actual priced
credit guard. For a denial, it must bind the exact attempt to a current owner
denial and unchanged send state, with bounded native event/cursor evidence that
distinguishes no-send from uncertainty. Positive HostedCopilot hard caps remain
unsupported until real native usage is associated with its platform turn and
joined through Events receipts. A price quote or zero-credit denial is not
positive at/beyond hard-limit acceptance.

AgentHost now permits `MaxModelTurns` only when every constrained send uses the
prepared-dispatch, action-grant and native-begin path. Other limits remain
unsupported until their exact native hook or capacity enforcement is wired and
tested. Existing no-resend, pending-accounting and sticky exhaustion rules must
remain in force. The executable check still needs source-owned actual priced
usage and native turn receipts for admitted boundary sends, plus a current owner
denial and bounded event evidence for the attempted no-send boundary. Estimated
counts, a failed dispatch response, a pricing snapshot, or a missing journal
event is not proof of no send.

## Missing P1 replacement and recovery evidence

P1 restart proof must use the ordinary Sandbox abandon/reconcile/reprovision,
new Host registration/bootstrap, and `RuntimeSessionRecovery` path. The
manifest-based suspend/resume facade and `RuntimeResumeBootstrapRequest` are
P2 and explicitly excluded; they are not a substitute for a gate/join restart
check. No same-lease Host restart API is required.

The ordinary Environment bootstrap profile registry is immutable and maps an
Environment owner plus `ProfileId` to one exact `ProviderResourceRef`.
`EnvironmentRuntimePlacementReader` rejects a replacement resource unless an
accepted profile mapping names that exact resource. The current P1 source has
no owner-authorized ordinary producer for a new mapping. Even after a valid
replacement mapping exists, ordinary Host readiness reports registration,
fences, placement phases, image, and source grant, but not
`RuntimeSessionRecovery` mode or the cache receipt; the optional `ResumeCache`
is set only by the excluded P2 flow.

The ordinary P1 path needs owner receipts for accepted run/actor scope and
gate/join state; MAF checkpoint ID, parent, revision and dispatch result; old
and replacement Sandbox identities and current Environment/provider fences; a
verified replacement profile and native endpoint; fresh registration and
bootstrap receipts with the current source grant; and the real recovery mode
plus cache consumption or authenticated journal evidence.

The final before/after observation must show the same logical checkpoint,
work plan and dispatch result, the preserved Core execution fence, and no
duplicate native send, journal event, or external effect. Do not infer this
from cache compatibility, startup readiness, a changed Pod UID alone, a missing
event, or a caller-supplied cache/receipt. The fixed source-only command cannot
perform this run or collect those live owner receipts, so the check remains
blocked until an accepted ordinary P1 replacement profile/observer and exact
gate/join evidence exist.
