import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';
import { hostname } from 'node:os';
import test from 'node:test';
import {
  CACHE_SCHEMA,
  acquireLock,
  analyzeProject,
  computeCacheIdentity,
  ensureProject,
  materializeDependencyTree,
  resolveCommonDir,
  validateCacheEntry,
} from '../shared-deps.mjs';
import { areasForPaths } from '../validate.mjs';

const scratchRoot = path.resolve('scripts/ci/tests/.scratch-shared-deps');

function resetScratch(name) {
  const target = path.join(scratchRoot, name);
  rmSync(target, { recursive: true, force: true });
  mkdirSync(target, { recursive: true });
  return target;
}

function runtime(overrides = {}) {
  return {
    nodeVersion: 'v24.1.0',
    npmVersion: '11.1.0',
    platform: 'win32',
    arch: 'x64',
    installConfigHash: 'default-config',
    ...overrides,
  };
}

function identity(overrides = {}) {
  return computeCacheIdentity({
    packageJsonBytes: Buffer.from('{"name":"fixture"}'),
    lockfileBytes: Buffer.from('{"lockfileVersion":3}'),
    ...runtime(),
    ...overrides,
  });
}

function writeProject(repoRoot, { workspaces = false, linked = false } = {}) {
  const packageJson = {
    name: 'fixture',
    private: true,
    ...(workspaces ? { workspaces: ['packages/*'] } : {}),
  };
  const packageLock = {
    name: 'fixture',
    lockfileVersion: 3,
    packages: {
      '': { name: 'fixture' },
      ...(linked
        ? { 'node_modules/local-package': { resolved: 'packages/local-package', link: true } }
        : {}),
    },
  };
  writeFileSync(path.join(repoRoot, 'package.json'), JSON.stringify(packageJson));
  writeFileSync(path.join(repoRoot, 'package-lock.json'), JSON.stringify(packageLock));
}

function writeFakeInstall(projectPath) {
  const packagePath = path.join(projectPath, 'node_modules', 'example');
  mkdirSync(packagePath, { recursive: true });
  writeFileSync(path.join(packagePath, 'package.json'), '{"name":"example","version":"1.0.0"}');
  writeFileSync(path.join(projectPath, 'node_modules', '.package-lock.json'), JSON.stringify({
    lockfileVersion: 3,
    packages: {
      '': {},
      'node_modules/example': { version: '1.0.0' },
    },
  }));
  return { elapsedSeconds: '0.01' };
}

test.after(() => {
  rmSync(scratchRoot, { recursive: true, force: true });
});

test('cache key includes lockfile, Node/npm, OS, architecture, and invalidation nonce', () => {
  const baseline = identity();
  assert.notEqual(baseline.key, identity({ lockfileBytes: Buffer.from('changed') }).key);
  assert.notEqual(baseline.key, identity({ nodeVersion: 'v25.0.0' }).key);
  assert.notEqual(baseline.key, identity({ npmVersion: '12.0.0' }).key);
  assert.notEqual(baseline.key, identity({ platform: 'linux' }).key);
  assert.notEqual(baseline.key, identity({ arch: 'arm64' }).key);
  assert.notEqual(baseline.key, identity({ installConfigHash: 'different-config' }).key);
  assert.notEqual(baseline.key, identity({ npmArgsHash: 'different-args' }).key);
  assert.notEqual(baseline.key, identity({ invalidationNonce: 'new-generation' }).key);
});

test('lock acquisition excludes a concurrent owner until release', () => {
  const root = resetScratch('locking');
  const lockPath = path.join(root, 'cache.lock');
  const releaseFirst = acquireLock(lockPath, { timeoutMs: 2000 });
  assert.throws(
    () => acquireLock(lockPath, { timeoutMs: 25, pollIntervalMs: 5 }),
    /timed out waiting/u,
  );
  releaseFirst();
  const releaseSecond = acquireLock(lockPath, { timeoutMs: 100 });
  releaseSecond();
});

test('dead stale locks are recovered', () => {
  const root = resetScratch('stale-lock');
  const lockPath = path.join(root, 'cache.lock');
  mkdirSync(lockPath);
  writeFileSync(path.join(lockPath, 'owner.json'), JSON.stringify({
    token: 'abandoned',
    pid: 2147483647,
    hostname: hostname(),
    startedAt: new Date(Date.now() - 60_000).toISOString(),
  }));

  const release = acquireLock(lockPath, {
    timeoutMs: 1000,
    staleAfterMs: 1,
    pollIntervalMs: 10,
  });
  release();
});

test('cache validation detects incomplete package trees and marker corruption', () => {
  const root = resetScratch('corruption');
  const key = 'cache-key';
  const nodeModules = path.join(root, 'node_modules');
  const packagePath = path.join(nodeModules, 'example');
  mkdirSync(packagePath, { recursive: true });
  writeFileSync(path.join(packagePath, 'package.json'), '{"name":"example"}');
  const hiddenLock = JSON.stringify({
    lockfileVersion: 3,
    packages: {
      '': {},
      'node_modules/example': { version: '1.0.0' },
    },
  });
  writeFileSync(path.join(nodeModules, '.package-lock.json'), hiddenLock);
  const digest = createHash('sha256').update(hiddenLock).digest('hex');
  writeFileSync(path.join(root, 'ready.json'), JSON.stringify({
    schema: CACHE_SCHEMA,
    key,
    hiddenLockHash: digest,
    packageCount: 1,
  }));

  assert.equal(validateCacheEntry(root, key).valid, true);
  rmSync(path.join(packagePath, 'package.json'));
  assert.match(validateCacheEntry(root, key).reason, /incomplete/u);
  writeFileSync(path.join(root, 'ready.json'), JSON.stringify({
    schema: CACHE_SCHEMA + 1,
    key,
    hiddenLockHash: digest,
    packageCount: 1,
  }));
  assert.match(validateCacheEntry(root, key).reason, /completion marker/u);
});

