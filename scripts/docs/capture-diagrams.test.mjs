import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { checkSourceArtifacts, render } from './capture-diagrams.mjs';
import { createDiagramStamp, parseDiagramStamp } from './diagram-sources.mjs';
import { DRAWIO_CLI_VERSION, jsonFileToDrawio } from './drawio-generator.mjs';

test('detects missing, stale, and current generated draw.io artifacts', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-drawio-check-'));
  const outputDirectory = path.join(root, 'out');
  const generatedDirectory = path.join(root, 'generated');
  const sourcePath = path.join(root, 'sample.json');
  const source = { name: 'sample', kind: 'json', path: sourcePath };
  try {
    await mkdir(outputDirectory);
    await mkdir(generatedDirectory);
    await writeFile(sourcePath, JSON.stringify({
      title: 'Sample',
      nodes: [{ id: 'a', label: 'A', icon: 'box', badge: { text: 'Step', tone: 'neutral' } }],
      edges: [],
    }));

    assert.match((await checkSourceArtifacts(source, { outputDirectory, generatedDirectory })).message, /Missing rendered output/);
    await writeFile(path.join(outputDirectory, 'sample.png'), 'png');
    await writeFile(path.join(outputDirectory, 'sample.hash.txt'), 'legacy-hash\n');
    assert.match((await checkSourceArtifacts(source, { outputDirectory, generatedDirectory })).message, /obsolete source-only hash/);
    await writeFile(path.join(generatedDirectory, 'sample.drawio'), '<mxfile><mxGraphModel/></mxfile>');
    await writeFile(
      path.join(outputDirectory, 'sample.hash.txt'),
      JSON.stringify(await createDiagramStamp(
        source,
        path.join(generatedDirectory, 'sample.drawio'),
        path.join(outputDirectory, 'sample.png'),
        { rendererVersion: DRAWIO_CLI_VERSION },
      )),
    );
    assert.match((await checkSourceArtifacts(source, { outputDirectory, generatedDirectory })).message, /stale/);
    await writeFile(path.join(generatedDirectory, 'sample.drawio'), await jsonFileToDrawio(sourcePath, { name: 'sample' }));
    await writeFile(
      path.join(outputDirectory, 'sample.hash.txt'),
      JSON.stringify(await createDiagramStamp(
        source,
        path.join(generatedDirectory, 'sample.drawio'),
        path.join(outputDirectory, 'sample.png'),
        { rendererVersion: DRAWIO_CLI_VERSION },
      )),
    );
    assert.equal((await checkSourceArtifacts(source, { outputDirectory, generatedDirectory })).ok, true);
    await writeFile(path.join(outputDirectory, 'sample.png'), 'changed');
    assert.match((await checkSourceArtifacts(source, { outputDirectory, generatedDirectory })).message, /PNG drift/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('cannot accept a mismatched renderer as canonical provenance', async () => {
  await assert.rejects(
    () => render(
      ['canonical-coordinator-architecture'],
      {
        drawioCli: 'fake-draw.io',
        allowVersionMismatch: true,
        execute: () => 'draw.io 99.8.7',
      },
    ),
    /99\.8\.7 cannot write canonical diagram outputs or stamps/,
  );

  const root = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-drawio-version-'));
  const sourcePath = path.join(root, 'sample.json');
  const drawioPath = path.join(root, 'sample.drawio');
  const pngPath = path.join(root, 'sample.png');
  const source = { name: 'sample', kind: 'json', path: sourcePath };
  try {
    await Promise.all([
      writeFile(sourcePath, '{"title":"Sample","nodes":[],"edges":[]}'),
      writeFile(drawioPath, '<mxfile><mxGraphModel/></mxfile>'),
      writeFile(pngPath, 'png'),
    ]);
    const stamp = await createDiagramStamp(
      source,
      drawioPath,
      pngPath,
      { rendererVersion: '99.8.7' },
    );
    assert.equal(stamp.renderer.rendererVersion, '99.8.7');
    assert.throws(
      () => parseDiagramStamp(JSON.stringify(stamp), 'fake.hash.txt'),
      /does not match the current draw.io rendering recipe/,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
