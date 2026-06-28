# Sandbox browser preview — Reference

Terse reference for the **sandbox browser preview** port-forward API: the routes that open, list, and stop
a tunnel from a port *inside* a run's sandbox pod to a loopback port on the API host, so a human can open a
live browser preview of a server the agent started.

**Kubernetes-only.** The tunnel targets the run's bound sandbox pod, resolved by run id via
[`PodNameRegistry`](./sandbox-pods.md#pod-naming-and-the-executing-pod-surface). On local/dev backends
there is no claim pod, so `POST` fails with `409`. Every call verifies the run exists and the caller owns
it (`404`/`403`).

Implementation: `PortForwardService` shells out to
`kubectl port-forward --address 127.0.0.1 pod/{podName} :{targetPort} -n {namespace}` (it does **not** use
the Kubernetes API), parses the `Forwarding from 127.0.0.1:<port> ->` line to learn the local port, and
probes loopback TCP until ready.

## Routes

| Method & path | Body | Returns | Notes |
|---|---|---|---|
| `POST /api/runs/{runId}/sandbox/port-forward` | `{ "target_port": <1..65535> }` | `PortForwardSessionDto` | Starts a `kubectl port-forward` from the pod's `target_port` to a loopback port on the API, returns the session. |
| `GET /api/runs/{runId}/sandbox/port-forward` | — | `PortForwardSessionDto[]` | Lists active preview sessions for the run. |
| `DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}` | — | `{ session_id, stopped: true }` | Stops the session and tears down its tunnel. |

## `PortForwardSessionDto`

| Field | Type | Meaning |
|---|---|---|
| `session_id` | string | Session identifier; used as `{sessionId}` to stop the session via `DELETE`. |
| `local_port` | number | Loopback port **on the API host** that `kubectl` bound. The API returns this port — **not** a public URL. |
| `target_port` | number | Port **inside** the sandbox pod being forwarded. |
| `pod_name` | string | Bound sandbox pod the tunnel targets (from `PodNameRegistry`). |
| `started_at` | string | ISO timestamp of when the session started. |
| `preview_url` / `previewUrl` | string \| null | **Web-only, optional.** The frontend reads these to render an embedded iframe and an *Open preview* button. The backend does **not** currently populate them; the UI says so when no proxied URL is returned. |

## Status codes

| Code | When |
|---|---|
| `200 OK` | Session started (`POST`), listed (`GET`), or stopped (`DELETE`). |
| `400 Bad Request` | `target_port` outside `1..65535`, or `runId` not parseable. |
| `403 Forbidden` | Caller does not own the run. |
| `404 Not Found` | Run does not exist, or (`DELETE`) the session id is unknown for the run. |
| `409 Conflict` | No active sandbox pod for the run — the run must be `in_progress` with an active Kubernetes sandbox. |
| `429 Too Many Requests` | A session cap was hit (`PortForwardLimitExceededException`). |
| `500` | kubectl failed to start the tunnel (e.g. `kubectl` not on PATH). |

## Limits

| Limit | Default | Config key (fallback) |
|---|---|---|
| Concurrent sessions per run | **3** | `Sandbox:PortForward:MaxConcurrentSessionsPerRun` (`:MaxPerRun`) |
| Concurrent sessions globally | **20** | `Sandbox:PortForward:MaxConcurrentSessionsGlobal` (`:MaxGlobal`) |
| `kubectl` path | `kubectl` | `Sandbox:KubectlPath` |
| Namespace | `agentweaver` | `Sandbox:Kubernetes:Namespace` |

Each cap is floored at `1`. Exceeding either raises `429`.

## Lifecycle

- **In-memory, no TTL.** Sessions live only in `PortForwardService`'s in-process maps; there is no expiry
  timer.
- **Per-port, explicit.** One session forwards one `target_port`; opening another preview is a second
  `POST`. Sessions are listed and stopped individually.
- **Ends on:** explicit `DELETE`; run end (`RunWatchLoopService` stops every session and unregisters the
  pod); the `kubectl` process exiting on its own; or `Dispose()` at API shutdown.
- **Bound to the pod.** Valid only while the run's pod is bound; releasing/replacing the pod
  (suspend/resume, run end) ends forwarding, and a new preview must be started against the re-claimed pod.

## Example

```http
POST /api/runs/run_01HXYZ/sandbox/port-forward
Content-Type: application/json

{ "target_port": 3000 }
```

```json
{
  "session_id": "a1b2c3d4e5f6",
  "local_port": 54321,
  "target_port": 3000,
  "pod_name": "agent-pod-worker-7",
  "started_at": "2026-06-28T09:20:07Z"
}
```

```http
DELETE /api/runs/run_01HXYZ/sandbox/port-forward/a1b2c3d4e5f6
```

```json
{ "session_id": "a1b2c3d4e5f6", "stopped": true }
```

## See also

- [Sandbox browser preview — User Guide](../experience/sandbox-browser-preview.md) — the step-by-step user flow.
- [Sandbox browser preview — Deep Dive](../deep-dive/sandbox-browser-preview.md) — how the tunnel works end to end.
- [Sandbox pods reference](./sandbox-pods.md#sandbox-preview-port-forward-feature-017) — pod naming and the wider sandbox API surface.
