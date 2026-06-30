# Authorize MCP clients to act for a user

**Issue:** [#3](https://github.com/sabbour/agentweaver/issues/3)  
**Area:** Identity & access

## User story

As a developer using an MCP-capable assistant, I want to connect the assistant to Agentweaver with a standards-based bearer flow, so that the assistant can operate projects and runs on my behalf without sharing my GitHub secret directly.

## Context / problem

Agentweaver is useful from both the browser and assistant clients. Hosted MCP clients need a discoverable authorization path, while local clients need a practical authenticated connection.

## Scope

### In
- local and hosted MCP connection experiences
- OAuth-style discovery and consent for hosted clients
- bearer-token validation before tool use
- forwarding the caller identity to Agentweaver actions

### Out
- client-specific setup wizards
- unrestricted anonymous MCP access
- tool behavior unrelated to authentication

## Acceptance criteria

- [ ] A hosted MCP client can discover how to authenticate when it calls without a bearer token.
- [ ] A user can complete a browser consent flow and return to the MCP client.
- [ ] Authenticated MCP tool calls operate as the resolved caller, not as an anonymous service account.
- [ ] Invalid or missing bearer tokens are rejected before protected tools run.
- [ ] Health and discovery endpoints remain reachable without granting product access.

## Notable edge cases

- Native loopback clients can complete auth without a fixed port.
- Rejected or malformed redirect requests do not issue tokens.
- Legacy token paths, when enabled, still resolve to a concrete user identity.
