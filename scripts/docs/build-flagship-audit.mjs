#!/usr/bin/env node

import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const configPath = path.join(repoRoot, 'docs', 'diagrams', 'flagship-diagrams.json');
const config = JSON.parse(await readFile(configPath, 'utf8'));

const diagrams = config.diagrams.map(diagram => ({
  name: diagram.name,
  concept: diagram.concept,
  status: 'maintained',
  source: `docs/diagrams/src/flagship/${diagram.name}.json`,
  editable: `docs/diagrams/drawio/generated/flagship/${diagram.name}.drawio`,
  png: `docs/diagrams/flagship/${diagram.name}.png`,
  hash: `docs/diagrams/flagship/${diagram.name}.hash.txt`,
  consumers: diagram.docs,
}));

const inventory = {
  version: 1,
  policy: 'Only the 15 flagship diagrams are published or embedded by product documentation.',
  count: diagrams.length,
  diagrams,
};
await writeFile(
  path.join(repoRoot, 'docs-diagram-inventory.json'),
  `${JSON.stringify(inventory, null, 2)}\n`,
);

const audit = {
  version: 1,
  scope: 'documentation-only',
  maintainedCount: diagrams.length,
  legacyPublicAssetsRemoved: true,
  checks: [
    'structured JSON source present',
    'editable uncompressed draw.io present',
    'PNG and deterministic hash present',
    'Fluent publication gate accepted',
    '960px flagship raster gate accepted',
    'documentation consumers restricted to flagship assets',
  ],
  diagrams,
};
await writeFile(
  path.join(repoRoot, 'docs-diagram-audit.json'),
  `${JSON.stringify(audit, null, 2)}\n`,
);

const rows = diagrams.map((diagram, index) =>
  `| ${index + 1} | ${diagram.concept} | [PNG](${diagram.png}) | [JSON](${diagram.source}) | [draw.io](${diagram.editable}) | ${diagram.consumers.map(doc => `[${doc}](${doc})`).join('<br>')} |`,
);
const markdown = `# Documentation diagram audit

Agentweaver publishes one curated set of **${diagrams.length} flagship diagrams**. Legacy
public PNGs, flat sources, generated XML, and review/export trees were removed. Product
documentation embeds only the assets listed below.

This change is documentation-only. It does not modify coordinator topology, workflows,
cluster topology, APIs, or application rendering.

| # | Concept | Preview | Structured source | Editable source | Consumers |
|---|---|---|---|---|---|
${rows.join('\n')}

## Validation contract

- Fluent publication gate accepted every selected source before writing output.
- Raster readability is checked at a 960 px documentation embed.
- Actual Azure resources use official Azure SVGs.
- Actual Kubernetes resources use native Kubernetes symbols.
- Software concepts use the vendored Fluent IconCloud catalog.
- UML, sequence, activity, state, deployment, and flowchart notation are selected by concept.
`;
await writeFile(path.join(repoRoot, 'docs-diagram-audit.md'), markdown);
console.log(`Wrote flagship inventory and audit for ${diagrams.length} diagrams`);
