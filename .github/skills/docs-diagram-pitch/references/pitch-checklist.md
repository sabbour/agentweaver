# Pitch checklist

## Scope

- [ ] Changes are limited to documentation rendering and authoring.
- [ ] Product topology/workflow UI, routing helpers, runtime APIs, and shipped behavior remain untouched.
- [ ] `packages/Agentweaver.Squad` workflow definitions remain unchanged.
- [ ] Root dependencies used by non-doc tooling remain installed.

## Inventory

- [ ] Discover without changing diagram sources or documentation references.
- [ ] Record one stable canonical name and `docs/diagrams/<name>.png` output.
- [ ] Assign `retain`, `reuse`, `merge`, `remove`, `redesign`, or `unreviewed`.
- [ ] Name a target for every `reuse` or `merge` entry.
- [ ] Assign one `owner_area` and one active writer per diagram.
- [ ] Process only selected names/areas; do not begin the full migration implicitly.

## Grounding

- [ ] Read the target page and canonical diagram index.
- [ ] Launch exactly two or three separate bounded GPT-6 Astra subject-research agents.
- [ ] Do not count multiple searches in one current-model context as separate agents.
- [ ] Cite repository file/line evidence or authoritative links for every node and connector.
- [ ] Separate current facts, future proposals, and assumptions.
- [ ] Record the one-sentence takeaway.

## Visual research

- [ ] Inspect the current Agentweaver React diagram styling and draw.io design-system assets.
- [ ] Inspect relevant visual references and official brand resources.
- [ ] Record logo/symbol source and usage rights.
- [ ] Choose A5 portrait or landscape based on the story, not a fixed default.

## Symbol inventory

- [ ] Mark each symbol `native:azure`, `native:kubernetes`, `native:c4`, `native:uml`, `native:flowchart`, `native:bpmn`, `native:networking`, `native:database`, `native:cloud`, or `custom:agentweaver`.
- [ ] Use native draw.io symbols when they are semantically correct.
- [ ] Reserve custom components for Agentweaver-specific concepts or genuine library gaps.

## Source and export

- [ ] One A5 page with all visible content inside printable bounds.
- [ ] Uncompressed editable XML.
- [ ] Agentweaver palette, typography, card hierarchy, groups, shadows, badges, and connector semantics preserved.
- [ ] Pitch `.drawio`, PNG, and change/research record use distinct `-pitch` names.
- [ ] Actual PNG inspected at A5 print size and enlarged detail.
- [ ] Pitch artifacts handed to `docs-diagram-iterate`; they are not called final.
