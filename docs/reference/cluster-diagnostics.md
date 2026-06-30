# Cluster diagnostics reference

## Overview

`GET /api/diagnostics/cluster` returns a real-time snapshot of the Agentweaver Kubernetes cluster: component health, namespace quota, active and orphaned agent-host pods, and subtasks waiting for capacity.

This endpoint is only available in AKS deployments. Non-AKS deployments return `404 Not Found`.

For the user-facing Cluster page guide see [Cluster page](../experience/cluster-page.md). For the API endpoint table see [API reference → Workspace, diagnostics, and metrics](./api.md#workspace-diagnostics-and-metrics).

## Authentication

Standard bearer-token authentication is required. See [API reference → Authentication](./api.md#authentication).

## Response — ClusterDiagnosticsDto

`200 OK` — `application/json`

```json
{
  "component_health": [
    {
      "name": "postgres",
      "status": "pass",
      "detail": null,
      "duration_ms": 12
    },
    {
      "name": "github_installation_token",
      "status": "pass",
      "detail": null,
      "duration_ms": 230
    },
    {
      "name": "key_vault",
      "status": "pass",
      "detail": null,
      "duration_ms": 45
    },
    {
      "name": "agent_pod_quota",
      "status": "warn",
      "detail": "CPU headroom: 1.2 cores (threshold: 2 cores)",
      "duration_ms": 38
    },
    {
      "name": "warm_pool",
      "status": "pass",
      "detail": null,
      "duration_ms": 22
    },
    {
      "name": "kubernetes_api",
      "status": "pass",
      "detail": null,
      "duration_ms": 8
    }
  ],
  "namespace_quota": {
    "cpu_used": 3.8,
    "cpu_total": 5.0,
    "memory_used_gi": 6.4,
    "memory_total_gi": 10.0
  },
  "active_agent_pods": [
    {
      "pod_name": "agent-host-abc123",
      "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6",
      "node": "katapool-vm-nodepool1-12345678-0",
      "started_at": "2026-06-27T17:55:00Z"
    }
  ],
  "orphaned_agent_pods": [],
  "pending_capacity_runs": [
    {
      "coordinator_run_id": "coord-abc123-...",
      "subtask_id": 7,
      "pending_since": "2026-06-27T17:58:30Z",
      "retry_count": 3
    }
  ]
}
```

`404 Not Found` — Cluster diagnostics are not available (non-AKS deployment).

## Fields

### Top-level

| Field | Type | Description |
| --- | --- | --- |
| `component_health` | `ComponentHealthDto[]` | Results of 6 concurrent health checks. Each check has a 5-second timeout. |
| `namespace_quota` | `NamespaceQuotaDto` | Current CPU and memory consumption vs. the namespace limits. `null` if quota could not be read. |
| `active_agent_pods` | `AgentPodInfoDto[]` | Agent-host pods with a matching active run record. |
| `orphaned_agent_pods` | `AgentPodInfoDto[]` | Agent-host pods with no matching active run (candidates for next reaper sweep). |
| `pending_capacity_runs` | `PendingCapacityRunDto[]` | Coordinator subtasks currently in `PendingCapacity` status. |

### ComponentHealthDto

| Field | Type | Description |
| --- | --- | --- |
| `name` | string | Check identifier. See table below for all check names. |
| `status` | string | `"pass"`, `"warn"`, or `"fail"`. |
| `detail` | string\|null | Human-readable explanation of a warn or fail; `null` on pass. |
| `duration_ms` | number | Wall-clock time the check took in milliseconds. Capped at 5000 for timed-out checks. |

### Health check names

| `name` | What it tests |
| --- | --- |
| `postgresql` | Postgres connectivity |
| `github_installation_token` | GitHub token-store validity for the configured scope |
| `key_vault` | Azure Key Vault reachability and required `mcp-oauth-signing-key` lookup. `critical: secret 'mcp-oauth-signing-key' not found` means `scripts/aks/16-provision-oauth-signing-key.sh` was skipped. |
| `agent_pod_quota` | CPU headroom ≥ 2 cores in the sandbox namespace |
| `warm_pool` | Warm-pool agent-sandbox availability for both pools: generic `agentweaver-sandbox` (`replicas: 3`) and AgentHost `agentweaver-agent-host` (`replicas: 2`) |
| `kubernetes_api` | Kubernetes API server reachability |

### NamespaceQuotaDto

| Field | Type | Description |
| --- | --- | --- |
| `cpu_used` | number | CPU consumed in the namespace, in cores. |
| `cpu_total` | number | Namespace CPU limit, in cores. |
| `memory_used_gi` | number | Memory consumed in the namespace, in GiB. |
| `memory_total_gi` | number | Namespace memory limit, in GiB. |

### AgentPodInfoDto

Appears in both `active_agent_pods` and `orphaned_agent_pods`.

| Field | Type | Description |
| --- | --- | --- |
| `pod_name` | string | Kubernetes pod name. |
| `run_id` | string\|null | The run ID the pod is serving. `null` for orphaned pods whose run cannot be identified. |
| `node` | string | Kubernetes node the pod is running on. |
| `started_at` | string (ISO 8601) | Pod creation timestamp. |

### PendingCapacityRunDto

| Field | Type | Description |
| --- | --- | --- |
| `coordinator_run_id` | string | The coordinator run whose subtask is waiting. |
| `subtask_id` | number | The subtask identifier within the work plan. |
| `pending_since` | string (ISO 8601) | When the subtask first entered `PendingCapacity` status. |
| `retry_count` | number | How many dispatch retries have been attempted. Max is 10; the subtask fails with `capacity_unavailable` after 10 retries. |

## Status codes

| Status | Condition |
| --- | --- |
| `200 OK` | Cluster diagnostics returned successfully. Individual checks may still be `warn` or `fail`. |
| `401 Unauthorized` | Missing or invalid bearer token. |
| `404 Not Found` | Cluster diagnostics endpoint not available (non-AKS deployment). |
| `500 Internal Server Error` | Unexpected error reading cluster state. |

## Notes

- All 6 component health checks run **concurrently**. The total response time is bounded by the slowest single check (5-second timeout), not the sum.
- The `agent_pod_quota` check and the `namespace_quota` DTO are computed separately: the check reports a pass/warn/fail threshold judgment; the DTO reports the raw values for the quota bars in the UI.
- The `warm_pool` check covers both the generic command sandbox pool and the AgentHost warm pool; an AgentHost pool below its intended two standby pods indicates slower run starts or capacity pressure.
- Orphaned pods in `orphaned_agent_pods` are not terminated by this endpoint; they will be reaped on the next `AgentHostReaperService` sweep (default: every ~2 minutes via `Coordinator:ReaperIntervalTicks`).

## Source

| Concern | File |
| --- | --- |
| Endpoint definition | `apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs` |
| Business logic | `apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs` — `GetClusterDiagnosticsAsync` |
| DTO definitions | `apps/Agentweaver.Api/Diagnostics/SystemDiagnosticsDto.cs` |

## Related reading

- [Cluster page](../experience/cluster-page.md) — user-facing guide to the Cluster UI.
- [API reference](./api.md) — all endpoints in one place.
- [Sandbox pod execution](../deep-dive/sandbox-pod-execution.md) — reaper service design, quota pre-flight, and `PendingCapacity` flow.
- [Coordinator internals](../deep-dive/coordinator-internals.md) — reaper as the 3rd heartbeat phase.
