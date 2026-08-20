---
title: Authentication
---

# Authentication

Agentweaver supports two authorization modes selected by deployment configuration:

- **`Auth:Mode=Entra`** — Microsoft Entra ID is the platform sign-in and authorization gate. GitHub accounts are linked afterward for repository access and GitHub Copilot entitlement.
- **`Auth:Mode=GitHubLegacy`** — GitHub remains both the platform sign-in path and the GitHub capability provider.

Both are supported deployment choices.

## Signing in

When you open Agentweaver for the first time (or after your session expires), you'll see the sign-in page.

![Sign in page](/guide/images/sign-in.png)

The button and follow-up flow depend on the deployment's configured mode:

- **Entra mode** — sign in with your organization's Entra account first. After your platform session is established, you can link one or more GitHub accounts for repository access and GitHub Copilot.
- **GitHub-based authorization mode** — sign in with GitHub directly. Once you authorize, GitHub redirects you back and your session is established.

For Entra deployments, the browser sign-in uses authorization code + PKCE. If the tenant allows
client secrets, Agentweaver can redeem the code as a confidential client; if the tenant blocks
password credentials, the same flow works without a client secret as long as the Entra app
registration allows public client flows.

::: tip One sign-in for everything
In **Entra mode**, the Entra sign-in grants platform access and your linked GitHub accounts provide repository access and GitHub Copilot entitlement.

In **GitHub-based authorization mode**, the GitHub sign-in grants both platform access and GitHub capability access.
:::

## Authorization requirements

Depending on how Agentweaver is deployed, access may require either:

- specific **Entra App Roles** plus project-level assignments (`Auth:Mode=Entra`), or
- membership in a specific **GitHub organization/team** (`Auth:Mode=GitHubLegacy`).

Organization access is not the same as access to every project. Project data and every run linked to
a project follow that project's authorization boundary. In Entra mode, project-level `Owner`,
`Contributor`, and `Viewer` assignments apply: viewers can inspect runs, while contributors and
owners can operate them. A linked GitHub login supplies repository and Copilot capability but does
not replace the Entra project identity for authorization. In GitHubLegacy mode, the persisted project
owner remains the boundary. Older runs with no project retain submitting-user ownership. Agentweaver
does not include a built-in superuser GitHub username; a user named `admin`
has the same ownership rules as any other user.

## How sessions work

Agentweaver uses server-side sessions. After a successful sign-in:

- Your session is stored on the server and identified by a secure browser cookie
- The cookie is `HttpOnly` and `SameSite` — it is never accessible to JavaScript
- You remain signed in until you explicitly sign out or the session expires on the server

When GitHub is connected, the GitHub token is stored server-side in your own Key Vault-backed user scope and is never written to shared storage.

No GitHub tokens are stored in `localStorage`, `sessionStorage`, or any other browser-accessible location.

## Connecting GitHub for repository access

When creating a project from GitHub, Agentweaver lists your repositories. If the GitHub account needed for that action is not yet connected (or its token has been revoked), the repository picker shows a **Connect GitHub** prompt.

Click **Connect GitHub** to start the authorization flow. After authorizing, the dialog reloads and your repositories appear.

In Entra mode, a single platform account can link **multiple GitHub accounts**. One linked GitHub identity acts as the default, and project-specific overrides can be used when a different GitHub identity should own the repository relationship or provide GitHub Copilot entitlement for that project.

Linking an additional account always opens GitHub's account picker, so you can choose a different GitHub identity even when you are already signed in to github.com in that browser. The linked account marked as **default** is your *active* account: Agentweaver uses its token for repository access, GitHub Copilot entitlement, and agent sessions. Switching the default in **Settings → GitHub accounts** switches the token everything uses.

Project roles in Agentweaver do **not** translate to GitHub permissions. Being a project `Owner`, `Contributor`, or `Viewer` only affects what you can do inside Agentweaver. Repository clone/push/PR/admin rights come solely from the resolved linked GitHub account's real permission on GitHub.

You can also type `owner/repo` manually in the repository field without connecting GitHub — useful if you know the repository name and don't need the browse-and-search experience.

## AI provider credentials

GitHub Copilot credentials come from the GitHub account Agentweaver is using for that project — no extra key is needed.

**Microsoft Foundry** uses separate credentials configured at the installation level (not per-user). Contact your administrator if Foundry is unavailable as a provider option.

::: warning Foundry credentials are not tied to GitHub sign-in
Signing in with Entra or GitHub does not authorize Microsoft Foundry. Foundry credentials are configured separately, server-side, and shared across all projects.
:::

## Signing out

To sign out, open the **Settings** page (accessible from the top navigation) and click **Sign out**.

After signing out:
- Your server-side session is invalidated
- The browser cookie is cleared
- You are redirected to the sign-in page

Any in-flight runs continue to completion on the server — signing out does not interrupt running agents.

## Authentication errors

If you see an error on the sign-in page (e.g., "Authentication failed"), common causes are:

| Error | Likely cause |
|---|---|
| `entra_role_required` | Your Entra account is signed in but does not have an allowed platform App Role |
| `org_membership_required` | Your GitHub account is not a member of the required organization |
| `token_exchange_failed` | The OAuth callback was interrupted; try signing in again |
| `session_expired` | Your session timed out; sign in again to continue |

If the error persists, contact your Agentweaver administrator.
