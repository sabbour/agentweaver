#!/usr/bin/env node

import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DIAGRAM_DISPOSITIONS,
  loadInventory,
  selectInventoryEntries,
  validateInventoryEntries,
  writeInventoryArea,
} from './diagram-inventory.mjs';
import { listDiagramSources } from './diagram-sources.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const specsDir = path.join(repoRoot, 'docs', 'diagrams', 'src');
const inventoryRoot = path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'inventory');

export function parseInventoryArgs(args) {
  const filters = { names: [], areas: [], dispositions: [] };
  let checkMode = false;
  let listMode = false;
  let setName;
  let setDisposition;
  let owner;
  let replacement;
  let notes;
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    const value = args[index + 1];
    if (arg === '--check') checkMode = true;
    else if (arg === '--list') listMode = true;
    else if (['--spec', '--area', '--disposition', '--set', '--owner', '--replacement', '--notes'].includes(arg)) {
      if (!value || value.startsWith('--')) throw new Error(`${arg} requires a value`);
      if (arg === '--spec') filters.names.push(value.replace(/\.(json|drawio)$/, ''));
      if (arg === '--area') filters.areas.push(value);
      if (arg === '--disposition') filters.dispositions.push(value);
      if (arg === '--set') setName = value.replace(/\.(json|drawio)$/, '');
      if (arg === '--owner') owner = value;
      if (arg === '--replacement') replacement = value;
      if (arg === '--notes') notes = value;
      index += 1;
    } else throw new Error(`Unknown argument: ${arg}`);
  }
  if (!checkMode && !listMode && !setName) listMode = true;
  if (setName && !setDisposition && filters.dispositions.length === 1) setDisposition = filters.dispositions[0];
  if (setName && filters.dispositions.length > 1) throw new Error('--set accepts at most one --disposition');
  if (setName && setDisposition && !DIAGRAM_DISPOSITIONS.has(setDisposition)) throw new Error(`Unknown disposition: ${setDisposition}`);
  return { checkMode, listMode, setName, setDisposition, owner, replacement, notes, filters };
}

async function main() {
  const options = parseInventoryArgs(process.argv.slice(2));
  const sources = await listDiagramSources(specsDir);
  const inventory = await loadInventory(inventoryRoot);
  validateInventoryEntries(inventory.entries, sources);

  if (options.setName) {
    const entry = inventory.entries.find((candidate) => candidate.name === options.setName);
    if (!entry) throw new Error(`Diagram inventory entry not found: ${options.setName}`);
    if (options.setDisposition) entry.disposition = options.setDisposition;
    if (options.owner !== undefined) entry.owner = options.owner === 'none' ? null : options.owner;
    if (options.replacement !== undefined) entry.replacement = options.replacement === 'none' ? null : options.replacement;
    if (options.notes !== undefined) entry.notes = options.notes;
    validateInventoryEntries(inventory.entries, sources);
    await writeInventoryArea(inventoryRoot, entry.area, inventory.entries.filter((candidate) => candidate.area === entry.area));
    console.log(`Updated ${entry.name} in area ${entry.area}`);
  }

  if (options.checkMode) console.log(`OK: ${inventory.entries.length} diagram inventory entries match source discovery`);
  if (options.listMode) {
    const selected = selectInventoryEntries(inventory.entries, options.filters);
    for (const entry of selected) {
      console.log([entry.name, entry.area, entry.disposition, entry.owner ?? '-', entry.paths.png].join('\t'));
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
}
