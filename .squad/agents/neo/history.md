

## 2026-07-06T07-29-39Z — v0.8.0 staging release

Neo's run-page polish (#195) shipped in the v0.8.0 staging wave. The integrated release deployed healthy to staging; do not close #195 or push/merge until Ahmed validates.


## 2026-07-07T00:00:00Z — v0.9.2 staging ship

Neo shipped the backend tool-approval 404 fix (`ba00d7b`): approve/deny now resolves the owning child subtask run via `ResolveApprovalOwningRunIdAsync`, preserving direct child posts and fixing coordinator-parent posts. Three new tests covered the routing behavior. The fix is included in v0.9.2 on staging AKS.
