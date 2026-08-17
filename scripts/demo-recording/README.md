# Demo recording CLI

Use one command surface for sign-in, session setup, plan preparation, capture, and
status:

```powershell
npm run demo:record -- help
```

## First use or expired sign-in

The recording CLI owns its own authentication. Do **not** use or copy the UI-harness
auth bridge (`scripts\ui-harness\.auth\...`) for a recording session; recording auth is
kept separately under `scripts\demo-recording\.auth\`.

Before interactive sign-in, close the CLI-owned persistent session:

```powershell
npm run demo:record -- close
```

Close remaining Edge windows normally. The CLI waits for Edge instead of terminating
processes. If a background Edge process must be closed, inspect it first and target
only the confirmed PID:

```powershell
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
  Select-Object ProcessId, CommandLine

# Use only after verifying the PID belongs to the intended Edge session.
Stop-Process -Id <verified-pid>
```

Never use a broad name-based kill such as `Stop-Process -Name msedge`.

```powershell
npm run demo:record -- signin
```

The command uses **only** the literal Microsoft Edge `Default` work profile at
`%LOCALAPPDATA%\Microsoft\Edge\User Data\Default`. Chrome and every other Edge
profile are refused. Close all Edge windows when prompted. The command then:

1. Validates that `Local State` identifies that exact `Default` profile, then copies
   it into a freshly created disposable, Git-ignored directory.
2. Opens that copy in Microsoft Edge for the human to complete the sign-in flow exposed by
   the target deployment.
3. Saves the Playwright storage state and Agentweaver `sessionStorage` sidecar.
4. Closes and deletes the disposable Edge profile.

The CLI may open the interactive Edge browser, but identity-provider interaction is
human-only. The agent must not select an account, click through the identity provider,
enter credentials, complete SSO/MFA/consent, or bypass authentication. A human performs
those actions in the displayed browser.

### Azure/AKS scenario handoff

Scenario 2 targets [`sabbour/AKS`](https://github.com/sabbour/AKS), not `Azure/AKS`.
It expects the staging deployment's **Entra** flow, so the human must use
**Sign in with Microsoft Entra ID**. The capture plan declares that requirement; pass the
plan to the session commands so the CLI requires that visible button rather than assuming
GitHubLegacy:

```powershell
npm run demo:record -- signin `
  --plan scripts\demo-recording\plans\azure-aks-demo.capture.json
npm run demo:record -- open `
  --plan scripts\demo-recording\plans\azure-aks-demo.capture.json
npm run demo:record -- status
```

This is a clean-from-scratch scenario only for its explicitly named
`Agentweaver Demo S2 - Azure AKS` fixture. Do not sign in, capture, delete any project,
deploy, or change live authentication while preparing the handoff. Shared staging
projects are out of scope; a capture owner may use the separately confirmed cleanup
workflow only at an inactive boundary and only for that declared fixture.

Only `signin` accesses the live Default directory. Microsoft Edge requires Edge
instances to be closed for DevTools attachment, and current Chromium releases reject
remote debugging against the default browser data directory. The disposable copy
preserves the required work-profile state without attaching automation to the live
profile. It launches Edge with `--profile-directory=Default`. Each refresh builds and
uses a new, Git-ignored disposable directory; it never falls back to an old clone or
another profile. Chromium lock files, caches, and transient `.tmp` files are not
copied. A brief `EPERM`/`EBUSY`/`EACCES` copy conflict receives three bounded retries
with backoff; a persistent failure leaves existing recording authentication unchanged
and reports the affected source/destination class and operation.

Authentication files stay under `scripts\demo-recording\.auth\`. Git ignores this
directory. Before any authentication write or profile copy, the CLI resolves the real
destination, rejects junction/reparse escapes, and verifies that it remains inside the
repository's protected, Git-ignored auth root. It never prints cookies, tokens,
storage-state contents, or session-storage values.

After sign-in, verify the live recording session before capture:

```powershell
npm run demo:record -- open
npm run demo:record -- status
```

`open` restores the recording session from its protected auth sidecar and verifies the
live Agentweaver app shell. It does not inspect, wait for, or copy the live Default
profile, so normal Edge use can continue. `status` also checks only protected auth and
the named session. Proceed only when it reports `Session authentication: verified`.

Treat recording authentication as short-lived (roughly 45 minutes). If a recording run
reaches an expected expiry/sign-in state, preserve its in-progress plan and media
artifacts. Repeat the close, Edge-close, and `signin` procedure above; then re-run
`open` and `status` before resuming. Do not recover through the UI-harness login path
or discard artifacts unless the recording workflow specifically directs it.

## Workflow checks and recovery

### Session-only Azure Speech narration

Use the Azure CLI-authenticated session launcher for narration synthesis. It discovers
the one accessible East US 2 Azure AI Services resource at runtime, obtains its key only
in memory, and supplies it only to the child narration process. It never writes the
resource settings to repository files or the user/machine environment.

```powershell
npm run demo:narrate:azure -- smoke
npm run demo:narrate:azure -- synthesize-beats `
  --plan scripts\demo-recording\plans\sizzle-reel.narration.md `
  --out-dir recordings\sizzle-reel\narration
