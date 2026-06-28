# Sandbox browser preview — Reference

Terse reference for the **sandbox browser preview** API: the routes that start, keep alive, stop, and list a
live HTTPS preview of a server an agent started **inside its run's sandbox pod**.

When `Sandbox:Preview:Enabled=true` (in-cluster), `POST …/port-forward` provisions a **Gateway-direct
reverse proxy** — a per-preview HTTPRoute → per-run ClusterIP Service → the run's sandbox pod — and returns a
public `preview_url` plus a `keepalive_url`. When disabled (local dev), the same route falls back to the
legacy `kubectl port-forward` and returns a loopback `local_port` instead. Every call verifies the run
exists and the caller owns it (`404`/`403`). Source:
[`SandboxEndpoints.cs`](#source), [`SandboxPreviewService.cs`](#source).

## Routes

| Method & path | Body | Returns | Notes |
|---|---|---|---|
| `POST /api/runs/{runId}/sandbox/port-forward` | `{ "target_port": <3000..9000> }` | `PortForwardSessionDto` | Starts a preview. Preview path: provisions Service + HTTPRoute, returns `preview_url` + `keepalive_url`. `target_port` must be within `AllowedPortMin..AllowedPortMax`. |
| `POST /api/runs/{runId}/sandbox/preview/{token}/keepalive` | — | `{ token, kept_alive: true }` | Bumps the preview's idle expiry to now + `IdleTimeoutMinutes`. Preview path only. Verifies the token's HTTPRoute carries the matching run before bumping. |
| `DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}` | — | `{ session_id, stopped: true }` | Explicit stop. For the preview path `sessionId` is the capability token; deletes the HTTPRoute then the Service. Verifies run↔token first. |
| `GET /api/runs/{runId}/sandbox/port-forward` | — | `PortForwardSessionDto[]` | Lists active legacy port-forward sessions for the run. |

The relative `keepalive_url` returned by `POST …/port-forward` is
`/api/runs/{runId}/sandbox/preview/{token}/keepalive` ([`SandboxEndpoints.cs:70`](#source)).

## `PortForwardSessionDto`

From [`apps/web/src/api/types.ts:1169`](#source).

| Field | Type | Meaning |
|---|---|---|
| `session_id` | string | Session identifier. In the preview path this **is** the capability token; used as `{sessionId}` to stop the preview. |
| `local_port` | number | Loopback port on the API host (legacy fallback only). In the preview path this is `0` — the preview is a public URL, not a loopback. |
| `target_port` | number | Port **inside** the sandbox pod being exposed. |
| `pod_name` | string | Bound sandbox pod the preview targets (resolved from the run's `SandboxClaim` status). |
| `started_at` | string | ISO timestamp of when the preview started. |
| `preview_url` / `previewUrl` | string \| null | Public HTTPS capability URL `https://{token}-preview.{ZoneSuffix}` (preview path). The web UI embeds it in a `no-referrer` iframe and offers **Open preview**. |
| `keepalive_url` / `keepaliveUrl` | string \| null | Relative URL the frontend pings ~every 60 s to keep the preview alive (preview path). |

## Configuration

Bound from the `Sandbox:Preview` section into [`SandboxPreviewOptions.cs`](#source).

| Config key | Default | Meaning |
|---|---|---|
| `Sandbox:Preview:Enabled` | `false` | Master switch. When `false` the Gateway path and reaper are no-ops (ships dark); the legacy `kubectl port-forward` is used instead. |
| `Sandbox:Preview:ZoneSuffix` | `""` | Managed `aksapp.io` zone; the host is `{token}-preview.{ZoneSuffix}`. Supplied by deploy. |
| `Sandbox:Preview:GatewayName` | `agentweaver-preview-gateway` | Shared Gateway the per-preview HTTPRoute attaches to. |
| `Sandbox:Preview:GatewayNamespace` | `agentweaver` | Namespace of the shared Gateway. |
| `Sandbox:Preview:Namespace` | `agentweaver` | Namespace where the per-preview Service / HTTPRoute / pod live. |
| `Sandbox:Preview:IdleTimeoutMinutes` | `30` | Sliding idle TTL; a preview not kept alive within this window is reaped. |
| `Sandbox:Preview:MaxLifetimeHours` | `8` | Hard cap; a preview is always reaped after this, regardless of keepalive. |
| `Sandbox:Preview:KeepAfterRun` | `true` | Retain the preview after the run completes / pod is released; only the reaper or an explicit stop removes it. |
| `Sandbox:Preview:AllowedPortMin` | `3000` | Lowest `target_port` a preview may expose (inclusive). Mirrors the NetworkPolicy range. |
| `Sandbox:Preview:AllowedPortMax` | `9000` | Highest `target_port` a preview may expose (inclusive). |

## Status codes

| Code | When |
|---|---|
| `200 OK` | Preview started (`POST`), kept alive (`keepalive`), stopped (`DELETE`), or listed (`GET`). |
| `400 Bad Request` | `target_port` outside `1..65535`, outside `AllowedPortMin..AllowedPortMax` (preview path), or `runId` not parseable. |
| `403 Forbidden` | Caller does not own the run. |
| `404 Not Found` | Run does not exist; or (keepalive/`DELETE`, preview path) the token's HTTPRoute does not carry the matching run (run↔token binding). |
| `409 Conflict` | No bound sandbox pod for the run (the `SandboxClaim` is missing or not yet `Bound`), or the Gateway preview is not enabled on the keepalive path. |
| `429 Too Many Requests` | A session cap was hit on the legacy port-forward fallback. |
| `500` | Unexpected failure provisioning the preview (or `kubectl` failed to start the legacy tunnel). |

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
| Endpoints (start / keepalive / stop / list) | `apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs` |
| Preview provisioning, keepalive, stop, reap | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs` |
| Config defaults & port-range check | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewOptions.cs` |
| Capability token | `apps/Agentweaver.Api/Sandbox/Preview/PreviewToken.cs` |
| SandboxClaim CRD coordinates + bound-pod parsing | `apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs` |
| DTO fields | `apps/web/src/api/types.ts` |
| API client | `apps/web/src/api/client.ts` |

## See also

- [Sandbox browser preview — User Guide](../experience/sandbox-browser-preview.md) — the step-by-step user flow.
- [Sandbox browser preview — Deep Dive](../deep-dive/sandbox-browser-preview.md) — how the reverse proxy works end to end.
- [Sandbox pods reference](./sandbox-pods.md) — pod naming and the wider sandbox API surface.
