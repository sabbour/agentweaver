# Orchestration

## The mental model

The Orchestrator turns confirmed intent into controlled execution. Its HTTP host
owns durable state, including Microsoft Agent Framework (MAF) execution
checkpoints, and calls other owners. Its Core library validates the domain rules
without owning the database or starting an agent.

Keep three questions separate:

1. **What work is authorized?** Typed outcome, workflow, and WorkPlan decisions
   establish the confirmed scope.
2. **What work is ready?** The planner combines dependencies, checkpoint progress,
   joined results, and capacity limits.
3. **What execution is current?** Owner fences, runtime registrations, placement,
   and action grants identify the active attempt.

A model can propose a decision. It cannot substitute a text reply for a typed
approval, create its own resource pin, or mark a platform gate complete.

## Where this lives

- `services/orchestrator/Agentweaver.Orchestrator/Program.cs` — Host composition,
  database verification, authentication, and endpoint registration.
- `services/orchestrator/Agentweaver.Orchestrator/CoordinationEndpoints.cs` —
  Session, decision, gate, message, and turn APIs.
- `services/orchestrator/Agentweaver.Orchestrator/CoordinationOwnerStore.cs` —
  Accepted roots, session tree, fences, turn state, and coordination outbox.
- `services/orchestrator/Agentweaver.Orchestrator/CoordinatorDecisionOwnerStore.cs` —
  Atomic decision, gate, grant, and outbox writes.
- `services/orchestrator/Agentweaver.Orchestrator/CoordinatorRunSelectionContextStore.cs` —
  Run-bound role and Sandbox context.
- `services/orchestrator/Agentweaver.Orchestrator/MafExecutionPlanner.cs` —
  Deterministic associations and ready-frontier calculation.
- `services/orchestrator/Agentweaver.Orchestrator/MafExecutionDispatchEndpoints.cs` —
  Explicit dispatch, durable intents, host sends, and result joining.
- `services/orchestrator/Agentweaver.Orchestrator/MafExecutionCheckpointStore.cs`
  and `PostgresMafCheckpointStore.cs` — MAF checkpoint binding and persistence.
- `services/orchestrator/Agentweaver.Orchestrator/SessionSuspendResumeCoordinator.cs` —
  Ordered suspend and resume phases.
- `services/orchestrator/Agentweaver.Orchestrator/RuntimeRegistrationOwner.cs`
  and `RuntimeUsageSourceOwner.cs` — Current runtime identity and SDK evidence.
- `services/orchestrator/Agentweaver.Orchestrator.Core/WorkflowContracts.cs` —
  Workflow steps and WorkPlan types.
- `services/orchestrator/Agentweaver.Orchestrator.Core/WorkflowDefinitionValidator.cs`,
  `WorkPlanValidator.cs`, and `CoordinatorDecisionContracts.cs` — Validation and
  typed decision transitions.
- `services/orchestrator/Agentweaver.Orchestrator.Core/ExecutableActionGuard.cs` —
  Current-grant, policy, and durable-receipt guard.
- `services/orchestrator/Agentweaver.Orchestrator.Core/BacklogReadinessEvaluator.cs`
  and `WorkflowScheduleEvaluator.cs` — Pure prerequisite and schedule evaluation.

## Core concepts and invariants

### A workflow is a versioned step catalog

Each `WorkflowDefinition` carries an ID, revision, catalog version, item bound,
and ordered steps. The step mode defines who creates its work:

| Mode | Meaning |
| --- | --- |
| `Fixed` | The workflow prescribes the task, role, phase, isolation, and outputs. |
| `Open` | The coordinator proposes bounded WorkPlan items for this step. |
| `Platform` | A named platform executor owns the gate. Ordinary agent work cannot replace it. |

The gate enum describes the domain vocabulary, not an executor-availability
promise. Dispatch reports steps without an available executor.

### Validation binds the entire plan

`WorkPlanValidator` checks the exact workflow revision, step identity,
cardinality, role, selected agent and model, isolation choice, capabilities,
output paths, and dependency graph. Model-proposed items belong only to open
steps.

Successful validation returns a snapshot. Failed validation returns structured
issues and no snapshot. For conflicting proposed outputs, the validator adds
deterministic dependency edges so the writes do not race.

Non-empty work, fixed work, and command-backed BuildTest steps require the
appropriate accepted isolation context. The owner resolves an existing resource
through a registered Sandbox adapter. It does not invent or provision a resource
at this boundary.

### Confirmation is state, not prose

`CoordinatorDecisionState` separates candidate and confirmed plans. Pending gates
prevent decomposition or dispatch as defined by the transition rules. Gate
answers bind the request ID, actor, and fence. Exact answer replay returns the
saved result.

