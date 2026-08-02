# Agentweaver UI harness

Playwright evidence driver for persona-led UI validation. It is a driver, not a UX
judge: it captures deterministic browser facts, then sends normalized evidence to
`../harness-judge/`.

```powershell
npm --prefix scripts/ui-harness install
node scripts/ui-harness/agent-driver-ui/tools.mjs login --base-url https://<host>.staging.<domain>
node scripts/ui-harness/agent-driver-ui/tools.mjs init --persona jordan --base-url https://<host>.staging.<domain>
```

`login` is the only headful step. Complete the visible GitHub or Microsoft Entra sign-in
manually in Microsoft Edge; the harness detects Agentweaver's authenticated session and saves the
local git-ignored `.auth/staging.storageState.json` is reused headlessly. Expiry stops
with `AUTH_EXPIRED`; the harness never automates reauthentication. Targets are
restricted to localhost/staging unless both `--allow-prod` and `--confirm-production`
are explicitly supplied. Storage state is never logged or attached to evidence.
