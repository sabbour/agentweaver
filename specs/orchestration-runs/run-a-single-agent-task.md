# Run a single-agent task

**Issue:** [#14](https://github.com/sabbour/agentweaver/issues/14)  
**Area:** Orchestration & runs

## User story

As a developer, I want to submit a focused task to one agent in an isolated workspace, so that small changes can be produced, watched, reviewed, and merged with clear accountability.

## Context / problem

Not every task needs a coordinator fan-out. A single-agent run is the smallest Agentweaver unit of work with identity, workspace, events, review, and terminal outcome.

## Scope

### In
- direct task submission
- project, branch, model, and agent selection where available
- isolated run workspace
- live execution navigation
- terminal states including no-change, failed, review, declined, and merged

### Out
- manual editing by the user inside the run workspace
- remote pull request management as the primary review surface
- multi-agent decomposition

## Acceptance criteria

- [ ] Users can submit a valid task and reach the live run view.
- [ ] The run is bound to a project/repository context and originating branch.
- [ ] Agent changes are isolated until review and merge.
- [ ] The run reports clear state as it progresses and terminates.
- [ ] No-change results are surfaced as a distinct outcome instead of pretending work merged.

## Notable edge cases

- Invalid repository or branch inputs fail with actionable feedback.
- A failed run remains inspectable and can be retried as a new run.
- Run ownership is enforced for status, stream, review, and artifact access.
