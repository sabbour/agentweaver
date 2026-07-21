# Cluster page

The **Cluster** page gives operators a real-time view of the Kubernetes cluster backing the Agentweaver AKS deployment: sandbox capacity, Kubernetes health checks, and any legacy subtasks recorded as waiting for capacity.

::: info Kubernetes owns scheduling (issue #217)
The platform no longer pre-gates on quota. It submits the `SandboxClaim` and waits for Kubernetes to schedule and bind the pod, so a **Pending** pod is an expected transient state, not a failure. The namespace `ResourceQuota` no longer caps CPU/memory (only object counts). The **Pending capacity** KPI, the **Waiting for capacity** badge, and the pending-capacity table are **back-compat surfaces** that only reflect historical runs.
:::

It is available under the **Cluster** nav item in the SYSTEM section of the project left rail. Route: `/projects/:projectId/cluster`.

![Cluster page with KPI cards, health checks, sandbox claims, and capacity tables](/screenshots/cluster-page.png)

> 📸 **Screenshot — `cluster-page.png`**
> *Shows:* the **Cluster** page with Orphaned, Pending capacity, Checks OK, and Warm pool KPI cards plus Health checks, Sandbox claims, orphaned pods, pending capacity, warm pools, and sandbox objects.
> *Path:* open a project → click **Cluster** in the SYSTEM section of the left rail → `/projects/:projectId/cluster`.

## When to use the Cluster page

Open the **Cluster** page when:

- a coordinator run shows subtasks in **⏳ Waiting for capacity** (amber badge in the topology graph);
- runs are dispatching slowly and you suspect pod scheduling or node-pool autoscaling delays;
- you want to confirm that all Kubernetes API components are reachable;
- orphaned pods are accumulating (the reaper has not swept them yet);
- after a deployment or scaling event, to confirm the cluster is healthy.

## KPI cards

The KPI cards at the top of the page summarize the cluster signals the current UI exposes:

| Card | What it shows |
|---|---|
| **Orphaned** | Agent pods that no longer match an active run. |
| **Pending capacity** | **Legacy.** Subtasks recorded in the historical `PendingCapacity` status; empty for new runs (Kubernetes now owns scheduling). |
| **Checks OK** | Healthy checks divided by all reported cluster checks. |
| **Warm pool** | Ready vs. desired warm sandbox replicas when warm-pool data is available. |

Below the KPIs, the page shows **Health checks**, **Sandbox claims**, **Orphaned agent pods** when present, **Pending capacity**, **Warm pools**, and **Sandbox objects**.

## Component health table

Cluster checks run concurrently each time the page loads:

| Check | What it tests | Typical failure cause |
|---|---|---|
| **Postgres** | Connectivity to the Postgres database | Network policy, password rotation |
| **GitHub token store** | Configured GitHub token store validity for the current scope | Token expiry, missing per-user token, GitHub API outage |
| **Azure Key Vault** | Key Vault reachability and required `mcp-oauth-signing-key` lookup | Managed identity misconfiguration, network policy, or skipped `npm run azure:provision-infra` |
| **Agent pod quota** | CPU headroom in the namespace. Since #217 removed the `ResourceQuota` CPU cap there is no hard limit to measure against, so this check now reports `unknown`. | Node-pool autoscaling delays |
| **Warm pool** | Warm-pool agent-sandbox availability for generic sandboxes (`replicas: 3`) and AgentHost (`replicas: 2`) | Warm-pool replica count below target, SandboxTemplate CRD issue |
| **Kubernetes API** | Kubernetes API server reachability | In-cluster network policy, apiserver overload |

Each check shows:

- A status badge such as `healthy`, `warning`, `degraded`, or `critical`.
- A detail message (visible on warn/fail) explaining the specific failure.
- The duration the check took in milliseconds.

All six checks have a **5-second individual timeout**. A timed-out check appears as `fail` with the detail `"timed out"`.

If the Key Vault row shows `critical: secret 'mcp-oauth-signing-key' not found`,
the required OAuth signing-key provisioning step was skipped. Run `npm run azure:provision-infra`
before redeploying; do not use the installer
`--skip-oauth-key` flag for a production first deploy.

## Active agent pods table

Lists pods currently running that have a matching active run record:

| Column | Meaning |
|---|---|
| **Pod name** | Kubernetes pod name |
| **Run ID** | The run the pod is serving (links to an orchestration detail when available) |
| **Node** | Kubernetes node the pod is scheduled on |
| **Started at** | When the pod was created |

A healthy system should show only pods with active runs here.

## Orphaned agent pods table

Lists pods that are running but have no matching active run. These will be terminated on the next reaper sweep (default: every ~2 minutes).

If orphaned pods are not being cleaned up, check:
- That the heartbeat is enabled and ticking (see the **Heartbeat** page).
- That `Coordinator:ReaperIntervalTicks` is not set to an unusually large value.

## Pending-capacity runs table

> **Legacy / back-compat.** Kubernetes now owns pod admission and scheduling (issue #217), so new runs never enter `PendingCapacity`. This table stays in the UI only to render historical records; for a live run whose pod is still being scheduled, look for `sandbox.provisioning_pending` heartbeats on the child run rather than an entry here.

Lists coordinator subtasks recorded in the historical `PendingCapacity` status:

| Column | Meaning |
|---|---|
| **Coordinator run ID** | The parent coordinator run |
| **Subtask** | The subtask that was waiting |
| **Pending since** | When the subtask entered `PendingCapacity` |
| **Retry count** | How many dispatch attempts were made under the removed park/retry loop |

## Warm pools table

Lists every SandboxWarmPool CRD object in the namespace. Each row represents one pool:

| Column | Meaning |
|---|---|
| **Name** | Kubernetes name of the SandboxWarmPool object |
| **Desired** | Target number of pre-warmed sandboxes declared in the pool spec |
| **Ready** | Sandboxes currently ready to accept a claim |
| **Available** | Sandboxes that are ready and not yet claimed by a run |
| **Status** | `healthy` when ready equals desired; `warning` when below desired; `critical` when none are ready |

A pool in `warning` or `critical` means new run dispatches fall back to creating an ad-hoc sandbox, which adds latency to run startup.

## Sandbox objects table

Lists all Sandbox CRD objects in the namespace, both warm-pool-managed and ad-hoc per-run sandboxes:

| Column | Meaning |
|---|---|
| **Name** | Kubernetes name of the Sandbox object |
| **Phase** | `standby` (warm, waiting for a claim), `running`, `pending`, or `unknown` |
| **Ready** | Whether the sandbox pod is ready |
| **Pod** | Underlying pod name, if scheduled |
| **Warm pool** | The SandboxWarmPool that owns this sandbox; blank for ad-hoc sandboxes |
| **Age** | How long the object has existed |

## Sandbox claims table

Lists all SandboxClaim CRD objects in the namespace:

| Column | Meaning |
|---|---|
| **Name** | Kubernetes name of the SandboxClaim object |
| **Phase** | `bound` (assigned to a sandbox), `pending` (waiting for one), or `unknown` |
| **Ready** | Whether the claimed sandbox is ready |
| **Run** | The run ID that created this claim, linking to an orchestration detail when present |
| **Bound sandbox** | The Sandbox object this claim is bound to; blank when still pending |
| **Warm pool** | The pool the bound sandbox came from; blank for ad-hoc claims |
| **Age** | How long the claim has existed |

## 404 fallback

When the API is not deployed on AKS, or the cluster diagnostics endpoint is unavailable, the page displays a message indicating that cluster diagnostics are not available for this deployment. No other page functionality is affected.

## Related reading

- [Operations](./operations.md) — all operations surfaces at a glance.
- [Cluster diagnostics reference](../reference/cluster-diagnostics.md) — full API response schema.
- [Sandbox pod execution](../deep-dive/sandbox-pod-execution.md) — reaper design and Kubernetes-owned pod admission (`sandbox.provisioning_pending`).
- [Heartbeat](./operations.md#heartbeat-experience) — the heartbeat that drives the reaper.
