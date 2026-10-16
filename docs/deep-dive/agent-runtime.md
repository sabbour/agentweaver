# Agent runtime

## The mental model

AgentHost is the execution host inside a selected Sandbox.
`Agentweaver.AgentRuntime` is the shared library that creates and guards its
native Copilot SDK session.

AgentHost does not choose its own actor, project, model, credentials, or placement.
It proves that the delivered configuration still matches Orchestrator,
Environment, and Identity before it starts a session or performs protected work.

The current host uses `GitHub.Copilot.SDK` 1.0.18 with native runtime 1.0.97.
Microsoft Agent Framework belongs to Orchestrator, not this model harness.

## Where this lives

- `services/agenthost/Agentweaver.AgentHost/Program.cs` — Executable modes,
  server-owned configuration, authentication, and runtime composition.
- `services/agenthost/Agentweaver.AgentHost/NativeRuntimeManifest.cs` — Image-owned
  native executable and manifest verification.
- `services/agenthost/Agentweaver.AgentHost/BuildTestOutputCollectorProgram.cs` —
  Separate trusted output and workspace collection modes.
- `packages/Agentweaver.AgentRuntime/RuntimeAgentHost.cs` — Bootstrap activation,
  readiness, bounded turn queues, usage acknowledgment, suspend, and resume.
- `packages/Agentweaver.AgentRuntime/RuntimeAgentHostEndpoints.cs` — Authenticated
  runtime routes and HTTP error mapping.
- `packages/Agentweaver.AgentRuntime/RuntimeBootstrapReceiver.cs` and
  `RuntimeSessionBootstrap.cs` — Configure delivery, grant exchange, skill reads,
  and SDK creation.
- `packages/Agentweaver.AgentRuntime/RuntimeCopilotSessionFactory.cs` — Native
  session options, model source, prompt capacity, skills, and recovery.
- `packages/Agentweaver.AgentRuntime/AuthorizedRuntimeSession.cs` — Current
  authority, guarded sends, durable content, usage, and cache writes.
- `packages/Agentweaver.AgentRuntime/RuntimeCopilotToolCallAuthorization.cs` —
  Native tool identity and permission checks.
- `packages/Agentweaver.AgentRuntime/RuntimeCopilotNativeTurnJournal.cs` — Native
  turn-event evidence.
- `packages/Agentweaver.AgentRuntime/RuntimeSessionRecovery.cs` and
  `RuntimeSessionMaterialHttpClient.cs` — Compatible cache and journal recovery.
- `packages/Agentweaver.AgentRuntime/MountedWorkspaceFlush.cs` — Mounted workspace
  flush evidence.
- `packages/Agentweaver.Identity/` — Runtime registration, grant, placement, and
  owner transport contracts.

## Core concepts and invariants

### Registration, credential, and placement must agree

Orchestrator owns the runtime registration and accepted execution binding.
Environment owns the current Sandbox lease, resource generation, image, and
runtime profile. Identity owns separate purpose-bound configure, observe, and
model-credential authority.

AgentHost compares these bindings rather than trusting request fields. It
rechecks current owner authority after remote waits and before protected effects.
A valid bearer alone does not make a stale registration current.

### Model source is explicit

Hosted Copilot uses the selected Identity-owned connection. BYOK uses the exact
selected credential reference and explicit native provider configuration.
Neither mode falls back to the other.

The current SDK path delivers a real hosted access token or BYOK key to the
runtime inside the Sandbox. It is not credential-less guest execution. The
[Identity architecture](../architecture/identity-secrets.md) separates that
implementation from the credential-less proposal.

### A ready process is not an authorized turn

The host requires distinct existing private-state and workspace directories,
an image-owned native executable, registered model bindings, and explicit owner
addresses. It checks startup phase evidence and budgets before reporting runtime
readiness.

`/health/live` reports process liveness. `/health/ready` checks current runtime
prerequisites. Authenticated `/runtime/v1/readiness` returns a current,
noncredential receipt; it does not start the SDK or send a turn.

## Bootstrap lifecycle

1. **Receive configuration.** `POST /runtime/v1/configure` accepts an
   authenticated pending configure delivery for the current registration.
   Delivery alone does not create a model session.
2. **Activate.** `POST /runtime/v1/configure/activate` supplies the exact
   registration, configuration, and operation identities.
3. **Read accepted content.** The bootstrap reads assigned, pinned skills through
   Orchestrator and verifies their content binding.
