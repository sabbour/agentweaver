import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { markerFile, recordAttempt, selectAttempts } from '../attempts.mjs';

const workflow = readFileSync(fileURLToPath(new URL('../../../.github/workflows/v1-ci.yml', import.meta.url)), 'utf8');

/** Creates a downloads directory with one artifact per entry, as download-artifact lays them out. */
function downloads(entries) {
  const root = mkdtempSync(join(tmpdir(), 'agentweaver-coverage-attempts-'));
  const source = join(root, 'downloads');
  for (const { name, suite, attempt, status = 'success', trx } of entries) {
    const directory = join(source, name);
    if (suite) recordAttempt({ directory, suite, runAttempt: String(attempt), jobStatus: status });
    else mkdirSync(directory, { recursive: true });
    if (trx) writeFileSync(join(directory, `${suite}.trx`), trx);
  }
  return { root, source, destination: join(root, 'coverage') };
}

const surfaces = [
  { name: 'v1-node-coverage-1', suite: 'node', attempt: 1 },
  { name: 'v1-web-coverage-1', suite: 'web', attempt: 1 },
];

test('a failed-jobs rerun combines the passing rerun shard, not the failed first attempt', () => {
  const { root, source, destination } = downloads([
    ...surfaces,
    { name: 'v1-dotnet-identity-broker-1-1', suite: 'identity-broker-1', attempt: 1, trx: 'passed' },
    { name: 'v1-dotnet-identity-broker-2-1', suite: 'identity-broker-2', attempt: 1, status: 'failure', trx: 'failed=1' },
    { name: 'v1-dotnet-identity-broker-2-2', suite: 'identity-broker-2', attempt: 2, trx: 'passed' },
  ]);
  try {
    const manifest = selectAttempts(source, destination);
    assert.equal(readFileSync(join(destination, 'dotnet/identity-broker-2/identity-broker-2.trx'), 'utf8'), 'passed');
    assert.equal(readFileSync(join(destination, 'dotnet/identity-broker-1/identity-broker-1.trx'), 'utf8'), 'passed');
    assert.ok(existsSync(join(destination, 'node', markerFile)));
    assert.ok(existsSync(join(destination, 'web', markerFile)));
    assert.deepEqual(manifest.suites['dotnet/identity-broker-2'],
      { artifact: 'v1-dotnet-identity-broker-2-2', attempt: 2, superseded: [1] });
    assert.deepEqual(manifest.suites['dotnet/identity-broker-1'].superseded, []);
    assert.deepEqual(JSON.parse(readFileSync(join(destination, 'attempts.json'), 'utf8')), manifest);
    // The failed attempt stays out of the coverage layout.
    assert.ok(existsSync(join(source, 'v1-dotnet-identity-broker-2-1')));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('combining fails when the newest attempt is missing, mislabeled, or from a failed job', async (t) => {
  const cases = [
    ['an artifact without a run attempt', [{ name: 'v1-dotnet-identity-broker-2' }], /no attempt\.json/],
    ['an artifact name that is not a coverage artifact', [{ name: 'v1-web-dist' }], /not named <artifact>-<run attempt>/],
    ['data recorded by another attempt',
      [{ name: 'v1-dotnet-identity-broker-2-2', suite: 'identity-broker-2', attempt: 1 }], /attempt 1/],
    ['data recorded by another suite',
      [{ name: 'v1-dotnet-identity-broker-2-1', suite: 'identity-broker-1', attempt: 1 }], /identity-broker-1 attempt 1/],
    ['a newest attempt that failed', [
      { name: 'v1-dotnet-mcp-1', suite: 'mcp', attempt: 1 },
      { name: 'v1-dotnet-mcp-2', suite: 'mcp', attempt: 2, status: 'failure' },
    ], /from a failure job/],
  ];
  for (const [name, dotnet, error] of cases) {
    await t.test(name, () => {
      const { root, source, destination } = downloads([...surfaces, ...dotnet]);
      try {
        assert.throws(() => selectAttempts(source, destination), error);
        assert.equal(existsSync(join(destination, 'attempts.json')), false);
      } finally {
        rmSync(root, { recursive: true, force: true });
      }
    });
  }
});

test('combining fails when a surface is missing', () => {
  const { root, source, destination } = downloads([
    { name: 'v1-node-coverage-1', suite: 'node', attempt: 1 },
    { name: 'v1-dotnet-mcp-1', suite: 'mcp', attempt: 1 },
  ]);
  try {
    assert.throws(() => selectAttempts(source, destination), /No web coverage artifact/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('recording rejects a missing status or an invalid attempt', () => {
  const directory = mkdtempSync(join(tmpdir(), 'agentweaver-coverage-record-'));
  try {
    assert.throws(() => recordAttempt({ directory, suite: 'web', runAttempt: '0', jobStatus: 'success' }), /positive integer/);
    assert.throws(() => recordAttempt({ directory, suite: 'web', runAttempt: '1', jobStatus: '' }), /Job status/);
    assert.deepEqual(recordAttempt({ directory, suite: 'web', runAttempt: '3', jobStatus: 'success' }),
      { suite: 'web', runAttempt: 3, jobStatus: 'success' });
  } finally {
    rmSync(resolve(directory), { recursive: true, force: true });
  }
});

test('the CI workflow names every coverage artifact by attempt and selects attempts before combining', () => {
  for (const name of ['v1-dotnet-${{ matrix.name }}', 'v1-node-coverage', 'v1-web-coverage']) {
    assert.ok(workflow.includes(`name: ${name}-\${{ github.run_attempt }}`), `${name} is not bound to its attempt`);
  }
  assert.equal(workflow.match(/node scripts\/coverage\/attempts\.mjs record /g)?.length, 3);
  const select = workflow.indexOf('node scripts/coverage/attempts.mjs select artifacts/downloads artifacts/coverage');
  assert.ok(select > 0 && select < workflow.indexOf('node scripts/coverage/dotnet.mjs --combine'));
  assert.doesNotMatch(workflow, /name: v1-(node|web)-coverage\r?\n\s+path: artifacts\/coverage\//);
});
