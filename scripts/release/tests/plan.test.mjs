import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { bumpVersion, createPlan, createPlanFromFile, planChecksum } from '../plan.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const planScript = path.join(repositoryRoot, 'scripts', 'release', 'plan.mjs');

function git(root, ...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

/** Creates a committed one-package repository with two pending changesets for the same component. */
function fixture(t) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'v1-plan-'));
  t.after(() => rmSync(root, {
    recursive: true, force: true, maxRetries: 10, retryDelay: 50,
  }));
  mkdirSync(path.join(root, 'packages', 'Pkg'), { recursive: true });
  mkdirSync(path.join(root, '.changeset'), { recursive: true });
  mkdirSync(path.join(root, 'releases'), { recursive: true });
  writeFileSync(path.join(root, 'packages', 'Pkg', 'Pkg.csproj'),
    '<Project><PropertyGroup><Version>1.4.2</Version></PropertyGroup></Project>');
  writeFileSync(path.join(root, 'packages', 'Pkg', 'packages.lock.json'), '{"version":1,"dependencies":{}}');
  writeFileSync(path.join(root, 'releases', 'foundation.json'), `${JSON.stringify({
    schemaVersion: 1,
    stage: 'draft',
    components: [{ id: 'Pkg', kind: 'library', version: '1.4.2', project: 'packages/Pkg/Pkg.csproj' }],
    compatibility: [],
  }, null, 2)}\n`);
  writeFileSync(path.join(root, '.changeset', 'fix.md'), '---\n"Pkg": patch\n---\n\nFix a defect.\n');
  writeFileSync(path.join(root, '.changeset', 'feature.md'), '---\n"Pkg": minor\n---\n\nAdd a capability.\n');
  git(root, 'init', '-q');
  git(root, 'config', 'core.autocrlf', 'false');
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'plan source');
  return root;
}

/**
 * Adds a valid initial baseline record, its changeset, and its notes receipt to a fixture.
 * Commits the files when `commit` is true; otherwise they stay only in the working tree.
 */
function addInitialBaseline(root, { commit }) {
  const receiptSha = 'a'.repeat(40);
  const receiptPath = `releases/receipts/${receiptSha}.json`;
  mkdirSync(path.join(root, 'releases', 'receipts'), { recursive: true });
  writeFileSync(path.join(root, '.changeset', 'initial-baseline.md'),
    '---\n"Pkg": baseline\n---\n\nSet the first package baseline without a version bump.\n');
  writeFileSync(path.join(root, receiptPath), JSON.stringify({
    schemaVersion: 1, sourceSha: receiptSha, components: [{ id: 'Pkg' }], changesets: [],
  }));
  writeFileSync(path.join(root, 'releases', 'initial-baseline.json'), `${JSON.stringify({
    schemaVersion: 1,
    issue: 1825,
    baselineVersion: '0.0.0',
    components: ['Pkg'],
    changeset: '.changeset/initial-baseline.md',
    initialNotesReceipt: receiptPath,
  }, null, 2)}\n`);
  if (commit) {
    git(root, 'add', '.');
    git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial baseline');
  }
}

/** Reads the fixture manifest as the object that `createPlan` expects. */
function readFixtureManifest(root) {
  return JSON.parse(readFileSync(path.join(root, 'releases', 'foundation.json'), 'utf8'));
}

function commitAll(root, message) {
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', message);
}

function sizedChangeset(size) {
  const prefix = '---\n"Pkg": patch\n---\n\n';
  assert.ok(size > Buffer.byteLength(prefix) + 1);
  return prefix + 'x'.repeat(size - Buffer.byteLength(prefix) - 1) + '\n';
}

test('bumps the stable semver core and discards prerelease or build metadata', () => {
  assert.equal(bumpVersion('1.2.3', 'patch'), '1.2.4');
  assert.equal(bumpVersion('1.2.3', 'minor'), '1.3.0');
  assert.equal(bumpVersion('1.2.3', 'major'), '2.0.0');
  assert.equal(bumpVersion('1.2.3-preview.4+sha.abc', 'patch'), '1.2.4');
  assert.equal(bumpVersion('0.9.9+build.7', 'minor'), '0.10.0');
});