4. **Consume and exchange.** Identity consumes the configure proof and exchanges
   it for the purpose-bound observe credential. The bootstrap separately redeems
   the selected model credential.
5. **Recover context.** Events supplies compatible cache material or ordered
   journal context when history exists.
6. **Create the SDK session.** The factory applies the accepted model, native
   tool hooks, skills, prompt capacity, and recovery input.
7. **Commit configuration and readiness.** The host records configuration
   acknowledgment and verifies current placement before returning readiness.

Bootstrap failure cleans up the prepared SDK session and invalidates acquired
credentials. Exact activation replay reuses the existing session only when the
configuration and binding match.

## Turn lifecycle

`POST /runtime/v1/a2a/message:send` accepts a bound user message with one text part,
a nonempty message ID, the native context ID, and `immediate` or `enqueue`
delivery.

The host fingerprints the request. An exact same-host retry shares the existing
turn result. Changed content for the same message ID conflicts. The queue has a
configured pending-turn bound.

The dispatcher serializes turns. It selects immediate work before queued work at
the next dispatch boundary; it does not interrupt an active native turn.

For a workflow-bound native turn:

1. Validate the current registration, observe proof, model authority, skills,
   placement, and owner admission.
2. Commit the user content to Events.
3. Send through the guarded native SDK session.
4. Read the native turn's event evidence and store the owner source receipt.
5. Commit assistant content and the native cache.
6. Flush usage, obtain Events accounting acknowledgments, and complete the
   owner-bound turn before returning its answer.

The non-workflow turn path also guards the model action and commits user content,
assistant content, and cache. It does not gain workflow-specific usage affinity
by supplying a message ID.

## Tool and budget enforcement

Native tools map to named actions such as `tool.read`, `tool.write`, and
`exec.shell`. Permission checks bind the native session, active message, tool
call ID, action, and input hash. Unknown tools, unmatched permissions, and
sandbox-bypass requests are rejected. Approved permissions apply once.

Owner admission enforces accepted model-turn and tool-call limits. Hosted
Copilot with a zero hard credit limit denies the turn before native Send and
records guard/no-send evidence through the owner path. A positive hard limit
requires the workflow-bound admission path; an unbound session is rejected with
`runtime_usage_affinity_unavailable`.

`MaxPromptTokens` is a per-prompt/context capacity, not a cumulative run token
budget. The factory resolves it against the concrete model and narrower limits.
The SDK compacts removable history and rejects a turn if required context still
does not fit.

## Durability and recovery

Turn content and SDK cache bytes use Events' typed material API. The runtime
does not choose storage keys or consider a local file a durable acknowledgment.
Usage travels through immutable Orchestrator source receipts and reference-only
Events accounting.

Recovery has three modes:

| Mode | Input |
| --- | --- |
| `Fresh` | No prior recovery context. |
| `NativeCache` | Verified cache matching the session, tenant, accepted selection, SDK/runtime, model, and prompt capacity. |
| `JournalRebuild` | Ordered recorded conversation context from the authenticated journal. |

Rebuilt context explicitly identifies prior messages as history, not permission
to repeat effects. Same-process message deduplication does not establish
exactly-once execution after a process loss.

Suspend stops new turn admission, drains admitted turns, flushes usage and native
state, and flushes the mounted workspace before returning evidence. Resume
verifies the saved configuration, cache or journal input, current registration,
and restored workspace through the owner-controlled path.

## Failure behavior

Authentication failure returns `401`; current-authority rejection returns `403`.
Invalid input returns `400`. Owner, startup, and execution failures return `503`.
Runtime responses use `Cache-Control: no-store`.

Usage persistence failure stops runtime dispatch. Missing accounting
acknowledgments do not become successful completion. Lost placement, expired
grants, changed skill content, or changed registration blocks continued use
instead of retaining earlier authority.

The collector executable modes are separate from model execution. They collect
bounded BuildTest files or workspace output under their trusted contracts; they
do not create an SDK session or schedule an agent.

## Continue reading

- [Orchestration](./orchestration.md) explains dispatch and current execution.
- [Events and sessions](./events-sessions.md) explains material and accounting.
- [Environment and sandbox](./environment-sandbox.md) explains placement and
  workspace ownership.
- [AgentHost architecture](../architecture/agenthost.md) and
  [contracts and endpoints](../reference/contracts.md) record runtime contracts.
