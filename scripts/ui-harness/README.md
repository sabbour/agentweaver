# Agentweaver UI harness

Playwright evidence driver for persona-led UI validation. It is a driver, not a UX
judge: it captures deterministic browser facts, then sends normalized evidence to
`../harness-judge/`.

```powershell
npm --prefix scripts/ui-harness install
node scripts/ui-harness/login-chrome-default.mjs --base-url https://<host>.staging.<domain>
node scripts/ui-harness/agent-driver-ui/tools.mjs init --persona jordan --base-url https://<host>.staging.<domain>
node scripts/ui-harness/agent-driver-ui/tools.mjs goto --session <sessionId> --path /
node scripts/ui-harness/agent-driver-ui/tools.mjs click --session <sessionId> --test-id <test-id>
node scripts/ui-harness/agent-driver-ui/tools.mjs capture --session <sessionId>
node scripts/ui-harness/agent-driver-ui/tools.mjs finish --session <sessionId>
```

`login-chrome-default.mjs` is the primary login script. It checks that Chrome is fully
closed, safely clones the managed Chrome Default profile
(`%LOCALAPPDATA%\Google\Chrome\User Data`) into a disposable git-ignored `.auth`
directory, and launches Chrome from that clone. It never launches or remote-debugs the
live Default directory. Close all Chrome windows first, then run:

```powershell
node scripts/ui-harness/login-chrome-default.mjs --base-url https://<host>.staging.<domain>
```

It navigates to the supplied base URL before checking or clicking Agentweaver's sign-in
button. It never automates Microsoft Entra account selection, credentials, MFA, or
consent. If Chrome is locked, it exits with a close-Chrome instruction rather than
opening an empty tab. Do not fall back to generic Playwright, direct CDP/DevTools,
ad-hoc profile launches/copies, or manual browser automation. `--cdp` and `--cdp-url`
are rejected; resolve the reported condition and rerun this command.
See `scripts/ui-harness/SKILL.md` for full options and what is saved.

The local git-ignored `.auth/staging.storageState.json` is reused headlessly. Expiry stops
with `AUTH_EXPIRED`; the harness never automates reauthentication. Any HTTPS host is
accepted (HTTP is loopback-only), with normal TLS validation. Automated navigation and
requests are same-origin; only the explicit headful login flow may visit configured
identity-provider origins. Storage state is origin-filtered and never logged or attached
to evidence.

The matching `staging.storageState.json.sessionStorage.json` sidecar is also the sole
authentication handoff to the API harness. Its `recorder-session` provider validates
the target origin and uses the bearer only in memory, so a completed Chrome Default SSO
login is not repeated for an API harness run.

`init` owns one headless browser worker per session. Separate action invocations reuse
that worker's page, so navigation and browser state survive a documented
`goto` → `click` → `capture` sequence. Commands are locked per session, different
sessions remain isolated, and `finish` closes the worker and deletes its private
recovery state. An abandoned worker is recovered from the last completed action without
opening a CDP or remote-debugging endpoint.

Persisted snapshots, actions, errors, screenshot metadata, and normalized artifacts keep
URLs only as origin plus pathname; userinfo, query strings, and fragments remain
in-memory only as required for the live page. Action arguments are sealed for the
session worker while crossing the private file transport. `finish` closes the
browser/runtime and removes harness-owned session state before writing evidence, so
artifact failures cannot strand local resources. If forced termination cannot be
confirmed, retry metadata remains instead of being deleted prematurely.

## Pointer drag

Use stable test IDs to reproduce canvas interactions with a real pointer sequence:

```powershell
# Connect two workflow nodes.
node scripts/ui-harness/agent-driver-ui/tools.mjs drag --session <sessionId> `
  --from-test-id workflow-node-implement-handle-source `
  --to-test-id workflow-node-review-handle-target --steps 16

# Reposition a node to a safe element-relative point inside the workflow canvas.
node scripts/ui-harness/agent-driver-ui/tools.mjs drag --session <sessionId> `
  --from-test-id workflow-node-implement --to-test-id workflow-canvas `
  --to-x 640 --to-y 420 --steps 20
```

`--from-x`, `--from-y`, `--to-x`, and `--to-y` are optional pixel offsets inside
their selected elements; omitted coordinates use the element center. Out-of-bounds
coordinates and invisible/missing targets fail before pointerdown. If a drag fails
after pointerdown, the driver releases the pointer and records the failed action in
the session transcript/evidence before exiting `2`.
