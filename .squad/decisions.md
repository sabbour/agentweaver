# Decisions Log

## 2026-08-30: GitHub Copilot App connection bugs — root cause + fix (session by @sabbour via Copilot CLI)

**Reported problem:** 6 broken GitHub Copilot App flows in production (v0.21.4): create project via GitHub,
generative/non-project API calls, start session, connect project, add repo to existing project, start task/run —
all failing with `github_binding_unavailable`, `github_copilot_auth_required`, or `github_copilot_connection_required`.

**Root cause #1 — deployment manifest gap (fixed, merged, deployed):**
The GitHub Copilot App and Repo App credentials existed correctly in Key Vault
(`agentweaver-kv-eus2euap`), but `k8s/base/secret-provider-class.yaml` and `k8s/base/api-deployment.yaml`
never mounted/wired them into the running API pod. Fixed in PR #1026 (merged to `dev`).

**Root cause #2 — startup crash bug (fixed, merged, deployed):**
Once root cause #1 was fixed and the Copilot App config became non-empty, the API crashed on every
boot. `CopilotAppRegistrationService.HasOnlyMandatoryMetadataReadPermission` in
`apps/Agentweaver.Api/Auth/CopilotAppRegistrationService.cs` required GitHub's public `/apps/{slug}`
endpoint to return exactly `{"permissions":{"metadata":"read"}}`, but a correctly-configured GitHub App
with no extra permissions actually returns `permissions: {}` (GitHub omits the implicit `metadata:read`
from the public API). This crashed `CopilotAppRegistrationStartupService.StartAsync`, taking down the
entire API host — meaning the API could not even start once root cause #1's config was wired in. Fixed
in PR #1028 (merged to `dev`), which also corrected the test suite that had locked in the wrong assumption.

**Also fixed this session:**
- PR #1027 — restored the sidebar GitHub identity indicator (`GitHubIdentityBadge.tsx`), which had been
  deleted with no replacement, after the user flagged a missing account-picker UI element.

**Deployed:** `node scripts/azure/cli.mjs deploy-from-commit origin/dev` at commit `86100421`
(includes PR #1026, #1027, #1028). Live verification: both `agentweaver-api` pods `Running 1/1`, 0
restarts, all `Auth__CopilotApp__*`/`Auth__RepoApp__*` env vars present in the running pod, `agentweaver-secrets`
k8s Secret contains all 7 expected keys, warm pool 2/2 ready, provenance 4/4 images verified.
**Not yet done:** live click-through verification of the actual 6 reported user flows (requires a real
authenticated GitHub/Entra session) — the user should verify these directly.

**Known follow-up (not yet filed as an issue, not fixed this session):**
`scripts/azure/deploy-from-commit.mjs` (and likely `deploy-from-release.mjs`) do not auto-load the
per-user `params.<username>.json` file the way `deploy-from-local` does in `cli.mjs` — only
`deploy-from-local` and `verify` get that treatment (see `scripts/azure/cli.mjs` around the
`mergeParamsIntoEnv` call). This session worked around it by manually loading params into `process.env`
before invoking `deploy-from-commit`. Should be fixed properly in its own small PR.

**Storage cleanup (user's secondary ask, partially investigated):**
- The `${repoRoot}.frontend-node_modules.<pid>` orphaned sibling-folder pattern
  (`scripts/azure/steps/20-build-push-images.mjs`, `stashFrontendNodeModules`/`restoreFrontendNodeModules`)
  is only restored in a `finally` block, which does not run on abrupt process kill/CI cancel — a real
  leak source, but no orphaned folders were found on disk in this environment at investigation time
  (likely already cleaned up by the user before this session).
- `scripts/demo-recording/.auth/sessions/<name>/` (currently ~52 MB for `agentweaver-demo`) is a
  full Edge browser profile (Cache, Code Cache, GPUCache, GrShaderCache, BrowserMetrics, etc.) created by
  the external `playwright-cli` tool's `-s=<session> open --persistent --browser=msedge` invocation in
  `scripts/demo-recording/lib/recording-session.mjs` (`restoreRecordingAuthentication`). This directory is
  **not created or cleaned by any code in this repo** — it's the external tool's own persistent-profile
  storage. The repo already defines the right exclusion list for a similar purpose
  (`PROFILE_COPY_EXCLUDED_DIRECTORIES` in the same file) but it is only applied to the one-time Edge
  profile *copy* used for `signInRecordingSession`, not to this persistent playwright-cli session
  directory. Not fixed this session — would need a small follow-up that prunes those same known-safe
  cache directories from `.auth/sessions/<name>/` after `closeRecordingSession`.
- ~80+ old `.worktrees/` entries and several full separate clones (`agentweaver-1007`,
  `agentweaver-aks-recording`, `agentweaver-deploy-eastus2euap`, etc.) under
  `C:\Users\asabbour\Git\` were noted but not cleaned — needs a manual pass to check which are stale.

**Resuming this work in a new session:** read this entry, then check `gh pr list --repo sabbour/agentweaver`
and `git log origin/dev` for anything merged since `86100421`. No formal versioned release has been cut
yet — this was deployed directly from `dev` at the user's request ("this is all a prototype").
