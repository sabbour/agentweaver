# Connect GitHub App capabilities without changing product sign-in

**Issue:** [#939](https://github.com/sabbour/agentweaver/issues/939)
**Area:** MCP & integrations

## User story

As an authenticated Agentweaver project owner, I want to connect explicitly scoped GitHub
capabilities through the Web, REST, or MCP surface, so that I can use repository and
Copilot features without turning GitHub into product sign-in or exposing credentials.

## Context / problem

GitHub capability authorization must be consistent across clients and safe for automation.
The production design uses separate Repo and Copilot Apps, while Microsoft Entra remains
the identity and authorization boundary for Agentweaver.

## Scope

### In

- explicit, single-use App authorization transactions and MCP browser handoff
- project-scoped Copilot binding and unattended repository configuration
- capability states shared by Web, REST, and MCP
- immutable execution identity snapshots and purpose-bound broker selection
- App-level webhook lifecycle routing

### Out

- GitHub as a product sign-in provider
- global or user-default unattended Copilot credentials
- generic token lookup, credential fallback, or credentials in model-controlled processes
- legacy OAuth/device-flow compatibility

## Acceptance criteria

- [ ] Credential mutation accepts only a human Entra subject through one shared predicate;
      an internal/shared API key cannot bind, replace, or disconnect a project Copilot
      credential.
- [ ] Authorization redemption requires the initiating subject, callback-cookie hash,
      unexpired single-use state, PKCE S256, and current Project Owner authorization.
- [ ] Each project has at most one active Copilot binding, while an explicit account may
      be independently bound to multiple projects.
- [ ] MCP polling belongs only to the initiating subject and reveals only safe lifecycle
      status.
- [ ] MCP browser handoff redemption requires the initiating user's authenticated Entra browser
      session and callback completion revalidates that same session; an opaque transaction ID or
      browser URL alone cannot mint a callback binding.
- [ ] Brokered, explicit purpose and snapshot selection is the only credential path.
- [ ] One App webhook verifies constrained raw deliveries and routes with installation ID
      plus canonical repository ID.
- [ ] No credential, App JWT, PEM, repository body, or raw provider error is exposed by a
      client, event, audit record, or model-controlled execution environment.

## Notable edge cases

- A callback replay, expired state, missing cookie, lost Owner role, or wrong subject fails
  closed and requires a new authorization.
- A repository rename or transfer cannot retarget a workflow because routing uses the
  immutable repository ID.