```

`smoke` synthesizes a disposable WAV, verifies it through the existing CLI ffprobe
step, and deletes the sample. The launcher refuses ambiguous resource selection.

- **FFmpeg/FFprobe discovery:** post-processing resolves each executable in this order:
  `AGENTWEAVER_DEMO_FFMPEG`/`AGENTWEAVER_DEMO_FFPROBE`, the current `PATH`, then a
  `Gyan.FFmpeg` installation registered by WinGet under `%LOCALAPPDATA%\Microsoft\WinGet\Packages`.
  This lets a fresh shell or recording agent use `winget install Gyan.FFmpeg` without
  inheriting another shell's `PATH`. For a non-WinGet or pinned toolchain, set the two
  `AGENTWEAVER_DEMO_*` variables to the full executable paths in that agent's durable
  environment; do not rely on a one-off shell assignment.
- **Wrong auth workflow:** recording auth and UI-harness auth are separate. Use only
  `demo:record` commands to recover a recording session; never copy either tool's auth
  artifacts.
- **Edge is still running during `signin`:** close the named recording session, then
  close Edge normally or use only a reviewed PID. Do not use broad process-name
  termination. This does not apply to `open`, `start`, `capture`, or `status`.
- **Default-profile refresh reports `EPERM`:** the CLI has already excluded Chromium
  transient files and retried the protected disposable copy. Close Edge and any
  profile-inspecting tool (such as Explorer preview or endpoint-security scan) normally,
  wait briefly, then rerun `signin`. Do not delete or move
  `%LOCALAPPDATA%\Microsoft\Edge\User Data\Default`; if the failure persists, provide
  the reported path class, operation, and error code to desktop support.
- **Expired or unverifiable session:** preserve the plan and media, repeat `signin`,
  then require the live `open`/`status` verification before resuming.
- **Staging fixture state:** this checkout's recording CLI verifies live authentication;
  clean deployed Agentweaver projects only through the documented cleanup command below.
  Do not delete artifacts as a recovery shortcut.

## Deployed-project cleanup boundary

Clean only the explicitly declared fixture for an active capture plan:

```powershell
node scripts\demo-recording\clean-staging.mjs `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --confirm-demo-cleanup
```

`--confirm-demo-cleanup` is required for every deletion. The cleanup command refuses to
run while its named recording session is open, requires the plan to target the configured
staging origin, and deletes only projects that match the plan's fully anchored
demo-fixture names. It paginates through every visible project before cleanup and verifies
that no matching fixture remains; it never deletes unrelated projects. The script reads
only this checkout's demo-recording auth sidecar and does not print auth values. Run it
only **before a new take** or **after a completed or failed take**.

## Dry-run defects