test('rejects malformed, oversized, and unknown version bumps', () => {
  for (const version of ['1.2', '1.2.3.4', 'v1.2.3', '1.x.3', '']) {
    assert.throws(() => bumpVersion(version, 'patch'), (error) =>
      error.message === `version: cannot bump malformed core version "${version}"`);
  }
  assert.throws(() => bumpVersion('9007199254740993.0.0', 'major'), /version: version component in "9007199254740993\.0\.0" exceeds safe integer range/);
  assert.throws(() => bumpVersion('1.2.3', 'prerelease'), /bump: unknown bump "prerelease"/);
});

test('plans the highest pending bump for a component from a manifest file', (t) => {
  const root = fixture(t);
  const plan = createPlanFromFile(path.join(root, 'releases', 'foundation.json'), { root });

  assert.equal(plan.sourceSha, git(root, 'rev-parse', 'HEAD'));
  assert.deepEqual(plan.components, [{
    id: 'Pkg',
    fromVersion: '1.4.2',
    toVersion: '1.5.0',
    bump: 'minor',
    changesets: ['.changeset/feature.md', '.changeset/fix.md'],
  }]);
  assert.equal(plan.checksum, planChecksum(plan));
});

test('batches 129 changeset reads at a 128-record boundary with parity to the legacy reader', (t) => {
  const root = fixture(t);
  for (let index = 0; index < 127; index += 1) {
    writeFileSync(path.join(root, '.changeset', `batch-${String(index).padStart(3, '0')}.md`),
      '---\n"Pkg": patch\n---\n\nBatch boundary record.\n');
  }
  commitAll(root, 'batch boundary');
  const calls = [];
  const legacyCalls = [];
  const batched = createPlan(readFixtureManifest(root), {
    root,
    batchSpawnSync: (command, args, options) => {
      assert.equal(command, 'git');
      assert.deepEqual(args, ['cat-file', '--batch']);
      assert.equal(options.shell, false);
      calls.push(options.input);
      return spawnSync(command, args, options);
    },
    readLegacySource: (sourceSha, file) => {
      legacyCalls.push(file);
      return execFileSync('git', ['show', `${sourceSha}:${file}`], {
        cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
      });
    },
  });
  const legacy = createPlan(readFixtureManifest(root), {
    root,
    readSource: (sourceSha, file) => execFileSync('git', ['show', `${sourceSha}:${file}`], {
      cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
    }),
  });

  assert.deepEqual(calls.map((input) => input.trimEnd().split('\n').length), [128, 1]);
  assert.deepEqual(legacyCalls, []);
  assert.deepEqual(batched.components, legacy.components);
  assert.equal(batched.checksum, legacy.checksum);
});

test('preserves UTF-8 decoding, invalid-byte replacement, and mixed line endings', (t) => {
  const root = fixture(t);
  const file = '.changeset/fix.md';
  const bytes = Buffer.concat([
    Buffer.from('---\r\n"Pkg": patch\n---\r\n\r\nRésumé — 東京 '),
    Buffer.from([0xff]),
    Buffer.from('\r\n'),
  ]);
  writeFileSync(path.join(root, file), bytes);
  commitAll(root, 'utf8 and line endings');

  const plan = createPlan(readFixtureManifest(root), { root });
  assert.equal(plan.components[0].changesets.length, 2);

  writeFileSync(path.join(root, file),
    Buffer.from(bytes.toString('latin1').replaceAll('\r\n', '\n'), 'latin1'));
  assert.throws(() => createPlan(readFixtureManifest(root), { root }),
    /changeset bytes differ from source HEAD/);
});

