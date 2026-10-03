---
"Agentweaver.Identity": minor
"Agentweaver.Abstractions": minor
---

Add the candidate trusted Identity library with exact actor/project/run/purpose/
SecretRef ID and version checks and immutable pre/post grant ID, revision and
expiry comparison. Add metadata-only, thread-safe lifetime narrowing to
SecretCredential without copying or reading its value; preserve backend
invalidation and invalidate acquired credentials on every subsequent failure.
Native async race regressions cover cancellation, revocation, expiry and
same-binding replacement. This is unreleased minor intent, not a broker,
durable store, running service or deployment (#1776).
