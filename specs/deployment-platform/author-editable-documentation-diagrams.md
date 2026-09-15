# Author editable documentation diagrams

**Issue:** [#1305](https://github.com/sabbour/agentweaver/issues/1305)
**Area:** Deployment & platform

## User story

As a documentation author, I want a visually editable architecture-diagram path so that I can create rich, accurate Agentweaver diagrams while retaining deterministic rendering for generated graphs and sequences.

## Context / problem

The existing JSON renderer produces consistent Fluent-styled graphs and sequences, but detailed architecture diagrams often need deliberate grouping, annotations, emphasis, and connector routing. Those refinements are awkward to express as data-only layout rules. Replacing the renderer would also discard a reliable path for generated workflow and sequence content.

## Scope

### In scope

- Use `.drawio` as the canonical source for rich architecture diagrams.
- Provide a Fluent template and reusable shape library.
- Export draw.io sources to PNG, with optional SVG and PDF outputs.
- Detect missing or stale exports without launching a browser.
- Preserve JSON graph and sequence rendering.
- Provide skills for research-grounded creation and mandatory visual iteration.
- Migrate one representative architecture diagram without changing its public image path.

### Out of scope

- Converting every existing JSON diagram.
- Replacing the React renderer or its layout algorithms.
- Requiring draw.io at documentation display time or during the fast CI drift check.
- Pixel-diff validation across operating systems.

## Acceptance criteria

- `docs/diagrams/src/` accepts either one `.drawio` or one `.json` source per basename.
- `npm run docs:render-diagrams` exports both source types and writes a source hash.
- `npm run docs:check-diagrams` checks both source types without requiring draw.io or Chromium.
- The draw.io command can be discovered automatically or supplied explicitly.
- The repository includes an Agentweaver Fluent template, component library, and documented authoring workflow.
- Diagram creation and iteration skills require repository grounding and final exported-image inspection.
- The coordinator architecture diagram is migrated to draw.io while its existing Markdown image references remain valid.

## Notable edge cases

- A `.json` and `.drawio` file with the same basename is rejected as ambiguous.
- Newline differences do not invalidate a draw.io hash across Windows and Linux.
- Missing draw.io Desktop produces an actionable error only during export, not during drift checks.
- Optional SVG/PDF exports do not become required CI artifacts; PNG remains the documentation contract.
