---
'agentweaver': patch
---

Fix an intermittent `GitHubCopilotUnauthorizedException` at the build-test assembly
gate: `KubernetesSandboxExecutor` now resolves the GitHub access token shipped in the
AgentHost `/configure` request through the refresh-aware `IGitHubAccessTokenProvider`
(falling back to the raw token store only when the provider is unavailable), instead
of reading a potentially stale/expired token directly. This closes a race where the
build-test gate's freshly-launched AgentHost pod could receive a token that expired
during earlier subtask stages of the same run.

Also improve `assembly_merge_failed` diagnostics: a working-tree-divergence merge
`Blocked` outcome (uncommitted local content that cannot be safely reconciled with the
merge result) now reports the affected relative file path(s) via `conflictingFiles`,
instead of always showing an empty list alongside the "cannot be safely reconciled"
message. This is a diagnostics-only change — the merge-safety refusal decision itself
is unchanged.
