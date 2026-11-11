# Agent runtime

## 1–4. Goals, constraints, context and strategy

Use this page to understand how AgentHost bootstraps and runs a model session
inside a selected Sandbox. AgentHost is the execution host;
`Agentweaver.AgentRuntime` is the shared library that creates and guards its
native Copilot SDK session.

AgentHost does not choose its own actor, project, model, credentials, or
placement. Before protected work, it checks that delivered configuration still
matches Orchestrator, Environment, and Identity.

The current host uses `GitHub.Copilot.SDK` 1.0.18 with native runtime 1.0.97.
Microsoft Agent Framework belongs to Orchestrator, not this model harness.

The approach keeps native execution inside a selected resource while owners
retain authority outside it. More owner checks mean more failure boundaries;
an earlier successful bootstrap cannot authorize later work after revocation.

<span id="find-the-runtime-implementation"></span>

## 5. Building blocks

| Source | Responsibility |
| --- | --- |
| `Agentweaver.AgentHost/Program.cs` | Executable modes, server-owned configuration, authentication, and composition |
| `Agentweaver.AgentHost/NativeRuntimeManifest.cs` | Image-owned native executable and manifest verification |
| `RuntimeAgentHost.cs` | Bootstrap activation, readiness, bounded turn queues, usage acknowledgment, suspend, and resume |
| `RuntimeBootstrapReceiver.cs` and `RuntimeSessionBootstrap.cs` | Configure delivery, grant exchange, skill reads, and SDK creation |
| `RuntimeCopilotSessionFactory.cs` | Native session options, model source, prompt capacity, skills, and recovery |
| `AuthorizedRuntimeSession.cs` | Current authority, guarded sends, durable content, usage, and cache writes |
| `RuntimeCopilotToolCallAuthorization.cs` | Native tool identity and permission checks |
| `RuntimeSessionRecovery.cs` and `RuntimeSessionMaterialHttpClient.cs` | Compatible cache and journal recovery |
| `BuildTestOutputCollectorProgram.cs` and `MountedWorkspaceFlush.cs` | Separate trusted collection and workspace-flush modes |

These files are under `services/agenthost/Agentweaver.AgentHost/` and
`packages/Agentweaver.AgentRuntime/`. Runtime registration, grant, placement,
and owner transport contracts live in `packages/Agentweaver.Identity/`.

## 6. Runtime view

### Check the authority boundaries

Orchestrator owns runtime registration and accepted execution binding.
Environment owns the current Sandbox lease, resource generation, image, and
runtime profile. Identity owns separate purpose-bound configure, observe, and
model-credential authority.

AgentHost compares these bindings rather than trusting request fields. It
rechecks current owner authority after remote waits and before protected
effects. A valid bearer alone does not make a stale registration current.

The model source is explicit. Hosted Copilot uses the selected Identity-owned
connection. Bring your own key (BYOK) uses the exact selected credential
reference and explicit native provider configuration. Neither mode falls back
to the other. The current SDK path delivers a real hosted access token or BYOK
key to the runtime inside the Sandbox; it is not credential-less guest
execution. The
[Identity architecture](../architecture/identity-secrets.md) describes the
separate credential-less proposal.

A ready process is not an authorized turn. Startup requires distinct existing
private-state and workspace directories, an image-owned native executable,
registered model bindings, and explicit owner addresses. The host checks
startup phase evidence and budgets before reporting runtime readiness.
`/health/live` reports process liveness. `/health/ready` checks current runtime
prerequisites. Authenticated `/runtime/v1/readiness` returns a current,
noncredential receipt; it does not start the SDK or send a turn.

### Bootstrap a runtime session

