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
| `Sandbox:ReleasePodOnSuspend` | `true`, `false` | `true` | When `pod-per-run` is active and the workflow graph suspends on an external gate (a HITL/review `RequestPort`, or the coordinator idling while it awaits child runs), `true` checkpoints the run and **releases** the claim and deletes the used pod so the pool replenishes capacity; an active preview can defer release. `false` keeps the pod warm across the suspension for low-latency resume or debugging, at the cost of held capacity. |
| `Sandbox:Kubernetes:AgentHostClaimCreationGraceSeconds` | Positive integer seconds | `300` | Minimum age before the orphan reaper may delete an AgentHost claim that is absent from the active-run map. The effective grace is the larger of this value and `Sandbox:Kubernetes:AgentHostReadyTimeoutSeconds + 30` seconds. |
| `AgentHost:ExecutionScratchRoot` | Absolute path | `/local-workspace` | Root of the disk-backed emptyDir used for pod-local execution workspaces and package caches. |
| `AgentHost:ExecutionScratchMinimumFreeBytes` | Non-negative integer bytes | `8589934592` (8 GiB) | Minimum available scratch space required before AgentHost prepares a local workspace. Failure returns typed reason `insufficient_ephemeral_storage`. |
| `Coordinator:AssemblyBuildTestTimeoutMinutes` | Positive number | `20` | Total assembly Build/Test wall-clock limit. Expiry cancels the gate and releases its retained AgentHost claim. |
| `Coordinator:AssemblyBuildTestStallTimeoutMinutes` | Positive number | `12` | Maximum interval without a forwarded Build/Test run event before the stall watchdog fails the gate. |

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

> AgentHost receives only a live, immutable capability credential redeemed for the configured run and purpose through `/configure`. It has no Key Vault, CSI, shared-filesystem, or ambient-token fallback.

## Pod identity and quota

A pod-per-run sandbox is the same Kata-isolated pod shape the sandbox subsystem already uses, claimed
from a warm pool, but now hosting the full agent (worker agents **and** the coordinator's own agent
turns) rather than only ad-hoc shell commands.

