import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  createInventoryEntries,
  inferDiagramArea,
  loadInventory,
  selectInventoryEntries,
  stableDiagramPaths,
  validateInventoryEntries,
  writeInventoryArea,
} from './diagram-inventory.mjs';
import { parseInventoryArgs } from './inventory-diagrams.mjs';

test('assigns stable paths and independent docs areas', () => {
  assert.equal(inferDiagramArea('experience-runs-fig1'), 'experience');
  assert.equal(inferDiagramArea('workflow-bug-fix'), 'workflows');
  assert.equal(inferDiagramArea('canonical-api-host'), 'canonical');
  assert.equal(inferDiagramArea('frontend-fig1'), 'deep-dive');
  assert.deepEqual(stableDiagramPaths({ name: 'sample', kind: 'json' }), {
    input: 'docs/diagrams/src/sample.json',
    drawio: 'docs/diagrams/drawio/generated/sample.drawio',
    png: 'docs/diagrams/sample.png',
    hash: 'docs/diagrams/sample.hash.txt',
  });
});

test('preserves reviewed dispositions while refreshing source-derived entries', () => {
  const entries = createInventoryEntries(
    [{ name: 'a', kind: 'json' }, { name: 'b', kind: 'drawio' }],
    [{ name: 'a', area: 'guide', disposition: 'reuse', replacement: 'b', owner: 'astra-1', notes: 'Shared overview' }],
  );
  assert.equal(entries[0].area, 'guide');
  assert.equal(entries[0].disposition, 'reuse');
  assert.equal(entries[0].replacement, 'b');
  assert.equal(entries[1].disposition, 'unreviewed');
  validateInventoryEntries(entries, [{ name: 'a' }, { name: 'b' }]);
});

test('filters inventory by name, area, and disposition and rejects invalid records', () => {
  const entries = [
    { name: 'a', area: 'guide', disposition: 'redesign', paths: { input: 'a', drawio: 'a', png: 'a', hash: 'a' } },
    { name: 'b', area: 'guide', disposition: 'retain', paths: { input: 'b', drawio: 'b', png: 'b', hash: 'b' } },
    { name: 'c', area: 'canonical', disposition: 'redesign', paths: { input: 'c', drawio: 'c', png: 'c', hash: 'c' } },
  ];
  assert.deepEqual(
    selectInventoryEntries(entries, { areas: ['guide'], dispositions: ['redesign'] }).map((entry) => entry.name),
    ['a'],
  );
  assert.throws(() => selectInventoryEntries(entries, { names: ['missing'] }), /not found/);
  assert.throws(() => selectInventoryEntries(entries, { dispositions: ['ship-it'] }), /Unknown disposition/);
  assert.throws(
    () => validateInventoryEntries([{ ...entries[0], disposition: 'merge', replacement: null }]),
    /requires a replacement/,
  );
});

test('loads and writes area-sharded inventory files', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-inventory-'));
  try {
    await mkdir(path.join(root, 'areas'));
    await writeFile(path.join(root, 'index.json'), JSON.stringify({ version: 1, areas: ['guide'] }));
    const entries = [{ name: 'a', area: 'guide', disposition: 'retain', owner: null, replacement: null, notes: '', paths: { input: 'a', drawio: 'a', png: 'a', hash: 'a' } }];
    await writeInventoryArea(root, 'guide', entries);
    assert.deepEqual((await loadInventory(root)).entries, entries);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('parses inventory list and update commands', () => {
  assert.deepEqual(parseInventoryArgs(['--list', '--area', 'guide']), {
    checkMode: false,
    listMode: true,
    setName: undefined,
    setDisposition: undefined,
    owner: undefined,
    replacement: undefined,
    notes: undefined,
    filters: { names: [], areas: ['guide'], dispositions: [] },
  });
  assert.equal(parseInventoryArgs(['--set', 'a', '--disposition', 'redesign', '--owner', 'astra-1']).setDisposition, 'redesign');
});
