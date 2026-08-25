# Morpheus decision: plural triggers with singular compatibility

- Context: workflow automation was persisted and dispatched through one `WorkflowDefinition.Trigger`,
  so configuring a schedule and GitHub event on the same workflow was impossible.
- Decision: make the ordered `Triggers` collection canonical. Accept and preserve the legacy singular
  `trigger:` YAML shape for one trigger, serialize multiple triggers as `triggers:`, and retain the
  API's singular `trigger` field as a first-trigger compatibility alias alongside the complete
  `triggers` list.
- Rationale: this evolves the existing trigger abstraction instead of creating a parallel model,
  requires no data migration, preserves existing clients and workflow files, and lets schedule and
  event dispatch evaluate the same definition independently.
- Related issue #716 does not share the singular-model root: webhook dispatch already emits and
  matches `github.issues.labeled`. Its failure boundary is the event editor hiding the action suffix
  of an existing `github.issues.opened` trigger. It is included in the same PR because this change
  already owns that editor surface; an explicit Issues action selector fixes it without altering
  webhook or predicate semantics.
