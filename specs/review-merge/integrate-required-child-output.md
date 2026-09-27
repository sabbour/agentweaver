# Integrate required child output without hiding parallel conflicts

**Issue:** [#1401](https://github.com/sabbour/agentweaver/issues/1401)

**Area:** Review & merge

## User story

As a reviewer, I want every required child contribution included in the reviewed
integration result, with ambiguous overlaps requiring an explicit decision.

## Context / problem

A missing or moved child branch could previously be excluded while the remaining
branches assembled successfully; a branch moved after verification could also
contribute unverified bytes. Independent children editing the same file could
silently accept whichever child's branch was merged last.

## Scope

### In

- Verify required child outputs before dependent launch and collective assembly.
- Preserve independent non-overlapping edits; block independent path overlaps.
- Identify the producer and a recovery action when a required output is unavailable.
- Carry immutable output/input identities and resolution lineage through review.

### Out

- Automatic semantic conflict resolution or a replacement Git engine.

## Acceptance criteria

- [x] A missing child run, branch, or matching recorded tree blocks required output
  rather than reducing the integration input set.
- [x] An explicitly empty output declaration with no recorded Git output permits a
  no-output task; a valid no-change branch remains an eligible input.
- [x] A dependent does not launch from an absent or incomplete integration base.
- [x] Non-overlapping children merge; independent same-path edits, including
  renames and deletions, block integration without replacing the prior ref.
- [x] Conflict events carry affected paths and contributor commit IDs, including
  both sides of directory/file collisions.
- [ ] Every required producer supplies a durable immutable commit/tree or verified
  no-change receipt, including retries and cancelled attempts.
- [ ] Each downstream attempt pins its accepted base and upstream identities,
  with a stale base detected before publication.
- [ ] Explicit resolution records produce a new revision, rerun applicable gates
  and invalidate stale approvals and affected output only.
- [ ] REST, MCP, and UI expose the same completeness, conflict and reviewed
  revision lineage; fault-injection covers publication and restart boundaries.

## Notable edge cases

Explicit `[]` is different from missing or malformed output metadata. A clean Git
three-way merge does not establish that independent same-file edits were intended
to combine. The remaining durable receipt and approval contracts depend on
[#1396](https://github.com/sabbour/agentweaver/issues/1396); executable dependency
editing and its lineage depend on [#1398](https://github.com/sabbour/agentweaver/issues/1398).