File dry-run defects as deduplicated GitHub issues: search for an existing issue first.
For a new issue, include:

- scenario and beat;
- concise reproduction steps;
- observed and expected behavior;
- environment; and
- non-secret evidence only.

Fixes may run in parallel at safe capture boundaries, but must not block an active take.

## Safe-boundary recovery

| Field | Detail |
|---|---|
| Scenario / beat | Azure/AKS demo, beat 3.1 |
| Resolution | `open` no longer refreshes the live Default profile. |
| Recovery | If saved recording auth expires, preserve artifacts and run `signin` at a safe boundary. |

## Start a persistent recording session

```powershell
npm run demo:record -- start `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json
```

`signin` alone refreshes sign-in state from the exact Edge Default source. `open`,
`start`, and ordinary `capture` restore only the protected recording-auth sidecar; they
do not enumerate, wait for, or copy the live Default profile. If the named
`playwright-cli` session is already open and authenticated, it remains open without
navigation or reset, preserving its state across beats. If saved auth is missing or
expired, these commands fail closed and direct the operator to `signin`. The default
session name is `agentweaver-demo`.

When a capture plan declares `authentication.mode`, pass `--plan` to `signin`, `open`,
`start`, or `capture`; the CLI selects the matching deployment flow. Use
`--auth-mode github-legacy` or `--auth-mode entra` only when no plan is available.

## Capture

Capture one beat:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --beat 1.1
```

Capture the complete plan:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\blueprint-demo.capture.json `
  --all
```

The capture command restores and verifies the persistent session before it runs the
generated script with `playwright-cli --raw`.

### Capture evidence artifacts

Every completed or failed beat writes atomic, auditable sidecars beside its raw WebM:

```text
<video>.capture-cues.json   # immutable observed cue timings and rectangles
<video>.activity.json       # observed capture activity
<video>.capture-gate.json   # expected-vs-observed cues, order/deadlines, timing, activity, pass/fail
```

The capture output reports the stable gate-manifest path and its `PASS`/`FAIL` result.
The cue and activity sidecars are the existing inputs to `analyze-take`; for example:

```powershell
node scripts\demo-recording\cli.mjs analyze-take `
  --video recordings\azure-aks\beat-0-1.webm `
  --capture-plan scripts\demo-recording\plans\azure-aks-demo.capture.json `
  --cues recordings\azure-aks\beat-0-1.webm.capture-cues.json `
  --activity-log recordings\azure-aks\beat-0-1.webm.activity.json `
  --gate-manifest recordings\azure-aks\beat-0-1.webm.capture-gate.json `
  --beat-id 0.1 `
  --out recordings\azure-aks\beat-0-1.analysis.json
```

Meaningful activity is limited to scripted `click`, `drag`, `eval`, `focus`, `goto`,
`press`, `select`, `waitFor`, and `waitText` interactions. Automatic lifecycle,
DOM-mutation, and cue-tracker events do not count. A completed capture with missing,
late, or out-of-order cues, invalid timing, or no meaningful scripted interaction fails
after all three sidecars have been persisted. If a later beat fails, evidence already
written for earlier beats remains available, and the failed beat receives a failed gate
manifest with failure provenance when the CLI can collect an error. Pass
`--gate-manifest` to `analyze-take` to carry that gate status and provenance into the
analysis output.

For a later contiguous beat that depends on the UI state left by the prior beat, retain
the verified session and opt in to continuity:

```powershell
npm run demo:record -- capture `
  --plan scripts\demo-recording\plans\azure-aks-demo.capture.json `
  --beat 1.2 `
  --resume
```

`--resume` refuses to run unless the named session is open and authenticated; it does
not refresh auth or navigate back to the base URL.

## Other commands

```powershell
npm run demo:record -- open
npm run demo:record -- prepare --plan <capture-plan>
npm run demo:record -- status
npm run demo:record -- close
```

Use `npm run demo:record -- help` for all options. Existing media processing commands
on `scripts\demo-recording\cli.mjs` remain available through the same entry point.
