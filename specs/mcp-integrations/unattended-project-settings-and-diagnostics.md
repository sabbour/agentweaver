# Review unattended project settings and diagnostics

**Issue:** [#946](https://github.com/sabbour/agentweaver/issues/946)
**Area:** MCP & integrations

## User story

As a project owner, I want a safe, plain-English view of unattended automation readiness so that
I can resolve missing prerequisites without receiving credentials or provider internals.

## Context / problem

Unattended work requires a project-bound Copilot App identity and a Repo App installation and
repository grant. These prerequisites must remain server-derived and must never become a path for
revealing or selecting credentials.

## Scope

### In

- live Copilot App registration validation at startup and readiness checks
- a project-scoped, redacted REST readiness status and web settings view
- removal of legacy per-project GitHub identity and webhook settings controls

### Out

- automation activation or consent
- caller-selected installation, repository, identity, display-name, or permission inputs
- credential, token, App JWT, PEM, provider-error, or repository-content disclosure

## Acceptance criteria

- [ ] The API fails closed when the live Copilot App registration reports permissions.
- [ ] A Project Owner can read a plain-English readiness status with a fixed reason code only.
- [ ] The readiness response includes no identifiers, names, permission maps, credentials, or
      arbitrary provider failures.
- [ ] The Web UI has no unattended activation control or activation-record write.
- [ ] Project Settings exposes no legacy per-project GitHub identity, webhook provisioning, or
      webhook-secret controls.

## Notable edge cases

- An unavailable, malformed, or incomplete App registration is reported as a fixed not-ready
  reason and prevents new Copilot App binding.
- A platform administrator receives no new binding or replacement authority; disconnect remains
  the existing human de-privileging path.
