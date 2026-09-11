# Cluster page

The **Cluster** page gives operators a real-time view of the Kubernetes cluster backing the Agentweaver AKS deployment: sandbox capacity, Kubernetes health checks, and any legacy subtasks recorded as waiting for capacity.

::: info Kubernetes owns scheduling (issue #217)
The platform no longer pre-gates on quota. It submits the `SandboxClaim` and waits for Kubernetes to schedule and bind the pod, so a **Pending** pod is an expected transient state, not a failure. The namespace `ResourceQuota` no longer caps CPU/memory (only object counts). The **Pending capacity** KPI, the **Waiting for capacity** badge, and the pending-capacity table are **back-compat surfaces** that only reflect historical runs.
:::

It is available under the **Cluster** nav item in the SYSTEM section of the project left rail. Route: `/projects/:projectId/cluster`. The page auto-refreshes every 30 seconds by default; you can toggle **Auto-refresh** off when you want to inspect a static snapshot.

![Cluster page with KPI cards, health checks, sandbox claims, and capacity tables](/screenshots/cluster-page.png)

> 📸 **Screenshot — `cluster-page.png`**
> *Shows:* the **Cluster** page with Orphaned, Pending capacity, Checks OK, and Warm pool KPI cards plus Health checks, Sandbox claims, orphaned pods, pending capacity, and warm pools.
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

Below the KPIs, the page shows **Health checks**, **Sandbox claims**, **Orphaned agent pods** when present, **Pending capacity**, and **Warm pools**.

## Component health table

Cluster checks run concurrently each time the page loads:

| Check | What it tests | Typical failure cause |
|---|---|---|
| **Postgres** | Connectivity to the Postgres database | Network policy, password rotation |
| **Azure Key Vault** | CSI delivery of the required `mcp-api-key` | Managed identity misconfiguration, network policy, or a missing API authentication secret |
| **Agent pod quota** | Effective admission headroom from the enforced `pods` and SandboxClaim object quotas. Healthy means plenty of room remains, warning means only a handful of starts remain, and critical means no new AgentHost can be admitted. | Namespace object-quota exhaustion |
| **Warm pool** | Warm-pool agent-sandbox availability for generic sandboxes (`replicas: 3`) and AgentHost (`replicas: 2`) | Warm-pool replica count below target, SandboxTemplate CRD issue |
| **Kubernetes API** | Kubernetes API server reachability | In-cluster network policy, apiserver overload |

Each check shows:

- A status badge such as `healthy`, `warning`, `degraded`, or `critical`.
- A detail message (visible on warn/fail) explaining the specific failure.
- The duration the check took in milliseconds.

All five checks have a **5-second individual timeout**. A timed-out check appears as `fail` with the detail `"timed out"`.

If the Key Vault row shows `critical: secret 'mcp-api-key' not found`,
restore the required API authentication secret with `npm run azure:provision-infra`
before redeploying.

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

Subtasks that could not get a sandbox immediately because the warm pool had no free capacity appear here until a slot frees up. Zero is healthy: it means every run got a sandbox right away.

> **Legacy / back-compat.** Kubernetes now owns pod admission and scheduling (issue #217), so new runs rarely enter `PendingCapacity`. This table stays in the UI mainly to render historical records; for a live run whose pod is still being scheduled, look for `sandbox.provisioning_pending` heartbeats on the child run rather than an entry here.

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

## Resource topology

The **Resource topology** starts with the runtime layer: Pods and the Agentweaver sandbox
CRDs. Enable the **Networking**, **Workloads**, **Storage**, **Autoscaling**, and
**Availability** checkboxes to discover related Kubernetes resources only when you need
them.

The graph uses Kubernetes relationship fields rather than name guesses:

- owner references connect controllers to owned resources;
- selectors connect Services, workloads, NetworkPolicies, and PodDisruptionBudgets to
  matching resources;
- Gateway API `parentRefs` and `backendRefs` connect routes;
- `serviceAccountName`, `scaleTargetRef`, PVC/PV claims, and storage class names connect
  their corresponding resources.

Solid edges are authoritative references. Dashed edges are selector-derived and therefore
inferred. Select a resource card to open its safe drill-down summary. The drill-down does
not expose full manifests, Secret data, tokens, internal endpoints, or container
environment values.

Layer badges show when a resource API is unavailable or only partially readable. Missing
optional CRDs and RBAC denials do not hide the rest of the graph.

The diagnostics API also provides optional, resource-specific `details` for the cluster,
warm pools, warm instances, sandbox claims, and AgentHost pods. A topology card can use
the stable resource identity, concise status/reason, ownership, timing, capacity,
runtime, and deep-link identifiers when it is expanded. The contract marks resources
that need attention so the UI can auto-expand unhealthy, pending, orphaned, warming, or
usefully claimed resources without recomputing health rules in the browser.

The expanded metadata is intentionally bounded. It does not expose credentials, secret
values, internal IP addresses, raw Kubernetes objects, condition messages, or logs.

Every resource card can expand independently. Select a card with the mouse, or focus it
and press <kbd>Enter</kbd> or <kbd>Space</kbd>. Expanded cards show the details that are
most useful for that resource:

- cluster cards show snapshot timing and unhealthy check reasons;
- warm pools show ready, available, and allocated capacity;
- warm instances show their pool, claim, run, project, state, and age;
- claims show their phase, readiness, bound sandbox, pool, run, and age;
- agent pods show their claim/run relationship, state, and age.

Resources that need attention expand on first load. Claimed warm instances and bound
claims also start expanded so their ownership chain is visible immediately. Healthy
resources stay compact. Expanding or collapsing one card does not change the other cards,
and the selection is preserved while the page polls for a new diagnostics snapshot.
When the API supplies both a project and run relationship, the expanded card includes
safe links to the project and orchestration detail pages.

### Kubernetes resource layers

The graph opens in the same runtime-only view so a normal cluster remains easy to scan.
Use the compact layer controls to add any combination of Kubernetes infrastructure:

| Layer | Resources |
|---|---|
| **Traffic** | Gateway, HTTPRoute, and Service |
| **Security** | NetworkPolicy and ServiceAccount |
| **Workloads** | Deployment and Pod |
| **Storage** | PersistentVolumeClaim (PVC), PersistentVolume (PV), and StorageClass |
| **Scaling** | HorizontalPodAutoscaler (HPA), PodDisruptionBudget (PDB), VerticalPodAutoscaler (VPA), and KEDA ScaledObject |
| **Agentweaver** | Agentweaver custom resources, including sandbox templates, warm pools, claims, and sandboxes |

Layer choices are independent, are remembered in the current browser, and survive
diagnostics polling. **Reset to runtime** clears the saved choices. If discovery is not
available, returns no objects, or cluster RBAC denies a resource type, the affected
control shows a small inline status instead of failing the whole graph.

Expanding any card can also reveal up to one hop of directly related resources. This
card-driven subgraph is temporary and does not select or clear a global layer. Collapse
the card to hide resources that are not otherwise visible through a selected layer.

Solid arrows are relationships reported authoritatively by Kubernetes or an owning
controller. Dashed arrows are inferred from selectors, names, labels, or another
best-effort correlation. The graph legend communicates the distinction without relying
on color. Resource cards use the same status, type-icon, disclosure, keyboard, and
responsive layout conventions across runtime and infrastructure objects.

To avoid turning large clusters into an unreadable canvas, the UI bounds infrastructure
nodes, one-hop reveals, and relationships. A hidden-count message and the expanded
card's related-resource summary indicate when more data exists. Zoom, pan, expanded
cards, selected layers, and stable runtime positions continue to work while polling.

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
