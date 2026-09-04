---
title: Authentication
---

# Authentication

Agentweaver uses Microsoft Entra ID for browser sign-in and platform authorization.
Sign in with your organization account. Entra App Roles and project assignments control your Agentweaver actions.

The deployed OAuth issuer is pinned to the configured public HTTPS origin, and the MCP
resource is always that exact origin plus `/mcp`. It is not inferred from request
headers. Hosted signing and encryption keys come from the active and previous usable
versions of configured Azure Key Vault certificate families.

GitHub access is separate from sign-in. The Copilot App provides AI access. The Repo App provides repository access.

## Signing in

When your session expires, select **Sign in with Microsoft Entra ID**. Browser sign-in uses an authorization code with PKCE.

If no platform role exists, ask a Platform Admin to assign one in Microsoft Entra ID. Then reload Agentweaver.

## Complete required setup

A model provider is the required activation milestone. Agentweaver blocks AI work until this setup is ready.

A Platform Admin can choose one active model provider:

- GitHub Copilot with a platform account
- A custom-key provider

The Platform Admin can add more providers during required setup.
Only one provider is active at a time.

The active platform provider applies to interactive and unattended work for all users and
projects. A configured custom-key provider (BYOK) is therefore a complete platform provider,
not an interactive-only fallback.

If you cannot manage this setup, Agentweaver shows **Unavailable to you**. Ask a Platform Admin to complete the setup.

When the provider is ready, select **Continue to Agentweaver**.
Agentweaver opens the app shell and starts a short product tour.

The tour introduces **Projects**, **Sessions**, and **Start task**.
You can skip the tour or press Escape.

To start the tour again, open the settings menu.
Then select **Take product tour**.

## GitHub capabilities

Repository access is optional. Local agent work can continue without a GitHub repository.

Pull-request publishing and GitHub repository operations require repository access. Authorize the Repo App when you start one of these actions.

When you create a project from GitHub, authorize the Repo App. Then select a repository from the bounded list.

Agentweaver verifies the repository selection on the server. It does not accept an unverified repository identifier.

A project can use a project GitHub Copilot account. Otherwise, project work inherits the active
platform GitHub Copilot account or custom-key provider. This project hierarchy applies to
orchestration and background work.

Open **Project settings → Background** to see the effective model provider. The status identifies the provider and its project or platform scope.

Project and platform Copilot authorization creates a durable server-side binding. Agentweaver
does not borrow the token of whichever user is currently signed in, and the Copilot App has no
repository installation screen. Repository authorization remains a separate Repo App capability.
Personal session chat uses a separate account-level hierarchy:

1. An active platform custom-key provider applies automatically to every user.
2. Otherwise, the user can select a personal custom-key provider.
3. Otherwise, the user must authorize their own GitHub Copilot account.

Agentweaver does not use the platform-default Copilot credential for personal session chat because
Copilot entitlement belongs to the individual GitHub account. Open **Account settings → AI Access**
to authorize Copilot or add a personal provider. These settings do not change project background
execution.

A Copilot binding keeps the refresh token that GitHub returns with its access token. GitHub
access tokens expire after about eight hours. Agentweaver redeems the refresh token
automatically, shortly before the access token expires, and stores the new pair. You do not
reconnect after an expiry. Agentweaver asks you to connect again only when GitHub rejects the
refresh token, for example after you revoke the authorization.

GitHub authorization does not replace your Entra identity. It does not grant an Agentweaver platform role or project membership.

Project roles in Agentweaver do not translate to GitHub permissions. Repository and
Copilot capabilities are granted only through their respective GitHub Apps.

## Callback registration

One exact Copilot App callback serves project-scoped, platform-default, and personal-user OAuth
completion flows. Persisted one-time state selects the correct flow. The MCP browser handoff enters
the project-scoped flow.

```
https://<public-host>/auth/github/copilot-app/callback
```

Register it with wildcard matching disabled. GitHub currently allows up to 10
callback URLs. Apps created before 2026-08-03 with one callback URL may have
wildcard matching enabled by default; explicitly inspect and disable it for
exact matching. Repo App authorization uses the separate
`/auth/github/repo-app/callback`, while Entra sign-in uses
`/auth/entra/callback`; each belongs on its corresponding application. A
wildcard for one callback path does not match a sibling path.

When migrating a Copilot App client ID shared by older deployments, add the new
exact URL first. Keep the old exact URL temporarily, inventory deployment
versions and the shared client ID, upgrade everything to v0.23.1 or later, then
wait 15 minutes after the last older deployment stops before removing the old
URL. On deployed staging, verify all three entry points: project-scoped, MCP
browser handoff into the project-scoped flow, and platform-default. Local
end-to-end testing may be impossible when Entra redirects are deployment-only,
so deployed staging is the end-to-end proof. See
[Configuration](configuration.md#project-copilot-app-binding) for the full
operator sequence.

## Signing out and errors

To sign out, open the account menu and select **Sign out**. In-flight runs continue on the server.

If sign-in fails, make sure that your Entra account has an Agentweaver App Role.

If the redirect fails, make sure that the Entra redirect URI matches the deployed Agentweaver URL.
