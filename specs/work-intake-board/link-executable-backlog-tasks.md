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
- claim-time identities of accepted upstream runs and verified integrated commit/tree hashes
- shared backlog REST, MCP, and board projections of blockers and dependents

### Out
- cross-project links or a replacement graph store
- editing the inputs of already claimed work

## Acceptance criteria

- [x] Prerequisite results distinguish integration, accepted no-change, failure, cancellation, and delegation.
- [x] Ready selection filters unmet dependencies before its limit in both stores.
- [x] Derived overlap edges are acyclic, even through transitive paths.
- [x] Link edits reject invalid and cyclic graphs without partial writes; concurrent opposite edits cannot both win.
- [x] Claimed tasks retain accepted prerequisite run/generation and commit/tree identities; web, REST, and MCP show the graph and waits.
- [x] Exact-revision retained collective output and no-change receipts are available through #1396.

## Notable edge cases

- Stale graph revisions require a fresh preview.
- Archived prerequisites preserve lineage while preventing new claims.
- A producer retry is not a success until a verified terminal outcome is recorded.
- An integrated result without both commit and tree has the machine-readable
  `upstream_output_identity_unavailable` blocker. A commit/tree snapshot is not
  a durable output-content revision or retention guarantee.
