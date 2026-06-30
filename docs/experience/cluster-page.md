# Cluster page

The **Cluster** page gives operators a real-time view of the Kubernetes cluster backing the Agentweaver AKS deployment: pod activity, quota health, component checks, and any subtasks waiting for capacity.

It is available under the **Cluster** nav item in the SYSTEM section of the project left rail. Route: `/projects/:projectId/cluster`.

![Cluster page with KPI cards, quota bars, and pod tables](/screenshots/cluster-page.png)

> 📸 **Screenshot — `cluster-page.png`**
> *Shows:* the Cluster page with KPI cards, quota bars (CPU, memory), the component health table with 6 checks, and the Active / Orphaned / Pending pods tables.
> *Path:* open a project → click **Cluster** in the SYSTEM section of the left rail → `/projects/:projectId/cluster`.

## When to use the Cluster page

Open the **Cluster** page when:

- a coordinator run shows subtasks in **⏳ Waiting for capacity** (amber badge in the topology graph);
- runs are dispatching slowly and you suspect pod scheduling or quota issues;
- you want to confirm that all Kubernetes API components are reachable;
- orphaned pods are accumulating (the reaper has not swept them yet);
- after a deployment or scaling event, to confirm the cluster is healthy.

## KPI cards

The four KPI cards at the top of the page give a quick cluster-health summary:

| Card | What it shows |
|---|---|
| **Active pods** | Number of agent-host pods currently serving a live run. |
| **Orphaned pods** | Pods running with no matching active run. These are candidates for the next reaper sweep (roughly every 2 minutes). A non-zero count here is a leading indicator of quota pressure. |
| **CPU used / total** | Current CPU consumption vs. the namespace limit, in cores. |
| **Pending runs** | Subtasks that are waiting for CPU headroom to become available. Each one retries every 60 seconds for up to 10 attempts before failing with `capacity_unavailable`. |

## Quota bars

The two quota bars show namespace resource consumption as a percentage of the configured limit:

- **CPU** — consumed core count vs. the namespace CPU limit.
- **Memory** — consumed GiB vs. the namespace memory limit.

Color coding:
- **Green** — below 60 % of limit
- **Amber** — 60–85 % of limit
- **Red** — above 85 % of limit

![Cluster page with quota near-limit (red bar)](/screenshots/cluster-page-quota-warning.png)

> 📸 **Screenshot — `cluster-page-quota-warning.png`**
> *Shows:* the Cluster page with the CPU quota bar showing a red near-limit state, and one or more subtasks in the Pending-capacity runs table.
> *Path:* open a project → click **Cluster** → observe a red CPU bar.

A red CPU bar combined with entries in the **Pending-capacity runs** table means new pods cannot be scheduled. If the Warm pool row warns, AgentHost may still work but run launch loses the fastest path because fewer than two pods are pre-warmed. Options:

1. Wait for running pods to finish (the reaper will clean orphans within 2 minutes).
2. Check the **Orphaned pods** table — if there are orphaned pods, they will be reaped on the next sweep.
3. Scale up the `katapool` node pool if persistent capacity shortage is expected.

## Component health table

Six checks run concurrently each time the page loads:

| Check | What it tests | Typical failure cause |
|---|---|---|
| **Postgres** | Connectivity to the Postgres database | Network policy, password rotation |
| **GitHub token store** | Configured GitHub token store validity for the current scope | Token expiry, missing per-user token, GitHub API outage |
| **Azure Key Vault** | Key Vault reachability and required `mcp-oauth-signing-key` lookup | Managed identity misconfiguration, network policy, or skipped `scripts/aks/16-provision-oauth-signing-key.sh` |
| **Agent pod quota** | CPU headroom ≥ 2 cores | Too many active pods, under-provisioned node pool |
| **Warm pool** | Warm-pool agent-sandbox availability for generic sandboxes (`replicas: 3`) and AgentHost (`replicas: 2`) | Warm-pool replica count below target, SandboxTemplate CRD issue |
| **Kubernetes API** | Kubernetes API server reachability | In-cluster network policy, apiserver overload |

Each check shows:

- A status badge: `pass` (green), `warn` (amber), or `fail` (red).
- A detail message (visible on warn/fail) explaining the specific failure.
- The duration the check took in milliseconds.

All six checks have a **5-second individual timeout**. A timed-out check appears as `fail` with the detail `"timed out"`.

If the Key Vault row shows `critical: secret 'mcp-oauth-signing-key' not found`, the required OAuth signing-key provisioning step was skipped. Run `scripts/aks/16-provision-oauth-signing-key.sh` before redeploying; do not use the installer `--skip-oauth-key` flag for a production first deploy.

## Active agent pods table

Lists pods currently running that have a matching active run record:

| Column | Meaning |
|---|---|
| **Pod name** | Kubernetes pod name |
| **Run ID** | The run the pod is serving (links to the run page) |
| **Node** | Kubernetes node the pod is scheduled on |
| **Started at** | When the pod was created |

A healthy system should show only pods with active runs here.

## Orphaned agent pods table

Lists pods that are running but have no matching active run. These will be terminated on the next reaper sweep (default: every ~2 minutes).

If orphaned pods are not being cleaned up, check:
- That the heartbeat is enabled and ticking (see the **Heartbeat** page).
- That `Coordinator:ReaperIntervalTicks` is not set to an unusually large value.

## Pending-capacity runs table

Lists coordinator subtasks currently in `PendingCapacity` status:

| Column | Meaning |
|---|---|
| **Coordinator run ID** | The parent coordinator run |
| **Subtask** | The subtask waiting for capacity |
| **Pending since** | When the subtask entered `PendingCapacity` |
| **Retry count** | How many dispatch attempts have been made (max 10) |

Each subtask retries every 60 seconds. After 10 retries, the subtask fails with detail code `capacity_unavailable` and the OutcomeSpec panel shows a human-readable explanation.

## 404 fallback

When the API is not deployed on AKS, or the cluster diagnostics endpoint is unavailable, the page displays a message indicating that cluster diagnostics are not available for this deployment. No other page functionality is affected.

## Related reading

- [Operations](./operations.md) — all operations surfaces at a glance.
- [Cluster diagnostics reference](../reference/cluster-diagnostics.md) — full API response schema.
- [Sandbox pod execution](../deep-dive/sandbox-pod-execution.md) — reaper design, quota pre-flight, and `PendingCapacity` flow.
- [Heartbeat](./operations.md#heartbeat-experience) — the heartbeat that drives the reaper.
