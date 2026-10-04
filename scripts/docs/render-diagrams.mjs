#!/usr/bin/env node
// npm-facing entry point for the browser-free draw.io diagram pipeline.
//
// docs/diagrams/src contains canonical .json graph/sequence specs and editable
// .drawio architecture sources. Add a source only when an existing canonical
// diagram cannot be reused, then rerun this script.
//
// Usage:
//   node scripts/docs/render-diagrams.mjs           # render + commit PNG + hash
//   node scripts/docs/render-diagrams.mjs --check   # CI: verify no drift (no browser needed)
//   node scripts/docs/render-diagrams.mjs --spec name
//   node scripts/docs/render-diagrams.mjs --spec first --spec second
//   node scripts/docs/render-diagrams.mjs --area deep-dive
//   node scripts/docs/render-diagrams.mjs --area workflows --disposition redesign
//   node scripts/docs/render-diagrams.mjs --list --area experience
//   node scripts/docs/render-diagrams.mjs --check --spec name
//   node scripts/docs/render-diagrams.mjs --spec name --drawio-format svg
//   node scripts/docs/render-diagrams.mjs --spec name --drawio-cli C:\\tools\\draw.io.exe
//   node scripts/docs/render-diagrams.mjs --spec name --drawio-format pdf --no-embed
//   node scripts/docs/render-diagrams.mjs --spec name --allow-version-mismatch

import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadInventory, selectInventoryEntries, validateInventoryEntries } from './diagram-inventory.mjs';
import { DRAWIO_FORMATS, listDiagramSources } from './diagram-sources.mjs';
import { check, render } from './capture-diagrams.mjs';

export function parseArgs(args) {
  const specs = [];
  const areas = [];
  const dispositions = [];
  const drawioFormats = new Set(['png']);
  let drawioCli;
  let embed = true;
  let allowVersionMismatch = false;
  let checkMode = false;
  let listMode = false;
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--check') {
      checkMode = true;
      continue;
    }
    if (arg === '--list') {
      listMode = true;
      continue;
    }
    if (arg === '--area' || arg === '--disposition') {
      const value = args[index + 1];
      if (!value || value.startsWith('--')) throw new Error(`${arg} requires a value`);
      (arg === '--area' ? areas : dispositions).push(value);
      index += 1;
      continue;
    }
    if (arg === '--spec') {
      const name = args[index + 1];
      if (!name || name.startsWith('--')) {
        throw new Error('--spec requires a diagram spec name');
      }
      specs.push(name.replace(/\.(json|drawio)$/, ''));
      index += 1;
      continue;
    }
    if (arg === '--drawio-format') {
      const format = args[index + 1]?.toLowerCase();
      if (!DRAWIO_FORMATS.has(format)) {
        throw new Error('--drawio-format requires png, svg, or pdf');
      }
      drawioFormats.add(format);
      index += 1;
      continue;
    }
    if (arg === '--drawio-cli') {
      drawioCli = args[index + 1];
      if (!drawioCli || drawioCli.startsWith('--')) {
        throw new Error('--drawio-cli requires an executable path');
      }
      index += 1;
      continue;
    }
    if (arg === '--no-embed') {
      embed = false;
      continue;
    }
    if (arg === '--allow-version-mismatch') {
      allowVersionMismatch = true;
      continue;
    }
    throw new Error(`Unknown argument: ${arg}`);
  }
  return {
    checkMode,
    listMode,
    specs,
    areas,
    dispositions,
    drawioFormats: [...drawioFormats],
    drawioCli,
    embed,
    allowVersionMismatch,
  };
}

async function main() {
  const {
    checkMode,
    listMode,
    specs,
    areas,
    dispositions,
    drawioFormats,
    drawioCli,
    embed,
    allowVersionMismatch,
  } = parseArgs(process.argv.slice(2));
  let selectedSpecs = specs;
  if (areas.length || dispositions.length || listMode) {
    const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
    const inventoryRoot = path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'inventory');
    const { entries } = await loadInventory(inventoryRoot);
    validateInventoryEntries(entries, await listDiagramSources(path.join(repoRoot, 'docs', 'diagrams', 'src')));
    const selected = selectInventoryEntries(entries, { names: specs, areas, dispositions });
    if (listMode) {
      for (const entry of selected) console.log([entry.name, entry.area, entry.disposition, entry.paths.png].join('\t'));
      return;
    }
    selectedSpecs = selected.map((entry) => entry.name);
  }
  if (checkMode) {
    const ok = await check(selectedSpecs);
    if (!ok) process.exitCode = 1;
    return;
  }
  await render(selectedSpecs, { drawioFormats, drawioCli, embed, allowVersionMismatch });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((err) => {
    console.error(err);
    process.exitCode = 1;
  });
}
