---
name: "agentweaver-demo-recording"
description: "Record a narrated demo video of the deployed Agentweaver staging app using playwright-cli, authenticated via the ui-harness's persisted human login session. Use when asked to record a demo, screencast, or walkthrough video of the live staging app."
domain: "testing"
confidence: "high"
source: "verified interactively 2026-07-27"
allowed-tools: Bash(playwright-cli:*) Bash(node scripts/ui-harness/agent-driver-ui/tools.mjs:*)
---

# Demo recording: bridging ui-harness auth into playwright-cli

This skill connects two previously-disconnected tools:

- **`playwright-cli`** — has full video/screencast recording (`video-start`,
  `video-chapter`, `video-show-actions`, `run-code` + `page.screencast.*`). See
  `.copilot/skills/agentweaver-playwright-cli/references/video-recording.md` for the
  complete recording API — do not duplicate it here.
- **The UI-harness's staging login** — a human completes GitHub OAuth once via
  `node scripts/ui-harness/agent-driver-ui/tools.mjs login --base-url <staging-url>`,
  which persists auth to `scripts/ui-harness/.auth/staging.storageState.json` (+ a
  sessionStorage sidecar, see below). See `scripts/ui-harness/SKILL.md` and
  `scripts/ui-harness/README.md` for the full login/auth contract — do not duplicate it
  here, and **never** run the login flow yourself; it requires a human in the loop.

`playwright-cli` has no awareness of the ui-harness's auth files, and the ui-harness has
no video recording. The recipe below is the verified bridge between them.

## Prerequisite

A human must have already run the ui-harness login once, so both of these exist:

- `scripts/ui-harness/.auth/staging.storageState.json`
- `scripts/ui-harness/.auth/staging.storageState.json.sessionStorage.json`

If either is missing, **stop and ask the human to run the login command** — do not
fabricate these files or attempt to automate GitHub OAuth yourself.

## Why this is non-obvious: two different storages

Agentweaver's session token lives in **`sessionStorage`**, not cookies or
`localStorage` (see `apps/web/src/config.ts`, and the comment in
`scripts/ui-harness/lib/auth.mjs`). Playwright's `storageState()`/`state-load` API only
restores cookies + `localStorage` — it cannot see `sessionStorage` at all. Verified
directly: `staging.storageState.json` for this project contains only GitHub OAuth
cookies (`origins: []` — no localStorage entries for the Agentweaver origin itself).
Loading it with `playwright-cli state-load` alone is **not sufficient** to authenticate
into the Agentweaver app; you must separately seed the sessionStorage sidecar.

## Verified recipe

```bash
# 1) Open a persistent playwright-cli session. On Windows, --browser=chrome commonly
#    fails with "Chromium distribution 'chrome' is not found" unless Chrome is
#    installed; --browser=msedge works out of the box on Windows.
playwright-cli -s=demo open --persistent --browser=msedge

# 2) Load the storageState (restores GitHub OAuth cookies; harmless/optional for the
#    app session itself, but keep it in case cookie-based flows are exercised later).
playwright-cli -s=demo state-load scripts/ui-harness/.auth/staging.storageState.json

# 3) Navigate to the staging app ONCE first. At this point the app will render its
#    "Sign in with GitHub" page — this is expected, sessionStorage is not seeded yet.
playwright-cli -s=demo goto https://<staging-app-host>

# 4) Seed sessionStorage from the sidecar file, then reload. Do NOT pass the token as a
#    literal CLI argument or inline run-code string — it will be echoed back in the
#    "Ran Playwright code" section of the response and end up in your own transcript/
#    logs. Instead, write a small script file that embeds the values (see below) and
#    invoke it with `--raw`, which suppresses the echoed source/code section.
playwright-cli -s=demo --raw run-code --filename=<path-to-generated-seed-script.cjs>
playwright-cli -s=demo reload
playwright-cli -s=demo snapshot   # should now show the authenticated app shell (nav,
                                   # user menu with the logged-in username, Overview
                                   # page) instead of the sign-in page
```

### Generating the seed script safely (no secret ever printed)

`run-code` runs in a sandboxed context with **no `require` and no dynamic `import`**
(verified: both throw `ReferenceError`/`ERR_VM_DYNAMIC_IMPORT_CALLBACK_MISSING`), so the
script cannot read the sidecar file itself from inside the sandbox. Instead, generate
the script from PowerShell/Node *outside* playwright-cli, embedding the sessionStorage
entries directly, then run it with `--filename` + `--raw`:

