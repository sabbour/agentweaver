import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { expectedDotnetShardIds } from '../coverage.mjs';
import { main, parseArgs } from '../coverage-combine.mjs';

function fixtureDirectory(t) {
  const directory = mkdtempSync(path.join(tmpdir(), 'agentweaver-coverage-combine-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  return directory;
}

// This exercises the module through its real CLI entry point (not just
// regex-matching the workflow YAML that invokes it), so a load-time error
// such as importing a function from the wrong module — which previously
// shipped undetected because nothing actually executed this file — fails
// the suite immediately.
test('coverage-combine requires both CLI arguments', () => {
  assert.throws(() => parseArgs([]), /usage: coverage-combine\.mjs/);
  assert.throws(() => parseArgs(['--downloaded-dir', 'x']), /usage: coverage-combine\.mjs/);
  assert.deepEqual(parseArgs(['--downloaded-dir', 'a', '--out', 'b']), { downloadedDir: 'a', out: 'b' });
});

test('coverage-combine reports every expected shard as missing when no artifacts were downloaded, without attempting a merge', (t) => {
  const root = fixtureDirectory(t);
  const downloadedDir = path.join(root, 'downloaded', 'dotnet-shards');
  const outDir = path.join(root, 'out');
  mkdirSync(downloadedDir, { recursive: true });

  assert.throws(
    () => main(['--downloaded-dir', downloadedDir, '--out', outDir]),
    /Required \.NET coverage families are partial/,
  );

  const status = JSON.parse(readFileSync(path.join(outDir, 'status.json'), 'utf8'));
  assert.equal(status.complete, false);
  assert.equal(status.completed.length, 0);
  assert.equal(status.combined, null);
  assert.equal(status.missing.length, expectedDotnetShardIds().length);
  for (const id of expectedDotnetShardIds()) {
    assert.ok(
      status.missing.some((entry) => entry.startsWith(`${id}: coverage artifact was not produced`)),
      `status.json should report ${id} as missing`,
    );
  }
});

test('coverage-combine rejects a malformed downloaded report without attempting a merge', (t) => {
  const root = fixtureDirectory(t);
  const downloadedDir = path.join(root, 'downloaded', 'dotnet-shards');
  const outDir = path.join(root, 'out');
  const [firstShardId] = expectedDotnetShardIds();
  const shardArtifactDir = path.join(downloadedDir, `dotnet-coverage-${firstShardId}`);
  mkdirSync(shardArtifactDir, { recursive: true });
  writeFileSync(path.join(shardArtifactDir, 'coverage.cobertura.xml'), 'not xml');

  assert.throws(
    () => main(['--downloaded-dir', downloadedDir, '--out', outDir]),
    /Required \.NET coverage families are partial/,
  );

  const status = JSON.parse(readFileSync(path.join(outDir, 'status.json'), 'utf8'));
  assert.equal(status.completed.length, 0);
  assert.ok(status.missing.some((entry) => entry.startsWith(`${firstShardId}: Cobertura report is malformed`)));
});
