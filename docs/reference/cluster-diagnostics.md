# Cluster diagnostics reference

## Overview

`GET /api/diagnostics/cluster` returns a real-time Kubernetes snapshot. It includes dependency checks, agent-host pod inventory, SandboxWarmPool objects, and SandboxClaim objects.

This endpoint requires bearer authentication. Non-AKS deployments return `404 Not Found`.

## Response

The response is a `ClusterDiagnosticsDto`:

```json
{
  "generated_utc": "2026-09-03T12:00:00Z",
  "total_duration_ms": 45,
  "checks": [
    {
      "name": "postgresql",
      "status": "healthy",
      "message": "SELECT 1 returned in 12ms",
      "latencyMs": 12
    }
  ],
  "active_agent_pods": [],
  "orphaned_agent_pods": [],
  "pending_capacity_runs": [],
  "warm_pools": [],
  "sandbox_claims": []
}
```

| Field | Type | Description |
| --- | --- | --- |
| `generated_utc` | string | ISO 8601 timestamp for the snapshot. |
| `total_duration_ms` | number | Total time for the snapshot. |
| `checks` | `DetailedHealthCheckDto[]` | Results of five concurrent dependency checks. |
| `active_agent_pods` | `AgentPodInfoDto[]` | Bound pods for active runs. |
| `orphaned_agent_pods` | `AgentPodInfoDto[]` | Pods without a matching active run. |
| `pending_capacity_runs` | `PendingCapacityRunDto[]` | Capacity-waiting subtasks. New runs usually leave this legacy surface empty. |
| `warm_pools` | `WarmPoolStatusDto[]` | SandboxWarmPool objects in the namespace. |
| `sandbox_claims` | `SandboxClaimObjectDto[]` | SandboxClaim objects in the namespace. |

## Checks

Each check has `name`, `status`, `message`, and `latencyMs`. Status values are `healthy`, `warning`, `critical`, and `unknown`.

| Name | What it measures |
| --- | --- |
| `postgresql` | PostgreSQL connectivity. |
| `key_vault` | Key Vault CSI delivery of `mcp-api-key`. |
| `agent_pod_quota` | Admission headroom from the tighter `pods` or `sandboxclaims` object quota. |
| `warm_pool` | Readiness of the AgentHost warm pool. |
| `k8s_api` | Kubernetes API reachability. |

`agent_pod_quota` and `warm_pool` can include `used`, `limit`, `unit`, or `pendingCount` when those values apply.

## Inventory objects

`AgentPodInfoDto` has `claim_name`, optional `run_id`, optional `pod_name`, `status`, and optional `age_seconds`.

`WarmPoolStatusDto` has `name`, `desired_replicas`, `ready_replicas`, `available_replicas`, `status`, `instances`, and optional `age_seconds`.

`SandboxClaimObjectDto` has `name`, `phase`, `ready`, optional `run_id`, optional `bound_sandbox`, optional `warm_pool`, and optional `age_seconds`.

## Status codes

| Status | Condition |
| --- | --- |
| `200 OK` | The snapshot was returned. Individual checks can report a non-healthy status. |
| `401 Unauthorized` | The bearer credential is missing or invalid. |
| `404 Not Found` | Cluster diagnostics are unavailable in this deployment. |

## Source

| Concern | File |
| --- | --- |
| Endpoint | `apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs` |
| Snapshot and checks | `apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs` |
| DTOs | `apps/Agentweaver.Api/Diagnostics/SystemDiagnosticsDto.cs` |

## Related reading

- [API reference](./api.md)
- [Sandbox pods reference](./sandbox-pods.md)
