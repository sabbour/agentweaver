---
"agentweaver": patch
---

Stop `start_preview` from aborting a preview registration that is still succeeding.

`PreviewPublishTool.CreateHttpClient` never set `HttpClient.Timeout`, so it kept the .NET default of
100 seconds. The tool arms a 3-minute registration budget and reports that value to the agent on
timeout, but the shorter client timeout always fired first — making the configured budget unreachable
dead code and producing a "did not complete within 180 seconds" error after roughly 100 seconds.

Publishing a preview routinely takes 90-120 seconds (port-forward, DNS convergence, health probe),
which straddles that default, so registration failed on a healthy, auto-approved app with no pending
approval to resolve. The client timeout is now disabled, leaving the linked cancellation token source
as the single authority on the budget. `Agentweaver.Mcp`'s client already did this; this path had
drifted from it.
