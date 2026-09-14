---
name: "agentweaver-demo-recording"
description: "Record a narrated demo video of the deployed Agentweaver staging app using the demo recording CLI. Use when asked to record a demo, screencast, or walkthrough video of the live staging app."
domain: "testing"
confidence: "high"
source: "verified interactively 2026-07-27"
allowed-tools: Bash(playwright-cli:*) Bash(node scripts/ui-harness/agent-driver-ui/tools.mjs:*)
---

# Demo recording

Use the recording CLI for setup, capture, and status:

```powershell
npm run demo:record -- help
```

## Authentication

When the session is closed or auth is expired, re-authenticate autonomously:

```powershell
npm run demo:record -- signin
npm run demo:record -- open
```

This opens the literal installed Google Chrome, using a disposable copy of the
`Default` work profile, and navigates to the Agentweaver app. The recorder clicks
**Sign in with Microsoft Entra ID** for you — cached SSO normally completes
authentication with no prompt. Do not interact with any Microsoft Entra page
(account selection, credentials, MFA, consent). If SSO does not auto-complete,
wait; do not enter any credentials.

Do not ask the user to re-authenticate. The agent handles this independently.

**An expired token is not a blocker — it is an instruction to run `signin`.**
Never report the task as blocked on authentication before actually running
`signin` and `open` and observing what happens. Running them is the expected
path, not a workaround; see
[`scripts/demo-recording/README.md`](../../../scripts/demo-recording/README.md#microsoft-entra-boundary-for-agents)
for the exact boundary. Only a Microsoft Entra prompt that actually appears and
actually blocks progress justifies a human handoff.

## Recording

Capture the unauthenticated handoff beat only (beat 0.0):

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --beat 0.0 `
  --unauthenticated
```

For all authenticated beats:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --all
```

Use `npm run demo:record -- help` for the available recording commands. Close the
recording session when finished and do not commit recordings or authentication data.
