#!/usr/bin/env node
import { mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ICONCLOUD_CATALOG, iconCloudSourceUrl } from './fluent-icon-catalog.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const requireFromDocs = createRequire(path.join(repoRoot, 'docs', 'package.json'));
const React = requireFromDocs('react');
const { renderToStaticMarkup } = requireFromDocs('react-dom/server');
const fluentIcons = requireFromDocs('@fluentui/react-icons');
const outputDirectory = path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'icons');

await mkdir(outputDirectory, { recursive: true });
for (const [name, entry] of Object.entries(ICONCLOUD_CATALOG.icons)) {
  const Icon = fluentIcons[entry.component];
  if (!Icon) throw new Error(`@fluentui/react-icons does not export ${entry.component}`);
  const svg = renderToStaticMarkup(React.createElement(Icon))
    .replace(/\sclass="[^"]*"/, '')
    .replace(/\sdata-fui-icon=""/, '')
    .replace('width="1em" height="1em"', 'width="28" height="28"')
    .replace('<svg ', `<svg data-iconcloud-source="${iconCloudSourceUrl(name).replaceAll('&', '&amp;')}" `);
  await writeFile(path.join(outputDirectory, `${name}.svg`), `${svg}\n`);
}
console.log(`Generated ${Object.keys(ICONCLOUD_CATALOG.icons).length} IconCloud-referenced Fluent SVGs.`);
