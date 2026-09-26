# Inspect execution identity and decision lineage

**Issue:** [#1404](https://github.com/sabbour/agentweaver/issues/1404)
**Area:** Observability & operations

## User story

As an operator, I want one safe, durable record that identifies who initiated an
execution, which agent and backend performed it, which permission binding applied,
and which tool or approval decisions followed, so that I can audit a run without
joining sensitive internal data by hand.

## Scope

### In

- one immutable execution descriptor for every run lifecycle attempt
- atomic creation with the run launch or lifecycle transition
- parent delegation and retry/replacement lineage
- agent assignment, workflow pin, approval-policy, backend, and sandbox evidence
- the current effective permission binding and safe tool/gate decision summaries
- authorization-filtered REST, MCP, terminal-diagnostic, and run-page projections
- explicit partial and legacy-missing evidence states

### Out

- changing or replacing effective-permission enforcement
- restoring authority from a historical binding
- complete failure-cause explanation
- addressed coordinator messaging
- exposing prompts, command arguments, credentials, tokens, repository roots, or
  raw event payloads

## Acceptance criteria

- [x] A descriptor is inserted in the same transaction as a new run.
- [x] A new immutable descriptor is inserted for each retry/recovery attempt.
- [x] Root, delegated child, and replacement runs preserve explicit lineage.
- [x] Local and AgentHost backends report distinct backend evidence without exposing
  pod, namespace, claim, or filesystem identities.
- [x] Permission inspection resolves current policy and cannot revive a revoked grant.
- [x] Tool results, errors, approvals, and permission denials correlate through safe
  identifiers; missing correlation is labeled rather than guessed.
- [x] Legacy runs without descriptors return `missing_legacy_descriptor`.
- [x] Unauthorized callers receive no cross-project identity evidence.
- [x] REST, MCP, terminal diagnostics, and the run UI expose the same safe identity.

## Contract

The immutable descriptor contains stable identifiers and provenance captured at
launch. Mutable authorization state is joined only when the descriptor is read.
This distinction is deliberate: the descriptor explains which permission version
was observed, but it is never an authorization token or a frozen grant.

`GET /api/runs/{run_id}/execution-identity` and MCP
`run_execution_identity(run_id)` return:

- `evidence_state`: `complete`, `partial`, or `missing_legacy_descriptor`
- a descriptor summary with opaque principal and assignment references
- backend kind and an opaque sandbox reference when sandbox evidence exists
- the currently effective permission binding
- bounded, sanitized decision summaries linked to safe tool-call identifiers

The run page presents this projection in the **Execution identity** panel. Terminal
diagnostics include the descriptor ID and identity evidence state so a failure can
be correlated without copying raw execution payloads.

## Notable edge cases

- A legacy run may have events and a terminal diagnostic but no descriptor.
- A remote backend can be known while its sandbox identity evidence is missing.
- A policy can be narrowed after launch; inspection reports current effective
  authority and does not use the historical record to widen it.
- A decision event without a valid matching tool-call ID remains visible with
  `correlation_state: "missing_tool_call"`.