test('uses an explicit historical source SHA instead of the current HEAD', (t) => {
  const root = fixture(t);
  const changesetPath = path.join(root, '.changeset', 'fix.md');
  const historicalText = readFileSync(changesetPath, 'utf8');
  const historicalSha = git(root, 'rev-parse', 'HEAD');
  writeFileSync(changesetPath, historicalText.replace('Fix a defect.', 'Different at HEAD.'));
  commitAll(root, 'later changeset edit');
  const currentSha = git(root, 'rev-parse', 'HEAD');
  assert.notEqual(historicalSha, currentSha);
  writeFileSync(changesetPath, historicalText);
  const inputs = [];
  const legacyCalls = [];

  const plan = createPlan(readFixtureManifest(root), {
    root,
    git: (command, ...args) => command === 'rev-parse' && args[0] === 'HEAD'
      ? historicalSha
      : git(root, command, ...args),
    batchSpawnSync: (command, args, options) => {
      inputs.push(options.input);
      return spawnSync(command, args, options);
    },
    readLegacySource: (sourceSha, file) => {
      legacyCalls.push(file);
      return execFileSync('git', ['show', `${sourceSha}:${file}`], {
        cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
      });
    },
  });
  assert.equal(plan.sourceSha, historicalSha);
  assert.equal(plan.checksum, planChecksum(plan));
  assert.ok(inputs.length > 0);
  assert.ok(inputs.every((input) => input.split('\n').filter(Boolean)
    .every((expression) => expression.startsWith(`${historicalSha}:`))));
  assert.deepEqual(legacyCalls, []);
});

test('keeps supplied source readers in the original order and skips batching', (t) => {
  const root = fixture(t);
  const expected = ['.changeset/feature.md', '.changeset/fix.md'];
  const calls = [];
  const plan = createPlan(readFixtureManifest(root), {
    root,
    readdirSync: () => ['feature.md', 'fix.md'],
    readSource: (sourceSha, file) => {
      calls.push(file);
      return execFileSync('git', ['show', `${sourceSha}:${file}`], {
        cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
      });
    },
    batchSpawnSync: () => {
      throw new Error('batching must not run for a supplied source reader');
    },
  });
  assert.deepEqual(calls, expected);
  assert.equal(plan.components[0].changesets.length, 2);
});

test('falls back to legacy reads for failed and truncated batch output', async (t) => {
  const root = fixture(t);
  const sha = git(root, 'rev-parse', 'HEAD');
  const firstPayload = execFileSync('git', ['show', `${sha}:.changeset/feature.md`], { cwd: root });
  const secondPayload = execFileSync('git', ['show', `${sha}:.changeset/fix.md`], { cwd: root });
  for (const [name, response] of [
    ['failure', { status: 1, signal: null, error: new Error('batch failed'), stdout: Buffer.alloc(0), stderr: Buffer.from('failed') }],
    ['incomplete payload', {
      status: 0,
      signal: null,
      stdout: Buffer.concat([
        Buffer.from(`${sha} blob 4\n`),
        Buffer.from('abc'),
      ]),
      stderr: Buffer.alloc(0),
    }],
    ['missing payload delimiter', {
      status: 0,
      signal: null,
      stdout: Buffer.concat([
        Buffer.from(`${sha} blob ${firstPayload.length}\n`),
        firstPayload,
        Buffer.from('!'),
        Buffer.from(`${sha} blob ${secondPayload.length}\n`),
        secondPayload,
        Buffer.from('\n'),
      ]),
      stderr: Buffer.alloc(0),
    }],
  ]) {
    await t.test(name, () => {
      const legacyCalls = [];
      const plan = createPlan(readFixtureManifest(root), {
        root,
        batchSpawnSync: () => response,
        readLegacySource: (sourceSha, file) => {
          legacyCalls.push(file);
          return execFileSync('git', ['show', `${sourceSha}:${file}`], {
            cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
          });
        },
      });
      assert.equal(plan.components[0].changesets.length, 2);
      assert.deepEqual(legacyCalls, ['.changeset/feature.md', '.changeset/fix.md']);
    });
  }
});

