---
name: "ui-harness-cli"
description: "Run the Agentweaver persona-driven Playwright UI evidence harness. Use for a specific persona's deployed browser flow, end-to-end UI validation, or investigation of a UI-reported issue."
domain: "testing"
confidence: "high"
source: "scripts/ui-harness/agent-driver-ui/tools.mjs"
---

# UI harness CLI contract

Use this harness to capture deterministic browser evidence for a persona against an
Agentweaver deployment. It is a driver, not a UX judge: `finish` emits driver P0 facts
and normalized evidence for `scripts/harness-judge/`; do not call subjective UX quality
verified solely from its output.

## Prerequisites

From the repository root, install the harness dependencies and run its fixture tests:

```powershell
node scripts/ci/shared-deps.mjs ensure --project scripts/ui-harness
npm --prefix scripts/ui-harness test
```

Targets are host-agnostic. Require an absolute HTTPS URL except for loopback HTTP, keep
normal TLS validation enabled, and reject URL credentials/fragments. Automated browser
requests and navigation stay on the configured app origin; only the explicit headful
login flow may visit configured identity-provider origins.

## Authentication

Agentweaver staging uses Microsoft Entra Conditional Access, which blocks plain
Chromium (and device-code flow). Authentication must use the managed **Edge Default
profile** on Windows (enrolled device).

### Option A — Edge is not currently running (preferred)

Close all Edge windows first (save any open work — they will be lost), then:

```powershell
node scripts/ui-harness/login-edge-default.mjs --base-url https://<host>.staging.<domain>
```

This launches the real Edge Default profile (`%LOCALAPPDATA%\Microsoft\Edge\User Data`).
Entra SSO often completes automatically. If the sign-in page appears, complete it in the
Edge window, then press Resume in the Playwright Inspector.

### Option B — Edge is already running (CDP attach)

Relaunch Edge with remote debugging (requires closing the current Edge first):

