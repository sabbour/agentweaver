# Operate health, heartbeat, and cluster status

**Issue:** [#31](https://github.com/sabbour/agentweaver/issues/31)  
**Area:** Observability & operations

## User story

As a platform operator, I want to inspect system health, background automation, and cluster capacity, so that I can diagnose why work is stuck before users lose trust in automation.

## Context / problem

Agentweaver includes operational views for health checks, heartbeat pickup, active flow, and cluster sandbox capacity. These surfaces show real state rather than hiding warnings.

## Scope

### In
- global and project diagnostics
- heartbeat status and recent automation activity
- Flow view of agent activity
- cluster diagnostics for pods, quota, and component health
- manual refresh and auto-refresh controls

### Out
- editing deployment secrets from the UI
- full Kubernetes administration
- hidden telemetry not exposed in product surfaces

## Acceptance criteria

- [ ] Diagnostics show pass/warn/fail checks with details and timings.
- [ ] Heartbeat shows whether automation is enabled, ticking, disabled, or waiting for the first tick.
- [ ] Recent activity reports automation actions and errors.
- [ ] Flow and board views make active, queued, blocked, and done work visible.
- [ ] Cluster diagnostics show capacity and pod health when available.

## Notable edge cases

- Non-cluster deployments explain that cluster diagnostics are unavailable.
- Empty heartbeat history is distinguishable from failure.
- A healthy API can still report project-specific or capacity-specific warnings.
