# Link executable backlog tasks safely

**Issue:** [#1398](https://github.com/sabbour/agentweaver/issues/1398)
**Area:** Work intake & board

## User story

As a project operator, I want dependent story runs to wait for accepted upstream
outcomes and to preview and edit the links safely, so a multi-story plan can run
independent branches in parallel without consuming unverified work.

## Context / problem

A blocked prefix must not hide ready work. Completed assembly can have a different
terminal status from a conventional merge. Automatically derived overlap edges
must never introduce a cycle.

## Scope

### In
- ordered, dependency-aware backlog pickup for SQLite and PostgreSQL
- atomic per-project graph revision and add/remove/replace edits with affected-task preview
- claim-time identities of accepted upstream runs and available commit/tree hashes
- shared backlog REST, MCP, and board projections of blockers and dependents

### Out
- cross-project links or a replacement graph store
- guaranteed retention of historical artifact bytes before #1396 finishes
- editing the inputs of already claimed work

## Acceptance criteria

- [ ] Prerequisite results distinguish integration, accepted no-change, failure, cancellation, and delegation.
- [ ] Ready selection filters unmet dependencies before its limit in both stores.
- [ ] Derived overlap edges are acyclic, even through transitive paths.
- [ ] Link edits reject invalid and cyclic graphs without partial writes; concurrent opposite edits cannot both win.
- [ ] Claimed tasks retain accepted prerequisite identities; web, REST, and MCP show the graph and waits.

## Notable edge cases

- Stale graph revisions require a fresh preview.
- Archived prerequisites preserve lineage while preventing new claims.
- A producer retry is not a success until a verified terminal outcome is recorded.
