import assert from 'node:assert/strict';
import test from 'node:test';
import { parseArgs } from './render-diagrams.mjs';

test('parses draw.io source names, extra formats, and CLI override', () => {
  assert.deepEqual(
    parseArgs([
      '--spec', 'architecture.drawio',
      '--drawio-format', 'svg',
      '--drawio-format', 'pdf',
      '--drawio-cli', 'C:\\tools\\draw.io.exe',
    ]),
    {
      checkMode: false,
      listMode: false,
      specs: ['architecture'],
      areas: [],
      dispositions: [],
      drawioFormats: ['png', 'svg', 'pdf'],
      drawioCli: 'C:\\tools\\draw.io.exe',
      embed: true,
      allowVersionMismatch: false,
    },
  );
});

test('keeps check mode browser-free and validates option values', () => {
  assert.deepEqual(parseArgs(['--check', '--spec', 'flow.json']), {
    checkMode: true,
    listMode: false,
    specs: ['flow'],
    areas: [],
    dispositions: [],
    drawioFormats: ['png'],
    drawioCli: undefined,
    embed: true,
    allowVersionMismatch: false,
  });
  assert.throws(() => parseArgs(['--drawio-format', 'gif']), /png, svg, or pdf/);
  assert.throws(() => parseArgs(['--drawio-cli']), /executable path/);
});

test('parses embedding and deterministic-version overrides', () => {
  assert.deepEqual(parseArgs(['--no-embed', '--allow-version-mismatch']), {
    checkMode: false,
    listMode: false,
    specs: [],
    areas: [],
    dispositions: [],
    drawioFormats: ['png'],
    drawioCli: undefined,
    embed: false,
    allowVersionMismatch: true,
  });
});

test('parses repeatable area and disposition filters for parallel bulk work', () => {
  assert.deepEqual(parseArgs([
    '--list',
    '--area', 'deep-dive',
    '--area', 'canonical',
    '--disposition', 'redesign',
  ]), {
    checkMode: false,
    listMode: true,
    specs: [],
    areas: ['deep-dive', 'canonical'],
    dispositions: ['redesign'],
    drawioFormats: ['png'],
    drawioCli: undefined,
    embed: true,
    allowVersionMismatch: false,
  });
});
