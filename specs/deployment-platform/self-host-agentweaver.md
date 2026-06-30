# Self-host Agentweaver locally or on AKS

**Issue:** [#37](https://github.com/sabbour/agentweaver/issues/37)  
**Area:** Deployment & platform

## User story

As a platform operator, I want to run Agentweaver locally for development or on AKS for shared usage, so that teams can control their own agent orchestration environment, storage, secrets, and sandbox posture.

## Context / problem

Agentweaver is self-hosted alpha software. Operators need install and deployment paths that bring up the web UI, API, MCP server, persistence, ingress, and sandbox execution in predictable ways.

## Scope

### In
- local development startup
- AKS deployment and redeployment
- persistent storage and secrets integration at the platform level
- public web/API/MCP routing where deployed
- documented operational boundaries for alpha use

### Out
- managed SaaS hosting by Agentweaver
- production hardening guarantees
- editing application specs during deployment

## Acceptance criteria

- [ ] Operators can start the core local components for development.
- [ ] Operators can deploy the core services and sandbox execution environment to AKS.
- [ ] Deployment exposes the web UI, API, and MCP surfaces consistently.
- [ ] Stateful resources preserve projects, runs, events, and memory across process restarts where configured.
- [ ] Operators can redeploy with a pinned image version.

## Notable edge cases

- Missing prerequisites fail early with actionable guidance.
- Optional cloud resources can be skipped only when already provided.
- Alpha limitations are visible so operators do not assume production support.
