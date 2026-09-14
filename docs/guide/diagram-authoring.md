---
title: Author documentation diagrams
---

# Author documentation diagrams

Agentweaver uses a curated set of 15 Fluent draw.io diagrams. The inventory and consumer
pages are declared in
[`flagship-diagrams.json`](../diagrams/flagship-diagrams.json).

Diagram work is documentation-only. Do not modify coordinator topology, workflow semantics,
cluster topology, APIs, or application rendering to make a documentation visual easier.

## Choose the workflow

- **Repository-wide audit or consolidation:** invoke `docs-diagram-audit`.
- **New or substantially redesigned diagram:** invoke `docs-diagram-pitch`.
- **Correction or refinement of an existing flagship:** invoke `docs-diagram-iterate`.

The skills are checked in under `.github/skills/` so Copilot and other repository agents
can invoke the same research, pitch, and review contracts.

## Research before drawing

Use GPT-6 Astra to inspect current implementation, tests, configuration, specs, and written
documentation. Existing diagrams are legacy artifacts, not factual or compositional
evidence. Resolve disagreements in favor of current code and explicitly qualify gaps
between intended and implemented behavior.

Choose the representation that best explains the concept:

- UML component or deployment views for ownership and containment;
- UML sequence views for communication and ordering;
- activity, state, or flowchart notation for choices and lifecycle;
- a restrained hybrid only when one notation cannot carry the essential meaning.

Compress the research model before rendering. A flagship normally needs 5–10 primary
nodes or 4–8 sequence participants, not every manifest object or implementation class.

## Icon and visual rules

- Use Fluent icons from the vendored IconCloud-derived catalog for software concepts.
- Use official Azure icons for actual Azure resources.
- Use native Kubernetes icons for actual Kubernetes resources.
- Use native notation shapes for decisions, states, documents, actors, and deployment boundaries.
- Preserve the React-derived Fluent typography, hierarchy, spacing, palette, and card anatomy.
- Keep connectors orthogonal and inspect every arrow direction and label at the final size.

## Source and output locations

```text
docs/diagrams/src/flagship/<name>.json
docs/diagrams/drawio/generated/flagship/<name>.drawio
docs/diagrams/flagship/<name>.png
docs/diagrams/flagship/<name>.hash.txt
```

The editable draw.io XML is generated from the structured JSON and remains uncompressed.
The public PNG is exported by pinned draw.io Desktop `31.4.5`.

## Build and review

```powershell
npm run docs:build-flagship-specs
npm run docs:render-diagrams -- --spec <name>
npm run docs:check-flagship-diagrams
```

For a substantial redesign, inspect a pitch and at least four bounded iterations. Later
passes correct concrete defects rather than introducing a new composition. The final pass
must trace every semantic arrow and verify the actual PNG at the 960 px documentation
embed width.

The renderer fails closed on text overflow, collisions, missing junctions, ambiguous
crossings, double arrowheads, and invalid card geometry. Do not bypass the gate or pad XML
to satisfy process checks.

## Update documentation references

The product docs embed only the 15 flagship PNGs. After changing the inventory or consumer
pages, run:

```powershell
npm run docs:consolidate-flagship-references
```

This removes legacy embeds and writes the authoritative PNG, structured-source, and
editable-draw.io links into each consumer listed by `flagship-diagrams.json`.

Finish with the relevant diagram tests, the flagship raster gate, the documentation build,
and `git diff --check`.
