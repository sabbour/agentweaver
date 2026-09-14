# Documentation diagram audit checklist

## Scope and evidence

- [ ] Documentation rendering/authoring only; shipped product surfaces remain untouched.
- [ ] Repository implementation, configuration, tests, and current written docs are ground truth.
- [ ] Existing diagrams are treated only as legacy artifacts to assess.
- [ ] GPT-6 Astra agents perform the area research and downstream pitch/iteration work.

## Complete inventory

- [ ] Every diagram source, PNG, hash, and Markdown reference is recorded.
- [ ] Every concept and diagram has one owner area.
- [ ] Every concept and diagram has a disposition.
- [ ] `reuse` and `merge` entries name valid targets.
- [ ] Stable output paths are recorded before changes.
- [ ] Refresh preserves remove/reuse/merge tombstones and path-change history.

## Consolidation

- [ ] Duplicate concepts resolve to one canonical diagram.
- [ ] Shared diagrams have one writer and are reused across docs areas.
- [ ] Intentional path changes list all affected consumers.
- [ ] Parallel workstreams have disjoint files and report entries.

## Authoring

- [ ] Every surviving concept completed `docs-diagram-pitch`.
- [ ] Every surviving concept completed `docs-diagram-iterate`.
- [ ] Ground truth was re-researched rather than copied from legacy diagrams.
- [ ] Final source is uncompressed editable draw.io XML.
- [ ] Stable paths are preserved unless consolidation explicitly changes them.

## Integrity and reports

- [ ] No orphaned source.
- [ ] No orphaned PNG.
- [ ] No orphaned hash.
- [ ] No broken diagram reference.
- [ ] No ambiguous source basename.
- [ ] Historical targets exist and do not point to removed entries.
- [ ] Machine-readable report validates with `--final`.
- [ ] Human summary records dispositions, consolidations, ownership, path changes, and blockers.
