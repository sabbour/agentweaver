

## 2026-07-05T20:40:00-07:00 — v0.7.11 release batch
Authored v0.7.11 documentation for Suggested blueprint, observability telemetry, Overview, new-project dialogs, and slide-up panel behavior; regenerated MCP docs; docs build was green.


## 2026-07-06T22:05:00Z — v0.7.12 UI iteration docs

Updated existing v0.7.11 docs for v0.7.12 UI refinements: shared Blueprint panel/tab behavior, Templates parity, View all templates, single footer No blueprint, bounded dialog scrolling, personal GitHub repos, persistent Drafting state, transient 404 polling, terminal pre-draft failure, Confirming/409/double-submit feedback, and screenshot coverage. Commit `d3e9f81`; docs build passed.


## 2026-07-06T07-29-39Z — v0.8.0 staging release

Dozer's release documentation update (commit 37793de) shipped with the v0.8.0 staging wave. VitePress build passed, staging deployed healthy, and docs should not be pushed/merged until Ahmed validates the release.


## 2026-07-06 v0.9.0 staging wave
- Published docs updates for the released wave; docs build stayed green.

## 2026-07-14T04:00:00-07:00 -- Pagination frontend work (REQUEST_CHANGES, handed to Apoc)
Built server/client-side pagination across `ProjectGalleryPage`, `OrchestrationsPage`,
`MemoriesPage`, plus ~10 non-target callers, after niobe's snake_case paged-envelope contract
landed mid-task. Peer review returned REQUEST_CHANGES (real truncation bugs at the 100-item cap).
Per Reviewer Rejection Protocol, Apoc owns the revision -- did not touch those files further in
the #302 task below.

## 2026-07-14T04:00:00-07:00 -- Dozer: fix #302 timestamp display on messages
Added a subtle relative-time timestamp (with absolute-time tooltip) to each agent message in the
run-detail message stream (`CoordinatorRunPage` -> `Timeline` -> `TurnGroup` -> `AgentMessageBubble`).
New shared `utils/relativeTime.ts` util (none existed; consolidated a pattern duplicated inline in
3 other pages, without refactoring those existing call sites -- kept in scope). Timestamps are
client-side receipt time (`Date.now()` when the reducer processes the message event) since there
is no timestamp on the SSE/replay wire and the persisted `RunEvents.CreatedAt` column isn't
currently returned by the API -- flagged as a backend follow-up in
`.squad/decisions/inbox/dozer-302-timestamps.md`. Build + full test suite green (80 files / 725
tests). Not committed; flagged for peer review.

## 2026-07-14T04:00:00-07:00 -- Dozer: investigate #282 model badge on run/chat panel
Added a small, unobtrusive FluentUI `Badge` showing the agent's model (via the existing
`formatModelLabel()` formatter) next to the agent name/avatar in `AgentSessionPanel`'s header.
Model data was already available (`SubtaskNodeData.model` / `WorkflowNodeData.modelId` in
`CoordinatorRunPage`, already shown on the graph canvas pill) but not yet plumbed into the session
panel's `RunSessionTree`/`sessionMeta`/`buildTree` path -- added a `model?: string` field there and
threaded it through; it reaches the panel for free since `FlatTreeNode` spreads `RunSessionTree`.
No backend contract involved (pure frontend plumbing of already-available data), so nothing to
reconcile against another agent's work. Did not touch `client.ts`/`types.ts` (pagination-frozen,
Apoc's). Build + full test suite green (80 files / 728 tests). Not committed; flagged for peer
review. Findings in `.squad/decisions/inbox/dozer-282-model-badge.md`.

## 2026-07-14T04:45:00-07:00 -- Dozer: investigate #283 stale backlog item
#283 ("Add a session insights/observability slide-in panel") turned out to be a genuinely
ambiguous new-feature ask, not a scoped bug/small fix -- "Exact contents TBD" per the issue
author's own words, no existing slide-in-panel component to extend (only scattered existing
pieces: `AiCredits`, `AgentTokenBreakdown`, `TransactionTracePanel`, a whole separate
Observability section), and placement ("reachable from the run view") is itself undecided
(coordinator run? child run? `AgentSessionPanel`?). Same judgment call Trinity made on #201:
did not force an implementation, reported back with analysis + recommendation instead. No code
changed. Findings in `.squad/decisions/inbox/dozer-283-investigation.md`.

## 2026-07-14T15:15:00Z — #282 / #302 shipped, #283 deferred
Dozer's model-badge and subtle-timestamp work landed in the release wave after follow-up passes, while the broader #283 observability-panel idea was correctly deferred as design-sized work rather than squeezed into the batch.
