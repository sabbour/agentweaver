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
npm --prefix scripts/ui-harness install
npm --prefix scripts/ui-harness test
```

Targets are limited to localhost and staging by the shared target guard. A production
target requires **both** `--allow-prod` and `--confirm-production` on every applicable
command. Never add those flags without explicit authorization.

## Authentication

Authenticate through a visible browser exactly once per valid session:

```powershell
node scripts/ui-harness/agent-driver-ui/tools.mjs login --base-url https://<host>.staging.<domain>
```

`login` opens Chromium headfully. Complete the visible GitHub or Microsoft Entra sign-in
yourself in that window, then
resume Playwright. The command saves the local, git-ignored storage state at
`scripts/ui-harness/.auth/staging.storageState.json`; it is reused by the remaining
headless commands. Treat that file as a credential: never print, commit, log, or attach
it to evidence. The harness never automates reauthentication. On `AUTH_EXPIRED`, run
the headful `login` command again (or pass `--storage-state <local-path>` consistently).
`init` validates that the selected storage-state file exists and has a usable
Playwright shape before it creates a scenario session; it does not launch or automate
the login flow.

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
node scripts/ui-harness/agent-driver-ui/tools.mjs capture --session <sessionId> --path /<path>
```

`goto` and `capture` wait up to 30 seconds for the authenticated Agentweaver app shell
after `domcontentloaded`; a transient authentication spinner is allowed to resolve
during that window. A persistent authentication-loading shell or visible sign-in
prompt exits `3` as `AUTH_EXPIRED`, while another page that never becomes usable exits
`2` as `APP_NOT_READY`. For a route with a different legitimate semantic readiness
anchor, declare either `--ready-test-id <test-id>` or both
`--ready-role <aria-role> --ready-name <exact-accessible-name>`. Override the wait only
when necessary with `--readiness-timeout <milliseconds>`.

To open an Agentweaver sandbox preview returned by the UI, use the dedicated
preview action:

```powershell
node scripts/ui-harness/agent-driver-ui/tools.mjs open-preview --session <sessionId> --url https://<generated-preview-host>
```

The browser permits this cross-origin navigation only for a generated
`{token}-preview.<staging-zone>` host associated with the session's Agentweaver
staging host. Other cross-origin navigation remains blocked.

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
