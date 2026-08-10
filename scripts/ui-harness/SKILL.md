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
node scripts/ui-harness/agent-driver-ui/tools.mjs capture --session <sessionId> --path /<path>
```

`drag` performs a genuine left-pointer move/down/move/up sequence. Use stable
`data-testid` targets, not generated React Flow classes. It defaults to the center of
each element and 12 intermediate move steps. Use `--steps <1-100>` to tune the path,
and optional element-relative `--from-x`, `--from-y`, `--to-x`, and `--to-y` offsets
for handles or node repositioning. Offsets must remain inside the selected elements.
For the visual workflow editor, targets include `workflow-canvas`,
`workflow-node-<node-id>`, and `workflow-node-<node-id>-handle-source|target`.
Failed drags release the pointer and append a failed evidence turn before exiting `2`.

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
subjective UX quality. The harness exits `3` for `AUTH_EXPIRED` and `2` for other command
errors; a successful command exits `0`.