test('workspace and lockfile links force isolated installation', () => {
  assert.equal(analyzeProject({ workspaces: ['packages/*'] }, { packages: {} }).reusable, false);
  assert.equal(
    analyzeProject({}, { packages: { 'node_modules/local': { link: true } } }).reusable,
    false,
  );
});

test('workspace fallback installs into the current worktree without materializing a cache', () => {
  const repoRoot = resetScratch('workspace-fallback');
  writeProject(repoRoot, { workspaces: true, linked: true });
  let materialized = false;
  const result = ensureProject({
    repoRoot,
    project: '.',
    cacheRoot: path.join(repoRoot, '.cache'),
    runtime: runtime(),
    environment: {},
    materialize() {
      materialized = true;
    },
    runNpmCi(projectPath) {
      const localPackage = path.join(projectPath, 'node_modules', 'local-package');
      mkdirSync(localPackage, { recursive: true });
      writeFileSync(path.join(localPackage, 'package.json'), '{"name":"local-package"}');
      return { elapsedSeconds: '0.01' };
    },
  });

  assert.equal(result.mode, 'isolated');
  assert.equal(materialized, false);
  assert.equal(
    JSON.parse(readFileSync(path.join(repoRoot, 'node_modules/local-package/package.json'))).name,
    'local-package',
  );
});

test('completed cache entries are reused without another npm install', () => {
  const repoRoot = resetScratch('cache-hit');
  const cacheRoot = path.join(repoRoot, '.cache');
  writeProject(repoRoot);
  let installs = 0;
  let materializations = 0;
  const options = {
    repoRoot,
    project: '.',
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath) {
      installs += 1;
      return writeFakeInstall(projectPath);
    },
    materialize() {
      materializations += 1;
    },
  };

  assert.equal(ensureProject(options).mode, 'shared');
  assert.equal(ensureProject(options).mode, 'shared');
  assert.equal(installs, 1);
  assert.equal(materializations, 2);
});

test('materialization failures fall back to a fresh isolated npm ci', () => {
  const repoRoot = resetScratch('attach-fallback');
  writeProject(repoRoot);
  let installs = 0;
  const result = ensureProject({
    repoRoot,
    project: '.',
    cacheRoot: path.join(repoRoot, '.cache'),
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath) {
      installs += 1;
      return writeFakeInstall(projectPath);
    },
    materialize() {
      throw new Error('links unavailable');
    },
  });

  assert.equal(result.mode, 'isolated');
  assert.equal(installs, 2);
  assert.equal(
    JSON.parse(readFileSync(path.join(repoRoot, 'node_modules/example/package.json'))).name,
    'example',
  );
});

test('materialized dependency trees are private writable copies', () => {
  const root = resetScratch('private-copy');
  const entryPath = path.join(root, 'entry');
  const cachedPackage = path.join(entryPath, 'node_modules', 'example');
  const localNodeModules = path.join(root, 'worktree', 'node_modules');
  mkdirSync(cachedPackage, { recursive: true });
  writeFileSync(path.join(cachedPackage, 'package.json'), '{"name":"example","value":"cache"}');
  const hiddenLock = JSON.stringify({
    lockfileVersion: 3,
    packages: {
      '': {},
      'node_modules/example': { version: '1.0.0' },
    },
  });
  writeFileSync(path.join(entryPath, 'node_modules', '.package-lock.json'), hiddenLock);
  writeFileSync(path.join(entryPath, 'ready.json'), JSON.stringify({
    schema: CACHE_SCHEMA,
    key: 'private-key',
    hiddenLockHash: createHash('sha256').update(hiddenLock).digest('hex'),
    packageCount: 1,
  }));
  mkdirSync(path.dirname(localNodeModules), { recursive: true });

  materializeDependencyTree(entryPath, localNodeModules, 'private-key');
  writeFileSync(
    path.join(localNodeModules, 'example', 'package.json'),
    '{"name":"example","value":"worktree"}',
  );

  assert.equal(
    JSON.parse(readFileSync(path.join(cachedPackage, 'package.json'))).value,
    'cache',
  );
});

test('Windows git common-dir paths normalize without depending on the worktree git file', () => {
  assert.equal(
    resolveCommonDir(
      'C:\\repo\\.worktrees\\feature',
      'C:/repo/.git',
      path.win32,
    ),
    'C:\\repo\\.git',
  );
  assert.equal(
    resolveCommonDir(
      'C:\\repo\\.worktrees\\feature',
      '..\\..\\.git',
      path.win32,
    ),
    'C:\\repo\\.git',
  );
});

test('validation path classification keeps layer and stack policy deterministic', () => {
  assert.deepEqual(areasForPaths(['scripts/ci/shared-deps.mjs']), ['node']);
  assert.deepEqual(areasForPaths(['scripts/ui-harness/test/auth.test.mjs']), ['harness']);
  assert.deepEqual(areasForPaths(['apps/web/src/App.tsx']), ['web']);
  assert.deepEqual(areasForPaths(['docs/guide/testing.md']), ['docs']);
  assert.deepEqual(areasForPaths(['packages/Agentweaver.Domain/Foo.cs']), ['dotnet']);
  assert.deepEqual(areasForPaths(['tests/Agentweaver.Tests/fixture.json']), ['dotnet']);
  assert.deepEqual(
    areasForPaths(['.github/workflows/ci.yml']),
    ['node', 'harness', 'web', 'docs', 'dotnet'],
  );
});