Orchestrator starts the root AgentHost after it accepts the root and admits the
first message. It also starts a child AgentHost after dispatch commits the
child's bound startup intent. Startup creates a ready runtime, but it does not
start a model turn. See the [run-start flow](./orchestration.md#start-child-runtimes)
for owner checks, durable retries, and terminal cleanup.

Before bootstrap, AgentHost needs the current runtime registration, an accepted
model binding, configured owner addresses, and the selected Sandbox placement.

1. **Receive configuration.** Authenticated `POST /runtime/v1/configure`
   accepts a pending configure delivery for the current registration. Delivery
   alone does not create a model session.
2. **Activate.** `POST /runtime/v1/configure/activate` supplies the exact
   registration, configuration, and operation identities.
3. **Read accepted content.** The bootstrap reads assigned, pinned skills
   through Orchestrator and verifies their content binding.
4. **Consume grants.** Identity consumes the configure proof and exchanges it
   for the purpose-bound observe credential. Bootstrap separately redeems the
   selected model credential.
5. **Recover context.** Events supplies compatible cache material or ordered
   journal context when history exists.
6. **Create the SDK session.** The factory applies the accepted model, native
   tool hooks, skills, prompt capacity, and recovery input.
7. **Commit readiness.** The host records configuration acknowledgment and
   verifies current placement before returning readiness.

Bootstrap failure cleans up the prepared SDK session and invalidates acquired
credentials. An exact activation replay reuses the existing session only when
configuration and binding match.

### Run a turn

Authenticated `POST /runtime/v1/a2a/message:send` accepts a bound user message
with one text part, a nonempty message ID, the native context ID, and
`immediate` or `enqueue` delivery.

The host fingerprints the request. An exact same-host retry shares the
existing turn result. Changed content for the same message ID conflicts. The
queue has a configured pending-turn bound.

The dispatcher serializes turns. It selects immediate work before queued work
at the next dispatch boundary; it does not interrupt an active native turn.

For a workflow-bound turn, the host:

1. Validates the current registration, observe proof, model authority, skills,
   placement, and owner admission.
2. Commits user content to Events.
3. Sends through the guarded native SDK session.
4. Reads native turn-event evidence and stores the owner source receipt.
5. Commits assistant content and the native cache.
6. Flushes usage, obtains Events accounting acknowledgments, and completes the
   owner-bound turn before returning its answer.

The non-workflow turn path also guards the model action and commits user
content, assistant content, and cache. Supplying a message ID does not give
this path workflow-specific usage affinity.

### Enforce tools and budgets

Native tools map to named actions such as `tool.read`, `tool.write`, and
`exec.shell`. Permission checks bind the native session, active message, tool
call ID, action, and input hash. The runtime rejects unknown tools, unmatched
permissions, and Sandbox-bypass requests. An approved permission applies once.

Owner admission enforces accepted model-turn and tool-call limits. Hosted
Copilot with a zero hard credit limit denies the turn before native Send and
records guard/no-send evidence through the owner path. A positive hard limit
requires workflow-bound admission; an unbound session is rejected with
`runtime_usage_affinity_unavailable`.

`MaxPromptTokens` limits per-prompt/context capacity; it is not a cumulative run
token budget. The factory resolves it against the concrete model and narrower
limits. The SDK compacts removable history and rejects a turn if required
context still does not fit. See the
[runtime configuration reference](../reference/runtime-configuration.md) and
[provider configuration guide](../guide/configure-providers.md) for supported
settings and provider setup.

### Understand durability and recovery

Turn content and SDK cache bytes use Events' typed material API. The runtime
does not choose storage keys or treat a local file as a durable acknowledgment.
Usage travels through immutable Orchestrator source receipts and reference-only
Events accounting.

| Recovery mode | Input |
| --- | --- |
| `Fresh` | No prior recovery context |
| `NativeCache` | Verified cache matching session, tenant, accepted selection, SDK/runtime, model, and prompt capacity |
| `JournalRebuild` | Ordered recorded conversation context from the authenticated journal |

Rebuilt context marks earlier messages as history, not permission to repeat
effects. Same-process message deduplication does not establish exactly-once
execution after process loss.

Suspend stops new turn admission, drains admitted turns, flushes usage and
native state, and flushes the mounted workspace before returning evidence.
Resume verifies saved configuration, cache or journal input, current
registration, and restored workspace through the owner-controlled path.

For an approved read-only coordinator-root turn, Orchestrator stores a
completion receipt only after the Host reports the native event range and the
Events acknowledgments for user text, assistant text, and SDK cache. The
receipt binds the request and content hashes to the source, model, image,
registration, accepted selection, and execution fence. It also records a
complete cache-baseline cursor for replacement checks.

Child model-backed turns use a separate completion receipt. The Host returns
it only after native completion, usage accounting, and Events acknowledgments
for the child user and assistant material. The receipt binds request, prompt,
and output hashes to the child session, WorkPlan association, registration,
accepted selection, fence, source, image, model selection, actual model, native
completion events, usage event IDs, and material references. Orchestrator
validates the receipt against the exact dispatch and stores it with the
checkpoint result. Dispatch exposes bounded `completionEvidence` entries, not
the prompt or output text.

A coordinator-root Host replacement passes the root-turn receipt and its cache
binding through the normal activation path. The new Host must restore the same
compatible SDK cache and report a complete reconstruction interval with no new
native event or tool effect before Orchestrator marks the operation ready. An
exact retry afterward uses replacement-lineage evidence and returns the saved
completion; it does not call native `Send` again. The general material reader
does not gain permission to read SDK cache bytes.

### Diagnose runtime failures

Authentication failure returns `401`; current-authority rejection returns
`403`; invalid input returns `400`; owner, startup, and execution failures
return `503`. Runtime responses use `Cache-Control: no-store`.

Usage persistence failure stops dispatch. Missing accounting acknowledgments
do not become successful completion. Lost placement, expired grants, changed
skill content, or changed registration block continued use rather than
preserving earlier authority.

Collector executable modes are separate from model execution. They collect
bounded BuildTest files or workspace output under trusted contracts; they do
not create an SDK session or schedule an agent.

## 7–9. Deployment, shared concepts and decisions

AgentHost runs per Sandbox, not as an automatic shared scheduler. Its image
owns the native executable; Environment supplies the placement, private state
and workspace mounts. The [deployment view](./infra-deployment.md) describes
the target infrastructure, not an observed running host.

Authority and credential handling follow [Identity](./identity-secrets.md).
Durable material follows [Events](./events-sessions.md), not local cache files.
The [system decision view](./00-system-overview.md#9-architecture-decisions)
links canonical ADRs and their statuses; the credential-less Sandbox approach
remains a proposal, not this runtime's implemented credential path.

## 10–12. Quality, risks and glossary

| Context and stimulus | Response and measure | Verification state |
| --- | --- | --- |
| Same host receives the same message ID again | Identical content shares the result; changed content conflicts | Runtime request/deduplication tests; not cross-process exactly-once proof |
| Placement or grant changes during a turn boundary | Current checks block another protected send | Owner/runtime controlled tests; no paid-model claim |
| Compatible cache is unavailable | Rebuild retained authorized context or fail explicitly; do not replay effects | Recovery tests; production restore is separate |

Process loss can leave uncertain external work. The runtime owner must resolve
recorded content and usage before admitting further work; a local cache or
readiness response cannot settle it. Native compaction is not a guarantee that
every internal provider request stays below a hard token threshold.

**Terms:** bootstrap creates a bound session; registration identifies current
owner execution; readiness reports prerequisites. `MaxPromptTokens` is prompt
capacity, not cumulative run usage. See the [shared glossary](./00-system-overview.md#12-glossary).

## Related content

- [Orchestration](./orchestration.md) explains dispatch and current execution.
- [Events and sessions](./events-sessions.md) explains material and accounting.
- [Environment and sandbox](./environment-sandbox.md) explains placement and
  workspace ownership.
- [AgentHost architecture](../architecture/agenthost.md) and
  [contracts and endpoints](../reference/contracts.md) record runtime contracts.
