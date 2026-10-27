import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { loadP1NativeProviders } from './cli.mjs';
import { validateP1NativeProbeResults } from '../../harness-shared/p1-acceptance-result.mjs';

const cli = fileURLToPath(new URL('./cli.mjs', import.meta.url));
const input = {
  environment: {
    baseUrl: 'https://environment.example.test',
    issuer: 'https://issuer.example.test',
    audience: 'environment',
    actorId: 'actor',
    tenantId: 'tenant',
  },
  pins: {
    context: {
      kubeContext: 'must-not-be-contacted',
      namespace: 'disposable',
      clusterIdentity: { sha256: 'a'.repeat(64) },
    },
    namespace: 'disposable',
  },
};
const factory = `export function createP1NativeProviders({ input }) {
  input.environment.baseUrl = 'https://must-not-replace-pins.example.test';
  return {
    authProvider: { getValidatedToken() { throw new Error('must not acquire credentials'); } },
    journeyAuthorizer: { authorize() { throw new Error('must not authorize mutation'); } },
    runtimeReadinessProvider: { readCurrent() { throw new Error('must not read live state'); } },
  };
}`;

async function fixture(t, source = factory) {
  const directory = await mkdtemp(join(tmpdir(), 'p1-native-cli-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const inputPath = join(directory, 'input.json');
  const providerPath = join(directory, 'owner-providers.mjs');
  const outputPath = join(directory, 'native.json');
  await Promise.all([
    writeFile(inputPath, JSON.stringify(input)),
    writeFile(providerPath, source),
  ]);
  return {
    directory, inputPath, providerPath, outputPath,
    args: ['--execute', '--input', inputPath, '--provider-module', providerPath, '--output', outputPath],
  };
}

function execute(args) {
  return spawnSync(process.execPath, [cli, ...args], { encoding: 'utf8', timeout: 15_000 });
}

test('importing the CLI is inert and help requires no inputs or providers', () => {
  const imported = spawnSync(process.execPath, ['--input-type=module', '-e',
    `await import(${JSON.stringify(new URL('./cli.mjs', import.meta.url).href)})`],
  { encoding: 'utf8', timeout: 15_000 });
  assert.equal(imported.status, 0);
  assert.equal(imported.stdout, '');
  assert.equal(imported.stderr, '');
  const result = execute(['--help']);
  assert.equal(result.status, 0);
  assert.match(result.stdout, /--execute.*--provider-module/);
});

test('standalone CLI retains a sealed blocked envelope for missing genuine receipts', async t => {
  const f = await fixture(t);
  const result = execute(f.args);
  assert.equal(result.status, 2, result.stderr);
  assert.equal(result.stderr, '');
  const envelope = JSON.parse(await readFile(f.outputPath, 'utf8'));
  validateP1NativeProbeResults([envelope]);
  assert.equal(envelope.overallStatus, 'blocked');
  assert.equal(envelope.probes.length, 4);
  assert.ok(envelope.probes.every(probe =>
    probe.status === 'blocked' && probe.code === 'provider_receipt_mismatch'
      && probe.cleanup.status === 'not-started'));
  assert.doesNotMatch(JSON.stringify(envelope), /must not acquire|must-not-replace/);
});

test('provider factory cannot rewrite pins or replace fixed transports or the clock', async t => {
  const f = await fixture(t);
  const original = structuredClone(input);
  const dependencies = await loadP1NativeProviders(original, f.providerPath);
  assert.deepEqual(original, input);
  assert.equal(dependencies.fetch, globalThis.fetch);
  assert.equal(dependencies.now, undefined);
  assert.equal(typeof dependencies.processProvider.run, 'function');
  await assert.rejects(() => dependencies.processProvider.run({
    recipeId: 'arbitrary-command', args: {},
  }));
  for (const extra of ['processProvider', 'fetch', 'now', 'environmentPodProvider']) {
    const path = join(f.directory, `${extra}.mjs`);
    await writeFile(path, factory.replace('authProvider:', `${extra}: {}, authProvider:`));
    await assert.rejects(() => loadP1NativeProviders(input, path), /exact_owner_providers_required/);
  }
});

test('missing execution intent and ambiguous or unknown options block before module import', async t => {
  const f = await fixture(t, `throw new Error('module-must-not-be-loaded');`);
  const cases = [
    f.args.filter(arg => arg !== '--execute'),
    [...f.args, '--execute'],
    [...f.args, '--input', f.inputPath],
    [...f.args, '--command', 'anything'],
    [...f.args, '--help'],
    [...f.args.slice(0, -1), ''],
  ];
  for (const args of cases) {
    const result = execute(args);
    assert.equal(result.status, 2);
    assert.match(result.stderr, /blocked at arguments/);
    assert.doesNotMatch(result.stderr, /module-must-not/);
  }
  await assert.rejects(readFile(f.outputPath), { code: 'ENOENT' });
});

test('output is reserved exclusively before importing any provider code', async t => {
  const f = await fixture(t, `throw new Error('module-must-not-be-loaded');`);
  await writeFile(f.outputPath, 'existing evidence');
  const result = execute(f.args);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /blocked at output_reservation/);
  assert.equal(await readFile(f.outputPath, 'utf8'), 'existing evidence');
});

test('malformed input blocks before provider import and output reservation', async t => {
  const f = await fixture(t);
  await writeFile(f.inputPath, '{invalid-secret-input');
  const result = execute(f.args);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /blocked at input_read/);
  assert.doesNotMatch(result.stderr, /invalid-secret-input/);
  await assert.rejects(readFile(f.outputPath), { code: 'ENOENT' });
});

test('provider contract and load errors block without leaking raw provider failures', async t => {
  const sources = [
    'export const notTheFactory = true;',
    'export function createP1NativeProviders() { return {}; }',
    factory.replace('runtimeReadinessProvider:', 'missingReadinessProvider:'),
    factory.replace('getValidatedToken()', 'notTheRequiredMethod()'),
    'throw new Error("sensitive-provider-error");',
    'export async function createP1NativeProviders() { throw new Error("sensitive-provider-error"); }',
  ];
  for (const source of sources) {
    const f = await fixture(t, source);
    const result = execute(f.args);
    assert.equal(result.status, 2);
    assert.match(result.stderr, /blocked at provider_load/);
    assert.doesNotMatch(result.stderr + result.stdout, /sensitive-provider-error/);
    assert.equal(await readFile(f.outputPath, 'utf8'), '');
  }
});
