import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { bumpVersion, createPlanFromFile, planChecksum } from '../plan.mjs';

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
  t.after(() => rmSync(root, { recursive: true, force: true }));
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
  t.after(() => rmSync(outDir, { recursive: true, force: true }));
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
