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

## 2026-08-30 (later same day): Two more real bugs found + fixed, storage cleanup, second production deploy

**User tested live after the first deploy and reported none of the 6 original errors were fixed.** This led to
finding two more real bugs that had been sitting as uncommitted local commits in a worktree from earlier in this
same session (`.worktrees/fix-github-copilot-connection`, branch `fix/github-copilot-connection-bugs`) — reviewed,
rebased onto latest `dev`, tested, and merged as **PR #1031**:

- **Bug #3 — case-insensitive credential JSON parsing:** `GitHubCapabilityBroker.cs` and
  `GitHubRepositorySelectionBroker.cs` used `JsonDocument.Parse` + `TryGetProperty("status"/"accessToken"/"expiresAt")`,
  which is case-sensitive. If the stored credential JSON used different casing than expected, a valid signed-in
  credential was silently treated as missing — this is a strong candidate for the `github_copilot_auth_required`
  "live run-bound capability snapshot" error even after the config-wiring fix. Fixed by switching to
  `JsonSerializer.Deserialize<Credential>(value, new(JsonSerializerDefaults.Web))`, which matches property names
  case-insensitively by default.
- **Bug #4 — missing "connect a new GitHub repo to a project" endpoint:** `POST /api/projects/{id}/github/repository`
  and `GET /api/projects/{id}/github/repository-owners` had been removed in an earlier (overengineered) session
  and were never restored. This is the most likely root cause of the reported "add repo → 404" error. Restored:
  lists the caller's GitHub user + orgs as repo owners, creates a new GitHub repo via the Repo App's live credential,
  and pushes the project's existing local git history into it.
- Both changes: 48/48 targeted tests pass, 449/449 broader Auth/Project suite passes (20 skipped Postgres-container
  tests, expected), clean rebase onto `dev`, changesets included, merged via PR #1031.

**Confirmed both GitHub Apps ARE correctly wired in the k8s manifests** (`k8s/base/api-deployment.yaml`,
`k8s/overlays/production/kustomization.yaml`, from PR #1026): `Auth__CopilotApp__*` (Client ID/Secret/CallbackUrl,
Slug hardcoded to `agentweaver-orchestrator-copilot` — matches the slug the user confirmed) and `Auth__RepoApp__*`
(Client ID/Secret/CallbackUrl/AppId/PrivateKeySecretName) are both present as distinct env var groups. An earlier
`grep` search that appeared to find nothing was because it ran against the stale main checkout
(`C:\Users\asabbour\Git\agentweaver`, stuck on an unrelated branch) instead of `origin/dev` — same trap as before,
noted again as a recurring hazard of this repo's messy shared main checkout.

**Second production deploy — commit `428bb6d4`** (includes PR #1026–#1031): ran via
`node scripts/azure/cli.mjs deploy-from-commit origin/dev` from a clean worktree synced to `dev` (running it from
the stale main checkout silently used the OLD cli.mjs without the params-autoload fix and failed with
`KEYVAULT_NAME is not set` — copied `scripts/azure/params.asabbour.json`, which is gitignored, into the fresh
worktree, and the auto-load fix from PR #1030 worked correctly on retry). Rollout briefly reported "timed out
waiting for condition" because `kubectl rollout status --timeout=180s` raced the last pod's readiness right at the
180s mark — re-running `kubectl rollout status` immediately after confirmed `successfully rolled out`. Verified:
both `agentweaver-api` pods `1/1 Running`, 0 restarts, clean startup logs (no exceptions), all 9 expected
`Auth__CopilotApp__*`/`Auth__RepoApp__*` env vars present including the correct Copilot App slug.

**Investigated the "938/947/949" confusion the user raised:** #938 is the epic "deliver the two-GitHub-App fleet
migration" (14 sub-issues). As of a 2026-08-30 checklist correction already on that issue: only **#947** (sandbox
backend adapters) and **#949** (MCP capability parity) remain open — separate feature scopes, not blockers for the
6 originally-reported connection bugs. The epic's own notes confirm: "#951/#952 were closed based on code-level
tests, not live production verification — the production Kubernetes deployment was never wired with the real
GitHub App credentials until PR #1026, which is the actual root cause of the connection errors reported after
v0.21.4." This matches this session's independent findings. **#1007** (workflow-editor bug) is closed/fixed,
unrelated. **PR #1018** (Kata executor loopback transport) is a separate open issue about sandbox execution
transport on Kata, unrelated to GitHub auth — still open, not addressed this session, no action taken on it.

**Release process:** user confirmed "this is all a prototype" — direct deploy from `dev` continues to be the
right process; no formal versioned release cut for this round of fixes. Only the k8s manifest/runtime config
needed redeploying for the two-app slug/callback-URL reconfiguration — no image rebuild was required for that
part, though the code fixes in PR #1031 did require a normal image rebuild + redeploy (handled by
`deploy-from-commit`, which builds and pushes images before applying manifests).

**Current status — all 6 originally-reported bugs, updated:**
1. `github_binding_unavailable` (409, create project via GitHub) — fixed by PR #1026 (config wiring) + #1028
   (startup crash). **High confidence.**
2. `github_copilot_auth_required` "live run-bound capability snapshot" — fixed by #1026/#1028, **plus now also**
   by #1031's case-insensitive credential fix, which is a more direct match for this exact error message.
   **Higher confidence after PR #1031.**
