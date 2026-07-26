---
"agentweaver": patch
---

Fix `start_preview` (agent-initiated preview registration) returning HTTP 403
for the run's own agent in every real deployment: `IsOwnerOrServiceCaller`
only recognized the internal service caller via a configured `Auth:User`
setting that no deployment ever sets (only `Auth:ApiKey` is injected). The
shared service key actually resolves to the hardcoded
`agentweaver-internal` identity, which is now checked directly, matching the
authorization already used for memory/decision/casting callbacks.
