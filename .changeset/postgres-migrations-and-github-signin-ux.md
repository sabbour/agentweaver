---
"agentweaver": patch
---

fix: add missing Postgres migrations for notifications/GitHub-linking, polish sidebar GitHub sign-in UX

- Added the Postgres counterparts of two migrations that only ever existed in the
  SQLite dev-migrations project (`apps/Agentweaver.Api/Migrations`), so the live
  production Postgres provider (which resolves migrations from the separate
  `Agentweaver.Api.Migrations.Postgres` assembly) never created their tables:
  - `dismissed_notifications` — caused `GET /api/notifications` to 500.
  - `github_account_link_states` / `project_github_identity_overrides` — caused
    "Link another GitHub account" to 500 with
    `42P01: relation "github_account_link_states" does not exist`.
- `GitHubSignIn` (sidebar popover): wrapped the trigger in a tooltip so it's
  discoverable as the GitHub account switcher, added a persistent "Entra ID"
  badge + popover banner when signed in via Microsoft Entra ID, fixed the
  collapsed-rail (64px) layout so the trigger and status/version badge no
  longer squish together, and truncated long account name/login text in the
  popover's account lists.
- `SettingsPage`: added a confirmation toast when landing on
  `?auth=github_linked&login=...` (the redirect from the GitHub account-link
  flow), then strips those query params so a refresh doesn't re-fire it.
