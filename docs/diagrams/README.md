# Flagship documentation diagrams

Agentweaver maintains **15 flagship diagrams** as the only diagrams embedded by the
product documentation. They are documentation assets only: they do not change
coordinator topology, workflow behavior, cluster resources, APIs, or application views.

The maintained inventory is [`flagship-diagrams.json`](flagship-diagrams.json).

## Repository layout

```text
docs/diagrams/
├── flagship/                       # published PNGs and deterministic hash stamps
├── src/flagship/                   # structured JSON sources
├── drawio/generated/flagship/      # editable uncompressed draw.io XML
├── drawio/icons/                   # vendored Fluent SVGs with IconCloud provenance
├── drawio/fluent-template.drawio   # Fluent design reference
├── drawio/fluent-library.xml       # reusable draw.io library
└── flagship-diagrams.json          # the 15-diagram contract and consumer pages
```

Each flagship has one JSON source, one editable `.drawio`, one PNG, and one hash stamp
with the same basename.

## Visual and notation contract

The renderer preserves the React-derived Fluent language:

- warm neutral canvas, Segoe UI typography, and clear title/subtitle hierarchy;
- rounded near-white cards with restrained shadows and semantic accent colors;
- orthogonal connectors, readable labels, bridges, and explicit revision paths;
- Fluent icons for Agentweaver and software concepts;
- official Azure icons for retained Azure resources;
- native Kubernetes symbols for actual Kubernetes resources;
- UML, sequence, activity, state, flowchart, or deployment notation when it explains
  the concept better than a generic card graph.

The publication gate blocks unresolved roles, text overflow, collisions, ambiguous
crossings, malformed arrowheads, missing junctions, and unreadable raster scale.

## Build and validate

Use the pinned draw.io Desktop `31.4.5` renderer.

```powershell
npm run docs:build-flagship-specs
npm run docs:render-diagrams -- --spec canonical-coordinator-architecture
npm run docs:check-flagship-diagrams
npm run test:docs-diagrams
npm run docs:build
```

`npm run docs:consolidate-flagship-references` removes legacy documentation embeds and
reinstalls the authoritative flagship blocks from `flagship-diagrams.json`.

## Research and revision workflow

For a new flagship or substantial redesign:

1. Invoke `docs-diagram-audit` for repository-wide research and consolidation.
2. Invoke `docs-diagram-pitch` for independent GPT-6 Astra grounding and notation choice.
3. Ignore legacy diagram composition as architectural evidence.
4. Build the smallest model that preserves the important boundaries, states, and failure paths.
5. Invoke `docs-diagram-iterate` for bounded PNG review and final arrow tracing.
6. Render the selected batch with the publication gate enabled.
7. Inspect the PNG at the 960 px documentation embed width.
8. Commit JSON, editable draw.io, PNG, hash, and documentation references together.

The flagship set is deliberately curated. Add, remove, or replace a flagship only by
updating `flagship-diagrams.json` and all affected consumer pages in the same change.
