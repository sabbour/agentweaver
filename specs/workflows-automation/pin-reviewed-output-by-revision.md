# Pin reviewed output by immutable revision

**Issue:** [#1396](https://github.com/sabbour/agentweaver/issues/1396)

**Area:** Workflows & automation

## User story

As a reviewer, I want to retrieve the exact bytes of the output I reviewed even
after revisions, retries, branch movement, and workspace cleanup, so that
approval and publication cannot silently refer to a different result.

## Context / problem

The executable workflow definition is already pinned on root runs by
[#1588](https://github.com/sabbour/agentweaver/pull/1588). The separate output
contract is not yet implemented: `Run.Diff` and the review-ready tree hash are
mutable, `run_revisions` is an audit of feedback cycles rather than an output
blob store, and the merged commit SHA is only populated after publication.
The worktree and Git branch may change or disappear during review.

### Existing identity inventory

| Input or output | Current binding | Remaining boundary |
|---|---|---|
| Executable workflow | Persisted YAML, definition id/source/version, schema version and SHA-256 digest before root execution (#1588) | Reuse unchanged; link its digest to the consumed output manifest |
| Review graph | Run-scoped graph/events and pending request/checkpoint | Bind the effective review decision to an output revision; do not confuse graph display with executable YAML |
| Blueprint/team, agent charter | Launch selection, team assignment and `Run.AgentCharter` | Record consumed revisions only where the actual runtime reads mutable inputs |
| Skills/resources | Currently resolved at execution | Inventory consumed content before promising reproducible replay; avoid credential copies |
| Capability/approval policy | Purpose-bound launch and execution-identity snapshots; current access and safety checks | Preserve current revocation and restrictive policies; snapshots confer no authority |
| Repository source | Originating/worktree branch and review tree hash; merged commit SHA after merge | Pin the exact consumed base and review-ready tree before accepting approval |
| Knowledge | Separate versioned memory work (#1400) | Out of scope |
| Produced files and assembly | Mutable worktree/branch and `Run.Diff`; terminal merge commit | Store review-ready bytes independently of worktree/ref lifecycle and bind approval/publication to that revision |

Application release, manifest schema, selected configuration, content digest,
Git commit and lifecycle attempt are different identities. A display-name change
does not change an output digest.

## Scope

### In

- Durable content-backed review-ready output revisions for ordinary and collective runs
- Producer attempt, consumed input manifest, predecessor/supersession and integration lineage
- Revision-fenced review and transactional/idempotent publication across direct,
  queued, resumed, commit and collective-assembly paths
- Current-authorization-gated REST/MCP/UI history, comparison and exact retrieval
- Explicit legacy compatibility and a documented content-retention policy

### Out

- Knowledge/memory revisions (#1400), new grant semantics (#1397), or duplicated workflow pins
- Bit-for-bit LLM replay or credentials embedded in input manifests

## Acceptance criteria

- [x] Merged runs with a recorded commit serve file content and workspace listings
  from that commit after source branch movement and worktree removal. A missing
  pinned commit reports `410`; an absent file reports `404`; current run access
  still applies. This is a **committed-output slice**, not review-ready revisioning.
- [x] Root executable workflow pins survive mutation/deletion between checkpoint
  and resume and reject incompatible manifest schemas (#1588).
- [ ] An immutable review-ready output revision contains exact stored bytes, a
  digest and stable ID, schema version, producer run/attempt, consumed manifest,
  predecessor and integration lineage. Duplicate publication is idempotent;
  cancelled/superseded attempts cannot publish.
- [ ] An approval names the exact revision and becomes stale after any output
  change, including uncommitted edits. All merge/commit/assembly paths reject
  unfenced approval, including deferred delivery after restart.
- [ ] Historical bytes survive request-changes, retry, branch movement, restart
  and workspace cleanup for a declared retention period. Missing/expired
  content and unsupported schemas fail explicitly.
- [ ] Authenticated REST/MCP/UI expose shared revision history, comparison and
  exact-revision retrieval; legacy runs with incomplete manifests have explicit,
  non-misleading compatibility behavior.

## Dependency-aware delivery plan

1. **Content foundation:** define a versioned manifest and content-addressed
   blob store with transactional metadata/bytes, retention and integrity checks
   for both SQLite and PostgreSQL; migrate SQLite-to-PostgreSQL with verified
   blobs. Derive a stable attempt/revision ID from a persisted publication
   intent, not a mutable branch name. Backfill only *verifiably available* legacy
   content; mark incomplete manifests rather than inventing a digest.
2. **Producer and publication fence (depends on 1):** capture review-ready
   ordinary output and collective assembly bytes before displaying a review
   gate. Enforce expected lifecycle generation, pending request, output digest,
   and branch/tree identity in an atomic publication CAS. Reconcile an
   interrupted intent before acknowledging success; retries with identical
   identity+bytes reuse the committed receipt, mismatches fail.
3. **Approval binding (depends on 2):** carry the revision ID through REST
   `/review`, MCP, web, durable pending delivery, resumed and direct merge,
   `/commit`, and `/assembly/review`. Re-check revision and tree under the merge
   slot. Reject changes after approval rather than silently approving the new
   branch; persist approved revision on the terminal receipt.
4. **Reader contracts (depends on 1-3):** shared DTOs for history, comparison
   and exact bytes, same project/run authorization on each endpoint and
   corresponding MCP/web views. Preserve a legacy read-only path with a clear
   `manifest_incomplete` state; never label branch-following data immutable.
   Exercise stale approval, retry, cleanup, expiry, schema incompatibility,
   revocation, duplicate/interrupted publication and cross-surface parity.

**Delivery owner:** Agentweaver maintainers. **Target:** v0.34.0. The
content foundation and merge fence must ship together with approval binding
before any new public revision contract is advertised; a diff-only endpoint
without `/commit` and deferred-review fences would be unsafe.

## Notable edge cases

- `run_revisions` numbers feedback cycles; they are not artifact revision IDs.
- A valid SHA on a merged run may become unreachable after a force rewrite and
  garbage collection. The committed-output slice returns `410` rather than
  substituting the new branch; it does not establish a retention guarantee.
- The currently accessible output is not necessarily approved output. Never
  infer approval from `Run.Diff`, a branch tip, or a tree hash alone.
