# Sandbox pod execution experience

This page is about what a person **sees and feels** when Agentweaver runs each agent in its own per-run
sandbox pod — both the user watching a run in the web UI and the operator reasoning about the cluster.
For the design logic see the [Sandbox pod execution deep dive](../deep-dive/sandbox-pod-execution.md);
for flags, identity, and the token mechanism see the
[Sandbox pods reference](../reference/sandbox-pods.md).

Related journeys: [Runs, board & watch](./runs-board-watch.md),
[Coordinator & orchestration](./coordinator-orchestration.md), [Operations](./operations.md),
and the [A2A distributed agents experience](./a2a-distributed-agents.md).

## Mental model

The most important thing to feel about pod-per-run is that **almost nothing changes in how a run looks**.
The board, live timeline, coordinator topology, and review gates retain their existing roles.
What changes is *where the work physically runs*: each run's agent now executes inside its own
Kata-isolated pod instead of inside the worker process. A **pod name** on the agent box shows recorded
placement. Provisioning delays and remote-turn failures can also be visible.

<!-- Shared diagram: canonical-sandbox-experience; consume the stable canonical PNG.
     The shared owner maintains the source and visual validation. -->

A well-behaved run should make the user barely notice the sandbox. The pod pill is a placement
signal, not independent proof of the sandbox's full isolation or credential posture.

## What the pod pill is

When the backend runs inside Kubernetes, each agent box in a run's topology shows a small **pod pill** —
a compact, monospace pill with a server icon and the executing pod's name (for example
`agentweaver-api-abc123` or `agent-pod-worker-7`). Hovering it shows the tooltip **"Executing in pod
{name}"**, and it is exposed to assistive technology with the same label.

What the user can take from it:

- **Where this agent is actually running.** Under pod-per-run, the pill on a node is *that run's* sandbox
  pod, so two concurrently-running agents can show two different pod names — a direct, visible cue that
  their work is isolated from each other.
- **Which placement was recorded.** Different pod names distinguish execution locations; configuration
  and cluster policy, not the pill alone, establish isolation.

The pill is **Kubernetes-only and quiet by design**:

- on local/dev runs, or anywhere the backend is not in Kubernetes, **no pill is shown** — the UI stays
  clean and there is nothing to explain;
- if the pod name is not yet known (the claim has not bound), **no pill is shown** rather than a
  placeholder; and
- if the runtime probe fails, the UI **degrades silently** to no pill rather than erroring.

