import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { lstat, mkdir, mkdtemp, readFile, readdir, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { KIND, runAdmissionPreflight } from '../squad-admission-preflight.mjs';
import { materializeAdmissionLedger, parseMaterializeArguments } from '../squad-admission-ledger.mjs';

const repository = 'sabbour/agentweaver';
const prNumber = '1508';
const headA = 'a'.repeat(40);
const headB = 'b'.repeat(40);
const ledger = (headSha) => ({
  kind: KIND,
  repository,
  prNumber: 1508,
  headSha,
  findings: [],
});

async function fixture() {
  const root = await mkdtemp(join(tmpdir(), 'agentweaver-ledger-'));
  const inputFile = join(root, 'input.json');
  return { root, inputFile };
}

async function materialize(root, inputFile, headSha, extra = {}) {
  return materializeAdmissionLedger(repository, prNumber, {
    stateDirectory: root,
    headSha,
    inputFile,
    readInput: readFile,
    ...extra,
  });
}

test('accepts only the fixed materialize CLI and rejects unknown or repeated options', () => {
  assert.deepEqual(parseMaterializeArguments([
    'materialize', repository, prNumber, '--head-sha', headA, '--input-file', 'ledger.json',
  ]), {
    repository,
    prNumber,
    headSha: headA,
    inputFile: 'ledger.json',
  });
  assert.throws(() => parseMaterializeArguments(['write', repository, prNumber]), /usage/u);
  assert.throws(() => parseMaterializeArguments([
    'materialize', repository, prNumber, '--head-sha', headA, '--head-sha', headA, '--input-file', 'ledger.json',
  ]), /repeated/u);
  for (const option of ['--root', '--output', '--destination', '--policy', '--reviewer', '--waiver', '--admit']) {
    assert.throws(() => parseMaterializeArguments([
      'materialize', repository, prNumber, '--head-sha', headA, '--input-file', 'ledger.json', option, 'value',
    ]), /unknown option/u);
  }
});

test('CLI failures are redacted JSON and never expose the input path', () => {
  const script = fileURLToPath(new URL('../squad-admission-ledger.mjs', import.meta.url));
  const secretPath = join(tmpdir(), 'reviewer-waiver-prose.json');
  const result = spawnSync(process.execPath, [
    script,
    'materialize',
    repository,
    prNumber,
    '--head-sha',
    headA,
    '--input-file',
    secretPath,
  ], { encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.deepEqual(JSON.parse(result.stderr), {
    operation: 'materialize',
    error: 'filesystem operation failed',
  });
  assert.equal(result.stderr.includes(secretPath), false);
});

test('creates, reads back, and idempotently preserves exact ledger bytes', async () => {
  const { root, inputFile } = await fixture();
  const bytes = Buffer.from(`${JSON.stringify(ledger(headA), null, 2)}\n`);
  await writeFile(inputFile, bytes);
  const created = await materialize(root, inputFile, headA);
  assert.equal(created.disposition, 'created');
  assert.equal(created.bytes, bytes.length);
  assert.deepEqual(Object.keys(created).sort(), [
    'bytes', 'disposition', 'headSha', 'ledgerKey', 'operation', 'prNumber', 'repository', 'sha256',
  ]);
  const destination = join(root, 'admission', 'findings', 'sabbour', 'agentweaver', '1508.json');
  assert.deepEqual(await readFile(destination), bytes);
  assert.equal((await lstat(destination)).isFile(), true);
  const unchanged = await materialize(root, inputFile, headA);
  assert.equal(unchanged.disposition, 'unchanged');
  assert.equal(unchanged.sha256, created.sha256);
});

test('requires the exact valid old head for replacement and preserves the old ledger on rejection', async () => {
  const { root, inputFile } = await fixture();
  await writeFile(inputFile, JSON.stringify(ledger(headA)));
  await materialize(root, inputFile, headA);
  const destination = join(root, 'admission', 'findings', 'sabbour', 'agentweaver', '1508.json');
  const original = await readFile(destination);
  await writeFile(inputFile, JSON.stringify(ledger(headB)));
  await assert.rejects(() => materialize(root, inputFile, headB), /replace-existing-head/u);
  await assert.rejects(() => materialize(root, inputFile, headB, { replaceExistingHead: 'c'.repeat(40) }), /stale/u);
  assert.deepEqual(await readFile(destination), original);
  const replaced = await materialize(root, inputFile, headB, { replaceExistingHead: headA });
  assert.equal(replaced.disposition, 'replaced');
  assert.equal(JSON.parse(await readFile(destination, 'utf8')).headSha, headB);
});

test('rejects malformed encoding, schema, and CLI context mismatches before writing', async () => {
  for (const [name, bytes, pattern] of [
    ['bom', Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(JSON.stringify(ledger(headA)))]), /BOM/u],
    ['utf8', Buffer.from([0xc3, 0x28]), /UTF-8/u],
    ['json', Buffer.from('{'), /valid JSON/u],
    ['schema', Buffer.from(JSON.stringify({ ...ledger(headA), unknown: true })), /contain exactly/u],
    ['repository', Buffer.from(JSON.stringify({ ...ledger(headA), repository: 'sabbour/other' })), /repository does not match/u],
    ['pr', Buffer.from(JSON.stringify({ ...ledger(headA), prNumber: 1509 })), /PR number does not match/u],
    ['head', Buffer.from(JSON.stringify({ ...ledger(headB) })), /stale/u],
  ]) {
    const { root, inputFile } = await fixture();
    await writeFile(inputFile, bytes);
    await assert.rejects(() => materialize(root, inputFile, headA), pattern, name);
    assert.deepEqual(await readdir(root), ['input.json']);
  }
});

test('rejects symlinked path components and leaves no temporary files', async (t) => {
  const { root, inputFile } = await fixture();
  await writeFile(inputFile, JSON.stringify(ledger(headA)));
  const outside = await mkdtemp(join(tmpdir(), 'agentweaver-ledger-outside-'));
  await mkdir(join(root, 'admission'));
  try {
    await symlink(outside, join(root, 'admission', 'findings'), 'junction');
  } catch (error) {
    if (error?.code === 'EPERM') {
      t.skip('symlink creation is unavailable');
      return;
    }
    throw error;
  }
  await assert.rejects(() => materialize(root, inputFile, headA), /symlink|junction/u);
  assert.deepEqual(await readdir(outside), []);
});

test('cleans up injected failures and restores the prior destination after failed read-back', async () => {
  const { root, inputFile } = await fixture();
  await writeFile(inputFile, JSON.stringify(ledger(headA)));
  await materialize(root, inputFile, headA);
  const destination = join(root, 'admission', 'findings', 'sabbour', 'agentweaver', '1508.json');
  const original = await readFile(destination);
  await writeFile(inputFile, JSON.stringify(ledger(headB)));
  let reads = 0;
  await assert.rejects(() => materialize(root, inputFile, headB, {
    replaceExistingHead: headA,
    operations: {
      readLedger: async (...args) => {
        const { readAdmissionLedger } = await import('../squad-admission-common.mjs');
        const result = await readAdmissionLedger(...args);
        reads += 1;
        if (reads === 3) result.bytes = Buffer.from('{}');
        return result;
      },
    },
  }), /read-back verification/u);
  assert.deepEqual(await readFile(destination), original);
  const entries = await readdir(join(root, 'admission', 'findings', 'sabbour', 'agentweaver'));
  assert.deepEqual(entries, ['1508.json']);
});

test('removes exclusive temporary files after an injected install failure', async () => {
  const { root, inputFile } = await fixture();
  await writeFile(inputFile, JSON.stringify(ledger(headA)));
  await assert.rejects(() => materialize(root, inputFile, headA, {
    operations: {
      link: async () => {
        const error = new Error('injected link failure');
        error.code = 'EIO';
        throw error;
      },
    },
  }), /injected link failure/u);
  const directory = join(root, 'admission', 'findings', 'sabbour', 'agentweaver');
  assert.deepEqual(await readdir(directory), []);
});

test('materialize output feeds preflight with the same persisted digest', async () => {
  const { root, inputFile } = await fixture();
  await writeFile(inputFile, `${JSON.stringify(ledger(headA))}\n`);
  const written = await materialize(root, inputFile, headA);
  const checked = await runAdmissionPreflight(repository, prNumber, { stateDirectory: root, headSha: headA });
  assert.equal(checked.admitted, true);
  assert.equal(checked.sha256, written.sha256);
  assert.equal(checked.bytes, written.bytes);
});
