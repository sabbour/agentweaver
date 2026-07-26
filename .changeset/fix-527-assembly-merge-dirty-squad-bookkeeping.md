---
'agentweaver': patch
---

Fix `coordinator.assembly_merge_failed` ("the working tree cannot be safely reconciled
with the merge result because uncommitted content diverges") firing after an already
fully-approved coordinator run's human review, when a subtask's own sandboxed coding
agent appends new entries directly to already-tracked Squad bookkeeping files (for
example `.squad/decisions.md`, `.squad/agents/*/history.md`) without committing them.
`WorktreeManager` now auto-commits dirty content on already-tracked, modified paths in
the checked-out originating-branch working tree immediately before computing merge
safety, so this uncommitted-but-legitimate content becomes an ordinary extra parent
commit instead of blocking the merge. This also fixes the reported symptom where the
`conflictingFiles` list grew across repeated retries: every merge attempt now sweeps
whatever is currently dirty, so retries can no longer compound into an ever-larger,
unresolvable conflict set. A genuine textual collision between the auto-committed
content and the child branch's own change to the same file still correctly fails the
merge for human resolution — auto-committing never hides a real conflict.
