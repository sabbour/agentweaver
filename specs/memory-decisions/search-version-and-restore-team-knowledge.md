# Search, version, and restore team knowledge

**Issue:** [#1400](https://github.com/sabbour/agentweaver/issues/1400)
**Area:** Memory & decisions

## User story

As a project operator, I want team memory and decisions to be searchable, versioned, and
restorable, so I can find relevant knowledge, understand exactly what informed a run, and
recover an earlier state without erasing audit history.

## Context / problem

Memory and decisions retain provenance and approval state, but mutable rows previously
overwrote earlier content. Concurrent editors could replace one another, inactive memory
was not modeled explicitly, and search did not inspect text.

## Scope

### In
- immutable memory and decision revisions
- expected-revision writes and clear stale-write conflicts
- active, superseded, and archived memory lifecycle states
- same-project, acyclic replacement links
- restore as a new pending revision
- bounded text search with deterministic pagination and existing filters
- exact revision references in compiled-context telemetry
- REST, MCP, native in-run tools, and Memories UI history, comparison, and retrieval

### Out
- semantic or vector search
- autonomous forgetting or learning
- executable workflow pins
- permission-binding revisions
- sending complete history to child agents

## Acceptance criteria

- [ ] Every content, provenance, approval, and lifecycle change appends an immutable revision.
- [ ] Updates require the current revision and stale writes return a conflict with the latest revision.
- [ ] Restoring an earlier snapshot creates a new pending revision and leaves history unchanged.
- [ ] Default retrieval and prompt compilation exclude superseded and archived memory.
- [ ] Replacement links stay inside one project and cannot form cycles.
- [ ] Text search is bounded, deterministic, paginated, and composes with type and tag filters.
- [ ] Authorized REST, MCP, native-tool, and UI callers can inspect revisions and later pages.
- [ ] REST, MCP, and UI callers can compare, retrieve, and restore revisions.
- [ ] Revision responses redact secrets and expose fingerprints instead of raw source identities.
- [ ] Compiled context and its composition event identify every selected stable record and exact revision.

## Notable edge cases

- A replay with an obsolete expected revision cannot create another revision.
- Restoring a superseded snapshot makes a new active revision rather than reviving the old row in place.
- Reused agent display names do not change the stable record id or historical revision chain.
- A missing record or revision returns not found without crossing project authorization boundaries.
- Legacy rows receive a revision-one snapshot during SQLite and PostgreSQL migration.
