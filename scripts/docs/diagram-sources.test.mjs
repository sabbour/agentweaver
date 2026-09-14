import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  createDiagramStamp,
  diagramSourceHash,
  drawioExportArgs,
  listDiagramSources,
  resolveDrawioCommand,
  selectDiagramSources,
  validateUncompressedDrawio,
  verifyDrawioVersion,
} from './diagram-sources.mjs';
import { DRAWIO_CLI_VERSION } from './drawio-generator.mjs';

test('discovers JSON and draw.io sources while excluding schemas', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-diagrams-'));
  try {
    await Promise.all([
      writeFile(path.join(directory, 'architecture.drawio'), '<mxfile/>'),
      writeFile(path.join(directory, 'flow.json'), '{}'),
      writeFile(path.join(directory, 'graph-spec.schema.json'), '{}'),
    ]);

    const sources = await listDiagramSources(directory);
    assert.deepEqual(sources.map(({ name, kind }) => ({ name, kind })), [
      { name: 'architecture', kind: 'drawio' },
      { name: 'flow', kind: 'json' },
    ]);
    assert.deepEqual(selectDiagramSources(sources, ['flow']).map((source) => source.name), ['flow']);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('rejects duplicate source names and missing requested diagrams', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-diagrams-'));
  try {
    await Promise.all([
      writeFile(path.join(directory, 'same.drawio'), '<mxfile/>'),
      writeFile(path.join(directory, 'same.json'), '{}'),
    ]);
    await assert.rejects(() => listDiagramSources(directory), /ambiguous/);
    assert.throws(() => selectDiagramSources([], ['missing']), /not found/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('hashes JSON semantically and draw.io XML with normalized newlines', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-diagrams-'));
  try {
    const firstJson = path.join(directory, 'first.json');
    const secondJson = path.join(directory, 'second.json');
    const firstDrawio = path.join(directory, 'first.drawio');
    const secondDrawio = path.join(directory, 'second.drawio');
    await Promise.all([
      writeFile(firstJson, '{"b":2,"a":1}\n'),
      writeFile(secondJson, '{\n  "a": 1,\n  "b": 2\n}'),
      writeFile(firstDrawio, '<mxfile>\r\n</mxfile>\r\n'),
      writeFile(secondDrawio, '<mxfile>\n</mxfile>'),
    ]);

    assert.equal(
      await diagramSourceHash({ kind: 'json', path: firstJson }),
      await diagramSourceHash({ kind: 'json', path: secondJson }),
    );
    assert.equal(
      await diagramSourceHash({ kind: 'drawio', path: firstDrawio }),
      await diagramSourceHash({ kind: 'drawio', path: secondDrawio }),
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('builds deterministic draw.io export arguments', () => {
  assert.deepEqual(drawioExportArgs('source.drawio', 'diagram.png', 'png'), [
    '--export', '--format', 'png', '--border', '16', '--scale', '2', '--output', 'diagram.png', 'source.drawio',
  ]);
  assert.deepEqual(drawioExportArgs('source.drawio', 'diagram.svg', 'svg'), [
    '--export', '--format', 'svg', '--border', '16', '--embed-diagram', '--output', 'diagram.svg', 'source.drawio',
  ]);
  assert.deepEqual(drawioExportArgs('source.drawio', 'diagram.pdf', 'pdf', { embed: false }), [
    '--export', '--format', 'pdf', '--border', '16', '--output', 'diagram.pdf', 'source.drawio',
  ]);
  assert.throws(() => drawioExportArgs('source.drawio', 'diagram.gif', 'gif'), /Unsupported/);
});

test('respects explicit draw.io command and wraps headless Linux exports', () => {
  assert.deepEqual(resolveDrawioCommand({ explicitPath: 'C:\\tools\\draw.io.exe' }), {
    command: 'C:\\tools\\draw.io.exe',
    prefixArgs: [],
  });
  assert.deepEqual(resolveDrawioCommand({ platform: 'linux', env: {} }), {
    command: 'xvfb-run',
    prefixArgs: ['-a', 'drawio', '--no-sandbox'],
  });
});

test('validates editable XML and enforces the pinned draw.io Desktop version', () => {
  assert.equal(validateUncompressedDrawio('<mxfile compressed="false"><diagram><mxGraphModel/></diagram></mxfile>'), true);
  assert.throws(() => validateUncompressedDrawio('<mxfile compressed="true"><diagram><mxGraphModel/></diagram></mxfile>'), /uncompressed/);
  assert.throws(() => validateUncompressedDrawio('<svg/>'), /not editable/);

  const execute = (_command, args) => {
    assert.deepEqual(args, ['--version']);
    return `draw.io ${DRAWIO_CLI_VERSION}\n`;
  };
  assert.equal(verifyDrawioVersion({ command: 'draw.io' }, { execute }), DRAWIO_CLI_VERSION);
  assert.throws(
    () => verifyDrawioVersion({ command: 'draw.io' }, { execute: () => 'draw.io 1.2.3' }),
    /required for deterministic exports/,
  );
  assert.equal(
    verifyDrawioVersion({ command: 'draw.io' }, { execute: () => 'draw.io 1.2.3', allowMismatch: true }),
    '1.2.3',
  );
  const calls = [];
  assert.equal(verifyDrawioVersion(
    { command: 'C:\\tools\\draw.io.exe' },
    {
      platform: 'win32',
      execute: (command, args) => {
        calls.push({ command, args });
        return command === 'powershell.exe' ? `${DRAWIO_CLI_VERSION}.0` : '';
      },
    },
  ), DRAWIO_CLI_VERSION);
  assert.equal(calls[1].command, 'powershell.exe');
});

test('requires actual renderer provenance when creating stamps', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-diagrams-'));
  const sourcePath = path.join(directory, 'sample.json');
  const drawioPath = path.join(directory, 'sample.drawio');
  const pngPath = path.join(directory, 'sample.png');
  const source = { name: 'sample', kind: 'json', path: sourcePath };
  try {
    await Promise.all([
      writeFile(sourcePath, '{}'),
      writeFile(drawioPath, '<mxfile><mxGraphModel/></mxfile>'),
      writeFile(pngPath, 'png'),
    ]);
    await assert.rejects(
      () => createDiagramStamp(source, drawioPath, pngPath),
      /detected draw.io Desktop version is required/,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
