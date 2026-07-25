---
'agentweaver': patch
---

Fix UI harness auth replay for staging: Agentweaver's session token lives in
`sessionStorage`, which Playwright's `context.storageState()` does not capture
(only cookies and `localStorage` are persisted). Headless dry-runs replaying a
saved storage state always landed back on the GitHub sign-in page even with a
freshly captured, non-empty state. The `login` command now also captures a
companion `sessionStorage` seed file, and headless sessions re-hydrate it via
`context.addInitScript` before any page script runs.
