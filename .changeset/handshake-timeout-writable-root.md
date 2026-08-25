---
"agentweaver": patch
---

fix(sandbox): raise handshake timeout to exceed writable-root wait; address bwrap CPU throttling

Preview kept failing with repeated 30s `observe_bound_port` timeouts even after the v0.19.0
`observe_bound_port` handshake fix landed. Root cause: `PodExecSandboxClient`'s handshake
timeout (30s) was shorter than `KataBwrapExecutor`'s own internal wait (120s) for the
per-run writable-system-root "hold" helper to report `READY`, so the client gave up and
abandoned the connection before the sidecar could ever report success or failure, which the
sidecar then observed as a broken pipe.

- Raised `PodExecSandboxClient.HandshakeTimeout` from 30s to 150s (safely above the
  writable-root wait) and documented the dependency between the two timeouts.
- Rebalanced the `agentweaver-agent-host` sandbox pod's CPU split so the CPU-heavy
  `agentweaver-exec` container (which runs the bwrap writable-root setup) gets more
  request/limit headroom (600m/1000m -> 700m/1200m), taken from the thin relay-only
  `agentweaver-agent-host` container (400m/1000m -> 300m/800m). Combined pod-level totals
  (1000m request / 2000m limit) are unchanged, so katapool scheduling density is unaffected.
