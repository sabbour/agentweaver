---
title: Authentication
---

# Authentication

Agentweaver uses Microsoft Entra ID for browser sign-in and platform authorization.
Sign in with your organization account. Entra App Roles and project assignments control your Agentweaver actions.

GitHub access is separate from sign-in. The Copilot App provides AI access. The Repo App provides repository access.

## Signing in

When your session expires, select **Sign in with Microsoft Entra ID**. Browser sign-in uses an authorization code with PKCE.

If no platform role exists, ask a Platform Admin to assign one in Microsoft Entra ID. Then reload Agentweaver.

## Complete required setup

A model provider is the required activation milestone. Agentweaver blocks AI work until this setup is ready.

A Platform Admin can choose one active model provider:

- GitHub Copilot with a platform account
- A custom-key provider

The active provider applies to all users and projects. The completed setup status identifies the provider and its platform scope.

If you cannot manage this setup, Agentweaver shows **Unavailable to you**. Ask a Platform Admin to complete the setup.

## GitHub capabilities

Repository access is optional. Local agent work can continue without a GitHub repository.

Pull-request publishing and GitHub repository operations require repository access. Authorize the Repo App when you start one of these actions.

When you create a project from GitHub, authorize the Repo App. Then select a repository from the bounded list.

Agentweaver verifies the repository selection on the server. It does not accept an unverified repository identifier.

A project can use a project GitHub Copilot account. It can also inherit the platform GitHub Copilot account or custom-key provider.

Open **Project settings → Background** to see the effective model provider. The status identifies the provider and its project or platform scope.

GitHub authorization does not replace your Entra identity. It does not grant an Agentweaver platform role or project membership.

Project roles in Agentweaver do not translate to GitHub permissions. Repository and
Copilot capabilities are granted only through their respective GitHub Apps.

## Callback registration

For v0.23.1 and later, one exact Copilot App callback serves exactly two OAuth
completion flows: project-scoped and platform-default. The MCP browser handoff
enters the project-scoped flow; it is not a third completion flow.

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
