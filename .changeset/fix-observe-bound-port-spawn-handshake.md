---
"agentweaver": patch
---

Fix `observe_bound_port`/`health_check` silently reporting a preview process as started
when the sidecar's sandboxed spawn actually failed or was still resolving. The relay now
emits a start handshake (ready/error) from the sidecar's `Started`/`Error` frame, and
`StartSupervisedProcessAsync` blocks on it (30s timeout) before returning, so a failed
spawn now throws immediately instead of yielding a real-but-useless local PID that would
forever report `no_listening_port_discovered` with empty logs.