```powershell
$seed = Get-Content scripts\ui-harness\.auth\staging.storageState.json.sessionStorage.json -Raw | ConvertFrom-Json
$entriesJson = $seed.entries | ConvertTo-Json -Compress
@(
  "async page => {",
  "  await page.evaluate((entries) => {",
  "    for (const [key, value] of Object.entries(entries)) window.sessionStorage.setItem(key, value);",
  "  }, $entriesJson);",
  "  return { origin: '$($seed.origin)', keysSeeded: Object.keys($entriesJson) };",
  "}"
) | Set-Content -Path <scratch-path>\seed.cjs -Encoding utf8

playwright-cli -s=demo --raw run-code --filename=<scratch-path>\seed.cjs
```

Delete the generated seed script afterward — it contains the live session token in
plaintext. Never `view`/`cat`/print the sidecar file's `entries.agentweaver.sessionToken`
value in conversation, logs, or commits.

**Verified working end-to-end**: after this sequence, `playwright-cli snapshot` showed
the real Agentweaver Overview page (nav with "sabbour" logged in, Projects/Sessions
links, live status), not the sign-in screen — confirmed against the actual staging
deployment on 2026-07-27.

## Recording the demo once authenticated

Once authenticated, use the ordinary `playwright-cli` video commands — this part is
fully documented in
`.copilot/skills/agentweaver-playwright-cli/references/video-recording.md`; only the
Agentweaver-specific notes are called out here:

```bash
playwright-cli -s=demo video-start recordings/demo-<topic>.webm
playwright-cli -s=demo video-chapter "Overview" --description="..." --duration=1500
# ... perform real navigation/clicks against the authenticated staging app ...
playwright-cli -s=demo video-chapter "Projects" --description="..." --duration=1200
playwright-cli -s=demo video-stop
```

Verified: a `video-start` → `video-chapter` → click → `video-chapter` → `video-stop`
cycle against the authenticated staging Overview/Projects pages produced a valid,
non-empty `.webm` file (~850KB for ~5 seconds of activity with two chapter cards).

Notes specific to this app:

- The primary nav has **multiple elements matching `getByRole('link', { name: 'Projects' })`**
  (sidebar link, "View all projects →", "View projects" button) — prefer the snapshot
  `ref` (e.g. `e39`) over role-based locators to avoid strict-mode violations when
  scripting navigation.
- `scripts/demo-recording/lib/capture-plan.mjs` now emits a background approval watcher
  for `Tool Approval Required` cards. It waits briefly (~2.25s) so narrated beats can
  still call out the human-in-the-loop gate, then auto-clicks the real `Allow once` /
  `Approve` action if a beat forgot or a second gate appears. Keep explicit gate steps
  in beats that intentionally show the approval moment; use `plan.disableApprovalWatcher`
  only if a future capture truly needs a longer unresolved hold on screen.
- For fully-scripted, narrated recordings with overlays/highlights (the
  `page.screencast.showChapter`/`showOverlay` API), write a `run-code --filename=...`
  script exactly as described in the video-recording reference; the sessionStorage
  seeding step above only needs to happen once per browser session, before the first
  `goto`/`reload` of the app — it is not part of the recording script itself.
- Always clean up test/scratch recordings (`video-start` output files, generated seed
  scripts) that aren't the intended final deliverable; do not commit them.

## Security notes (do not skip)

- `scripts/ui-harness/.auth/staging.storageState.json` and its `.sessionStorage.json`
  sidecar are **git-ignored, single-human-session artifacts** — they contain a live
  GitHub OAuth session and Agentweaver session token. Never fabricate them, never copy
  them elsewhere, never commit them, and never print their secret values (token,
  cookies) into chat, logs, or committed files.
- If the session has expired (`AUTH_EXPIRED` from the ui-harness, or the recipe above
  still shows the sign-in page after seeding), ask the human to re-run
  `node scripts/ui-harness/agent-driver-ui/tools.mjs login --base-url <staging-url>` —
  do not attempt to complete GitHub OAuth yourself.
- Close the playwright-cli session (`playwright-cli -s=demo close`) and delete any
  scratch seed script when done, so the plaintext token doesn't linger on disk longer
  than necessary.
