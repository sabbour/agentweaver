---
title: Authentication
---

# Authentication

Agentweaver uses GitHub OAuth to authenticate users. A single sign-in grants both repository access and authorization to use the GitHub Copilot AI provider.

## Signing in

When you open Agentweaver for the first time (or after your session expires), you'll see the sign-in page.

![Sign in page](/guide/images/sign-in.png)

Click **Sign in with GitHub**. You'll be redirected to GitHub to authorize the application. Once you authorize, GitHub redirects you back and your session is established.

::: tip One sign-in for everything
The GitHub sign-in grants both:
- **Repository access** — including private repositories, used when creating projects from GitHub
- **Organization membership checks** — `read:org` scope lets Agentweaver verify you belong to the required org
- **GitHub Copilot access** — the `copilot` scope authorizes Agentweaver to use GitHub Copilot as the AI provider for your runs

You do not need a separate API key for GitHub Copilot after signing in.
:::

## Organization membership requirement

Depending on how Agentweaver is deployed in your organization, access may require membership in a specific GitHub organization or team. If you see an authorization error after signing in, contact your administrator to confirm your GitHub account has the required org membership.

Organization access is not the same as access to every project. Project, run, team, backlog, and memory actions are scoped to resources you own. Agentweaver does not include a built-in superuser GitHub username; a user named `admin` has the same ownership rules as any other user.

## How sessions work

Agentweaver uses server-side sessions backed by your GitHub OAuth token. After a successful sign-in:

- Your session is stored on the server and identified by a secure browser cookie
- The cookie is `HttpOnly` and `SameSite` — it is never accessible to JavaScript
- Your GitHub OAuth token is stored server-side in your own Key Vault-backed user scope (`ghtok-user--{base32(userId)}`) and is never written to shared storage
- You remain signed in until you explicitly sign out or the session expires on the server

No GitHub tokens are stored in `localStorage`, `sessionStorage`, or any other browser-accessible location.

## Connecting GitHub for repository access

When creating a project from GitHub, Agentweaver lists your repositories. If your GitHub account is not yet connected (or the token has been revoked), the repository picker shows a **Connect GitHub** prompt.

Click **Connect GitHub** to start the authorization flow. After authorizing, the dialog reloads and your repositories appear.

You can also type `owner/repo` manually in the repository field without connecting GitHub — useful if you know the repository name and don't need the browse-and-search experience.

## AI provider credentials

GitHub Copilot credentials come from your GitHub sign-in — no extra key is needed.

**Microsoft Foundry** uses separate credentials configured at the installation level (not per-user). Contact your administrator if Foundry is unavailable as a provider option.

::: warning Foundry credentials are not tied to GitHub sign-in
Signing in with GitHub does not authorize Microsoft Foundry. Foundry credentials are configured separately, server-side, and shared across all projects.
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
| `org_membership_required` | Your GitHub account is not a member of the required organization |
| `token_exchange_failed` | The OAuth callback was interrupted; try signing in again |
| `session_expired` | Your session timed out; sign in again to continue |

If the error persists, contact your Agentweaver administrator.