```powershell
Start-Process msedge.exe "--remote-debugging-port=9222 --user-data-dir=`"$env:LOCALAPPDATA\Microsoft\Edge\User Data`" --profile-directory=Default --no-first-run https://<host>.staging.<domain>"
```

Then connect and capture:

```powershell
node scripts/ui-harness/login-edge-default.mjs --base-url https://<host>.staging.<domain> --cdp
```

### What is saved

Both options write to `scripts/ui-harness/.auth/` (git-ignored):

- `staging.storageState.json` — Playwright cookies + localStorage
- `staging.storageState.json.sessionStorage.json` — sessionStorage seed (Agentweaver token)
- `session-token.txt` — plain-text token for `AGENTWEAVER_TOKEN` (API harness only)

Treat these files as credentials: never print, commit, log, or attach them. The harness
never automates reauthentication. On `AUTH_EXPIRED`, run the login script again
(or pass `--storage-state <local-path>` consistently).

`init` validates that the selected storage-state file exists and has a usable
Playwright shape before it creates a scenario session. It starts that session's
headless browser worker, but it does not launch or automate the login flow.

> **Legacy note**: `login-capture-edge.mjs` and the `tools.mjs login` command use plain
> Chromium (channel `msedge` without the Default profile user-data-dir). They may fail
> Conditional Access. Prefer `login-edge-default.mjs` for Entra-protected staging.

## Run a persona flow

Initialize a session and retain the `sessionId` from its JSON output:

```powershell
node scripts/ui-harness/agent-driver-ui/tools.mjs init --persona priya --base-url https://<host>.staging.<domain>
```

Execute only the safe, concrete actions needed by the persona flow. Every action prints
a JSON evidence step and requires `--session <sessionId>`:

```powershell
node scripts/ui-harness/agent-driver-ui/tools.mjs goto --session <sessionId> --path /
node scripts/ui-harness/agent-driver-ui/tools.mjs click --session <sessionId> --test-id <test-id>
node scripts/ui-harness/agent-driver-ui/tools.mjs type-coordinator --session <sessionId> --text "<text>"
node scripts/ui-harness/agent-driver-ui/tools.mjs drag --session <sessionId> --from-test-id <source-test-id> --to-test-id <target-test-id>
node scripts/ui-harness/agent-driver-ui/tools.mjs capture --session <sessionId>
```

All action commands for one session are serialized through the same browser context and
page, even though each command is a separate CLI process. A `goto` followed by `click`,
`drag`, and `capture` therefore keeps the current route, DOM state, cookies, local
storage, and session storage. Pass `--path /<path>` to `capture` only when it should
navigate first; without `--path`, it captures the page left by the preceding action.

`drag` performs a genuine left-pointer move/down/move/up sequence. Use stable
`data-testid` targets, not generated React Flow classes. It defaults to the center of
each element and 12 intermediate move steps. Use `--steps <1-100>` to tune the path,
and optional element-relative `--from-x`, `--from-y`, `--to-x`, and `--to-y` offsets
for handles or node repositioning. Offsets must remain inside the selected elements.
For the visual workflow editor, targets include `workflow-canvas`,
`workflow-node-<node-id>`, and `workflow-node-<node-id>-handle-source|target`.
Failed drags release the pointer and append a failed evidence turn before exiting `2`.

`goto` and `capture` wait up to 30 seconds for the authenticated Agentweaver app shell
after `domcontentloaded`; a transient authentication spinner is allowed to resolve
during that window. A persistent authentication-loading shell or visible sign-in
prompt exits `3` as `AUTH_EXPIRED`, while another page that never becomes usable exits
`2` as `APP_NOT_READY`. For a route with a different legitimate semantic readiness
anchor, declare either `--ready-test-id <test-id>` or both
`--ready-role <aria-role> --ready-name <exact-accessible-name>`. Override the wait only
when necessary with `--readiness-timeout <milliseconds>`. A declared readiness anchor
is mandatory for that command: the generic app shell cannot satisfy it.

Persisted evidence and recovery metadata store URLs as origin plus pathname only.
Userinfo, query strings, and fragments are removed recursively from snapshots,
actions, errors, screenshot metadata, and result artifacts.

Cross-origin preview navigation is intentionally not available from an authenticated
harness context. Validate preview URLs from a separate credential-free browser context.

For `click` and `type-coordinator`, use `--test-id` where available. Otherwise provide
both `--role <aria-role>` and `--name <exact-accessible-name>`. Do not use arbitrary CSS
selectors. `click` also accepts `--timeout <milliseconds>`; action evidence can record
the intended action with `--thought "<intent>"`.

Approval gates are deny-by-default:

```powershell
node scripts/ui-harness/agent-driver-ui/tools.mjs resolve-approval --session <sessionId> --decision approve --gate-type <type> --test-id <test-id>
```

Only request `approve` when the loaded persona adapter explicitly allows that exact safe
gate type. Otherwise use `--decision defer` or do not invoke the command.

Finish the session to emit the canonical JSON result:

```powershell
node scripts/ui-harness/agent-driver-ui/tools.mjs finish --session <sessionId> --batch-id <batchId> --scenario-id <scenarioId> --input-seed <seed> --target-revision <revision>
```

The result contains `driver` P0 facts plus `normalizedEvidence`. It does not certify
subjective UX quality. `driver.pass` is false when no successful evidence exists, a
command failed, or captured evidence shows an authentication/loading shell instead of
the app. The harness exits `3` for `AUTH_EXPIRED` and `2` for other command errors; a
successful command exits `0`.

Always call `finish`: it closes the session's browser worker, removes private recovery
artifacts, and archives `result.json` beside the screenshots. If a worker crashes or
times out while idle, the next action starts a replacement from the last completed
action's URL and protected browser-storage snapshot. Separate session IDs use separate
workers and recovery directories; no CDP or remote-debugging endpoint is exposed.
Cleanup runs before result persistence, so an artifact write failure cannot strand the
browser worker or harness-owned session state. Live action arguments are sealed while
crossing the worker's private file transport and are sanitized separately for evidence.
If forced worker termination cannot be confirmed, session/runtime metadata remains so
cleanup can be retried instead of orphaning an untracked browser.
