# #579 — consolidate preview retention behind a run lifecycle

**Date:** 2026-08-10
**Owner:** Morpheus

## Decision

Keep HTTPRoute annotations as the replica-safe durable source of truth, but model their
run-level consequence explicitly as `Previewable` or `PreviewActive`. A single transition
handler owns both SandboxClaim TTL and pod eviction policy.

## Evidence

The linked fixes remained split across `StartPreviewAsync`, `KeepAliveAsync`,
`ReleaseAgentHostPodAsync`, and `AgentHostReaperService`: each caller independently invoked
TTL renewal and/or `safe-to-evict` updates. Final teardown only reset eviction state and
stopping one of multiple routes could unpin a still-previewed pod. No newer abstraction
consolidated the invariant.

The implemented lifecycle now covers:

- start and active-use keepalive entering/reasserting `PreviewActive`;
- turn-end release and orphan reaping deriving state from durable routes before deletion;
- TTL extension and eviction protection in one idempotent handler;
- final stop/expiry restoring normal TTL and eviction policy;
- multi-route cleanup retaining protection until the final route is gone.

This is intentionally a focused consolidation, not a new persisted state machine: durable
HTTPRoutes already provide replica-safe state and expiry, so duplicating them in a database
would introduce reconciliation risk without improving the lifecycle invariant.
