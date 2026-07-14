# Harness learnings

Append-only log of durable facts a harness run should already know before it starts:
discovered bugs/gotchas (including ones since fixed — keep the record so the same
mistake isn't reintroduced), environment facts, and "this is intentional, not a bug"
notes about scenario/adapter design.

Do not hand-edit this file. Append new entries with:

```powershell
node scripts/harness-shared/record-learning.mjs `
  --title "<short title>" `
  --category bug|environment-fact|scenario-design-note `
  --surface api|ui|mcp|all `
  --body "<detail>" `
  --status open|fixed  # optional, defaults to open
```

The script validates required fields and dedupes by title (case-insensitive) against
existing entries before appending — it will refuse to add an exact-title duplicate.

Each entry below follows this shape:

```
## <title>
- date: YYYY-MM-DD
- category: bug | environment-fact | scenario-design-note
- surface: api | ui | mcp | all
- status: open | fixed

<body>
```

---

## MCP stdio transport must never go through target-guard's URL validation

- date: 2026-07-14
- category: bug
- surface: mcp
- status: fixed

`client.mjs` used the literal string `"stdio"` as a transport-selector sentinel
(`options.target === 'stdio'` picks the stdio transport), but forwarded the same
`options` object into `createStdioTransport`, which called `assertTargetAllowed()` —
a URL-parsing guard meant only for the HTTP transport's network host allowlist. Since
`"stdio"` isn't a URL, this threw `target "stdio" is not a valid URL` on every stdio
connect attempt, breaking the harness's own documented smoke example. Fixed in commit
`80bf0121` by removing the guard call from `transport-stdio.mjs` entirely — stdio
spawns a local subprocess and has no network target to validate. Kept here so nobody
reintroduces the same conflation of "which transport to use" with "what network host
to validate" if the transport-selection code is ever refactored.

---

## Agentweaver MCP server endpoint path and auth requirement

- date: 2026-07-14
- category: environment-fact
- surface: mcp
- status: open

The Agentweaver MCP server's live HTTP endpoint is at the `/mcp`-suffixed path
(`https://<host>/mcp`), not the bare origin — connecting to the origin alone will not
reach the MCP endpoint. The server also requires OAuth: connecting over http
transport needs a valid, authenticated bearer token (`--token`/`AGENTWEAVER_TOKEN`),
not an arbitrary string. Obtain one via the app's own OAuth sign-in flow, or
`gh auth token` where that identity is what the server trusts. Stdio transport has
neither requirement since it never leaves the local subprocess.

---

## Staging Agentweaver environment can be undeployed between sessions

- date: 2026-07-14
- category: environment-fact
- surface: all

The staging Agentweaver environment (the `agentweaver` namespace on the
`agentweaver-aks-2` cluster) is periodically undeployed/deleted, not a permanently
available fixture. Before treating "can't reach staging" as a Harness or application
bug, run `kubectl get pods,svc,ingress -n agentweaver` first. If nothing is there,
this is an environment-availability gap to raise/redeploy, not a defect to chase in
harness or product code.

---

## `priya.api` adapter intentionally never confirms past the outcome-spec gate

- date: 2026-07-14
- category: scenario-design-note
- surface: api

`scripts/persona-briefs/surfaces/priya.api.md`'s Intent mapping ends with "Stop at
the outcome-spec confirmation gate without confirming execution." This is by design:
the scenario tests the pushback/revise-spec loop (Priya's mandatory two grounded
objections), not full-run completion. A run that stops there is working as intended,
not stuck or broken. If a caller wants a scenario that drives to full completion,
they need a different adapter/scenario, not this one.

This pattern generalizes across the catalog: every persona core in
`scripts/persona-briefs/personas/*.md` currently has a "Where to stop (safe
checkpoint)" section that stops before or at a confirmation/review gate, and every
`*.api.md` surface adapter explicitly says "Stop at the outcome-spec confirmation
gate without confirming execution." Any adapter whose Intent mapping says "stop at
X" should be treated as intentionally non-terminal, not a stuck/broken run, when
triaging a "the run didn't finish" report. See `scripts/persona-briefs/catalog.json`
for the `runsToCompletion` flag recorded per persona/surface pair.
