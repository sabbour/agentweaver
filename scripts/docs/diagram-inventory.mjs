import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

export const INVENTORY_VERSION = 1;
export const DIAGRAM_DISPOSITIONS = new Set([
  'unreviewed',
  'retain',
  'reuse',
  'merge',
  'remove',
  'redesign',
]);

export function inferDiagramArea(name) {
  if (name.startsWith('experience-')) return 'experience';
  if (name.startsWith('guide-')) return 'guide';
  if (name.startsWith('workflow-')) return 'workflows';
  if (name.startsWith('canonical-')) return 'canonical';
  if (name.startsWith('reference-')) return 'reference';
  return 'deep-dive';
}

export function stableDiagramPaths(source) {
  const sourceExtension = source.kind === 'drawio' ? 'drawio' : 'json';
  return {
    input: `docs/diagrams/src/${source.name}.${sourceExtension}`,
    drawio: source.kind === 'drawio'
      ? `docs/diagrams/src/${source.name}.drawio`
      : `docs/diagrams/drawio/generated/${source.name}.drawio`,
    png: `docs/diagrams/${source.name}.png`,
    hash: `docs/diagrams/${source.name}.hash.txt`,
  };
}

export function createInventoryEntries(sources, existingEntries = []) {
  const existing = new Map(existingEntries.map((entry) => [entry.name, entry]));
  return [...sources]
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((source) => {
      const previous = existing.get(source.name);
      return {
        name: source.name,
        area: previous?.area ?? inferDiagramArea(source.name),
        disposition: previous?.disposition ?? 'unreviewed',
        owner: previous?.owner ?? null,
        replacement: previous?.replacement ?? null,
        notes: previous?.notes ?? '',
        paths: stableDiagramPaths(source),
      };
    });
}

export function validateInventoryEntries(entries, sources = []) {
  if (!Array.isArray(entries)) throw new Error('Diagram inventory entries must be an array');
  const names = new Set();
  for (const entry of entries) {
    if (!entry?.name || names.has(entry.name)) throw new Error(`Duplicate or missing inventory diagram name: ${entry?.name ?? '<empty>'}`);
    names.add(entry.name);
    if (!/^[a-z0-9][a-z0-9-]*$/.test(entry.area ?? '')) throw new Error(`Invalid inventory area for ${entry.name}: ${entry.area}`);
    if (!DIAGRAM_DISPOSITIONS.has(entry.disposition)) throw new Error(`Invalid disposition for ${entry.name}: ${entry.disposition}`);
    if (['reuse', 'merge'].includes(entry.disposition) && !entry.replacement) {
      throw new Error(`${entry.name} requires a replacement diagram for disposition ${entry.disposition}`);
    }
    for (const key of ['input', 'drawio', 'png', 'hash']) {
      if (!entry.paths?.[key]) throw new Error(`${entry.name} is missing stable path ${key}`);
    }
  }
  if (sources.length) {
    const sourceNames = new Set(sources.map((source) => source.name));
    const missing = [...sourceNames].filter((name) => !names.has(name));
    const orphaned = [...names].filter((name) => !sourceNames.has(name));
    if (missing.length || orphaned.length) {
      throw new Error(`Diagram inventory mismatch (missing: ${missing.join(', ') || 'none'}; orphaned: ${orphaned.join(', ') || 'none'})`);
    }
  }
  return true;
}

export async function loadInventory(inventoryRoot) {
  const indexPath = path.join(inventoryRoot, 'index.json');
  const index = JSON.parse(await readFile(indexPath, 'utf8'));
  if (index.version !== INVENTORY_VERSION || !Array.isArray(index.areas)) {
    throw new Error(`Unsupported diagram inventory index: ${indexPath}`);
  }
  const entries = [];
  for (const area of index.areas) {
    const areaPath = path.join(inventoryRoot, 'areas', `${area}.json`);
    const document = JSON.parse(await readFile(areaPath, 'utf8'));
    if (document.version !== INVENTORY_VERSION || document.area !== area || !Array.isArray(document.diagrams)) {
      throw new Error(`Invalid diagram inventory area file: ${areaPath}`);
    }
    entries.push(...document.diagrams);
  }
  validateInventoryEntries(entries);
  return { index, entries };
}

export function selectInventoryEntries(
  entries,
  { names = [], areas = [], dispositions = [] } = {},
) {
  const invalidDispositions = dispositions.filter((value) => !DIAGRAM_DISPOSITIONS.has(value));
  if (invalidDispositions.length) throw new Error(`Unknown disposition: ${invalidDispositions.join(', ')}`);
  const nameSet = new Set(names);
  const areaSet = new Set(areas);
  const dispositionSet = new Set(dispositions);
  const missingNames = [...nameSet].filter((name) => !entries.some((entry) => entry.name === name));
  if (missingNames.length) throw new Error(`Diagram inventory entry not found: ${missingNames.join(', ')}`);
  const selected = entries.filter((entry) =>
    (!nameSet.size || nameSet.has(entry.name))
    && (!areaSet.size || areaSet.has(entry.area))
    && (!dispositionSet.size || dispositionSet.has(entry.disposition)));
  if ((nameSet.size || areaSet.size || dispositionSet.size) && !selected.length) {
    throw new Error('No diagrams matched the requested name, area, and disposition filters');
  }
  return selected.sort((left, right) => left.name.localeCompare(right.name));
}

export async function writeInventoryArea(inventoryRoot, area, entries) {
  validateInventoryEntries(entries);
  const areaPath = path.join(inventoryRoot, 'areas', `${area}.json`);
  await writeFile(areaPath, `${JSON.stringify({
    version: INVENTORY_VERSION,
    area,
    diagrams: entries.sort((left, right) => left.name.localeCompare(right.name)),
  }, null, 2)}\n`);
  return areaPath;
}
