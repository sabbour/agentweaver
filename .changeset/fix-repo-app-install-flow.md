---
"agentweaver": minor
---

Fix the broken "Install GitHub Repo App" flow so the resulting GitHub App installation actually
binds to the project. Previously the button only opened GitHub's generic installation page, and
`RepoAppInstallationLifecycleService.BindAsync` — which creates the `GitHubInstallationRecord`
and `GitHubRepositoryGrantRecord` unattended runs require — had no production caller, so projects
could get a repository attached (via personal OAuth) but never a working App installation.

- Add `RepoAppInstallationAuthorizationService`, a project-pinned, Owner-authorized transaction
  flow mirroring the existing Copilot App project-binding pattern: a signed, single-use `state`
  value plus a one-time `__Host-` callback cookie, stored via the existing purpose-agnostic
  `GitHubAuthorizationRecord`/`ClaimAuthorizationAsync`/`CompleteAuthorizationAsync` persistence
  (no new crypto primitives).
- `POST /api/projects/{id}/github/repo-app-installation/authorizations` begins the flow and
  returns an installation URL carrying that `state`.
- `GET /auth/github/repo-app/installation/callback` is GitHub's App installation **Setup URL**
  (distinct from the OAuth callback URL). It reads `installation_id`/`setup_action`/`state`,
  validates the transaction, resolves the connected repository's numeric ID from the live
  installation, and calls `RepoAppInstallationLifecycleService.BindAsync` — completing the
  previously-missing binding. `setup_action=request` (pending org-owner approval) is reported as
  an informational "pending" outcome instead of erroring. The browser is redirected back to
  Project Settings' Background automation section with a clear success/failure indicator.
- The Project Settings "Install GitHub Repo App" button now starts this flow instead of linking to
  a generic, unbound installation URL. After connecting a repository via personal OAuth, users are
  now taken directly to the Background automation section so they can complete the App
  installation step next.
- Fix `GitHubConnectionsPersistenceStore.CompleteAuthorizationAsync`, which this flow is the first
  production caller of: its `ExecuteUpdateAsync` call failed to translate on the SQLite/relational
  provider due to an inline ternary/DateTimeOffset conversion.

This requires a one-time manual GitHub App configuration change (cannot be automated): set the
Repo App's **Setup URL** to `https://<api-host>/auth/github/repo-app/installation/callback` and
enable **"Redirect on update"**. See `docs/guide/configuration.md` for details.
