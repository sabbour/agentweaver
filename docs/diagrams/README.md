# Diagram authoring

The v1 documentation uses the existing Fluent draw.io pipeline. Each maintained figure has a structured JSON source, an editable draw.io file, a PNG, and a version 2 hash stamp. Graph sources use `src/graph-spec.schema.json`; sequence sources use `src/sequence-spec.schema.json`.

The flagship list is in `docs/diagrams/flagship-diagrams.json`. The source map connects implementation paths to documentation pages and figures.

## Update a figure

1. Edit the JSON source under `docs/diagrams/src/flagship/`.
   Use the schema referenced by its `$schema` field. Sequence steps support messages, activation bars, notes, and nested fragments.
2. Generate the editable draw.io file:

   ```powershell
   npm run docs:build-flagship-specs
   ```

3. Render the figure with draw.io Desktop 31.4.5:

   ```powershell
   npm run docs:render-diagrams -- --spec <diagram-name>
   ```

4. Run the diagram checks:

   ```powershell
   npm run docs:check-diagrams
   npm run docs:check-flagship-diagrams
   npm run test:docs-diagrams
   ```

Keep each figure readable at a 960-pixel embed width. Add a factual alt description, caption, nearby explanation, full-size PNG link, and editable source link to its page. Add each public figure to `flagship-diagrams.json` and map its source to the maintained page in `docs-source-map.json`.

The checks validate committed JSON, draw.io geometry, PNG output, and hash stamps. They do not require draw.io Desktop. Python 3 is required for draw.io normalization.

The figures describe current source. Do not copy the 0.x product topology into v1 diagrams.
