# Platform components

Agentweaver splits its platform into replaceable areas, such as Sandbox,
Memory, and Secrets. Each area has an owner service that selects one or more
implementations. The platform components report shows, for every area, the
implementation that's active now, the implementations that are registered,
and whether the owner reports them as ready.

Use this report to answer these questions:

- Which implementation serves each area in this deployment?
- Which owner service is responsible for each area?
- Is each area ready, and if not, why?

The report is read-only. To change a selection, see
[Configure providers](./configure-providers.md).

## Prerequisites

- A Broker-issued token with the `api.read` scope. The token must not be bound
  to a project or run.
- A current TenantAdmin assignment for your tenant, or a current PlatformAdmin
  assignment. Projects & Config checks the assignment on every request, so a
  revoked assignment takes effect immediately.

## Read the report

Send a `GET` request through the Gateway:

```http
GET /api/v1/platform/components
Authorization: Bearer <token>
```

The response has `Cache-Control: no-store`. The Gateway forwards the request
to Projects & Config and doesn't build or change the report.

| Status | Meaning |
| --- | --- |
| `200` | The report. It always contains all 16 areas, even when owners don't answer. |
| `401` | The token is missing or invalid. |
| `403` | The token lacks `api.read`, is bound to a project or run, or the caller isn't a current TenantAdmin or PlatformAdmin. |

## How Projects builds the report

Projects & Config asks each owner service for its areas in parallel. Each
owner answers from its selected implementation, its registered
implementations, and its existing readiness check. Projects waits up to three
seconds for each owner. It then combines the answers with a fixed list of
areas, so a slow or failed owner can't remove an area from the report.

```mermaid
sequenceDiagram
    participant C as Client
    participant G as Gateway
    participant P as Projects & Config
    participant O as Owner services (5)
    C->>G: GET /api/v1/platform/components
    G->>P: GET /api/platform/components (proxy)
    P->>P: Require api.read, unbound token, current TenantAdmin or PlatformAdmin
    par Each owner, 3-second limit
        P->>O: GET /internal/platform/components (private service token)
        O->>O: Describe selected and registered implementations
        O->>O: Run existing readiness check (2-second limit)
        O-->>P: Owner report (no-store)
    end
    P->>P: Combine with the 16-area list; missing owners become unknown
    P-->>G: 200 PlatformComponentReport (no-store)
    G-->>C: 200 PlatformComponentReport
```

The owner services are Events & Sessions, Environment, Knowledge,
Orchestrator, and Identity Broker. Their route isn't exposed through the
Gateway.

## Areas and owners

| Area | `area` value | Owner | Cardinality |
| --- | --- | --- | --- |
| Sessions | `sessions` | Events & Sessions | `exclusive` |
| Snapshots | `snapshots` | Environment | `exclusive` |
| Sandbox | `sandbox` | Environment | `exclusive` |
| Storage | `storage` | Environment | `exclusive` |
| Memory | `memory` | Knowledge | `exclusive` |
| Policy | `policy` | Orchestrator | `platformSingleton` |
| Guardrails | `guardrails` | Orchestrator | `orderedComposite` |
| Network Policy | `networkPolicy` | Environment | `layered` |
| Cost | `cost` | Events & Sessions | `keyedByMeterSource` |
| Application Hosting | `applicationHosting` | Environment | `perApplication` |
| Canvas | `canvas` | None | `perCanvas` |
| Secrets | `secrets` | Identity Broker | `platformSingleton` |
| Source Control | `sourceControl` | Orchestrator | `exclusive` |
| Telemetry | `telemetry` | Events & Sessions | `orderedComposite` |
| Messaging | `messaging` | Events & Sessions | `platformSingleton` |
| Object Store | `objectStore` | Events & Sessions | `platformSingleton` |

Cardinality tells you how many active implementations an area can have:

- `exclusive` and `platformSingleton` areas have at most one.
- `orderedComposite` areas run several in order. Each entry has an `order`.
- `layered` areas have one per layer. Each entry has a `layer`, such as `l3l4`
  or `l7`.
- `keyedByMeterSource` areas have one per usage meter source. Each entry has a
  `meterSource`.

For the design of each area, see
[Provider extensibility](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md).

## Readiness and reasons

Each area and each active implementation has a `readiness` value. When the
value isn't `ready`, `reason` explains it.

| Readiness | Meaning |
| --- | --- |
| `ready` | The owner's readiness check passed. |
| `degraded` | The implementation works with reduced capability. |
| `notReady` | The owner's readiness check failed. |
| `disabled` | The owner has no implementation selected for the area. |
| `notAvailable` | The area has no implementation or no backend owner in this release. |
| `unknown` | Projects couldn't get a valid answer, or the readiness check timed out. |

| Reason | Meaning |
| --- | --- |
| `ownerUnavailable` | Projects didn't get a valid report from the owner within three seconds. |
| `noBackendOwner` | No backend service owns the area. Canvas always reports this reason. |
| `notSelected` | The owner has no implementation selected. |
| `notImplemented` | The area has no implementation in this release. Application Hosting reports this reason. |
| `readinessCheckFailed` | The owner's readiness check failed. |
| `readinessTimedOut` | The owner's readiness check didn't finish within two seconds. |

An area's readiness is the least ready value among its active implementations.

Readiness comes from each owner's existing readiness check, such as its
database check. It isn't an end-to-end test of the external backend. A
configured implementation is never reported as active unless its owner
answers.

## Example response

```json
{
  "observedAt": "2026-03-04T10:15:00+00:00",
  "areas": [
    {
      "area": "memory",
      "purpose": "Authoritative knowledge records and retrieval",
      "owner": "knowledge",
      "cardinality": "exclusive",
      "readiness": "ready",
      "reason": null,
      "active": [
        {
          "implementationId": "postgres.native-memory",
          "displayName": "PostgreSQL memory",
          "version": "1.0.0",
          "hosting": "inProcess",
          "layer": null,
          "meterSource": null,
          "order": null,
          "readiness": "ready",
          "reason": null,
          "observedAt": "2026-03-04T10:15:00+00:00"
        }
      ],
      "available": [
        { "implementationId": "postgres.native-memory", "displayName":         "PostgreSQL memory", "version": "1.0.0", "enabled": true },
                { "implementationId": "cosmos.memory", "displayName": "Cosmos DB memory", "version": "1.0.0", "enabled": false }
              ]
    },
    {
      "area": "secrets",
      "purpose": "Resolve credential references for trusted callers",
      "owner": "identityBroker",
      "cardinality": "platformSingleton",
      "readiness": "unknown",
      "reason": "ownerUnavailable",
      "active": [],
      "available": []
    }
  ]
}
```

The example shows two of the 16 areas. Implementation identifiers and names
depend on your provider catalog.

## What the report doesn't include

The report contains only stable identifiers, display names, versions, and
readiness. It never contains option values, option revisions, endpoints,
credentials, or resource identifiers.

## Configure owner reporting

Owner reporting needs a private service audience, a Projects workload token
for that audience, owner addresses, and a network path from Projects to each
owner. Until you configure them, the report still returns all 16 areas, but
every area with a backend owner is `unknown` with reason `ownerUnavailable`.
For the settings, see
[Platform component report settings](../reference/runtime-configuration.md#platform-component-report-settings).

## Related content

- [API reference](../reference/api.md#catalog-and-platform-routes)
- [Configure providers](./configure-providers.md)
- [Provider extensibility](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md)
