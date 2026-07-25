---
'agentweaver': patch
---

Fix a 500 error when approving or denying the very first tool call of a run
(e.g. a `web_fetch` during coordinator spec drafting). The approval-gate
owning-run resolution could return a synthetic coordinator-phase key (e.g.
`{runId}-coordinator-draft`) that is not a real run id, which then crashed in
`RunId.Parse`. That synthetic key is now recognized and treated as the posted
coordinator run for ownership/status checks, while still using it to look up
the approval-gate request.
