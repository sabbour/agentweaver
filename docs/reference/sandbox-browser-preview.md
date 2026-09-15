# Sandbox browser preview — Reference

See Gateway-direct HTTPS capability URL and separate control plane for the shared visual model.

Terse reference for the **sandbox browser preview** API: the routes that start, keep alive, stop, and list a
live HTTPS preview of a server inside a run's sandbox pod. For the platform-owned Build & Test live-preview
path, AgentHost also fronts the app with a pod-local TCP forwarder so the Gateway always targets a pod-IP-
reachable port.

Calls resolve the persisted run's project: mutations require Contributor/Owner, listing Viewer. Legacy non-project runs retain submitting-principal ownership; the agent-start endpoint explicitly allows a trusted internal callback.

## Routes

| Method & path | Body | Returns | Notes |
|---|---|---|---|
| `POST /api/runs/{runId}/sandbox/port-forward` | `{ "target_port": <3000..9000> }` | `PortForwardSessionDto` | Starts a preview. Preview path provisions Service + HTTPRoute and returns `preview_url` + `keepalive_url`; it does not API-probe `podIP:{target_port}`. `target_port` must be within `AllowedPortMin..AllowedPortMax`. **Human/operator-initiated** (Contributor/Owner (legacy run ownership where applicable)). |
| `POST /api/runs/{runId}/sandbox/preview` | `{ "target_port": <3000..9000>, "preview_runner_session_id": "..." }` | `PortForwardSessionDto` | **Agent-initiated** variant of the start route. Two caller surfaces hit it: the in-sandbox `start_preview(port)` agent tool and the `start_preview(run_id, port, session_id?)` MCP tool on `agentweaver-mcp` ([`RunTools.cs`](#source)). The optional process-session ID lets the API recheck process health before publication. Routes through a human-in-the-loop approval gate ([`AgentPreviewGate`](#source)) before running the *same* preview-start path. Authorized for the run's **Contributor/Owner or the explicitly permitted trusted internal callback** ([`SandboxEndpoints.cs:60`](#source)). |
| `POST /api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry` | — | `202 Accepted` with fresh `request_id`, `retry_of_request_id`, and `expires_at` | Contributor/Owner retry for the latest **expired** preview approval on a non-terminal run. Reuses the retained PreviewRunner process; it does not rerun the preview command or restart the run. |
| `POST /api/runs/{runId}/sandbox/preview/{token}/keepalive` | — | `{ token, kept_alive: true }` | Bumps the preview expiry by its configured lifetime. Preview path only. Verifies the token's HTTPRoute carries the matching run before bumping. |
| `DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}` | — | `{ session_id, stopped: true }` | Explicit stop. For the preview path `sessionId` is the capability token; deletes the HTTPRoute then the Service. Verifies run↔token first. |
| `GET /api/runs/{runId}/sandbox/port-forward` | — | `PortForwardSessionDto[]` | Lists active preview sessions for the run. Liveness is the policy-safe existence of a bound pod with the preview-run label, not an API-side TCP probe. |

The relative `keepalive_url` returned by `POST …/port-forward` is
`/api/runs/{runId}/sandbox/preview/{token}/keepalive` ([`SandboxEndpoints.cs:107`](#source)).

## Readiness and liveness model

`SandboxPreviewService.StartPreviewAsync` is orchestration-only: it resolves the bound pod, patches labels,
creates the ClusterIP Service, and creates the HTTPRoute. It deliberately does **not** connect from the API pod
to `podIP:{target_port}` because `sandbox-allow-preview-ingress` admits TCP `3000-9000` only from the preview
Gateway's data-plane pods ([`SandboxPreviewService.cs:134`](#source), [`k8s/base/networkpolicy-sandbox.yaml`](#source)).
For platform live-preview, readiness is the AgentHost in-pod observation: log hints are tried first, then the
pod's own `/proc/net/tcp` and `/proc/net/tcp6` tables are parsed for new listening sockets, the app responds on
its real port, the in-pod `TcpPortForwarder` listens on `0.0.0.0:{publicPort}`, and AgentHost verifies the
forwarder public port before registration (`apps/Agentweaver.AgentHost/PreviewRunner.cs:262`, `:610`). The
`tcp6` table matters for Node's default IPv6-any binds. After registration, the real end-to-end check is opening the returned Gateway hostname (`preview_url`). A
new generated hostname can remain NXDOMAIN while App Routing creates its per-preview DNS record, so this
validation probes immediately, then retries DNS name-resolution failures with a bounded backoff for up to
`GatewayConvergenceTimeoutSeconds` (ten minutes by default). The legacy
`DnsConvergenceTimeoutSeconds` key remains supported. A hostname whose record already exists succeeds
on that initial probe. Gateway `502`, `503`, and `504` responses remain in this infrastructure
convergence phase because they can mean the HTTPRoute is not programmed yet. This environment's
App Routing `external-dns` reconciles every 3 minutes, so the convergence budget must exceed that
interval. The API does not add wildcard DNS records and does not patch the managed App Routing
addon. A `404`, a `500`, or any other status outside `502`/`503`/`504` starts
`PublicationTimeoutSeconds`. `ListForRunAsync` uses a label-selector pod-existence
check as its liveness proxy, because the same NetworkPolicy makes an API-side TCP liveness probe invalid
(`SandboxPreviewService.cs:399`, `:768`).

## Agent-initiated preview (`start_preview`)

A running agent can expose a server it started **without a human typing a port in the UI**, via the
`start_preview` tool. The model calls `start_preview(port: int)`; the tool POSTs
`{ "target_port": <port> }` to `POST /api/runs/{runId}/sandbox/preview` and returns the resulting
`preview_url` string back to the agent. The tool is **run-scoped**: the `runId` is captured server-side in
the tool closure ([`AgentweaverApiTools.cs:245`](#source)), so the model supplies only the port and can
never target another run.

The external MCP surface also accepts the optional `session_id` returned by
`observe_bound_port` and forwards it as `preview_runner_session_id`. When supplied, the API
checks that exact supervised process before publishing. MCP transport timeouts, unreachable API
errors, and terminal-run conflicts return specific recovery guidance instead of a generic tool
execution failure.

Build & Test evaluates build/tests only. Coordinator assembly subsequently invokes platform-owned `PreviewStep` on the retained sandbox for APPROVED and REQUEST_CHANGES results, not DECLINED. It resolves a command, maps cwd to the effective checkout, starts a supervised process, observes its port, obtains approval and publishes through Gateway.

The request routes through a **human-in-the-loop approval gate** before any preview is provisioned
([`AgentPreviewGate.RequestApprovalAsync`, `AgentPreviewGate.cs:108`](#source)):

- If an auto-approve source is on (see below) the request is **auto-granted** immediately.
- Otherwise a `tool.approval_required` event is emitted onto the run timeline
  ([`AgentPreviewGate.cs:131`](#source)) and the call **suspends** until an operator grants it via
  `POST /api/runs/{runId}/tool-approvals` (with the emitted `request_id`) or the approval window times out
  (1440 minutes / 24 hours by default; configurable per project from 1–1440 minutes).

`StartPreviewRequest` ([`SandboxEndpoints.cs:475`](#source)) uses the snake_case DTO convention — the wire
field is `target_port` via `[JsonPropertyName("target_port")]`, unlike `PortForwardRequest` which
binds camelCase `target_port`.

### Auto-approve sources

Any one being true auto-grants the preview (production default is human-gated):

| Source | Where | Default |
|---|---|---|
| `Sandbox:Preview:AutoApprove` config / env `SANDBOX_PREVIEW_AUTO_APPROVE` | [`AgentPreviewGate.cs:176`](#source) | `false` |
| Per-run `AutoApproveTools` operator option | `IRunOptionsStore.Get(runId)` | `false` |
| An existing run/always-scoped allow policy on the shared gate | `IToolApprovalGate.IsAutoApproved` | none |

The env var `SANDBOX_PREVIEW_AUTO_APPROVE` is read directly (not via the ASP.NET `__` hierarchy separator),
so the exact name works as an environment variable. It exists so an automated demo can run the preview flow
end-to-end unattended; leave it `false` in production.

The approval wait window is stored on the run's project and defaults to 1440 minutes (24 hours).
Project owners configure 1–1440 minutes in **Project settings → Sandbox policy** or via
`PUT /api/projects/{projectId}/preview-settings` with
`{ "approval_timeout_minutes": 1440, "lifetime_minutes": 1440, "dns_convergence_timeout_seconds": 600 }`.
Project owners can set each approval and preview lifetime from 1–1440 minutes; both
default to 24 hours. They can set the infrastructure convergence deadline from
60–3600 seconds; it defaults to 600 seconds (10 minutes).
`Sandbox:Preview:ApprovalTimeoutMinutes` and
`SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES` remain a 24-hour-default fallback only for legacy/non-project
runs.

When a request expires, `tool.approval_resolved` removes it from pending notifications while the timeline
keeps the audit record and exposes **Retry approval**. Retry is guarded by run authorization, terminal state,
request expiry, and latest-preview-state checks. It creates a fresh request id and, for the deterministic
preview step, reuses the retained private process instead of executing the command twice.

The relative `keepalive_url` example below is for the operator route.

## `PortForwardSessionDto`

From [`apps/web/src/api/types.ts:1169`](#source).

| Field | Type | Meaning |
|---|---|---|
| `session_id` | string | Session identifier. In the preview path this **is** the capability token; used as `{sessionId}` to stop the preview. |
| `local_port` | number | Loopback port on the API host (local fallback only). In the preview path this is `0` — the preview is a public URL, not a loopback. |
| `target_port` | number | Port **inside** the sandbox pod being exposed. For manual previews this is the user-requested app port. For platform live-preview this is the forwarder's pod-IP-reachable public port; the app's real port is observed separately by AgentHost. |
| `pod_name` | string | Bound sandbox pod the preview targets (resolved from the run's `SandboxClaim` status). |
| `started_at` | string | ISO timestamp of when the preview started. |
| `preview_url` / `previewUrl` | string \| null | Public HTTPS capability URL `https://{token}-preview.{ZoneSuffix}` (preview path). The web UI embeds it in a `no-referrer` iframe and offers **Open preview**. |
| `keepalive_url` / `keepaliveUrl` | string \| null | Relative URL the frontend pings ~every 60 s to keep the preview alive (preview path). |

## Configuration

Bound from the `Sandbox:Preview` section into [`SandboxPreviewOptions.cs`](#source).

| Config key | Default | Meaning |
|---|---|---|
| `Sandbox:Preview:Enabled` | `true` (AKS) / `false` (local dev) | Master switch. When `true` the API provisions Gateway-direct HTTPRoute+Service objects and returns a `preview_url`. When `false` the Gateway path and reaper are no-ops and `kubectl port-forward` is used instead. **Enabled by default** in AKS deployments via `Sandbox__Preview__Enabled=true`. |
| `Sandbox:Preview:ZoneSuffix` | Empty until deployment sets it | Managed zone used by `{token}-preview.{ZoneSuffix}`; read current deployment configuration, not an example cluster hostname. |
| `Sandbox:Preview:GatewayName` | `agentweaver-preview-gateway` | Shared Gateway the per-preview HTTPRoute attaches to. Applied from `k8s/base/gateway-preview.yaml`. |
| `Sandbox:Preview:GatewayNamespace` | `agentweaver` | Namespace of the shared preview Gateway. |
| `Sandbox:Preview:Namespace` | `agentweaver` | Namespace where the per-preview Service / HTTPRoute / pod live. |
| `Sandbox:Preview:LifetimeMinutes` | `1440` | Preview lifetime fallback for legacy/non-project runs. It is used for both sliding expiry and the hard cap. Project-backed previews use their project lifetime setting. |
| `Sandbox:Preview:KeepAfterRun` | `true` | Keep routing after completion while a live preview defers backing-pod release; expiry, stop and missing-pod reconciliation still bound cleanup; only the reaper or an explicit stop removes it. |
| `Sandbox:Preview:AllowedPortMin` | `3000` | Lowest `target_port` a preview may expose (inclusive). Mirrors the NetworkPolicy range and the AgentHost forwarder public-port scan. |
| `Sandbox:Preview:AllowedPortMax` | `9000` | Highest `target_port` a preview may expose (inclusive). Mirrors the NetworkPolicy range and the AgentHost forwarder public-port scan. |
| `Sandbox:Preview:GatewayConvergenceTimeoutSeconds` | `600` | Upper-bound deadline for App Routing to create a new generated preview hostname and program the Gateway HTTPRoute. Publication probes immediately, then retries with a 1 s, 2 s, 4 s, 8 s, then 10 s-max backoff. This does not create or modify DNS records. This environment's App Routing `external-dns` reconciles every 3 minutes, so keep this budget above that interval. |
| `Sandbox:Preview:DnsConvergenceTimeoutSeconds` | `600` | Supported legacy alias for `GatewayConvergenceTimeoutSeconds`. Existing deployments can keep this key. |
| `Sandbox:Preview:PublicationTimeoutSeconds` | `90` | Bounded wait after a response proves the request reached a backend. `404`, `500`, and any status outside `502`/`503`/`504` start this window. Gateway `502`, `503`, and `504` stay in the convergence window. |
| Project `approval_timeout_minutes` | `1440` | Human approval window for agent-initiated preview, configurable by a project owner from 1–1440 minutes. |
| Project `lifetime_minutes` | `1440` | Published preview lifetime and hard cap, configurable by a project owner from 1–1440 minutes in **Project settings → Sandbox policy**. It is used consistently for the route expiration and maximum lifetime. |
| Project `dns_convergence_timeout_seconds` | `600` | Stable project API and storage field for the infrastructure convergence deadline. It now covers DNS and Gateway route programming. It stays under the legacy wire name so existing projects, clients, and the persisted `PreviewDnsConvergenceTimeoutSeconds` column keep working. Project owners configure it from 60–3600 seconds in **Project settings → Sandbox policy**. |
| `Sandbox:Preview:ApprovalTimeoutMinutes` (env `SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES`) | `1440` | Fallback for legacy/non-project runs. Values clamp to 1–1440 minutes. Project-backed runs use the project setting. |
| `Sandbox:Preview:AutoApprove` (env `SANDBOX_PREVIEW_AUTO_APPROVE`) | `false` | When `true`, the agent-initiated `start_preview` approval gate auto-grants without an operator. Read in [`AgentPreviewGate.cs:176`](#source). Keep `false` in production. |
| Run `auto_approve_tools` policy | `false` | When explicitly selected at direct start or atomically captured from backlog pickup settings, auto-approves `start_preview` without creating an approval card, notification, or waiter. The decision cites the persisted immutable policy snapshot ID and sanitized target port. Port/process/ownership/publication validation remains enforced. |

## Status codes

| Code | When |
|---|---|
| `200 OK` | Preview started (`POST`), kept alive (`keepalive`), stopped (`DELETE`), or listed (`GET`). |
| `202 Accepted` | A fresh retry approval attempt was created. |
| `400 Bad Request` | `target_port` outside `1..65535`, outside `AllowedPortMin..AllowedPortMax` (preview path), or `runId` not parseable. |
| `403 Forbidden` | Insufficient run/project access, or preview approval denied. |
| `408 Request Timeout` | Agent-preview approval expired; response identifies the expired request and retry availability. |
| `404 Not Found` | Run does not exist; or (keepalive/`DELETE`, preview path) the token's HTTPRoute does not carry the matching run (run↔token binding). |
| `409 Conflict` | No bound sandbox pod for the run, the Gateway preview is not enabled on keepalive, or a retry targets a non-expired/superseded approval or terminal run. |
| `429 Too Many Requests` | A session cap was hit on the port-forward fallback. |
| `500` | Unexpected failure provisioning the preview (or `kubectl` failed to start the fallback tunnel). |

Platform live-preview failures are emitted as `sandbox.preview_failed` events rather than API status codes.
Discovery/observe reasons include `no_listening_port_discovered`, `process_exited:exit={code}`, and
`observe_error`; forwarder-specific reasons include `bound_unreachable` (the app was reachable on loopback, but
the forwarder's public pod-IP port failed health check) and `no_public_port_available` (no free public port in
the allowed `3000-9000` range). See [Decoupled live-preview provisioning — Reference](./live-preview-provisioning.md).

## Example

```http
POST /api/runs/run_01HXYZ/sandbox/port-forward
Content-Type: application/json

{ "target_port": 3000 }
```

```json
{
  "session_id": "swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2d4f7",
  "local_port": 0,
  "target_port": 3000,
  "pod_name": "agent-pod-worker-7",
  "started_at": "2026-06-28T09:20:07Z",
  "preview_url": "https://swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2d4f7-preview.preview.cluster.westus2.aksapp.io",
  "keepalive_url": "/api/runs/run_01HXYZ/sandbox/preview/swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2d4f7/keepalive"
}
```

```http
DELETE /api/runs/run_01HXYZ/sandbox/port-forward/swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2d4f7
```

```json
{ "session_id": "swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2d4f7", "stopped": true }
```

## Source

| Concern | File |
|---|---|
| Endpoints (start / agent-start / keepalive / stop / list) | `apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs` |
| Agent-initiated approval gate (HITL + auto-approve) | `apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs` |
| `start_preview` agent tool (run-scoped HTTP callback) | `packages/Agentweaver.AgentRuntime/AgentweaverApiTools.cs` |
| Owner-or-agent-callback authorization | `apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs` |
| Preview provisioning, keepalive, stop, reap | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs` |
| Live-preview pod-local TCP forwarder | `apps/Agentweaver.AgentHost/TcpPortForwarder.cs` |
| Live-preview runner `/proc` port discovery, observation, and forwarder lifecycle | `apps/Agentweaver.AgentHost/PreviewRunner.cs` |
| Deterministic live-preview step | `apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs` |
| Config defaults & port-range check | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewOptions.cs` |
| Capability token | `apps/Agentweaver.Api/Sandbox/Preview/PreviewToken.cs` |
| SandboxClaim CRD coordinates + bound-pod parsing | `apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs` |
| Build & Test preview activation prompt | `packages/Agentweaver.AgentRuntime/Workflow/BuildTestTurnExecutor.cs` |
| DTO fields | `apps/web/src/api/types.ts` |
| API client | `apps/web/src/api/client.ts` |

## See also

- [Sandbox browser preview — User Guide](../experience/sandbox-browser-preview.md) — the step-by-step user flow.
- [Sandbox browser preview — Deep Dive](../deep-dive/sandbox-browser-preview.md) — how the reverse proxy works end to end.
- [Live-preview provisioning](./live-preview-provisioning.md) — the Build & Test preview outcome contract.
- [Sandbox pods reference](./sandbox-pods.md) — pod naming and the wider sandbox API surface.

Gateway routes use a per-preview ClusterIP Service selecting the run's pod. Browser data bypasses the API; the API owns authorization and routing lifecycle.

With preview enabled (AKS default), start returns `preview_url` and `keepalive_url`; disabled-preview kubectl forwarding returns API-host loopback `local_port`. Gateway publication failure does not automatically select kubectl. Approval timeout returns 408 and preserves the supervised process for retry; published lifetime and approval timeout are separate limits.

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
<tr><td>assurance</td><td>No API → pod TCP readiness probe. Publication failure rolls back; infrastructure convergence has a bounded retry window.</td></tr>
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
