---
'agentweaver': patch
---

Fix tool-approval "AgentHost approval endpoint is unreachable" (503) during the
coordinator draft/decompose/orchestrate phases. `ResolveApprovalOwningRunIdAsync` did
not know about the synthetic `-coordinator-draft`/`-coordinator-decompose`/
`-coordinator-orchestrate` run-id suffixes used to key approval-gate context for those
LLM turns, so an operator's "Allow once" click on a grounding tool call (e.g.
`web_fetch`) raised during outcome-spec drafting always failed with `no_context`.
