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

## Authentication boundary (do not skip)

For a recording that starts unauthenticated, an agent may click **only Agentweaver's
own** **Sign in with Microsoft Entra ID** button to begin the redirect. Cached SSO may
then finish authentication without further action.

Once the Microsoft Entra page or redirect is reached, the agent must not interact with
it. In particular, never select an account, enter credentials, handle MFA, grant
consent, or access tokens, cookies, session storage, browser profiles, or account
details. Do not inspect, create, copy, restore, or seed authentication artifacts.

If cached SSO does not complete the redirect, stop the recording flow. A human must
complete authentication privately and off camera before authenticated recording resumes.
Never run a browser-profile or sign-in helper to work around this boundary.

## Recording

Capture the pre-IdP handoff only:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --beat 0.0 `
  --unauthenticated
```

This isolated beat may show the Agentweaver handoff dialog and its button, but must cut
before any Microsoft Entra UI is shown. Do not record account content.

For authenticated plans, use an already-authenticated session supplied without the
agent handling any authentication material:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --all
```

Use `npm run demo:record -- help` for the available recording commands. Close the
recording session when finished and do not commit recordings or authentication data.
