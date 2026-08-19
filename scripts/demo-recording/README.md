# Demo recording CLI

Use one command surface for sign-in, session setup, plan preparation, capture, and
status:

```powershell
npm run demo:record -- help
```

## First use or expired sign-in

```powershell
npm run demo:record -- signin
```

## Microsoft Entra boundary for agents

An agent may click Agentweaver's own **Sign in with Microsoft Entra ID** button to
start its redirect. Cached SSO may complete authentication after that click. Once the
redirect reaches Microsoft Entra, the agent must not interact with that UI: no account
selection, credentials, MFA, consent, or access to tokens, cookies, session storage,
browser profiles, or account data. If cached SSO does not complete authentication, stop
and have a human complete sign-in privately and off camera. Agents must not run `signin`
or inspect authentication artifacts to work around this boundary.

The command uses **only** the literal Microsoft Edge `Default` work profile at
`%LOCALAPPDATA%\Microsoft\Edge\User Data\Default`. Chrome and every other Edge
profile are refused. Close all Edge windows when prompted. The command then:

1. Validates that `Local State` identifies that exact `Default` profile, then copies
   it into a freshly created disposable, Git-ignored directory.
2. Opens and foregrounds the Agentweaver shell in that copy, waits until its **Sign in with
   Microsoft Entra ID** button is visible, then clicks that Agentweaver-owned button. Cached
   SSO may return directly to Agentweaver. When the redirect reaches Microsoft Entra, the
   recorder stops automation there; account selection, credentials, MFA, and consent remain
   human-only.
3. Saves the Playwright storage state and Agentweaver `sessionStorage` sidecar.
4. Closes and deletes the disposable Edge profile.

## Safe sign-in recovery

After a recorder-owned session closes or authentication expires, keep any planned media
and fixtures unchanged, then close only the named recording session:

```powershell
npm run demo:record -- close
```

Close any remaining Microsoft Edge windows through their normal UI. Then run:

```powershell
npm run demo:record -- signin
npm run demo:record -- open
npm run demo:record -- status
```

Proceed only after `status` reports that the recording session is authenticated. Do not
use another tool's auth artifacts, terminate Edge by name, or clean fixtures as part of
this recovery.

The live Default directory is never automated. Microsoft Edge requires Edge instances
to be closed for DevTools attachment, and current Chromium releases reject remote
debugging against the default browser data directory. The disposable copy preserves
the required work-profile state without attaching automation to the live profile. It
launches Edge with `--profile-directory=Default`. A copy is built in a temporary
directory and only replaces the automation copy after a complete refresh succeeds; the
tool never falls back to an old clone or another profile. If Edge leaves a file locked,
the tool waits without terminating any process, then fails clearly if the exact source
cannot be refreshed.

Authentication files stay under `scripts\demo-recording\.auth\`. Git ignores this
directory. Before any authentication write or profile copy, the CLI resolves the real
destination, rejects junction/reparse escapes, and verifies that it remains inside the
repository's protected, Git-ignored auth root. It never prints cookies, tokens,
storage-state contents, or session-storage values.

## Start a persistent recording session

```powershell
npm run demo:record -- start `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json
```

`open`, `start`, and `capture` each refresh sign-in state from the exact Edge Default
source before opening the named `playwright-cli` session. If that named session is
already open, the CLI closes only that owned session before waiting for Edge; it never
terminates unrelated Edge processes. It does not reuse stale saved authentication as a
fallback. The default session name is `agentweaver-demo`.

## Capture

Capture one beat:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --beat 1.1
```

Capture the pre-IdP sign-in handoff only:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --beat 0.0 `
  --unauthenticated
```

This starts a dedicated non-persistent session without loading recording storage state
and captures only the Agentweaver handoff dialog. An agent may click that Agentweaver
button; cached SSO may then complete the redirect. Cut immediately when Microsoft Entra
is reached and do not interact with its UI or record account content. A human completes
any unfinished sign-in privately and off camera.

Capture the complete plan:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --all
```

Authenticated capture restores and verifies the persistent session before it runs the
generated script with `playwright-cli --raw`. `capture --all` automatically skips
unauthenticated handoff beats and begins with the first authenticated beat; it never
waits for their unauthenticated dialog.

Capture-plan prerequisites are evaluated only for selected beats. Thus Beat 0.0 can
capture its safe unauthenticated Agentweaver handoff without external GitHub-triage
variables, while `--all` or a later beat still requires that beat's declared
prerequisites. `open` performs only the normal Agentweaver sign-in recovery flow, so it
can reach the Agentweaver **Sign in** affordance for cached SSO recovery without capture
plan validation. It does not interact with the identity provider beyond that button.

## Clean Blueprint fixture

At an inactive recording boundary, remove only the plan's explicitly named demo fixture:

```powershell
node scripts\demo-recording\clean-staging.mjs `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --confirm-demo-cleanup
```

Cleanup refuses while any persistent `playwright-cli` session is open, a plan targeting
another origin, or a fixture pattern that differs from the declared fixture name (apart
from the deterministic UTC timestamp suffix). It lists every project page and verifies
that no matching fixture remains; unrelated projects are never deleted.

## Final-take preflight

Before a final capture, close all recorder sessions and run:

```powershell
node scripts\demo-recording\final-take-preflight.mjs `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json
```

The preflight requires the plan's isolated final-take output directory to contain every
planned media path, refuses any pre-existing planned output, and verifies that only that
plan's declared fixture is absent. It does not delete recordings, broad directories, or
projects; preserve or move an earlier take, then use the plan-scoped cleanup command at
an inactive boundary if its declared fixture remains.

## Other commands

```powershell
npm run demo:record -- open
npm run demo:record -- prepare --plan <capture-plan>
npm run demo:record -- status
npm run demo:record -- close
```

Use `npm run demo:record -- help` for all options. Existing media processing commands
on `scripts\demo-recording\cli.mjs` remain available through the same entry point.
