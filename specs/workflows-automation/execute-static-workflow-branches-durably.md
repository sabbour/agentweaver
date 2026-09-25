# Execute static workflow branches durably

**Issue:** [#1418](https://github.com/sabbour/agentweaver/issues/1418)  
**Area:** Workflows & automation

## User story

As a workflow author, I want independent agent steps to run as durable branches and rejoin once, so that parallel work survives retries and service restarts without duplicating child work.

## Context / problem

The workflow schema recognizes `fan_out` and `fan_in`, but the runtime intentionally leaves them unbound. Before those nodes can execute, Agentweaver needs durable parent-to-child correlation, ordered static branch records, continuation fencing, cancellation, and restart reconciliation.

## Scope

### In

- One durable child coordinator run and work plan per parent run and workflow node
- One ordered durable subtask per declared static branch
- Parent continuation persistence before dispatch
- Exactly-once resume preparation through existing pending-delivery fencing
- Restart reconciliation and parent-cancellation propagation
- Fail-closed validation for one non-nested, wait-all static fan region

### Out

- Runnable `fan_out` or `fan_in` executors
- Workflow generation or visual-editor exposure
- Dynamic branches, nested fans, quorum or partial joins
- Coordinator-composed work, integration merge, review, Scribe, or publication

## Acceptance criteria

- [ ] Retrying the same parent node reattaches to one child coordinator plan and does not duplicate branch subtasks.
- [ ] The parent continuation is durable before child dispatch can start.
- [ ] Terminal child work queues one ordered result and fenced delivery is retryable after a stale claim.
- [ ] A delivered result is not delivered again.
- [ ] Parent cancellation suppresses resume, prevents new dispatch, and requests cancellation of active child runs.
- [ ] Persisted parent workflow and node identity do not change if the source workflow is edited or deleted.
- [ ] SQLite and PostgreSQL enforce one correlated work plan per parent run and workflow node.
- [ ] Invalid first-release fan topology fails binder validation and workflow save.
- [ ] A valid static fan topology remains runtime-unbindable until the executor PR lands.

## Notable edge cases

- A crash after plan persistence but before continuation arming leaves the plan non-dispatchable and safe to reattach.
- A child plan that becomes terminal before resume preparation is reconciled into the same pending delivery.
- Stale `delivering` claims can be reclaimed, while `delivered` rows are no-ops.
- Branch order comes from persisted ordinals, not database-generated subtask ids or edited workflow YAML.
