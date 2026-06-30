# Sandbox pods reference

Exhaustive reference for **pod-per-run** sandbox execution: configuration flags, pod identity and quota,
run-scoped GitHub token injection, pod naming, and the security properties of the model. For the
reasoning behind these mechanics, see the
[Sandbox pod execution deep dive](../deep-dive/sandbox-pod-execution.md); for the operator/user view, see
the [Sandbox pod execution experience](../experience/sandbox-pod-execution.md).

This page documents the sandbox-pod *execution* surface (where the agent turn runs). The broader sandbox
isolation model — filesystem containment, governance, executor selection, and claim lifecycle — is the
[Sandbox deep dive](../deep-dive/sandbox.md), and operator install/config is
[Sandbox setup](./sandbox-setup.md).

## Configuration flags

| Flag | Values | Default | Effect |
|---|---|---|---|
| `Sandbox:AgentExecutionMode` | `in-api`, `pod-per-run` | `in-api` | `in-api` runs the agent turn in-process in the API/worker (today's behavior, the **rollback path**). `pod-per-run` relocates each run's agent turn into its own Kata-isolated sandbox pod via the A2A bridge. |
| `Sandbox:ReleasePodOnSuspend` | `true`, `false` | `true` | When `pod-per-run` is active and the workflow graph suspends on an external gate (a HITL/review `RequestPort`, or the coordinator idling while it awaits child runs), `true` checkpoints the run and **releases** the pod back to the warm pool. `false` keeps the pod warm across the suspension for low-latency resume or debugging, at the cost of held capacity. |

### Flag semantics

- **`pod-per-run` is the only value that activates the bridge.** Any other value (the `in-api` default)
  keeps execution in-process. There is no separate "pod-per-turn" mode — granularity within
  `pod-per-run` is the hybrid model (warm across consecutive turns, release on suspend), governed by
  `Sandbox:ReleasePodOnSuspend`, not by a distinct execution-mode value.
- **`ReleasePodOnSuspend` only matters under `pod-per-run`.** It is a tuning sub-flag; it never changes
  the execution-mode value. The release is internal behavior of `pod-per-run`.
- **Rollback is a flag flip, not a redeploy.** Setting `Sandbox:AgentExecutionMode=in-api` restores
  in-process execution immediately. This is the documented mitigation for any instability in the
  `-preview` A2A transport — there is no alternate wire transport to deploy. See the
  [A2A reference](./a2a.md) for the transport's preview status and pinning.

> A related delivery flag, `Sandbox:GitHubTokenDelivery`, governs how the run-scoped GitHub token reaches
> the pod (see [Run-scoped GitHub token delivery](#run-scoped-github-token-delivery)).

## Pod identity and quota

A pod-per-run sandbox is the same Kata-isolated pod shape the sandbox subsystem already uses, claimed
from a warm pool, but now hosting the full agent (worker agents **and** the coordinator's own agent
turns) rather than only ad-hoc shell commands.

| Property | Value / behavior |
|---|---|
| Runtime class | `kata-vm-isolation` — a VM boundary around the container, so each run's secret and execution live inside a per-run microVM and are destroyed with it. |
| Identity | Dedicated sandbox service account; **workload identity** (federated OIDC) is the preferred path for the model credential, projecting **only** the narrowly-scoped workload-identity token volume — not the full Kubernetes API service-account token. |
| Cluster API access | None. The pod does not automatically receive Kubernetes API credentials; the sandbox stays tokenless for the cluster API even when workload identity is enabled for the model endpoint. |
| Provisioning | Claimed from a **warm pool** via a `SandboxClaim`; the executor waits until the claim is `Bound` to a concrete pod. For pod-per-run AgentHost pods it then **waits for the in-pod AgentHost to serve `/healthz` on `:8088`** before returning the A2A endpoint (a bound claim only means the pod is `Running` — Kestrel binds `:8088` ~20–30 s later). Warm-pool size is a capacity decision balanced against claim latency and quota. |
| AgentHost readiness gate | With the agent-host warm pool at `replicas: 0` every run cold-starts, so the executor polls `GET {scheme}://{podIP}:8088/healthz` (bounded `Sandbox:Kubernetes:AgentHostReadyTimeoutSeconds`, default `90`s; `…ReadyPollIntervalMs`, default `1000`) before the first A2A turn; on timeout the launch fails deterministically rather than refusing mid-run. The `a2a-sandbox-pod` HttpClient additionally retries connection-refused only. (A supplementary `tcpSocket` startup/readiness probe on `8088` in the agent-host template is *recommended* but owned by the cluster/infra side.) |
| A2A turn authentication | Pod launch generates a 256-bit random `AgentHost:TurnBearerToken`, injects it as `AgentHost__TurnBearerToken`, and registers it in `IAgentHostTurnTokenRegistry`. `RemoteAgentProxy` sends `Authorization: Bearer {token}` on `message:stream`; each pod accepts only its own run's token. |
| Per-pod resources | Sized for a real agent runtime (a live session + model I/O), not a `sleep infinity` placeholder — materially larger CPU/memory requests than the shell-only baseline. Exact numbers are a capacity decision. |
| Quota | Namespace `ResourceQuota` caps pod count, CPU/memory requests, and sandbox-claim count. Heavier per-pod requests plus multiple web/worker replicas require these caps to be **raised deliberately** via a reviewed manifest change, never a live patch. |
| Lifetime | Bounded by the run and the claim TTL. Under the hybrid model, a pod is released on suspend and a fresh pod is re-claimed on resume; pods never persist past the run. |
| Egress | Default-deny NetworkPolicy with a narrow allowlist (see [Security properties](#security-properties)). |
| Storage | Mounts the **shared workspace volume** (the worktree path) so worktree commit/diff stays on the worker side; the pod is otherwise stateless beyond the live turn. |

## Run-scoped GitHub token delivery

A pod-per-run sandbox acts **as the run's signed-in user** and needs a GitHub credential to clone/push
the worktree and call GitHub API tools. The mechanism projects that user's Key Vault-backed GitHub OAuth
token into *that run's pod* through a run-scoped CSI mount. No token is written to the shared workspace PVC.

### Sourcing

- The token is obtained by the **worker/API at claim-creation time** through the existing valid-access-
  token provider, which reads from the source-of-truth token store, transparently refreshes a near-expiry
  token, and persists the rotation to the user's Key Vault secret.
- **Scope is the user, not the installation.** A user-initiated run resolves to the owning user's scope;
  installation scope is reserved for background/system tasks with no caller and is never injected into a
  user's run.
- **Failure is a pre-flight gate.** If a valid token cannot be produced at claim time (signed-out, or
  expired-and-unrefreshable), the run **fails the claim** with a typed re-auth error rather than degrading
  — no pod is adopted, and no empty/placeholder token is ever injected. "Can this run touch GitHub as
  this user?" is decided *before* a pod is claimed.

### Delivery to the executing pod

Each authenticated user's GitHub token is stored in Azure Key Vault under a per-user key
(`ghtok-user--{base32(userId)}`) and is never written to shared storage. For pod-per-run execution,
`KubernetesSandboxExecutor` creates a run-scoped `SecretProviderClass` that references only the run
owner's Key Vault secret and aliases it to `user_{userId}.json`. The sandbox pod mounts that CSI
projection at `AgentHost:KvTokenMountPath` (for example `/mnt/user-tokens`) and reads only its own
user's token file.

The pod consumes the value directly:

- as a git credential helper / credential-store entry in `https://x-access-token:{token}@github.com`
  form, mirroring how the API already clones with `x-access-token`, so `git clone`/`git push` work
  without further wiring; and/or
- as a raw token file the pod wires into `GITHUB_TOKEN`/askpass for GitHub API tools.

### Lifetime and cleanup

- **Key Vault is the source of truth.** The API writes OAuth callback and refresh results only to the
  per-user Key Vault secret; the Key Vault token store uses no disk mirror in AKS.
- **Bounded by the run/claim.** The token's lifetime is tied to the run; it never outlives the run.
- **Projection is run-scoped.** The CSI mount is tied to the run's pod and contains only the run owner's
  token alias.
- **Rotation follows Key Vault/CSI.** The CSI driver refreshes the projected file from Key Vault, and a
  re-claim creates a fresh run-scoped projection for the same per-user secret.

### AgentHost pod-side read

In-pod, the token is served through a **read-only, single-scope** token store:

- it reads the token from the CSI-mounted per-user file and serves it for the **single** run scope the
  pod was launched for — no scope enumeration, no listing of other users' directories;
- it implements the standard token-store contract so nothing downstream changes: it returns the injected
  access token (with expiry if provided) and **`RefreshToken = null`**, and identity (login) if provided;
- **writes are refused.** Sign-out / set operations are no-ops or `NotSupported` — the pod must never
  write credential state back to a shared location. The **worker/API owns refresh**; the pod only ever
  *receives* a fresh access token.

If the token file is absent at container start, the pod retries briefly for the first CSI sync and then
fails clearly rather than proceeding unauthenticated.

```mermaid
sequenceDiagram
    participant Worker as Worker / API
    participant Provider as GitHub access-token provider
    participant KV as Azure Key Vault
    participant SPC as Run-scoped SecretProviderClass
    participant Mount as CSI mount
    participant Host as AgentHost (in pod)
    Worker->>Provider: GetValidAccessToken(userScope)
    alt no valid token
        Provider-->>Worker: null
        Worker-->>Worker: fail claim → typed re-auth error (no pod)
    else valid access token
        Provider->>KV: read/refresh ghtok-user--{base32(userId)}
        Provider-->>Worker: valid per-user token
        Worker->>SPC: reference only this user's secret
        Worker->>Host: claim/adopt pod
        SPC->>Mount: project user_{userId}.json
        Host->>Mount: read token (single scope)
        Host->>Host: serve token; writes refused
    end
```

## A2A turn bearer token

The A2A turn endpoint has a separate per-run bearer token from the GitHub user token above:

1. `KubernetesSandboxExecutor` creates 32 random bytes (`256` bits) at AgentHost pod launch.
2. The token is injected into the pod environment as `AgentHost__TurnBearerToken`.
3. The same token is stored in `IAgentHostTurnTokenRegistry` for the owning run.
4. `RemoteAgentProxy` reads the registry and sends `Authorization: Bearer {token}` on all calls to `POST /a2a/agent/v1/message:stream`.
5. `AgentHost` rejects turn requests whose header does not exactly match its own `AgentHostOptions.TurnBearerToken`.

This is application-layer auth on top of the A2A NetworkPolicy/mTLS boundary. The important blast-radius property is that a stolen token from one run cannot be reused against another run's pod.

## Pod naming and the executing-pod surface

A run's executing pod name is tracked so the UI can show *where* a run is running.

- **`PodNameRegistry`** is an in-memory map from **run id → bound pod name**. It is populated by the
  Kubernetes sandbox executor once a `SandboxClaim` reports its `Ready` condition `True`, and the entry is
  removed when the claim is deleted (e.g. on run cleanup or release).
- The registry is consumed in two places:
  - the **system runtime endpoint** (`GET /api/system/runtime`) returns `{ kubernetes, podName }`,
    where `podName` is the API/host pod name when running inside Kubernetes — the global fallback; and
  - the **run graph endpoint** (`GET /api/runs/{id}/graph`) populates an **`executionPodName`** field on
    each node from the registry, so a per-run/per-node pod name overrides the global fallback as the
    pod-per-run rollout begins carrying the correct per-pod value automatically.
- The frontend resolves `node.executionPodName ?? globalPodName` and renders it as a small pod pill
  (the "executing pod name" surfaced on agent boxes). The pill renders **only on Kubernetes** — when not
  running in-cluster (`kubernetes: false`) or when the pod name is null, nothing is shown, so local/dev
  runs stay clean. See the [experience doc](../experience/sandbox-pod-execution.md#what-the-pod-pill-is)
  for the rendered behavior.

| Field | Source | Meaning |
|---|---|---|
| `kubernetes` | `GET /api/system/runtime` | Whether the backend is running inside Kubernetes; gates whether any pod pill is shown. |
| `podName` (global) | `GET /api/system/runtime` | The host/API pod name — the fallback pill when no per-node value exists. |
| `executionPodName` (per node) | `GET /api/runs/{id}/graph`, topology deltas, `subtask.*` events | The bound sandbox pod name for that run/node, from `PodNameRegistry`; overrides the global fallback. |

> The same `PodNameRegistry` also lets preview/port-forward tooling locate a run's pod. That preview
> path is documented in the [Sandbox deep dive](../deep-dive/sandbox.md#why-run-ids-map-to-pod-names) and,
> for its API surface, in [Sandbox preview port-forward](#sandbox-preview-port-forward-feature-017) below.

## Sandbox preview port-forward (Feature 017)

> **Dedicated pages:** this feature now has its own [Reference](./sandbox-browser-preview.md),
> [User Guide](../experience/sandbox-browser-preview.md), and
> [Deep Dive](../deep-dive/sandbox-browser-preview.md). The summary below stays here for context within the
> sandbox-pods surface.

A **preview port-forward** exposes a port of a run's sandbox pod back through the API, so an operator can
reach a server the agent started **inside** the pod (a dev server, a built app, a debug endpoint) as a
live preview scoped to that one run's pod. `PortForwardService` shells out to
`kubectl port-forward --address 127.0.0.1 pod/{podName} :{targetPort} -n {namespace}` (it does **not** use
the Kubernetes API), parses the `Forwarding from 127.0.0.1:<port> ->` line to learn the local port, and
probes loopback TCP until ready. The pod is the same one `KubernetesSandboxExecutor` provisions through
the [agent-sandbox controller](../deep-dive/sandbox.md#the-agent-sandbox-controller-and-where-mxc-fits) —
the preview tunnels into *that* pod, not an MXC local sandbox.

This surface is **Kubernetes-only**: it tunnels through the [Kubernetes claim backend](./sandbox-setup.md#kubernetes-in-cluster)'s
pod, located by run id via the [`PodNameRegistry`](#pod-naming-and-the-executing-pod-surface). On local/dev
backends (no claim pod) there is nothing to forward, and the start call fails with a conflict — *"the run
must be `in_progress` with an active Kubernetes sandbox"*. Every call also verifies the run exists and the
caller owns it (`403`/`404` otherwise).

### Endpoints

| Method & path | Body | Returns | Effect |
|---|---|---|---|
| `POST /api/runs/{runId}/sandbox/port-forward` | `{ "target_port": <1..65535> }` | `PortForwardSessionDto` | Starts a `kubectl port-forward` from the run's pod's `target_port` to a loopback port on the API, and returns the new session. `429` when a session cap is hit; `409` when the run has no active sandbox pod. |
| `GET /api/runs/{runId}/sandbox/port-forward` | — | `PortForwardSessionDto[]` | Lists the active preview sessions for the run. |
| `DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}` | — | `{ session_id, stopped: true }` | Stops the identified session and tears down its tunnel. |

### `PortForwardSessionDto`

| Field | Meaning |
|---|---|
| `session_id` | Identifier for this preview session; used as `{sessionId}` to stop it via `DELETE`. |
| `local_port` | The loopback port **on the API host** that `kubectl` bound; what the API forwards from. The backend returns this port, **not** a public URL. |
| `target_port` | The port **inside** the sandbox pod that is being forwarded. |
| `pod_name` | The bound sandbox pod the tunnel targets (from `PodNameRegistry`). |
| `started_at` | When the session started. |
| `preview_url` / `previewUrl` | **Web-only, optional.** The frontend reads these to render an embedded iframe, but the backend does **not** currently populate them; the UI explicitly says so when no proxied URL is returned. |

### Behavior

- **Per-port, explicit.** A session forwards one `target_port`; opening another preview is a second
  `POST`. Sessions are listed and stopped individually.
- **Scoped to the run's pod.** A session can only reach *that* run's sandbox pod — the run id resolves to a
  single bound pod, so a preview never crosses into another run's pod.
- **Inbound only, no egress widening.** The tunnel is an inbound path the operator opens to the pod; it
  does **not** alter the pod's default-deny egress allowlist (see [Security properties](#security-properties)).
- **Capped per run and globally.** Default **3** concurrent sessions per run
  (`Sandbox:PortForward:MaxConcurrentSessionsPerRun`, fallback `:MaxPerRun`) and **20** globally
  (`Sandbox:PortForward:MaxConcurrentSessionsGlobal`, fallback `:MaxGlobal`); exceeding either raises
  `PortForwardLimitExceededException` → `429`.
- **In-memory, no TTL.** Sessions live only in `PortForwardService`'s in-process maps (`_sessions` /
  `_sessionsByRun`); there is no expiry timer. They end only on explicit `DELETE`, run end (via
  `RunWatchLoopService`, which also unregisters the pod), the `kubectl` process exiting on its own, or
  `Dispose()` at shutdown.
- **Bounded by the pod.** A session is only valid while the run's pod is bound; releasing or replacing the
  pod (suspend/resume, run end) ends forwarding, and a new preview must be started against the re-claimed
  pod.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    participant User as User / operator
    participant API as API (SandboxEndpoints)
    participant PF as PortForwardService
    participant Reg as PodNameRegistry
    participant K as kubectl
    participant Pod as Sandbox pod (run-bound)
    User->>API: POST /api/runs/{runId}/sandbox/port-forward { target_port }
    API->>API: validate port, run exists, caller owns run
    API->>PF: StartAsync(runId, target_port)
    PF->>Reg: TryGet(runId) → pod_name
    alt no pod registered
        Reg-->>PF: null
        PF-->>API: InvalidOperationException → 409
    else pod registered
        PF->>K: kubectl port-forward --address 127.0.0.1 pod/{pod} :{target_port} -n {ns}
        K->>Pod: tunnel target_port
        K-->>PF: "Forwarding from 127.0.0.1:{local_port} ->"
        PF->>PF: probe loopback TCP until ready
        PF-->>API: PortForwardSession
        API-->>User: { session_id, local_port, target_port, pod_name, started_at }
    end
    User->>API: open 127.0.0.1:{local_port} → reach server in pod
    User->>API: DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}
    API->>PF: Stop(runId, sessionId) → kill kubectl
```

## Security properties

| Property | Pod-per-run guarantee |
|---|---|
| Execution isolation | Each run's agent turn, tools, shell, and file ops run in the run's **own Kata-isolated pod** (`kata-vm-isolation`), not a shared process. |
| Control-plane isolation | The orchestration graph, HITL decisions, and run record stay in the **worker**; a compromised pod cannot alter *what happens next*. |
| Credential blast radius | The pod holds **only a short-lived, run-scoped credential** — never a broker key, never refresh material, never another run's or user's scope. There is **no `CapabilityTokenService`** and no central token broker. |
| A2A turn auth | `message:stream` requires `Authorization: Bearer {per-run token}`. The token is injected only into that run's AgentHost pod and removed from the registry when the pod is released. |
| GitHub token exposure | **Access token only** (bounded lifetime, no refresh), readable by the run's own pod, removed at run end; cannot mint new tokens or reach another user's scope. |
| Egress | **Default-deny** with a narrow allowlist: model endpoint, the API/worker bridge endpoint, and the run's legitimate git remote(s). The **database is not reachable** from sandbox pods — all run-state I/O flows through the worker. |
| At rest / past run | Token material does not persist past the run; under the per-run-Secret hardening it lives in an etcd-encrypted Secret on a per-pod `tmpfs` `0400` mount and is GC'd with the claim. |
| Reversibility | The whole mode is gated by `Sandbox:AgentExecutionMode`; flipping to `in-api` restores in-process execution with no redeploy. |

## Related reference

- [Sandbox setup](./sandbox-setup.md) — operator install/config of the sandbox backends.
- [API reference](./api.md) — the endpoints surfaced above.
- [A2A reference](./a2a.md) — the `-preview` transport (experimental) that carries agent turns.
- [Sandbox pod execution deep dive](../deep-dive/sandbox-pod-execution.md) — the reasoning.
- [Sandbox pod execution experience](../experience/sandbox-pod-execution.md) — the user/operator view.
- [Sandbox browser preview](../reference/sandbox-browser-preview.md) — preview routes (start/keepalive/stop)
  that expose a pod-internal server over a public HTTPS reverse proxy.
