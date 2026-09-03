# Agentweaver UI harness

Playwright evidence driver for persona-led UI validation. It is a driver, not a UX
judge: it captures deterministic browser facts, then sends normalized evidence to
`../harness-judge/`.

```powershell
npm --prefix scripts/ui-harness install
node scripts/ui-harness/agent-driver-ui/tools.mjs login --base-url https://<host>.staging.<domain>
node scripts/ui-harness/agent-driver-ui/tools.mjs init --persona jordan --base-url https://<host>.staging.<domain>
node scripts/ui-harness/agent-driver-ui/tools.mjs goto --session <sessionId> --path /
node scripts/ui-harness/agent-driver-ui/tools.mjs click --session <sessionId> --test-id <test-id>
node scripts/ui-harness/agent-driver-ui/tools.mjs capture --session <sessionId>
node scripts/ui-harness/agent-driver-ui/tools.mjs finish --session <sessionId>
```

`login-edge-default.mjs` is the primary login script. It opens the real Edge Default
profile (`%LOCALAPPDATA%\Microsoft\Edge\User Data`) to satisfy Conditional Access.
Close all Edge windows first, then run:

```powershell
node scripts/ui-harness/login-edge-default.mjs --base-url https://<host>.staging.<domain>
```

If Edge is already running with `--remote-debugging-port=9222`, use `--cdp` instead.
See `scripts/ui-harness/SKILL.md` for full options and what is saved.

The local git-ignored `.auth/staging.storageState.json` is reused headlessly. Expiry stops
with `AUTH_EXPIRED`; the harness never automates reauthentication. Any HTTPS host is
accepted (HTTP is loopback-only), with normal TLS validation. Automated navigation and
requests are same-origin; only the explicit headful login flow may visit configured
identity-provider origins. Storage state is origin-filtered and never logged or attached
to evidence.

`init` owns one headless browser worker per session. Separate action invocations reuse
that worker's page, so navigation and browser state survive a documented
`goto` → `click` → `capture` sequence. Commands are locked per session, different
sessions remain isolated, and `finish` closes the worker and deletes its private
recovery state. An abandoned worker is recovered from the last completed action without
opening a CDP or remote-debugging endpoint.

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