3. `github_copilot_connection_required` (start session, start run) — same root causes as #1/#2. **High confidence.**
4. "Connection could not be started" (connect project UI) — same root causes as #1. **High confidence.**
5. Add repo to existing project → 404 — **now has a concrete fix**: PR #1031 restores the missing endpoint.
   **Was previously the least-confident item; now directly addressed** (for blank projects creating a new repo —
   if the reported case was "connect an *existing* repo" rather than "create a new one", that would need a
   distinct endpoint not yet found/restored — flag this nuance to the user).
6. Sidebar GitHub identity indicator — fixed by PR #1027 (already confirmed low-risk UI restore).

**Not yet independently verified by live user click-through** — everything above is confirmed via code review,
targeted tests, and production pod health, but real GitHub OAuth click-through in a live browser session has not
been performed by this agent (cannot simulate the user's authenticated session). User should re-test all 6 flows
now against the second deploy (commit `428bb6d4`).

**Storage cleanup work merged this session (PR #1030, unrelated to the auth bugs but part of the user's original
ask):** demo-recording session-profile cache pruning (`.auth/sessions/<name>/`, ~44MB reclaimed on the one folder
inspected) and `deploy-from-commit`/`deploy-from-release` now auto-load `params.<username>.json` the same way
`deploy-from-local` always did (this fix was exercised live during this session's second deploy and confirmed
working).

**Not done / explicitly out of scope this session:**
- Broader ~80+ stale worktree / clone cleanup under `C:\Users\asabbour\Git\` — still not touched.
- PR #1018 (Kata executor transport) — separate issue, not part of this session's GitHub-auth focus.
- Distinguishing "create new repo" vs "connect existing repo" for the add-repo flow, if the user's reported case
  turns out to be the latter.

## 2026-08-30 (live user testing round): two more real bugs found and fixed via direct user feedback

After the second deploy, the user tested live and found two more concrete, reproducible bugs — this is the
process working as intended (deploy → user tests → report exact symptom → root-cause → fix → redeploy):

**Bug #5 — Copilot App OAuth callback URL was registered with a typo on GitHub's side (user-side fix, not code):**
User hit "Be careful! redirect_uri is not associated with this application" on the GitHub OAuth authorize page.
Root cause: the Copilot App's registered callback URL on GitHub was
`.../auth/github-copilot-app/callback` (hyphenated, no slash) but the server's actual route is
`.../auth/github/copilot-app/callback` (slash-separated — confirmed via `git grep` on `origin/dev` against
`ProjectEndpoints.cs:142` and `ProjectCopilotBindingService.cs`). The Repo App's registered callback was already
correct. **This was a GitHub App admin-console setting, not something fixable via code, API, or `kubectl`** — the
user corrected it directly on https://github.com/settings/apps/agentweaver-orchestrator-copilot.

**Bug #6 — post-auth browser redirect fell back to localhost (real code/infra bug, fixed in PR #1033):**
After bug #5 was corrected, the user got further — GitHub's authorize page succeeded, but the API then redirected
the browser to `http://localhost:5173/projects?copilot_app_auth=authorization_transaction_invalid...` instead of
the deployed production frontend. Root cause: both `ProjectCopilotBindingService.GetCallbackRedirectAsync` and
the equivalent method in `RepoAppUserAuthorizationService.cs` read `Auth:CopilotApp:FrontendUrl` /
`Auth:RepoApp:FrontendUrl` from configuration and fall back to `http://localhost:5173` (the local dev default)
when unset. The k8s manifest (`k8s/base/api-deployment.yaml`) wired every other Copilot/Repo App setting
(client id/secret, callback URL, slug/app id, private key secret name) but never set these two — a gap in the
same PR #1026 that did the original wiring. Fixed in **PR #1033**: added `Auth__CopilotApp__FrontendUrl` and
`Auth__RepoApp__FrontendUrl` env vars, both reusing the existing `ENTRA_FRONTEND_URL` configmap value (the same
production frontend URL Entra already uses — no new configmap key needed). Added two new assertions to
`scripts/azure/tests/deploy-render.test.mjs` confirming the rendered manifest wires both correctly; 21/21 tests
pass. **Deployed to production at commit `2e2e1a9b`** — confirmed via `kubectl exec ... printenv` that both new
env vars now correctly resolve to `https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io` on the
running pods; both API pods `1/1 Running`, 0 restarts, clean startup logs; 4/4 images provenance-verified against
commit `2e2e1a9b`.

**The `authorization_transaction_invalid` outcome code in the URL was a secondary symptom, not a separate bug**:
`GetCallbackRedirectAsync` builds `{frontend}{route}?copilot_app_auth={outcome}` — since `frontend` resolved to
`localhost:5173` (unreachable in the user's context) the browser never got far enough to show what the real
outcome would have been on the correct frontend; the `authorization_transaction_invalid` in the URL was likely a
downstream effect of the transaction/state cookie not surviving the redirect to the wrong origin, not evidence of
yet another distinct bug. **Follow-up for the user's next live test**: after PR #1033's redeploy, if
`authorization_transaction_invalid` still appears on the *correct* production frontend URL, that would indicate a
genuinely separate transaction/state-handling bug worth investigating next — flag if seen again.

**Process note reinforced this round**: this exact live-test-first workflow (deploy → user reproduces exact
symptom with a screenshot → agent traces to precise code location → small targeted PR → redeploy) found two real,
previously-invisible-to-code-review bugs in under an hour, validating the user's original request to avoid
"overengineering reviews" and instead get agents working, review for correctness, commit, and move to the next
concrete symptom.
