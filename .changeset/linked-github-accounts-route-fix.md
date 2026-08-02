---
"agentweaver": patch
---

fix(auth): repair linked-GitHub-account route/verb mismatches and the dead "link account" flow

The Entra multi-account GitHub linking feature never actually worked end-to-end:

- `client.ts`'s `listLinkedGitHubAccounts`, `setDefaultLinkedGitHubAccount`,
  `unlinkLinkedGitHubAccount`, and `listAccessibleGitHubRepos` called routes that never
  existed server-side (`/auth/github/linked-accounts*`, `/github/repos/accessible`), and one
  used the wrong HTTP verb (`POST` instead of `PUT`). Every one of these operations 404'd.
- "Add account" / "Link another GitHub account" built a URL to `/auth/github/authorize` with
  an `intent=link` query param the server never reads — that endpoint always runs a plain
  sign-in exchange, never the dedicated link flow. Added `apiClient.beginLinkGitHubAccount()`
  calling the correct, pre-existing `POST /auth/github-accounts/link` endpoint and rewired
  both call sites to use it.
- The accessible-repos response used inconsistent JSON casing versus what the frontend
  expects and was missing the source account's avatar/default-flag fields; fixed end to end.
- `LinkedGitHubAccountResponse` was missing `name`/`type` fields the frontend type requires;
  populated with `type: "user"` (GitHub identity links are always personal accounts, never
  orgs) and `name: null` (not fetched at link time).