test('rejects high-bit batch headers that ascii decoding would turn into valid syntax', (t) => {
  const root = fixture(t);
  const sourceSha = git(root, 'rev-parse', 'HEAD');
  const legacyCalls = [];
  const feature = execFileSync('git', ['show', `${sourceSha}:.changeset/feature.md`], { cwd: root });
  const fix = execFileSync('git', ['show', `${sourceSha}:.changeset/fix.md`], { cwd: root });
  const invalidHeader = Buffer.concat([
    Buffer.from(`${sourceSha} `),
    Buffer.from([0xe2]),
    Buffer.from(`lob ${feature.length}\n`),
    feature,
    Buffer.from('\n'),
    Buffer.from(`${sourceSha} blob ${fix.length}\n`),
    fix,
    Buffer.from('\n'),
  ]);

  const plan = createPlan(readFixtureManifest(root), {
    root,
    batchSpawnSync: () => ({
      status: 0,
      signal: null,
      stdout: invalidHeader,
      stderr: Buffer.alloc(0),
    }),
    readLegacySource: (sha, file) => {
      legacyCalls.push(file);
      return execFileSync('git', ['show', `${sha}:${file}`], {
        cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
      });
    },
  });

  assert.equal(plan.components[0].changesets.length, 2);
  assert.deepEqual(legacyCalls, ['.changeset/feature.md', '.changeset/fix.md']);
});

test('reports an uncommitted source record through the original missing-source error', (t) => {
  const root = fixture(t);
  writeFileSync(path.join(root, '.changeset', 'new.md'),
    '---\n"Pkg": patch\n---\n\nNot committed.\n');
  assert.throws(() => createPlan(readFixtureManifest(root), { root }),
    /\.changeset\/new\.md: missing committed source changeset: /);
});

test('reports an earlier local mismatch before a later oversized source failure', (t) => {
  const root = fixture(t);
  const first = '.changeset/a-first.md';
  const large = '.changeset/z-large.md';
  writeFileSync(path.join(root, first), '---\n"Pkg": patch\n---\n\nFirst.\n');
  writeFileSync(path.join(root, large), sizedChangeset(1258291));
  commitAll(root, 'oversized source fallback order');
  writeFileSync(path.join(root, first), '---\n"Pkg": patch\n---\n\nEdited first.\n');

  assert.throws(() => createPlan(readFixtureManifest(root), {
    root,
    readdirSync: () => ['a-first.md', 'z-large.md'],
  }), /a-first\.md: changeset bytes differ from source HEAD/);
});

test('rejects a committed 1.2 MiB changeset with the original ENOBUFS diagnostic', (t) => {
  const root = fixture(t);
  writeFileSync(path.join(root, '.changeset', 'large.md'), sizedChangeset(1258291));
  commitAll(root, 'oversized changeset');

  assert.throws(() => createPlan(readFixtureManifest(root), { root }), (error) =>
    /\.changeset\/large\.md: missing committed source changeset: /.test(error.message) &&
    /ENOBUFS/.test(error.message));
});

test('falls back after framing exceeds the cap for an exact 1 MiB committed changeset', (t) => {
  const root = fixture(t);
  writeFileSync(path.join(root, '.changeset', 'one-mib.md'), sizedChangeset(1024 * 1024));
  commitAll(root, 'exact one MiB changeset');

  const plan = createPlan(readFixtureManifest(root), { root });
  assert.ok(plan.components[0].changesets.includes('.changeset/one-mib.md'));
});

test('skips batching for empty discovery and rejects unsafe discovered names before Git', (t) => {
  const root = fixture(t);
  for (const name of ['feature.md', 'fix.md']) rmSync(path.join(root, '.changeset', name));
  commitAll(root, 'empty changesets');
  const plan = createPlan(readFixtureManifest(root), {
    root,
    batchSpawnSync: () => {
      throw new Error('batching must not run for empty discovery');
    },
  });
  assert.deepEqual(plan.components, []);

  assert.throws(() => createPlan(readFixtureManifest(root), {
    root,
    readdirSync: () => ['../escape.md'],
    batchSpawnSync: () => {
      throw new Error('Git must not receive an unsafe path');
    },
  }), /expected a safe \.changeset\/\*\.md record name/);
});

