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
> the pod (see [Run-scoped GitHub token injection](#run-scoped-github-token-injection)).

## Pod identity and quota

A pod-per-run sandbox is the same Kata-isolated pod shape the sandbox subsystem already uses, claimed
from a warm pool, but now hosting the full agent (worker agents **and** the coordinator's own agent
turns) rather than only ad-hoc shell commands.

| Property | Value / behavior |
|---|---|
| Runtime class | `kata-vm-isolation` — a VM boundary around the container, so each run's secret and execution live inside a per-run microVM and are destroyed with it. |
| Identity | Dedicated sandbox service account; **workload identity** (federated OIDC) is the preferred path for the model credential, projecting **only** the narrowly-scoped workload-identity token volume — not the full Kubernetes API service-account token. |
| Cluster API access | None. The pod does not automatically receive Kubernetes API credentials; the sandbox stays tokenless for the cluster API even when workload identity is enabled for the model endpoint. |
| Provisioning | Claimed from a **warm pool** via a `SandboxClaim`; the executor waits until the claim is `Bound` to a concrete pod. Warm-pool size is a capacity decision balanced against claim latency and quota. |
| Per-pod resources | Sized for a real agent runtime (a live session + model I/O), not a `sleep infinity` placeholder — materially larger CPU/memory requests than the shell-only baseline. Exact numbers are a capacity decision. |
| Quota | Namespace `ResourceQuota` caps pod count, CPU/memory requests, and sandbox-claim count. Heavier per-pod requests plus multiple web/worker replicas require these caps to be **raised deliberately** via a reviewed manifest change, never a live patch. |
| Lifetime | Bounded by the run and the claim TTL. Under the hybrid model, a pod is released on suspend and a fresh pod is re-claimed on resume; pods never persist past the run. |
| Egress | Default-deny NetworkPolicy with a narrow allowlist (see [Security properties](#security-properties)). |
| Storage | Mounts the **shared workspace volume** (the worktree path) so worktree commit/diff stays on the worker side; the pod is otherwise stateless beyond the live turn. |

## Run-scoped GitHub token injection

A pod-per-run sandbox acts **as the run's signed-in user** and needs a GitHub credential to clone/push
the worktree and call GitHub API tools. The mechanism delivers a **short-lived, run-scoped GitHub access
token** to *that run's pod*, then removes it when the run ends. No refresh material ever reaches the pod.

### Sourcing

- The token is obtained by the **worker/API at claim-creation time** through the existing valid-access-
  token provider, which reads from the source-of-truth token store, transparently refreshes a near-expiry
  token, persists the rotation, and returns **only the access-token string** — never the refresh token.
- **Scope is the user, not the installation.** A user-initiated run resolves to the owning user's scope;
  installation scope is reserved for background/system tasks with no caller and is never injected into a
  user's run.
- **Failure is a pre-flight gate.** If a valid token cannot be produced at claim time (signed-out, or
  expired-and-unrefreshable), the run **fails the claim** with a typed re-auth error rather than degrading
  — no pod is adopted, and no empty/placeholder token is ever injected. "Can this run touch GitHub as
  this user?" is decided *before* a pod is claimed.

### Delivery to the executing pod

The token is delivered through the **shared RWX workspace store** that the executing pod mounts: the
worker writes the run-scoped access token where *that run's* AgentHost reads it, keyed to the run, so the
pod consumes its own run's token from its own mount path. The token is a GitHub **access** token only;
the refresh token is never written to the pod-readable location.

The pod consumes the value directly:

- as a git credential helper / credential-store entry in `https://x-access-token:{token}@github.com`
  form, mirroring how the API already clones with `x-access-token`, so `git clone`/`git push` work
  without further wiring; and/or
- as a raw token file the pod wires into `GITHUB_TOKEN`/askpass for GitHub API tools.

> **Hardening direction.** The shared RWX delivery is being tightened toward a **per-run Kubernetes
> Secret projected as a read-only file** (mode `0400`) on a non-workspace `tmpfs` mount, so the token is
> readable by exactly one pod and confined to the per-run Kata microVM rather than living on a volume
> every run's pod can mount. This is governed by `Sandbox:GitHubTokenDelivery` ∈ { `shared-file`,
> `injected` } (default `shared-file` during bring-up), and is the same direction as the run-scoped
> *model* credential (workload identity / claim-time projected secret). The sourcing, lifetime, and
> pod-side read described here are identical for both delivery shapes.

### Lifetime and cleanup

- **Short-lived by construction.** The injected value is an access token (bounded by the GitHub App
  user-to-server token lifetime). No refresh token is ever placed where the pod can read it, so a pod can
  never mint new tokens.
- **Bounded by the run/claim.** The token's lifetime is tied to the run; it never outlives the run.
- **Removed when the run ends.** The token material is deleted on run cleanup / claim deletion. Under the
  per-run-Secret hardening, cleanup is by owner-reference garbage collection (the Secret is owned by the
  claim/pod and GC'd when the claim is deleted), belt-and-suspenders explicit deletion in the same
  cleanup path, and a label-selected reaper for orphans (e.g. a crash between create and claim).
- **Rotation on re-claim.** Under the hybrid release/re-claim model, each re-claim **re-runs sourcing**
  and writes a **fresh** token; the prior token material is removed with the prior claim. Tokens never
  accumulate, and a long run that outlives a single token naturally rotates at burst/re-claim boundaries.

### AgentHost pod-side read

In-pod, the token is served through a **read-only, single-scope** token store:

- it reads the token **once** from its delivered location and serves it for the **single** run scope the
  pod was launched for — no scope enumeration, no listing of other users' directories;
- it implements the standard token-store contract so nothing downstream changes: it returns the injected
  access token (with expiry if provided) and **`RefreshToken = null`**, and identity (login) if provided;
- **writes are refused.** Sign-out / set operations are no-ops or `NotSupported` — the pod must never
  write credential state back to a shared location. The **worker/API owns refresh**; the pod only ever
  *receives* a fresh access token.

If the token file is absent at container start, the pod should **fail fast** rather than proceed
unauthenticated.

```mermaid
sequenceDiagram
    participant Worker as Worker / API
    participant Store as Token source-of-truth
    participant Mount as Shared RWX store (run-keyed)
    participant Host as AgentHost (in pod)
    Worker->>Store: GetValidAccessToken(userScope)
    alt no valid token
        Store-->>Worker: null
        Worker-->>Worker: fail claim → typed re-auth error (no pod)
    else valid access token
        Store-->>Worker: access token (no refresh)
        Worker->>Mount: write run-scoped access token
        Worker->>Host: claim/adopt pod
        Host->>Mount: read token once (single scope)
        Host->>Host: serve token (RefreshToken=null); writes refused
        Note over Worker,Mount: on run end / re-claim:<br/>delete + rotate token
    end
```

## Pod naming and the executing-pod surface

A run's executing pod name is tracked so the UI can show *where* a run is running.

- **`PodNameRegistry`** is an in-memory map from **run id → bound pod name**. It is populated by the
  Kubernetes sandbox executor once a `SandboxClaim` transitions to `phase: Bound`, and the entry is
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
> path is documented in the [Sandbox deep dive](../deep-dive/sandbox.md#why-run-ids-map-to-pod-names).

## Security properties

| Property | Pod-per-run guarantee |
|---|---|
| Execution isolation | Each run's agent turn, tools, shell, and file ops run in the run's **own Kata-isolated pod** (`kata-vm-isolation`), not a shared process. |
| Control-plane isolation | The orchestration graph, HITL decisions, and run record stay in the **worker**; a compromised pod cannot alter *what happens next*. |
| Credential blast radius | The pod holds **only a short-lived, run-scoped credential** — never a broker key, never refresh material, never another run's or user's scope. There is **no `CapabilityTokenService`** and no central token broker. |
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
