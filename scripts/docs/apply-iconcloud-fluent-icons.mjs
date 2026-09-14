#!/usr/bin/env node
import { readFile, writeFile } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { listDiagramSources } from './diagram-sources.mjs';
import { resolveFluentIcon } from './fluent-icon-catalog.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const sourceDirectory = path.join(repoRoot, 'docs', 'diagrams', 'src');

function attributes(line) {
  return Object.fromEntries([...line.matchAll(/\b([A-Za-z][\w:-]*)="([^"]*)"/g)].map(match => [match[1], match[2]]));
}

function decodeText(value = '') {
  return value
    .replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&quot;', '"')
    .replaceAll('&apos;', "'").replaceAll('&amp;', '&')
    .replace(/<br\s*\/?>/gi, ' ').replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();
}

function iconSvg(name, color) {
  return readFileSync(new URL(`../../docs/diagrams/drawio/icons/${name}.svg`, import.meta.url), 'utf8')
    .trim()
    .replaceAll('currentColor', color);
}

export function applyIconCloudFluentIcons(xml) {
  const lines = xml.split(/\r?\n/);
  const cards = new Map();
  for (const line of lines) {
    if (!line.includes('<mxCell ')) continue;
    const attrs = attributes(line);
    if (!attrs.parent || !['title', 'subtitle', 'meta'].includes(attrs.fluentRole)) continue;
    const card = cards.get(attrs.parent) ?? { id: attrs.parent.replace(/^node-/, '').replace(/-(?:top|bottom)$/, '') };
    card[attrs.fluentRole === 'title' ? 'label' : attrs.fluentRole === 'subtitle' ? 'subLabel' : 'meta'] = decodeText(attrs.value);
    cards.set(attrs.parent, card);
  }

  let replaced = 0;
  let preservedKubernetes = 0;
  const output = lines.map(line => {
    if (!line.includes('fluentRole="icon"')) return line;
    const attrs = attributes(line);
    if (/mxgraph\.kubernetes\./.test(attrs.style ?? '')) {
      preservedKubernetes += 1;
      return line;
    }
    const card = cards.get(attrs.parent);
    if (!card) throw new Error(`Icon ${attrs.id ?? '<unknown>'} has no Fluent card text parent`);
    const color = attrs.style?.match(/strokeColor=(#[0-9A-Fa-f]{6})/)?.[1] ?? '#3f3682';
    const name = resolveFluentIcon(card);
    const style = `shape=image;aspect=fixed;image=data:image/svg+xml,${encodeURIComponent(iconSvg(name, color))};`;
    replaced += 1;
    return line.replace(/\bstyle="[^"]*"/, `style="${style}"`);
  });
  return { xml: output.join('\n'), replaced, preservedKubernetes };
}

async function main() {
  const sources = (await listDiagramSources(sourceDirectory)).filter(source => source.kind === 'drawio');
  let replaced = 0;
  let preservedKubernetes = 0;
  for (const source of sources) {
    const original = await readFile(source.path, 'utf8');
    const result = applyIconCloudFluentIcons(original);
    await writeFile(source.path, result.xml);
    replaced += result.replaced;
    preservedKubernetes += result.preservedKubernetes;
  }
  console.log(`Applied semantic IconCloud Fluent icons to ${replaced} card instances; preserved ${preservedKubernetes} Kubernetes icons.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
