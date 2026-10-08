import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync, renameSync as nativeRenameSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { applyPlan } from '../apply.mjs';
import { diffChanges, validateChangesets } from '../changesets.mjs';
import { createPlan } from '../plan.mjs';

const component = {
  id: 'Agentweaver.Web',
  kind: 'service',
  version: '0.1.0',
  project: 'apps/web/package.json',
};
const initialPackage = JSON.stringify({
  name: 'web',
  version: '0.1.0',
  dependencies: { react: '^19.0.0' },
}, null, 2) + '\n';
const initialLock = JSON.stringify({
  name: 'web',
  version: '0.1.0',
  lockfileVersion: 3,
  packages: {
    '': { name: 'web', version: '0.1.0', dependencies: { react: '^19.0.0' } },
    'node_modules/react': { version: '19.0.0', resolved: 'https://registry.invalid/react.tgz', integrity: 'sha512-fixed' },
  },
}, null, 2) + '\n';

function git(dir, ...args) {
  return execFileSync('git', args, { cwd: dir, encoding: 'utf8' }).trim();
}

function initRepo(t) {
  const testRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(testRoot, { recursive: true });
  const root = mkdtempSync(path.join(testRoot, 'v1-web-apply-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(path.join(root, 'apps', 'web'), { recursive: true });
  mkdirSync(path.join(root, '.changeset'), { recursive: true });
  mkdirSync(path.join(root, 'releases'), { recursive: true });
  writeFileSync(path.join(root, 'apps', 'web', 'package.json'), initialPackage);
  writeFileSync(path.join(root, 'apps', 'web', 'package-lock.json'), initialLock);
  writeFileSync(path.join(root, 'releases', 'foundation.json'), JSON.stringify({
    schemaVersion: 1,
    stage: 'draft',
    components: [component],
    compatibility: [],
  }, null, 2) + '\n');
  writeFileSync(path.join(root, '.changeset', 'web-feature.md'),
    '---\n"Agentweaver.Web": minor\n---\n\nAdd a web capability.\n');
  git(root, 'init', '-q');
  git(root, 'config', 'core.autocrlf', 'false');
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'web release source');
  return root;
}

const manifestPath = (root) => path.join(root, 'releases', 'foundation.json');
const readManifest = (root) => JSON.parse(readFileSync(manifestPath(root), 'utf8'));
const readJson = (root, file) => JSON.parse(readFileSync(path.join(root, file), 'utf8'));
const planFor = (root) => createPlan(readManifest(root), { root });

test('apply updates both package version mirrors while preserving npm dependency data', (t) => {
  const root = initRepo(t);
  const plan = planFor(root);
  const result = applyPlan(plan, manifestPath(root), { root });
  assert.deepEqual(result.applied, ['Agentweaver.Web']);
  assert.equal(readJson(root, 'apps/web/package.json').version, '0.2.0');
  const lock = readJson(root, 'apps/web/package-lock.json');
  assert.equal(lock.packages[''].version, '0.2.0');
  assert.equal(lock.packages[''].dependencies.react, '^19.0.0');
  assert.equal(lock.packages['node_modules/react'].version, '19.0.0');
  assert.equal(lock.packages['node_modules/react'].integrity, 'sha512-fixed');
  assert.equal(readManifest(root).components[0].version, '0.2.0');
  assert.deepEqual(applyPlan(plan, manifestPath(root), { root }).skipped, ['Agentweaver.Web']);
});

test('rollback restores package.json and package-lock.json when the lockfile rename fails', (t) => {
  const root = initRepo(t);
  const plan = planFor(root);
  assert.throws(() => applyPlan(plan, manifestPath(root), {
    root,
    renameSync(from, to) {
      if (to.endsWith('package-lock.json')) throw new Error('simulated lockfile rename failure');
      nativeRenameSync(from, to);
    },
  }), /rolled back: simulated lockfile rename failure/);
  assert.equal(readFileSync(path.join(root, 'apps', 'web', 'package.json'), 'utf8'), initialPackage);
  assert.equal(readFileSync(path.join(root, 'apps', 'web', 'package-lock.json'), 'utf8'), initialLock);
  assert.equal(readManifest(root).components[0].version, '0.1.0');
  assert.equal(existsSync(path.join(root, 'releases', 'receipts')), false);
  assert.ok(existsSync(path.join(root, '.changeset', 'web-feature.md')));
});

test('idempotence checks the package-lock root version and receipt-derived mirror bytes', (t) => {
  const root = initRepo(t);
  const plan = planFor(root);
  applyPlan(plan, manifestPath(root), { root });
  const lockPath = path.join(root, 'apps', 'web', 'package-lock.json');
  const lock = readJson(root, 'apps/web/package-lock.json');
  lock.packages[''].version = '0.3.0';
  writeFileSync(lockPath, JSON.stringify(lock, null, 2) + '\n');
  assert.throws(() => applyPlan(plan, manifestPath(root), { root }),
    /packages\[""\]\.version must match 0\.2\.0/);
});

test('a committed exact receipt-derived package mirror diff satisfies coverage', (t) => {
  const root = initRepo(t);
  const base = git(root, 'rev-parse', 'HEAD');
  const plan = planFor(root);
  applyPlan(plan, manifestPath(root), { root });
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'apply web release');
  const changes = diffChanges(root, base);
  assert.equal(validateChangesets(readManifest(root), { root, changes, ancestorSha: base }).size, 0);
});

test('a receipt cannot credit a dependency edit mixed into the package-lock mirror', (t) => {
  const root = initRepo(t);
  const base = git(root, 'rev-parse', 'HEAD');
  const plan = planFor(root);
  applyPlan(plan, manifestPath(root), { root });
  const lockPath = path.join(root, 'apps', 'web', 'package-lock.json');
  const lock = readJson(root, 'apps/web/package-lock.json');
  lock.packages['node_modules/react'].version = '19.0.1';
  writeFileSync(lockPath, JSON.stringify(lock, null, 2) + '\n');
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'tampered dependency lock');
  const changes = diffChanges(root, base);
  assert.throws(() => validateChangesets(readManifest(root), { root, changes, ancestorSha: base }),
    /missing new or modified changeset entries for Agentweaver\.Web/);
});

test('dependency-only lockfile changes require a fresh web changeset', (t) => {
  const root = initRepo(t);
  writeFileSync(path.join(root, '.changeset', 'web-feature.md'),
    '---\n"Agentweaver.Web": minor\n---\n\nAdd a web capability.\n');
  const lockPath = path.join(root, 'apps', 'web', 'package-lock.json');
  const lock = readJson(root, 'apps/web/package-lock.json');
  lock.packages['node_modules/react'].version = '19.0.1';
  writeFileSync(lockPath, JSON.stringify(lock, null, 2) + '\n');
  assert.throws(() => validateChangesets(readManifest(root), {
    root,
    changes: [{ status: 'M', file: 'apps/web/package-lock.json' }],
  }), /missing new or modified changeset entries for Agentweaver\.Web/);
  writeFileSync(path.join(root, '.changeset', 'web-deps.md'),
    '---\n"Agentweaver.Web": patch\n---\n\nUpdate the locked React dependency.\n');
  assert.equal(validateChangesets(readManifest(root), {
    root,
    changes: [
      { status: 'M', file: 'apps/web/package-lock.json' },
      { status: 'A', file: '.changeset/web-deps.md' },
    ],
  }).size, 2);
});
