# Validate deployed context budgets under deterministic pressure

**Issue:** [#1501](https://github.com/sabbour/agentweaver/issues/1501)
**Area:** Runtime resilience

## User story

As a staging operator, I want a safe deterministic acceptance profile for deployed
memory-context limits, so that I can prove item omission, token-budget omission, and
mandatory-decision failure without adding a production request bypass.

## Context / problem

Compiler tests cover context limits, but post-deployment validation could not reliably
force those outcomes through the REST surface. A public header, query parameter, run
option, or project setting would create an unsafe production bypass.

## Scope

### In

- opt-in non-production API-harness command
- existing deployment-scoped `MemoryContext__MaxItems` and
  `MemoryContext__MaxTokens` configuration
- exact API and worker deployment snapshot, bounded mutation, rollout/readback, and
  restoration
- three fresh deterministic datasets
- cancellation and failure-safe cleanup

### Out

- production fixture or per-request budget override
- feature-flag service or administrative endpoint
- compiler abstraction or changed runtime precedence
- live-deployment mutation during implementation or unit testing

## Acceptance criteria

- [ ] Production defaults remain 20 items and approximately 4,000 tokens.
- [ ] Existing positive call-site overrides still precede deployment configuration.
- [ ] The profile requires `isRelease=false` and rejects a target that is not bound to
      the exact Kubernetes context, namespace, and API HTTPRoute.
- [ ] API and worker templates are locked, snapshotted independently, patched with
      bounded values, observed at a new generation, rolled out, and read back.
- [ ] Fresh datasets prove `item_limit`, token-budget `budget` omission, and
      `mandatory_context_budget_exceeded`.
- [ ] Setup, scenario, cleanup, and signal cancellation all unwind through one awaited
      restoration path.
- [ ] Restoration removes variables that were absent and preserves exact prior
      `value`/`valueFrom` structures; any mismatch fails.

## Notable edge cases

- One deployment patches successfully and the other fails.
- A concurrent profile or deployment mutation attempts to change the same templates.
- A signal arrives during HTTP polling or a Kubernetes rollout.
- Scenario failure and cleanup failure occur together.
- A prior variable is absent on one deployment and sourced through `valueFrom` on the
  other.