Where the value comes from: the coordinator graph passes each node's `executionPodName` directly to the
pod indicator. A missing child-pod identity stays absent; it is not replaced with the API replica's name.
The binding is persisted in the shared run-event log so another replica can serve the recorded placement. The
data behind it is in the [reference](../reference/sandbox-pods.md#pod-naming-and-the-executing-pod-surface).

## What happens during a run

From the user's side, submitting and watching a run feels identical to before:

1. They submit work (or a coordinator goal) and watch the board / topology as usual.
2. Agent boxes light up as **running**, stream tokens and status live, and — on Kubernetes — show their
   pod pill.
3. The worker records pod-produced events and serves the existing timeline. Provisioning delays or an
   interrupted remote turn can still become visible.
4. When the run completes, the box settles into its terminal state as usual.

Under the hood the heavy work — the model session, the tools, the shell — is happening in the pod, and
the worker is relaying its stream into the same timeline. The user does not have to know that; the only
new thing on screen is the pod name.

## Sandbox preview: reaching a server inside the pod

Sometimes an agent starts a **server inside its sandbox pod** — a dev server, a freshly built app, a
debug endpoint — and a person wants to actually *open it* and look. Because the pod is isolated, that
server is not reachable by default. The **sandbox browser preview** publishes a run-scoped HTTPS
capability URL through the preview Gateway; the API provisions the route, rather than proxying app traffic.

A **Preview Sandbox** button appears on the coordinator run page only when the run is using
the Kubernetes sandbox (`sandboxBackend === 'kubernetes-sandbox-claim'`, read from the run's
`sandbox.selected` event) **and** a preview lifecycle state or existing preview session is present.
The current button is not gated simply on whether the run is active; retained previews can be opened
after completion. The flow a user follows:

1. **Open the preview dialog** ("Sandbox Preview") and **pick a port** — the port the agent's server is
   listening on *inside* the pod (the field defaults to `3000`; the default allowed range is `3000–9000`).
2. **Start the preview.** The app calls `apiClient.startPortForward(runId, port)` →
   `POST /api/runs/{runId}/sandbox/port-forward` with that port. Despite the historical endpoint name,
   the Gateway path resolves the bound pod and creates a ClusterIP Service and HTTPRoute.
3. **See it become active.** The dialog confirms with **"Preview active for port {target_port} on pod
   {pod_name}"** and shows the session id. If the API returned a proxied `preview_url`, the dialog embeds
   it in an iframe with an **"Open preview"** button. A public `preview_url` is the normal Gateway-path
   result; the dialog retains an explicit no-URL fallback for deployments without that path.
4. **Stop it when done.** Stopping the preview calls `apiClient.stopPortForward(runId, sessionId)` →
   `DELETE` on that session, removing its route and service. You can run more than one preview at a time (up to a
   per-run cap), each its own port and entry, and stop them individually.

<!-- Shared diagram: sandbox-browser-preview-fig1; owned by deep-dive-execution.
     Consume the stable canonical PNG; do not edit a local duplicate. -->

When to use it:

- **You want to look at what the agent built/ran** — a running web app, an API the agent stood up, a
  served artifact — without leaving the run view.
- **You're debugging inside the sandbox** and need a live endpoint into the pod for the moment.

What to expect:

- **Kubernetes-only.** The Gateway routes into the agent-sandbox controller's pod. On local/dev runs there is
  no claim pod to forward, so the button doesn't appear — the same "this is a cluster feature" boundary as
  the pod pill. (Local runs isolate commands with MXC, which has no pod to forward into.)
- **A public capability URL.** Anyone with the unguessable HTTPS URL can reach the preview. Do not share
  it as though it required an Agentweaver login.
- **Scoped to this run's pod.** A preview reaches only the run's own sandbox pod, never another run's, and
  is capped (default 3 per run, 20 globally).
- **Bounded lifetime and retained resources.** Project preview lifetime defaults to 1440 minutes
  (24 hours), used for both initial expiry and the hard cap. Keepalive cannot extend that hard cap.
  Preview retention can keep the claim/process available after a run ends or suspends; expiry, explicit
  Stop, or pod loss ends availability. A released/replaced pod may require a fresh preview.

The endpoints and the `PortForwardSessionDto` fields behind the dialog are in the
[reference](../reference/sandbox-pods.md#sandbox-preview-port-forward-feature-017).

> **Dedicated pages:** see the [Sandbox browser preview User Guide](./sandbox-browser-preview.md) for the
> full step-by-step, the [Reference](../reference/sandbox-browser-preview.md) for the API, and the
> [Deep Dive](../deep-dive/sandbox-browser-preview.md) for the Gateway control and data paths.

## Suspend and resume, from the user's view

Pod-per-run uses a **hybrid** lifecycle: the pod stays warm during active reasoning, and the worker can
release it at a workflow suspension when `Sandbox:ReleasePodOnSuspend=true`. This is a conditional,
best-effort release, not a rule that every external wait holds no pod. Assembly and active previews may
retain their pod/process and worktree.

What the user experiences across that boundary:

- **At a review/confirmation gate**, the run pauses for a decision. Where release is enabled and no
  retention applies, the worker attempts to release the execution pod while the human decides.
- **When they submit the decision**, a released pod is re-claimed and the run is
  rehydrated from its checkpoint. The worktree is already there (it lives on the shared workspace volume),
  and the resumed pod gets a **fresh** run-scoped credential.
- **The pod name may change after resume.** Because resume re-claims a (possibly different) warm pod, the
  pod pill on the agent box can show a **different name** than before the pause. That is expected and is
  the visible trace of release-and-rehydrate; the run, its history, and its workspace are continuous.

The coordinator's **orchestration loop stays in the worker** — only leaf agent turns occupy AgentHost.
Its timeline and steering do not depend on retaining a coordinator pod, although assembly preview
resources can deliberately stay alive during review.

A debug/low-latency option exists for operators (`Sandbox:ReleasePodOnSuspend = false`) that keeps the
pod warm across a suspension, at the cost of holding capacity. With release enabled, a name change across
a pause is normal; with retention, the same name can remain. Pod loss can change placement in either case.

## The operator's mental model

An operator reasons about pod-per-run as **"each run rents a pod for its active bursts, and gives it
back when it's waiting."** Concretely:

- **Mode is configuration.** `Sandbox:AgentExecutionMode` selects in-process (`in-api`, the code
  default) or per-run pods (`pod-per-run`, selected by the checked-in API/worker manifests).
  Apply changes through deployment; a mode change does not instantly migrate active turns.
  `Sandbox:ReleasePodOnSuspend` (default on) controls ordinary suspension release.
- **Kubernetes owns scheduling; the platform does not pre-gate on quota.** More concurrent runs means more claimed AgentHost pods; the `agentweaver-agent-host` pool keeps 2 run pods pre-warmed. The platform no longer checks quota headroom before a launch — it submits the `SandboxClaim` and waits for Kubernetes to schedule and bind the pod, so a **Pending** pod is an expected transient state (a node is freeing up or `katapool` is autoscaling), not a failure. The namespace `ResourceQuota` bounds only object counts (pod count, sandbox-claim count, PVCs, storage) — it no longer caps CPU/memory, and those counts are raised deliberately in the manifests, not patched live. See [Operations](./operations.md) and the
  [reference](../reference/sandbox-pods.md#pod-identity-and-quota).
- **The isolation backend is chosen per host.** Independently of pod-per-run, every host selects one
  command-isolation backend at run start and announces it with a `sandbox.selected` event (`backend`,
  `isRealIsolation`, `reason`). In-cluster that backend is the Kata-isolated `kubernetes-sandbox-claim`,
  whose pods are provisioned by the upstream **agent-sandbox controller** (installed by
  `scripts/azure/steps/10-create-cluster.mjs`); local dev gets `processcontainer` (**MXC**, a different
  local-host runtime) on Windows or `linux-bwrap` on Linux, falling back to `direct`
  (no isolation, shell still runs) only when nothing else is available. The deep dive's
  [executor seam](../deep-dive/sandbox-pod-execution.md#the-executor-seam-how-commands-are-actually-isolated)
  and [agent-sandbox controller](../deep-dive/sandbox-pod-execution.md#the-agent-sandbox-controller-mxc-vs-the-controller)
  sections explain how these relate to pod-per-run; backend install/selection is in
  [Sandbox setup](../reference/sandbox-setup.md#install-order).
- **The pod is disposable, not transparently replayable.** Durable workspace/checkpoint state supports
  recovery, but an interrupted A2A turn can fail visibly and require policy-driven redispatch. Retained
  preview pods can outlive the run; normal cleanup must not be confused with preview expiry.
- **Blast radius is small and visible.** Each pod is Kata-isolated, default-deny on egress (model +
  worker + git only, never the database), and holds no broker key. Inside the pod the boundary goes one
  level further: model-controlled commands run in a separate **executor sidecar container** with its
  own PID namespace and no cluster or cloud identity, so an injected agent cannot see, signal, or read
  the AgentHost process that holds the run's brokered GitHub token. The pod pill in the UI is the
  operator's quick "which pod is this run in?" answer.

### Diagnostics surface (MCP and runtime)

The same facts are available outside the topology view:

- **`GET /api/system/runtime`** answers "are we in Kubernetes, and what is the host pod name?" It is
  host diagnostics, not a replacement for missing child identity in the coordinator graph.
- **`GET /api/runs/{id}/graph`** carries `executionPodName` per node — the authoritative "which pod is
  this run/node executing in?" for a specific run.
- **MCP operations tools** expose the same operational health an operator needs around runs (diagnostics,
  heartbeat, sandbox policy). The MCP surface mirrors the web operations surface fact-for-fact; see the
  [MCP client experience](./mcp-client.md) and [Operations](./operations.md). Pod naming itself is a
  presentation detail surfaced primarily in the web topology; the underlying run/pod state is the same
  the API exposes.

The transport that carries agent turns into the pod is the **A2A bridge**, which ships on an experimental
`-preview` package line. Operators should treat it as such — pinned and behind the rollback flag — and
read the [A2A distributed agents experience](./a2a-distributed-agents.md) for what that means in practice.

## Edge cases the user may notice

- **No pill at all.** Local/dev or non-Kubernetes backends never show the pod pill — expected, not a bug.
- **Pill appears slightly after a node starts.** The name is shown once the pod is bound and registered;
  a brief gap before the pill appears is normal.
- **Pill name changes after a pause.** Release-on-suspend re-claims a fresh pod on resume, so the name can
  change across a review gate or a coordinator wait when release occurs.
- **Two runs, two pod names.** Different names show distinct recorded pod placements, not proof of every
  isolation control.

## Related reading

- [Sandbox pod execution deep dive](../deep-dive/sandbox-pod-execution.md) — the why and the logic.
- [Sandbox pods reference](../reference/sandbox-pods.md) — flags, identity/quota, token injection, naming.
- [Runs, board & watch](./runs-board-watch.md) and
  [Coordinator & orchestration](./coordinator-orchestration.md) — the journeys this annotates.
- [Operations](./operations.md) — health, heartbeat, and sandbox policy surfaces.
- [A2A distributed agents experience](./a2a-distributed-agents.md) — the `-preview` transport behind it.

<details id="diagram-context-canonical-sandbox-experience" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>From run request to reviewable work</td></tr>
<tr><td>takeaway</td><td>A run gets isolated execution, visible progress and bounded tools—not an unrestricted host shell.</td></tr>
<tr><td>group-title0</td><td>ADMIT AND PREPARE</td></tr>
<tr><td>group-title1</td><td>EXECUTE AND RETURN EVIDENCE</td></tr>
<tr><td>Authorized run</td><td>Authorized run</td></tr>
<tr><td>Authorized run</td><td>User requests repository work</td></tr>
<tr><td>Authorized run</td><td>Provider acceptance first</td></tr>
<tr><td>Authorized run</td><td>API owns run lifecycle</td></tr>
<tr><td>Authorized run</td><td>Project role required</td></tr>
<tr><td>SandboxClaim</td><td>SandboxClaim</td></tr>
<tr><td>SandboxClaim</td><td>Bind a warm AgentHost pod</td></tr>
<tr><td>SandboxClaim</td><td>Resolve claim-bound pod</td></tr>
<tr><td>SandboxClaim</td><td>Configure identity once</td></tr>
<tr><td>SandboxClaim</td><td>Claim state is shared</td></tr>
<tr><td>AgentHost</td><td>AgentHost</td></tr>
<tr><td>AgentHost</td><td>Model and governed tools</td></tr>
<tr><td>AgentHost</td><td>A2A authenticated turns</td></tr>
<tr><td>AgentHost</td><td>Run-scoped workspace</td></tr>
<tr><td>AgentHost</td><td>Copilot OR BYOK</td></tr>
<tr><td>Run experience</td><td>Run experience</td></tr>
<tr><td>Run experience</td><td>Events and human decisions</td></tr>
<tr><td>Run experience</td><td>Progress / tool evidence</td></tr>
<tr><td>Run experience</td><td>Approve consequential work</td></tr>
<tr><td>Run experience</td><td>Approval ≠ policy bypass</td></tr>
<tr><td>Isolated workspace</td><td>Isolated workspace</td></tr>
<tr><td>Isolated workspace</td><td>File and shell results</td></tr>
<tr><td>Isolated workspace</td><td>Contain paths and processes</td></tr>
<tr><td>Isolated workspace</td><td>Bound / redact tool output</td></tr>
<tr><td>Isolated workspace</td><td>Network policy also applies</td></tr>
<tr><td>Preview or review</td><td>Preview or review</td></tr>
<tr><td>Preview or review</td><td>Inspect resulting work</td></tr>
<tr><td>Preview or review</td><td>Preview needs publication proof</td></tr>
<tr><td>Preview or review</td><td>Review artifacts before merge</td></tr>
<tr><td>Preview or review</td><td>Release / reap compute</td></tr>
<tr><td>relation-0</td><td>1 start</td></tr>
<tr><td>relation-1</td><td>2 configure</td></tr>
<tr><td>relation-2</td><td>3 tools</td></tr>
<tr><td>relation-3</td><td>4 events / approvals</td></tr>
<tr><td>relation-4</td><td>5 work artifacts</td></tr>
<tr><td>assurance</td><td>Credentials arrive via /configure, not ambient stores. Approval, policy and isolation remain independent checks.</td></tr>
<tr><td>assurance-0-label</td><td>Workspace ownership</td></tr>
<tr><td>assurance-0-fact</td><td>Children use isolated worktrees.</td></tr>
<tr><td>assurance-0-source</td><td>RunOrchestrator.cs</td></tr>
<tr><td>assurance-1-label</td><td>Pod observation</td></tr>
<tr><td>assurance-1-fact</td><td>Pod telemetry is not branch ownership.</td></tr>
<tr><td>assurance-1-source</td><td>sandbox-pod-execution.md</td></tr>
<tr><td>assurance-2-label</td><td>Publication evidence</td></tr>
<tr><td>assurance-2-fact</td><td>Preview readiness proves public HTTPS.</td></tr>
<tr><td>assurance-2-source</td><td>SandboxPreviewPublicationTests.cs</td></tr>
<tr><td>n0</td><td>Provider acceptance first; API owns run lifecycle</td></tr>
<tr><td>n1</td><td>Resolve claim-bound pod; Configure identity once</td></tr>
<tr><td>n2</td><td>A2A authenticated turns; Run-scoped workspace</td></tr>
<tr><td>n3</td><td>Progress / tool evidence; Approve consequential work</td></tr>
<tr><td>n4</td><td>Contain paths and processes; Bound / redact tool output</td></tr>
<tr><td>n5</td><td>Preview needs publication proof; Review artifacts before merge</td></tr>
<tr><td>groups</td><td>ADMIT AND PREPARE; EXECUTE AND RETURN EVIDENCE</td></tr>
</tbody></table>
</details>

<details id="diagram-context-experience-sandbox-pod-execution-fig3" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>A durable run can outlive its pod</td></tr>
<tr><td>takeaway</td><td>A review wait may release compute; preview and assembly retention are explicit exceptions.</td></tr>
<tr><td>group-title-0</td><td>ACTIVE WORK AND WAIT</td></tr>
<tr><td>group-title-1</td><td>RESUME OR RETAIN</td></tr>
<tr><td>Active leaf</td><td>Active leaf</td></tr>
<tr><td>Active leaf</td><td>AgentHost returns output</td></tr>
<tr><td>Active leaf</td><td>pod -&gt; worker -&gt; timeline</td></tr>
<tr><td>Active leaf</td><td>Worker records events and owns workflow progression.</td></tr>
<tr><td>Worker gate</td><td>Worker gate</td></tr>
<tr><td>Worker gate</td><td>Human decision requested</td></tr>
<tr><td>Worker gate</td><td>checkpoint-backed wait</td></tr>
<tr><td>Worker gate</td><td>Persist resumable state; pause watchdog accounting.</td></tr>
<tr><td>Release decision</td><td>Release decision</td></tr>
<tr><td>Release decision</td><td>Policy and lifecycle checks</td></tr>
<tr><td>Release decision</td><td>pod-per-run + enabled</td></tr>
<tr><td>Release decision</td><td>Release requires support and no active retention exception.</td></tr>
<tr><td>Resumed work</td><td>Resumed work</td></tr>
<tr><td>Resumed work</td><td>Continue from saved state</td></tr>
<tr><td>Resumed work</td><td>events through worker</td></tr>
<tr><td>Resumed work</td><td>A released pod may be replaced; its name can change.</td></tr>
<tr><td>Durable checkpoint</td><td>Durable checkpoint</td></tr>
<tr><td>Durable checkpoint</td><td>Session and workspace</td></tr>
<tr><td>Durable checkpoint</td><td>authorized decision</td></tr>
<tr><td>Durable checkpoint</td><td>Load resumable state; claim/configure if released.</td></tr>
<tr><td>Release or retain</td><td>Release or retain</td></tr>
<tr><td>Release or retain</td><td>Claim deletion / keep pod</td></tr>
<tr><td>Release or retain</td><td>preview / assembly exception</td></tr>
<tr><td>Release or retain</td><td>Active preview defers deletion; release failures are logged.</td></tr>
<tr><td>e0</td><td>reach gate</td></tr>
<tr><td>e1</td><td>evaluate</td></tr>
<tr><td>e2</td><td>apply</td></tr>
<tr><td>e3</td><td>checkpoint</td></tr>
<tr><td>e4</td><td>resume</td></tr>
<tr><td>note</td><td>Human waiting does not imply zero retained compute. This is not arbitrary failed-turn rehydration.</td></tr>
<tr><td>n0</td><td>Worker records events and
owns workflow progression.</td></tr>
<tr><td>n1</td><td>Persist resumable state;
pause watchdog accounting.</td></tr>
<tr><td>n2</td><td>Release requires support and
no active retention exception.</td></tr>
<tr><td>n3</td><td>A released pod may be replaced;
its name can change.</td></tr>
<tr><td>n4</td><td>Load resumable state;
claim/configure if released.</td></tr>
<tr><td>n5</td><td>Active preview defers deletion;
release failures are logged.</td></tr>
<tr><td>groups</td><td>ACTIVE WORK AND WAIT; RESUME OR RETAIN</td></tr>
</tbody></table>
</details>

<details id="diagram-context-sandbox-browser-preview-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Preview readiness follows the public path</td></tr>
<tr><td>takeaway</td><td>Provision the route, then probe its exact HTTPS URL; object creation alone is not ready.</td></tr>
<tr><td>group-title0</td><td>CONTROL: PROVISION + PROBE</td></tr>
<tr><td>group-title1</td><td>GATEWAY DATA PATH</td></tr>
<tr><td>Preview API</td><td>Preview API</td></tr>
<tr><td>Preview API</td><td>Resolve bound SandboxClaim</td></tr>
<tr><td>Preview API</td><td>Patch run selector on pod</td></tr>
<tr><td>Preview API</td><td>Create Service + HTTPRoute</td></tr>
<tr><td>Preview API</td><td>State from cluster, not cache</td></tr>
<tr><td>Publication probe</td><td>Publication probe</td></tr>
<tr><td>Publication probe</td><td>Exact generated HTTPS URL</td></tr>
<tr><td>Publication probe</td><td>Wait for managed DNS</td></tr>
<tr><td>Publication probe</td><td>Check Gateway + application</td></tr>
<tr><td>Publication probe</td><td>Only then return ready</td></tr>
<tr><td>Browser preview</td><td>Browser preview</td></tr>
<tr><td>Browser preview</td><td>Open the returned URL</td></tr>
<tr><td>Browser preview</td><td>Run-scoped capability host</td></tr>
<tr><td>Browser preview</td><td>Keepalive via API</td></tr>
<tr><td>Browser preview</td><td>Iframe: no-referrer</td></tr>
<tr><td>Preview Gateway</td><td>Preview Gateway</td></tr>
<tr><td>Preview Gateway</td><td>Separate shared Gateway</td></tr>
<tr><td>Preview Gateway</td><td>HTTPS host match</td></tr>
<tr><td>Preview Gateway</td><td>HTTPRoute selects Service</td></tr>
<tr><td>Preview Gateway</td><td>Not API port-forward</td></tr>
<tr><td>ClusterIP Service</td><td>ClusterIP Service</td></tr>
<tr><td>ClusterIP Service</td><td>Per-preview target selector</td></tr>
<tr><td>ClusterIP Service</td><td>Service :80 → public port</td></tr>
<tr><td>ClusterIP Service</td><td>Routes to bound sandbox pod</td></tr>
<tr><td>ClusterIP Service</td><td>Allowed ports 3000–9000</td></tr>
<tr><td>Sandbox preview app</td><td>Sandbox preview app</td></tr>
<tr><td>Sandbox preview app</td><td>AgentHost pod-local path</td></tr>
<tr><td>Sandbox preview app</td><td>Live preview: TCP forwarder</td></tr>
<tr><td>Sandbox preview app</td><td>0.0.0.0 → loopback app</td></tr>
<tr><td>Sandbox preview app</td><td>Manual: chosen target port</td></tr>
<tr><td>relation-0</td><td>1 after create</td></tr>
<tr><td>relation-1</td><td>2 ready URL</td></tr>
<tr><td>relation-2</td><td>3 HTTPS probe</td></tr>
<tr><td>relation-3</td><td>4 HTTPS</td></tr>
<tr><td>relation-4</td><td>5 route</td></tr>
<tr><td>relation-5</td><td>6 public port</td></tr>
<tr><td>assurance</td><td>No API → pod TCP readiness probe. Publication failure rolls back; DNS convergence has a bounded retry window.</td></tr>
<tr><td>assurance-0-label</td><td>Public readiness</td></tr>
<tr><td>assurance-0-fact</td><td>Probe the exact generated HTTPS URL.</td></tr>
<tr><td>assurance-0-source</td><td>SandboxPreviewService.cs</td></tr>
<tr><td>assurance-1-label</td><td>Rollback on failure</td></tr>
<tr><td>assurance-1-fact</td><td>Unpublish failed preview resources.</td></tr>
<tr><td>assurance-1-source</td><td>SandboxPreviewPublicationTests.cs</td></tr>
<tr><td>assurance-2-label</td><td>Separate ingress</td></tr>
<tr><td>assurance-2-fact</td><td>DNS managed externally, not by API.</td></tr>
<tr><td>assurance-2-source</td><td>gateway-preview.yaml</td></tr>
<tr><td>n0</td><td>Patch run selector on pod; Create Service + HTTPRoute</td></tr>
<tr><td>n1</td><td>Wait for managed DNS; Check Gateway + application</td></tr>
<tr><td>n2</td><td>Run-scoped capability host; Keepalive via API</td></tr>
<tr><td>n3</td><td>HTTPS host match; HTTPRoute selects Service</td></tr>
<tr><td>n4</td><td>Service :80 → public port; Routes to bound sandbox pod</td></tr>
<tr><td>n5</td><td>Live preview: TCP forwarder; 0.0.0.0 → loopback app</td></tr>
<tr><td>groups</td><td>CONTROL: PROVISION + PROBE; GATEWAY DATA PATH</td></tr>
</tbody></table>
</details>
