---
title: Authentication
---

# Authentication

Agentweaver uses Microsoft Entra ID for browser sign-in and platform authorization.
Sign in with your organization's Entra account; platform App Roles and project
assignments determine the Agentweaver actions you may perform.

GitHub access is capability-specific. The Repo App authorizes repository discovery
and project creation, while the Copilot App authorizes Copilot-backed work. Each
authorization is an explicit, scoped browser handoff and does not replace your
Entra identity or grant project access.

## Signing in

When you open Agentweaver for the first time (or after your session expires), use
**Sign in with Microsoft Entra ID**. Browser sign-in uses authorization code with
PKCE. Configure the Entra application with the callback URL for your environment.

## GitHub capabilities

When creating a project from GitHub, authorize the Repo App and select a repository
from its bounded list. Agentweaver mints a short-lived selection code and verifies it
again server-side before creating the project.

When a project requires Copilot, authorize the Copilot App from its project-scoped
handoff. The browser handoff binds to your Entra session and returns only safe
completion status to the application.

Project roles in Agentweaver do not translate to GitHub permissions. Repository and
Copilot capabilities are granted only through their respective GitHub Apps.

## Signing out and errors

To sign out, use **Settings**. This invalidates the server-side session and clears
the browser cookie; in-flight runs continue on the server.

If sign-in fails, verify that your Entra account has an allowed platform App Role
and that the Entra application redirect URI matches the deployed Agentweaver URL.
