# Architecture decisions

This directory is the durable, lightweight record for cross-cutting architecture and
technical decisions: choices that affect multiple specs, subsystems, or recurring ways of
working. Create numbered files from [the template](0000-template.md), for example
`0002-short-title.md`.

Promote a decision here from `.squad/decisions/inbox/` when it needs to outlive the
operational ledger's normal compaction, or write it here directly when its significance is
already clear. Routine execution, rollout, and one-off coordination decisions remain in
`.squad/decisions.md`.

These are repository contribution records, not the product's database-backed decision
inbox. ADR statuses (`Proposed`, `Accepted`, `Superseded`) do not map to product inbox
states (`pending`, `merged`, `rejected`) or grant prompt-policy eligibility. Product
boundaries require active, approved architectural/scope decisions; see
[Memory & Decisions](../../deep-dive/memory-decisions.md#the-inbox-to-promotion-model).