| Property | Value / behavior |
|---|---|
| Runtime class | `kata-vm-isolation` — a VM boundary around the container, so each run's secret and execution live inside a per-run microVM and are destroyed with it. |
| Identity | Dedicated sandbox service account federated to `agentweaver-agenthost-identity`, a managed identity with **no Key Vault role assignments** (issue #471). **Workload identity** (federated OIDC) projects **only** the narrowly-scoped workload-identity token volume — not the full Kubernetes API service-account token — but it grants no vault access, so the sandbox cannot read any user's secrets. |
| Cluster API access | Current pod infrastructure enables service-account automount; the model-execution sidecar masks `/var/run/secrets/kubernetes.io/serviceaccount`. The whole pod is not universally tokenless. |
| Provisioning | Claimed from a **warm pool** via a `SandboxClaim`; the executor waits until the claim is bound to a concrete pod. AgentHost uses the shared `agentweaver-agent-host` pool (`replicas: 2`), then receives per-run context through `POST /configure` before `/healthz` is expected to become ready. No separate per-run template or per-run warm pool is created for AgentHost. A claim that stays unbound (pod **Pending**) while Kubernetes schedules is a legitimate wait — there is no app-side capacity pre-check — surfaced on the child run's stream via `sandbox.provisioning_pending` heartbeats (issue #217). |
| AgentHost readiness gate | Warm AgentHost pods start in standby. After binding, the executor calls `POST /configure` with run identity, a live Copilot capability or BYOK configuration, separate purpose-scoped credentials, and the execution-workspace descriptor, then polls `GET {scheme}://{podIP}:8088/healthz` (bounded `Sandbox:Kubernetes:AgentHostReadyTimeoutSeconds`, default `90`s; `…ReadyPollIntervalMs`, default `1000`) before the first A2A turn. `/configure` is excluded from readiness and returns `409` if called again. The `a2a-sandbox-pod` HttpClient additionally retries connection-refused only. |
| Transient API resilience | The idempotent claim create and the bind/IP polls (`WaitForBoundAsync`, `GetPodIpAsync`) retry transient Kubernetes API faults up to `MaxK8sAttempts` (3 total) with exponential backoff + jitter (`ExecuteK8sWithRetryAsync`): connection resets (`SocketException 104`/`IOException`/`HttpRequestException`), `429`/`5xx`, and `HttpClient` timeouts. `409 Conflict` is **not** treated as transient — it is attempt-aware to preserve idempotency (a retry-`409` = our own create that committed before a reset, so the claim is configured, not reused). Caller cancellation is never retried. The non-idempotent `POST /configure` is intentionally excluded (issue #230). |
| A2A turn authentication | Run launch generates a 256-bit random turn bearer token, sends it to the claimed warm pod in `POST /configure`, and registers it in `IAgentHostTurnTokenRegistry`. `RemoteAgentProxy` sends `Authorization: Bearer {token}` on `message:stream`; each pod accepts only its configured run token. |
| Tool-approval return path | When the API-side durable approval gate reports `Unknown`, pod-per-run mode forwards the grant/deny to the owning AgentHost pod's authenticated root endpoint so its in-memory gate can resolve. |
| Resources | AgentHost: requests 300m CPU/1Gi, limits 800m/2Gi. Execution sidecar: requests 700m/2Gi, limits 1200m/4Gi. Each requests 1Gi and limits 4Gi ephemeral storage; shared execution scratch is capped at 8Gi. |
| Quota | Namespace `ResourceQuota` (`k8s/base/quota.yaml`) bounds only **object counts** — pod count, sandbox-claim count, PVCs, and storage. It no longer caps CPU/memory: Kubernetes schedules on pod requests and the cluster autoscaler owns headroom, so a **Pending** pod waits for the pool to scale rather than being rejected on admission (issue #217). The object-count caps are **raised deliberately** via a reviewed manifest change, never a live patch. |
| Lifetime | Bounded by the run and the claim TTL. Under the hybrid model, a pod is released on suspend and a fresh pod is re-claimed on resume; a pod can outlive execution while an active preview retains it; release and orphan cleanup resume after durable retention evidence expires. |
| Egress | Default-deny with explicit API/MCP/DNS paths and public HTTPS excluding private/link-local ranges; not a per-run Git-host-only allowlist. No direct PostgreSQL access. |
| Storage | Mounts the **shared workspace volume** plus a dedicated disk-backed `execution-scratch` emptyDir at `/local-workspace` (`sizeLimit: 8Gi`) for pod-local execution. Assembly Build/Test and preview use `LocalReadOnly`; implementation turns use `LocalWritable` and publish through the verified Git write-back flow. Existing disk-backed `tmp` and `home` emptyDirs remain separate. |

### Orphan reaper creation grace

An AgentHost claim missing from the active-run map is not reaped while its Kubernetes
`creationTimestamp` is inside the effective creation-grace window. This keeps a newly bound claim
alive through the readiness wait (`AgentHostReadyTimeoutSeconds`, default `90` seconds); a missing or
unparseable timestamp receives no grace and remains eligible for cleanup.

## Run-scoped GitHub capability delivery

A pod-per-run sandbox receives only capability credentials tied to the run's immutable snapshots. `RunGitHubCapabilitySnapshotLifecycle` captures snapshots before launch and gives retries/resumes fresh references to the inherited capability. The API's `GitHubCapabilityBroker` fences the selected `UnattendedCopilot` or `UnattendedRepository` snapshot before and after redemption, then bounds the credential expiry.

In GitHub Copilot mode, `KubernetesSandboxExecutor` requires a live Copilot credential for the exact run. It transfers that credential in-memory through the one-time `/configure` call. In BYOK mode, the sandbox resolves its configured provider separately and does not require or use `copilotCredential`. `AgentHostGitHubCapabilityCredentialProvider` rejects credentials for a different run or past expiry. AgentHost does not read Key Vault, CSI mounts, shared filesystem tokens, user token stores, or configuration credentials.

| `/configure` field | Required | Meaning |
|---|---|---|
| `runId` | Yes | Configured run identity. |
| `copilotCredential` | GitHub Copilot mode only | Opaque snapshot reference, credential, and bounded expiry for that run's unattended Copilot capability. BYOK mode does not use this field. |
| `repositoryAccessToken` | No | Separately redeemed repository capability for narrowly-scoped Git/GitHub operations. |
| `turnBearerToken` | No | A2A turn authorization token, distinct from the GitHub capability. |

Credentials are never logged or persisted. Missing, revoked, expired, or purpose-mismatched snapshots fail closed before the pod becomes ready.

## A2A turn bearer token

The A2A turn endpoint has a separate per-run bearer token from the GitHub user token above:

1. `KubernetesSandboxExecutor` creates 32 random bytes (`256` bits) at AgentHost run launch.
2. The token is sent to the claimed warm pod in `POST /configure` and stored in `AgentHostRuntimeState`.
3. The same token is stored in `IAgentHostTurnTokenRegistry` for the owning run.
4. `RemoteAgentProxy` reads the registry and sends `Authorization: Bearer {token}` on all calls to `POST /a2a/agent/v1/message:stream`.
5. `AgentHost` rejects turn requests whose header does not exactly match its own `AgentHostOptions.TurnBearerToken`.

This is application-layer auth on top of the A2A NetworkPolicy/mTLS boundary. The important blast-radius property is that a stolen token from one run cannot be reused against another run's pod.

## Tool-approval forwarding endpoints

These are internal API-to-AgentHost routes, not public client endpoints. The public caller continues
to use `/api/runs/{id}/tool-approvals` and `/api/runs/{id}/tool-denials`.

| Method | AgentHost path | Body | Purpose |
| --- | --- | --- | --- |
| `POST` | `/tool-approvals` | `runId`, `requestId`, `scope` | Grant the pod-local pending request. Unknown scope values use `once`; `always` is pod/run-scoped and does not survive restart. |
| `POST` | `/tool-denials` | `runId`, `requestId` | Deny the pod-local pending request. |

Both routes accept the same pod-root bearer authorization used by PreviewRunner controls: either the
configured turn bearer or the per-run `previewRunnerCredential`. A mismatched `runId` returns
`409 state: "run_mismatch"`.

| AgentHost response | Meaning |
| --- | --- |
| `200` with `resolved: true` | State is `approved`, `denied`, or `expired` |
| `404` with `state: "unknown"` | The pod-local gate does not know the request |
| `409` with `state: "pending"` | The request remains pending |
| `401` | The bearer did not match the configured pod credentials |

The API locates the pod with `IAgentHostOriginResolver`, calls it through the `a2a-sandbox-pod`
client, and caps the decision call at 10 seconds. Missing origins, timeouts, transport failures,
5xx responses, and invalid responses surface publicly as `503 state: "agenthost_unreachable"`.
Terminal forwards cause the API to emit `tool.approval_resolved` for the owning run.

The credential's secret-store key is derived by `PreviewRunnerCredential.SecretKey(runId)` with the
prefix `preview-runner-cred--`; `KubernetesSandboxExecutor` mints it, persists it, and delivers its
value in-memory through `/configure`. Key Vault cleanup uses soft delete rather than purge. If the
same run launches again while that deterministic key is deleted but recoverable, the API recovers
the key, waits up to 30 seconds for it to become writable, and replaces the recovered value with a
fresh credential. Concurrent recovery attempts converge on the same active key. Secret values are
never included in recovery errors or logs, and terminal cleanup continues to bound the credential
lifetime to the backing pod without weakening Key Vault purge protection.

Sources: `apps/Agentweaver.AgentHost/Program.cs:287-288,486-588`,
`apps/Agentweaver.Api/Sandbox/AgentHostApprovalHttpClient.cs:28-112`,
`apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:2590-2718`,
`apps/Agentweaver.Api/Sandbox/Preview/PreviewRunnerCredential.cs:22-35`, and
`apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:706-759`,
`apps/Agentweaver.Api/Auth/KeyVaultSecretStore.cs`, and
`apps/Agentweaver.Api/Auth/KeyVaultRecoverableSecretWriter.cs`.

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
- `GET /api/system/runtime` reports the host/API pod, not a fallback execution attribution for coordinator children. Child and workflow nodes use topology `executionPodName` or null; an unbound child must not be labelled as executing on the API pod.

| Field | Source | Meaning |
|---|---|---|
| `kubernetes` | `GET /api/system/runtime` | Whether the backend is running inside Kubernetes; gates whether any pod pill is shown. |
| `podName` | API/host pod identity, not fallback attribution for a Coordinator child. |
| `executionPodName` | Authoritative bound execution pod for this run/node, or null. |

> The same `PodNameRegistry` also lets preview/port-forward tooling locate a run's pod. That preview
> path is documented in the [Sandbox deep dive](../deep-dive/sandbox.md#why-run-ids-map-to-pod-names) and,
> for its API surface, in [Sandbox preview port-forward](#sandbox-preview-port-forward-feature-017) below.

## Sandbox preview port-forward (Feature 017)

> **Dedicated pages:** this feature now has its own [Reference](./sandbox-browser-preview.md),
> [User Guide](../experience/sandbox-browser-preview.md), and
> [Deep Dive](../deep-dive/sandbox-browser-preview.md). The summary below stays here for context within the
> sandbox-pods surface.

With `Sandbox:Preview:Enabled=true`, the API creates Gateway-direct HTTPS routing to the run's sandbox and returns `preview_url` and `keepalive_url`. Browser traffic bypasses the API. See the [sandbox browser preview reference](./sandbox-browser-preview.md) for authorization, approval, publication, keepalive and stop semantics.

Viewer can list previews; Contributor/Owner can start, retry, keep alive or stop them. Legacy non-project runs retain submitting-principal ownership; trusted internal agent callbacks are explicitly scoped exceptions.

When preview creation is disabled, the operator start route uses the legacy `kubectl` implementation. This is not an automatic fallback after Gateway publication failure.

| Disabled-preview fallback | Scope |
|---|---|
| Bound pod required | Uses the process's `PodNameRegistry`; a local executor without a Kubernetes pod has nothing to forward. |
| `kubectl port-forward --address 127.0.0.1 pod/{pod} :{targetPort} -n {namespace}` | Binds loopback on the **API host**. A remote browser's localhost is not that host. |
| `local_port` | No public preview URL; arrange an appropriate local/operator connection separately. |
| Ports / caps | 1-65535; defaults 3 per run, 20 per service process. Gateway has its own configured allowed range. |
| Lifetime | Process-local, no persisted route annotations or session TTL; stop, exit, disposal or run/pod cleanup ends it. |

## Security properties

| Property | Pod-per-run guarantee |
|---|---|
| Execution isolation | Each run's agent turn, tools, shell, and file ops run in the run's **own Kata-isolated pod** (`kata-vm-isolation`), not a shared process. |
| Control-plane isolation | The orchestration graph, HITL decisions, and run record stay in the **worker**; a compromised pod cannot alter *what happens next*. |
| Capability boundary | The API's `GitHubCapabilityBroker` fences immutable purpose-bound snapshots before and after redemption. AgentHost receives the bounded capability through `/configure`, without ambient Key Vault/filesystem user-secret lookup. |
| A2A turn auth | `message:stream` requires `Authorization: Bearer {per-run token}`. The token is delivered only to the claimed AgentHost pod via `/configure` and removed from the registry when the pod is released. |
| GitHub token exposure | **Brokered by the API for the configured run owner only** and delivered in the one-time `/configure` call, then cached in memory for the pod lifetime; the sandbox identity has **no Key Vault access** (issue #471), and no CSI user-token file or shared workspace copy exists. |
| Egress | Default-deny with explicit API/MCP/DNS paths and public HTTPS excluding private/link-local ranges; not a per-run Git-host-only allowlist. No direct PostgreSQL access. |
| At rest / past run | Token material does not persist past the pod lifetime; no per-run Secret/SPC is created, and the bearer token is no longer written to `SandboxClaim.spec.env` in etcd. |
| Reversibility | Change `Sandbox:AgentExecutionMode` to in-api through the normal configuration rollout; startup DI wiring is not hot reloaded. |

## Related reference

- [Sandbox setup](./sandbox-setup.md) — operator install/config of the sandbox backends.
- [API reference](./api.md) — the endpoints surfaced above.
- [A2A reference](./a2a.md) — the `-preview` transport (experimental) that carries agent turns.
- [Sandbox pod execution deep dive](../deep-dive/sandbox-pod-execution.md) — the reasoning.
- [Sandbox pod execution experience](../experience/sandbox-pod-execution.md) — the user/operator view.
- [Sandbox browser preview](../reference/sandbox-browser-preview.md) — preview routes (start/keepalive/stop)
  that expose a pod-internal server over a public HTTPS reverse proxy.
- [Tool Approval SSE Contract](../tool-approval-sse-contract.md) — public approval outcomes and
  coordinator-to-child routing.

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
