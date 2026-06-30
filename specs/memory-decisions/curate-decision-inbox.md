# Curate the decision inbox

**Issue:** [#22](https://github.com/sabbour/agentweaver/issues/22)  
**Area:** Memory & decisions

## User story

As a coordinator or project owner, I want to review proposed decisions and promote or reject them, so that the team learns durable rules without turning every agent observation into policy.

## Context / problem

Agents and coordinators can propose learnings or decisions. The inbox is the governance buffer between draft knowledge and accepted project policy.

## Scope

### In
- submitting proposed decisions or learnings
- listing and filtering inbox entries
- promoting or merging accepted entries
- rejecting entries while retaining audit context
- accepted decision ledger updates

### Out
- auto-promoting every proposed item
- deleting rejected history as if it never happened
- policy decisions outside project scope

## Acceptance criteria

- [ ] Proposed knowledge appears in a reviewable inbox.
- [ ] Users can distinguish pending, merged, and rejected entries.
- [ ] Promoting an entry creates or updates accepted project knowledge.
- [ ] Rejecting an entry removes it from pending work while preserving the record.
- [ ] Accepted decisions become available to future project context.

## Notable edge cases

- Duplicate or repeated proposals do not create confusing authoritative duplicates.
- Already-processed inbox entries cannot be merged or rejected as pending.
- Rejected entries remain visible when explicitly requested.
