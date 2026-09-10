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

`AgentPodInfoDto` has `claim_name`, optional `run_id`, optional `pod_name`, `status`, optional `age_seconds`, and optional `details`.

`WarmPoolStatusDto` has `name`, `desired_replicas`, `ready_replicas`, `available_replicas`, `status`, `instances`, optional `age_seconds`, and optional `details`.

`SandboxClaimObjectDto` has `name`, `phase`, `ready`, optional `run_id`, optional `bound_sandbox`, optional `warm_pool`, optional `age_seconds`, and optional `details`.

Warm-pool instances also have optional `details`. The response root can include `details`
for the cluster itself. These additions are optional so older clients can continue to
consume the existing polling shape.

### Expandable resource details

Each `details` object uses the same bounded contract:

| Field | Description |
| --- | --- |
| `resource_id` | Stable type-prefixed identity, such as `warm-pool:agentweaver-agent-host`. |
| `resource_type` | `cluster`, `warm_pool`, `warm_instance`, `sandbox_claim`, or `agent_host_pod`. |
| `status` / `summary` | Concise state and display text for an expanded card. |
| `attention_required` | Whether the UI should consider expanding the resource automatically. |
| `reason` | Optional short failure, pending, claimed, or orphaned reason. |
| `created_utc` / `last_transition_utc` | Optional timing sources from the process or Kubernetes resource. |
| `ownership` | Optional run, project, agent, claim, run-status, and run-timing identifiers. |
| `capacity` | Optional desired, ready, available, claimed, used, and limit counts. |
| `runtime` | Optional pod name, node name, container image, and runtime class. |
| `deep_links` | Identifiers for existing project, run, claim, pool, and pod routes or filters. |

`attention_required` is true for unhealthy or degraded resources, pending pods and
claims, orphaned pods, warming instances, and claimed resources where ownership is
useful to inspect.

The details contract never includes credentials, secret values, internal IP addresses,
raw Kubernetes objects, condition messages, or logs. Kubernetes reasons are normalized
to a single line and capped at 240 characters.

## Kubernetes topology graph

`GET /api/diagnostics/cluster/topology` returns a separate bounded relationship graph.
Keeping it separate prevents the normal 30-second diagnostics poll from repeatedly listing
every opt-in Kubernetes resource.

The default request is runtime-only:

```text
GET /api/diagnostics/cluster/topology
```

Request additional comma-separated layers with `layers`:

```text
GET /api/diagnostics/cluster/topology?layers=runtime,networking,workloads,storage,autoscaling,availability
```

| Layer | Resources |
| --- | --- |
| `runtime` | Pod, SandboxClaim, SandboxWarmPool, SandboxTemplate |
| `networking` | Gateway, HTTPRoute, Service, NetworkPolicy |
| `workloads` | Deployment, ReplicaSet, ServiceAccount |
| `storage` | PersistentVolumeClaim, PersistentVolume, StorageClass |
| `autoscaling` | HorizontalPodAutoscaler, VerticalPodAutoscaler, KEDA ScaledObject |
| `availability` | PodDisruptionBudget |

Nodes have stable IDs, explicit resource types, namespace, health, a short summary, and a
small allow-listed `details` map. Edges record their relationship type and whether the
relationship is inferred. Owner references, Gateway API references, identity references,
scale targets, and volume claims are authoritative. Selector matches are marked
`inferred: true`.

Each requested layer reports `available`, `partial`, or `unavailable`. A missing CRD or an
RBAC denial affects only that layer; it does not fail the graph. Lists are capped at 100
objects per resource type, the response is capped at 250 nodes and 500 edges, and
`truncated` reports when a cap was reached.

The response never includes full manifests, Secret values, tokens, service cluster IPs,
internal endpoint addresses, or container environment values.

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
| Topology discovery | `apps/Agentweaver.Api/Diagnostics/KubernetesTopologyService.cs` |

## Related reading

- [API reference](./api.md)
- [Sandbox pods reference](./sandbox-pods.md)
