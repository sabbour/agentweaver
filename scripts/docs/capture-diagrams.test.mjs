import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { checkSourceArtifacts } from './capture-diagrams.mjs';
import { createDiagramStamp, parseDiagramStamp } from './diagram-sources.mjs';
import { jsonFileToDrawio } from './drawio-generator.mjs';

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
        { rendererVersion: '31.4.5' },
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
        { rendererVersion: '31.4.5' },
      )),
    );
    assert.equal((await checkSourceArtifacts(source, { outputDirectory, generatedDirectory })).ok, true);
    await writeFile(path.join(outputDirectory, 'sample.png'), 'changed');
    assert.match((await checkSourceArtifacts(source, { outputDirectory, generatedDirectory })).message, /PNG drift/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('records the actual renderer version while preserving rendering recipe checks', async () => {
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
      { rendererVersion: '31.5.3.0' },
    );
    assert.equal(stamp.renderer.rendererVersion, '31.5.3.0');
    assert.deepEqual(parseDiagramStamp(JSON.stringify(stamp)), stamp);
    assert.equal(parseDiagramStamp(JSON.stringify({
      ...stamp, renderer: { ...stamp.renderer, rendererVersion: '31.4.5' },
    })).renderer.rendererVersion, '31.4.5');
    for (const renderer of [
      { ...stamp.renderer, rendererVersion: undefined },
      { ...stamp.renderer, rendererVersion: 'unknown' },
      { ...stamp.renderer, scale: 1 },
      { ...stamp.renderer, border: 0 },
      { ...stamp.renderer, format: 'svg' },
    ]) {
      assert.throws(
        () => parseDiagramStamp(JSON.stringify({ ...stamp, renderer })),
        /does not match the current draw.io rendering recipe/,
      );
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
