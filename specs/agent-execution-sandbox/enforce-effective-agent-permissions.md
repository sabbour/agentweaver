# Enforce effective agent permissions in every backend

**Issue:** [#1397](https://github.com/sabbour/agentweaver/issues/1397)
**Area:** Agent execution & sandbox

## User story

As a platform operator, I want the effective permissions assigned to an agent run to be
enforced identically in local and AgentHost execution, so that changing the backend,
automatic approval, delegation, or recovery cannot increase the agent's authority.

## Context / problem

Agentweaver already applies sandbox containment to local execution, but AgentHost
previously replaced project policy with a permissive in-pod policy. API-backed tools also
bypassed sandbox governance. The launch path therefore had no versioned, attributable
permission contract tying the run to the policy enforced by the backend.

## Scope

### In

- one credential-free, versioned effective permission binding per run attempt
- equivalent operation-family checks for local and AgentHost execution
- current-policy refresh before AgentHost turns
- parent-to-child restriction inheritance
- denial provenance in tool errors, degraded-run events, logs, and the run REST API
- authorization-filtered REST, MCP, and web inspection of configured versus effective
  permissions, current revocation, enforcement coverage, and safe denial provenance
- explicit fail-closed behavior for missing, malformed, unsupported, mismatched, or
  unclassified permissions

### Out

- a full identity descriptor or identity-policy join
- external tool transport policy
- replacing filesystem, process, network, or Kata isolation
- granting new permissions from role names, charters, or instructions

## Acceptance criteria

- [x] Bindings identify their schema, binding, version, source, run, attempt, and scope.
- [x] Bindings contain policy data only and no credentials.
- [x] Local and AgentHost permission handlers allow the same permitted operation and deny
  the same restricted operation.
- [x] URL/network denial runs before automatic approval.
- [x] Unknown operations and invalid bindings fail closed.
- [x] Child permissions are the intersection of current child permissions and the
  parent's durable launch ceiling plus current restrictions.
- [x] A current narrower policy is intersected with the launch binding, so a stale
  snapshot cannot restore revoked authority.
- [x] AgentHost receives the binding at configure time and requires a refreshed binding
  before each turn, including operator-assistant MCP turns.
- [x] Denials include safe binding provenance in existing tool/degraded-run evidence.
- [x] Operators can read configured operation limits and a run's effective binding through
  the REST API.
- [x] The authorized REST, `sandbox_policy_get(run_id: ...)` MCP, and run-page UI
  surfaces expose the same credential-free projection.
- [x] Inspection distinguishes configured policy, effective narrowed policy, inherited
  or launch-ceiling overrides, current revocation, enforcement coverage, and the latest
  permission-denial reason.
- [x] Inspection omits credentials, commands, URLs, tool arguments, and arbitrary event
  payloads.

## Delivery

- #1600 delivered the versioned binding and equivalent fail-closed enforcement in local,
  AgentHost, and operator-assistant MCP execution.
- The final slice adds the shared authorized inspection projection and cross-surface
  REST/MCP/UI coverage without changing the merged enforcement semantics.

## Notable edge cases

- A policy may be narrowed during a warm-pod run. The next turn rebuilds the pod tool
  context from the narrower policy.
- A later policy widening does not widen an already-running execution; a new run is
  required to receive the broader authority.
- A malformed settings file is an explicit denial, not a permissive default.
- Empty `allowed_operations` preserves legacy profile behavior. A non-empty list is an
  explicit narrowing allow-list.