test('binds a committed initial baseline record to the plan and still plans other pending bumps', (t) => {
  const root = fixture(t);
  addInitialBaseline(root, { commit: true });
  const plan = createPlan(readFixtureManifest(root), { root });

  assert.equal(plan.sourceSha, git(root, 'rev-parse', 'HEAD'));
  assert.deepEqual(plan.initialBaseline, {
    baselineVersion: '0.0.0',
    components: ['Pkg'],
    changeset: '.changeset/initial-baseline.md',
    initialNotesReceipt: `releases/receipts/${'a'.repeat(40)}.json`,
  });
  assert.deepEqual(plan.components.map(({ id, toVersion, changesets }) => ({ id, toVersion, changesets })), [{
    id: 'Pkg', toVersion: '1.5.0', changesets: ['.changeset/feature.md', '.changeset/fix.md'],
  }]);
  assert.equal(plan.checksum, planChecksum(plan));
});

test('rejects an initial baseline record that is not committed at the source commit', (t) => {
  const root = fixture(t);
  addInitialBaseline(root, { commit: false });
  assert.throws(() => createPlan(readFixtureManifest(root), { root }),
    /releases\/initial-baseline\.json: missing committed source baseline record: /);
});

test('rejects an initial baseline record whose working-tree bytes differ from the source commit', (t) => {
  const root = fixture(t);
  addInitialBaseline(root, { commit: true });
  const recordPath = path.join(root, 'releases', 'initial-baseline.json');
  writeFileSync(recordPath, readFileSync(recordPath, 'utf8').replace(/\n/g, '\r\n'));
  assert.throws(() => createPlan(readFixtureManifest(root), { root }),
    /releases\/initial-baseline\.json: baseline record bytes differ from source HEAD; commit edits or use a byte-preserving checkout/);
});

test('rejects a manifest file that fails release validation before planning', (t) => {
  const root = fixture(t);
  const manifestPath = path.join(root, 'releases', 'foundation.json');
  writeFileSync(manifestPath, '{"schemaVersion":1,"components":"not-a-list"}\n');
  assert.throws(() => createPlanFromFile(manifestPath, { root }));
});

test('the planning CLI reports usage errors without writing a plan', () => {
  for (const args of [[], ['releases/foundation.json', '--out']]) {
    const result = spawnSync(process.execPath, [planScript, ...args], { cwd: repositoryRoot, encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Release plan failed: usage: node scripts\/release\/plan\.mjs <manifest\.json> \[--out <plan\.json>\]/);
  }
});

test('the planning CLI writes the same source-bound plan as the library for the repository manifest', (t) => {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const outDir = mkdtempSync(path.join(parent, 'v1-plan-cli-'));
  t.after(() => rmSync(outDir, {
    recursive: true, force: true, maxRetries: 10, retryDelay: 50,
  }));
  const out = path.join(outDir, 'nested', 'plan.json');

  const expected = createPlanFromFile(path.join(repositoryRoot, 'releases', 'foundation.json'), { root: repositoryRoot });
  const result = spawnSync(process.execPath, [planScript, 'releases/foundation.json', '--out', out], {
    cwd: repositoryRoot, encoding: 'utf8',
  });
  assert.equal(result.status, 0, result.stderr);

  if (expected.components.length === 0 && !expected.initialBaseline) {
    assert.match(result.stdout, /No pending changesets; nothing to plan\./);
    assert.equal(existsSync(out), false);
    return;
  }
  const written = JSON.parse(readFileSync(out, 'utf8'));
  assert.equal(written.sourceSha, expected.sourceSha);
  assert.equal(written.checksum, expected.checksum);
  assert.deepEqual(written.components, expected.components);
  assert.match(result.stdout, new RegExp(`source ${expected.sourceSha}`));
  for (const component of expected.components) {
    assert.ok(result.stdout.includes(`${component.id}: ${component.fromVersion} -> ${component.toVersion} (${component.bump})`));
  }
});
