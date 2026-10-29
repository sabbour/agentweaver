---
"Agentweaver.Orchestrator": minor
---

Add the isolated Scribe Knowledge client and immutable per-child output intent:
preserve the actual producing agent and full result, reject oversized serialized
payloads, and replay the existing Memory API with actor-bound idempotency.
Created records remain untrusted pending-review Memories, not Decisions or
auto-approved SessionContext. Selected-gate dispatch and checkpoint wiring are
integrated separately by their runtime owner.