Acknowledging a message records receipt. It does not approve the outcome,
confirm a workflow, or complete work.

## From intent to a confirmed plan

1. **Accept the root.** Orchestrator reads the accepted Projects selection and
   current authority, then records the root session and initial decision state.
   Configured Hosted Copilot credit limits also use Events run admission.
2. **Propose the outcome.** Core validates the typed outcome specification.
   Clarifying questions and outcome confirmation become explicit gate state.
3. **Select the workflow.** Selection uses the authorized workflow catalog.
   Generated definitions require first-use confirmation.
4. **Propose the WorkPlan.** Core validates the full plan against the selected
   workflow and run context. The owner persists the candidate and its
   confirmation gate.
5. **Confirm or revise.** An accepted gate answer makes the plan dispatchable.
   Scope-changing revisions introduce another confirmation boundary. Accepted
   revisions consume the configured revision-attempt allowance.

`CoordinatorDecisionOwnerStore` serializes transitions against current owner
state. A winning transaction commits the decision version together with its gate,
action grants, and decision outbox. The owner rechecks authority around waits and
before committing protected writes.

The owner routes are rooted at
`/api/projects/{projectId}/runs/{runId}/coordination`. Gateway exposes the
versioned public routes. Use the [reference](../reference/contracts.md) for
request shapes rather than constructing them from this lifecycle.

## Dispatch and join

An explicit `actions/dispatch` request supplies the expected decision-state
version and WorkPlan ID. The handler rejects a changed or unconfirmed plan before
dispatch.

The handler then:

1. Restores the bound MAF checkpoint and validates the plan again.
2. Reconciles child lifecycle records with saved dispatch intents and joined
   results.
3. Computes the ready frontier from step and item dependencies.
4. Applies child, concurrency, wall-time, and runtime admission limits.
5. Commits child records, stable dispatch identities, pending intents, and
   checkpoint progress before sending work.
6. Rechecks authority, runtime registration, and host readiness, then sends the
   ready batch.
7. Validates each host response against its native context and persists the
   joined result.

Fixed work gets an immutable activation association. Open work uses its confirmed
WorkPlan item. Both resolve to deterministic child-session IDs. Dispatch messages
also have stable IDs, so retry does not create a new logical send.

Later explicit dispatch requests reconcile saved progress and expose newly ready
dependencies. This is request-driven execution, not an automatic scheduler.
Command-backed BuildTest delegates resource work to Environment while Orchestrator
retains the accepted command and checkpoint. Source Control output and merge
evidence use their separate owner paths.

## Policy and runtime authority

Core's `ExecutableActionGuard` checks a current owner-issued grant before invoking
its protected callback. It evaluates the selected policy, stores source evidence,
waits for the durable Events policy acknowledgment, and rechecks current authority
before the effect. A policy result is evidence, not a grant.

Runtime registration adds a separate boundary. Orchestrator compares the accepted
selection, active session and work association, execution fence, and Environment
placement before returning current runtime context. An earlier registration
record alone does not authorize a turn.

## Recovery and failure behavior

- A stale decision version or execution fence produces a conflict instead of
  applying work to a newer attempt.
- Missing Sandbox negotiation or unavailable runtime prerequisites block
  dispatch; they do not select a synthetic resource.
- A pending dispatch intent keeps its identity across retries. Missing or
  uncertain result evidence is not success.
- Owner-reported failure or indeterminate execution advances the run fence.
  Explicit recovery checks the expected fence and run version before advancing
  it again and restoring eligible logical turns.
- Suspend and resume use a persisted phase machine. Suspend orders fencing,
  draining, checkpointing, journal and workspace flushes, manifest commit, and
  placement release. Resume orders manifest validation, environment readiness,
  egress verification, fence advancement, and dispatch. A retry cannot skip a
  required phase.

A workflow checkpoint and native SDK cache are different recovery inputs.
Restoring conversation context does not replay external effects.

## Related rules

The Core backlog evaluator checks exact graph and task revisions against
owner-projected prerequisite execution and output evidence. A dependency label
alone does not make a task ready.

The schedule evaluator computes daily, weekly, and monthly occurrences in UTC.
It is a pure function, not a hosted scheduling loop.

## Continue reading

- [Events and sessions](./events-sessions.md) explains journal and message
  ownership.
- [Agent runtime](./agent-runtime.md) explains bootstrap and guarded SDK work.
- [Environment and sandbox](./environment-sandbox.md) and
  [source control](./source-control.md) explain resource and repository effects.
- [Contracts and endpoints](../reference/contracts.md) records the API surface.
- The [orchestration design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/orchestration.md)
  records the broader design and decisions.
