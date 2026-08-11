import assert from 'node:assert/strict';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';
import { hostname } from 'node:os';
import test from 'node:test';
import {
  CACHE_SCHEMA,
  acquireCacheUse,
  acquireLock,
  acquireMaintenanceLock,
  acquireProjectCacheUse,
  acquireValidationLock,
  analyzeProject,
  computeCacheIdentity,
  ensureProject,
  invalidateProject,
  resolveCommonDir,
  verifyProjectResolution,
} from '../shared-deps.mjs';
import {
  areasForPaths,
  foreignDotnetOutputRoots,
} from '../validate.mjs';

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
    libc: 'none',
    installConfigHash: 'default-config',
    lifecycleEnvironmentHash: 'default-environment',
    hasCredentials: false,
    ...overrides,
  };
}

function identity(overrides = {}) {
  return computeCacheIdentity({
    packageRootIdentity: 'apps/web',
    packageJsonBytes: Buffer.from('{"name":"fixture"}'),
    lockfileBytes: Buffer.from('{"lockfileVersion":3}'),
    ...runtime(),
    ...overrides,
  });
}

function writeProject(repoRoot, {
  project = '.',
  workspaces = false,
  linked = false,
  lockVersion = '1.0.0',
} = {}) {
  const projectPath = path.resolve(repoRoot, project);
  mkdirSync(projectPath, { recursive: true });
  const packageJson = {
    name: 'fixture',
    private: true,
    dependencies: { example: lockVersion },
    ...(workspaces ? { workspaces: ['packages/*'] } : {}),
  };
  const packageLock = {
    name: 'fixture',
    lockfileVersion: 3,
    packages: {
      '': { name: 'fixture', dependencies: { example: lockVersion } },
      'node_modules/example': { version: lockVersion },
      ...(linked
        ? { 'node_modules/local-package': { resolved: 'packages/local-package', link: true } }
        : {}),
    },
  };
  writeFileSync(path.join(projectPath, 'package.json'), JSON.stringify(packageJson));
  writeFileSync(path.join(projectPath, 'package-lock.json'), JSON.stringify(packageLock));
  return projectPath;
}

function writeFakeInstall(projectPath, _npmArgs, cachePath) {
  const packagePath = path.join(projectPath, 'node_modules', 'example');
  mkdirSync(packagePath, { recursive: true });
  writeFileSync(
    path.join(packagePath, 'package.json'),
    '{"name":"example","version":"1.0.0","main":"index.js"}',
  );
  writeFileSync(path.join(packagePath, 'index.js'), 'module.exports = "example";');
  writeFileSync(path.join(projectPath, 'node_modules', '.package-lock.json'), JSON.stringify({
    lockfileVersion: 3,
    packages: {
      '': {},
      'node_modules/example': { version: '1.0.0' },
    },
  }));
  if (cachePath) {
    mkdirSync(cachePath, { recursive: true });
    writeFileSync(path.join(cachePath, '_fake-content'), 'cached');
  }
  return { elapsedSeconds: '0.01' };
}

function generationStates(cacheRoot) {
  const generationRoot = path.join(cacheRoot, 'generations');
  if (!existsSync(generationRoot)) {
    return [];
  }
  return readdirSync(generationRoot)
    .map((entry) => JSON.parse(
      readFileSync(path.join(generationRoot, entry), 'utf8'),
    ));
}

test.after(() => {
  rmSync(scratchRoot, { recursive: true, force: true });
});

test('cache key includes package root, manifests, toolchain, platform, flags, environment, and generation', () => {
  const baseline = identity();
  assert.notEqual(baseline.key, identity({ packageRootIdentity: 'docs' }).key);
  assert.notEqual(baseline.key, identity({ packageJsonBytes: Buffer.from('changed') }).key);
  assert.notEqual(baseline.key, identity({ lockfileBytes: Buffer.from('changed') }).key);
  assert.notEqual(baseline.key, identity({ nodeVersion: 'v25.0.0' }).key);
  assert.notEqual(baseline.key, identity({ npmVersion: '12.0.0' }).key);
  assert.notEqual(baseline.key, identity({ platform: 'linux' }).key);
  assert.notEqual(baseline.key, identity({ arch: 'arm64' }).key);
  assert.notEqual(baseline.key, identity({ libc: 'musl-1.2' }).key);
  assert.notEqual(baseline.key, identity({ installConfigHash: 'different-config' }).key);
  assert.notEqual(baseline.key, identity({ npmArgsHash: 'different-args' }).key);
  assert.notEqual(
    baseline.key,
    identity({ lifecycleEnvironmentHash: 'different-environment' }).key,
  );
  assert.notEqual(
    baseline.key,
    identity({ invalidationGeneration: 'new-generation' }).key,
  );
  assert.notEqual(
    baseline.key,
    identity({ repositoryCacheIdentity: 'another-repository' }).key,
  );
});

test('lock acquisition excludes a concurrent owner until release', () => {
  const root = resetScratch('locking');
  const lockPath = path.join(root, 'cache.lock');
  const releaseFirst = acquireLock(lockPath, {
    timeoutMs: 2000,
    processStartTokenFn: () => 'same-process',
  });
  assert.throws(
    () => acquireLock(lockPath, {
      timeoutMs: 25,
      pollIntervalMs: 5,
      processStartTokenFn: () => 'same-process',
    }),
    /timed out waiting/u,
  );
  releaseFirst();
  const releaseSecond = acquireLock(lockPath, {
    timeoutMs: 100,
    processStartTokenFn: () => 'same-process',
  });
  releaseSecond();
});

test('dead locks and reused PIDs are recovered only after ownership verification', async () => {
  const root = resetScratch('stale-lock');
  const deadLock = path.join(root, 'dead.lock');
  mkdirSync(deadLock);
  writeFileSync(path.join(deadLock, 'owner.json'), JSON.stringify({
    token: 'abandoned',
    pid: 2147483647,
    hostname: hostname(),
    processStartToken: 'old-process',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
  }));
  const releaseDead = acquireLock(deadLock, {
    timeoutMs: 1000,
    staleAfterMs: 1,
    pollIntervalMs: 10,
    processStartTokenFn: () => null,
  });
  releaseDead();

  const reusedLock = path.join(root, 'reused.lock');
  mkdirSync(reusedLock);
  writeFileSync(path.join(reusedLock, 'owner.json'), JSON.stringify({
    token: 'reused',
    pid: process.pid,
    hostname: hostname(),
    processStartToken: 'old-process',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
  }));
  const releaseReused = acquireLock(reusedLock, {
    timeoutMs: 1000,
    staleAfterMs: 1,
    pollIntervalMs: 10,
    processStartTokenFn: () => 'current-process',
  });
  releaseReused();
});

test('one canonical worktree validation mutex prevents overlapping validation', () => {
  const repoRoot = resetScratch('validation-mutex');
  const cacheRoot = path.join(repoRoot, '.cache');
  const release = acquireValidationLock(repoRoot, { cacheRoot, timeoutMs: 100 });
  assert.throws(
    () => acquireValidationLock(repoRoot, { cacheRoot, timeoutMs: 25 }),
    /timed out waiting/u,
  );
  release();
});

test('npm cache users run concurrently but exclude maintenance operations', () => {
  const cacheRoot = resetScratch('cache-maintenance');
  const projectUse = acquireProjectCacheUse(cacheRoot, '.', { timeoutMs: 100 });
  assert.equal(projectUse.generation, '0');
  assert.equal(
    generationStates(cacheRoot).find(
      (state) => state.packageRootIdentity === '.',
    )?.generation,
    '0',
  );
  assert.throws(
    () => acquireMaintenanceLock(cacheRoot, { timeoutMs: 25, pollIntervalMs: 5 }),
    /timed out waiting for active npm cache users/u,
  );
  projectUse.release();

  const releaseMaintenance = acquireMaintenanceLock(cacheRoot, { timeoutMs: 100 });
  assert.throws(
    () => acquireCacheUse(cacheRoot, { timeoutMs: 25 }),
    /timed out waiting for dependency lock/u,
  );
  releaseMaintenance();
});

test('the generic cache-use primitive still excludes maintenance operations', () => {
  const cacheRoot = resetScratch('generic-cache-maintenance');
  const releaseUse = acquireCacheUse(cacheRoot, { timeoutMs: 100 });
  assert.throws(
    () => acquireMaintenanceLock(cacheRoot, { timeoutMs: 25, pollIntervalMs: 5 }),
    /timed out waiting for active npm cache users/u,
  );
  releaseUse();
});

test('workspace and lockfile links force isolated installation', () => {
  assert.equal(analyzeProject({ workspaces: ['packages/*'] }, { packages: {} }).reusable, false);
  assert.equal(
    analyzeProject({}, { packages: { 'node_modules/local': { link: true } } }).reusable,
    false,
  );
});

test('workspace fallback installs only into the current worktree', () => {
  const repoRoot = resetScratch('workspace-fallback');
  writeProject(repoRoot, { workspaces: true, linked: true });
  let sharedCachePath;
  const result = ensureProject({
    repoRoot,
    cacheRoot: path.join(repoRoot, '.cache'),
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, cachePath) {
      sharedCachePath = cachePath;
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  });
  assert.equal(result.mode, 'isolated');
  assert.equal(sharedCachePath, null);
  assert.equal(existsSync(path.join(repoRoot, 'node_modules/example/package.json')), true);
});

test('authenticated npm configuration uses isolated fallback', () => {
  const repoRoot = resetScratch('credential-fallback');
  writeProject(repoRoot);
  let sharedCachePath;
  const result = ensureProject({
    repoRoot,
    cacheRoot: path.join(repoRoot, '.cache'),
    runtime: runtime({ hasCredentials: true }),
    environment: { NPM_TOKEN: 'not-written-anywhere' },
    runNpmCi(projectPath, npmArgs, cachePath) {
      sharedCachePath = cachePath;
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  });
  assert.equal(result.mode, 'isolated');
  assert.equal(sharedCachePath, null);
});

test('shared npm cache is reused while node_modules stays physical and worktree-local', () => {
  const root = resetScratch('shared-download-cache');
  const cacheRoot = path.join(root, '.cache');
  const firstWorktree = path.join(root, 'first');
  const secondWorktree = path.join(root, 'second');
  writeProject(firstWorktree);
  writeProject(secondWorktree);
  const cachePaths = [];
  let installs = 0;
  const options = (repoRoot) => ({
    repoRoot,
    cacheRoot,
    repositoryIdentity: 'shared-download-cache-repository',
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, cachePath) {
      installs += 1;
      cachePaths.push(cachePath);
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  });

  assert.equal(ensureProject(options(firstWorktree)).mode, 'shared-cache');
  assert.equal(ensureProject(options(secondWorktree)).mode, 'shared-cache');
  assert.equal(ensureProject(options(firstWorktree)).mode, 'local');
  assert.equal(installs, 2);
  assert.equal(cachePaths[0], cachePaths[1]);
  assert.equal(
    realpathSync.native(path.join(firstWorktree, 'node_modules')).startsWith(firstWorktree),
    true,
  );
  assert.equal(
    realpathSync.native(path.join(secondWorktree, 'node_modules')).startsWith(secondWorktree),
    true,
  );
});

test('lockfile changes invalidate only the current worktree dependency tree', () => {
  const repoRoot = resetScratch('lockfile-change');
  const cacheRoot = path.join(repoRoot, '.cache');
  writeProject(repoRoot);
  const cachePaths = [];
  const options = {
    repoRoot,
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, cachePath) {
      cachePaths.push(cachePath);
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  };
  ensureProject(options);
  writeProject(repoRoot, { lockVersion: '2.0.0' });
  ensureProject(options);
  assert.equal(cachePaths.length, 2);
  assert.notEqual(cachePaths[0], cachePaths[1]);
});

test('ensure -> invalidate -> ensure rotates the persisted project namespace', () => {
  const repoRoot = resetScratch('invalidation-generation');
  const cacheRoot = path.join(repoRoot, '.cache');
  const rootProject = writeProject(repoRoot);
  const webProject = writeProject(repoRoot, { project: 'apps/web' });
  const installs = [];
  const options = (project = '.') => ({
    repoRoot,
    project,
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, cachePath) {
      installs.push({ projectPath, cachePath });
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  });

  const firstRoot = ensureProject(options());
  const firstWeb = ensureProject(options('apps/web'));
  const initialStates = generationStates(cacheRoot);
  assert.equal(
    initialStates.find((state) => state.packageRootIdentity === '.')?.generation,
    '0',
  );
  assert.equal(
    initialStates.find((state) => state.packageRootIdentity === 'apps/web')?.generation,
    '0',
  );

  const invalidation = invalidateProject({
    repoRoot,
    project: '.',
    cacheRoot,
    runtime: runtime(),
    environment: {},
  });
  assert.equal(invalidation.previousKey, firstRoot.key);
  assert.notEqual(invalidation.generation, '0');
  assert.equal(existsSync(path.join(rootProject, 'node_modules')), false);
  assert.equal(existsSync(path.join(webProject, 'node_modules/example')), true);
  assert.equal(
    readdirSync(path.join(cacheRoot, 'quarantine'))
      .some((entry) => entry.startsWith(`${firstRoot.key}-`)),
    true,
  );

  const rotatedStates = generationStates(cacheRoot);
  assert.equal(
    rotatedStates.find((state) => state.packageRootIdentity === '.')?.generation,
    invalidation.generation,
  );
  assert.equal(
    rotatedStates.find((state) => state.packageRootIdentity === 'apps/web')?.generation,
    firstWeb.generation,
  );

  const secondRoot = ensureProject(options());
  assert.equal(secondRoot.mode, 'shared-cache');
  assert.equal(secondRoot.generation, invalidation.generation);
  assert.notEqual(secondRoot.key, firstRoot.key);
  assert.notEqual(installs[2].cachePath, installs[0].cachePath);
  assert.equal(
    installs.filter(({ projectPath }) => projectPath === rootProject).length,
    2,
  );
  assert.equal(ensureProject(options()).mode, 'local');
});

test('invalidation rotates a persisted generation when no cache namespace exists', () => {
  const repoRoot = resetScratch('invalidation-without-cache');
  const cacheRoot = path.join(repoRoot, '.cache');
  const projectPath = writeProject(repoRoot);
  writeFakeInstall(projectPath, [], null);
  let installs = 0;

  const invalidation = invalidateProject({
    repoRoot,
    project: '.',
    cacheRoot,
    runtime: runtime(),
    environment: {},
  });
  assert.notEqual(invalidation.generation, '0');
  assert.equal(existsSync(path.join(projectPath, 'node_modules')), false);
  assert.equal(
    generationStates(cacheRoot).find(
      (state) => state.packageRootIdentity === '.',
    )?.generation,
    invalidation.generation,
  );

  const ensured = ensureProject({
    repoRoot,
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(currentProjectPath, npmArgs, cachePath) {
      installs += 1;
      return writeFakeInstall(currentProjectPath, npmArgs, cachePath);
    },
  });
  assert.equal(installs, 1);
  assert.equal(ensured.mode, 'shared-cache');
  assert.equal(ensured.generation, invalidation.generation);
  assert.notEqual(ensured.key, invalidation.previousKey);
});

test('a custom cache root isolates generations and namespaces between repositories', () => {
  const root = resetScratch('custom-cache-repository-isolation');
  const cacheRoot = path.join(root, '.cache');
  const firstRepo = path.join(root, 'first-repo');
  const secondRepo = path.join(root, 'second-repo');
  writeProject(firstRepo);
  writeProject(secondRepo);
  const cachePaths = new Map();
  let secondInstalls = 0;
  const options = (repoRoot) => ({
    repoRoot,
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, cachePath) {
      cachePaths.set(repoRoot, cachePath);
      if (repoRoot === secondRepo) {
        secondInstalls += 1;
      }
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  });

  const first = ensureProject(options(firstRepo));
  const second = ensureProject(options(secondRepo));
  assert.notEqual(first.key, second.key);
  invalidateProject({
    repoRoot: firstRepo,
    project: '.',
    cacheRoot,
    runtime: runtime(),
    environment: {},
  });
  assert.equal(existsSync(cachePaths.get(secondRepo)), true);
  rmSync(path.join(secondRepo, 'node_modules'), { recursive: true, force: true });

  const secondAfterInvalidation = ensureProject(options(secondRepo));
  assert.equal(secondAfterInvalidation.key, second.key);
  assert.equal(secondAfterInvalidation.generation, second.generation);
  assert.equal(secondInstalls, 2);
});

test('Windows package-root casing aliases share one invalidation generation', {
  skip: process.platform !== 'win32',
}, () => {
  const repoRoot = resetScratch('invalidation-casing');
  const cacheRoot = path.join(repoRoot, '.cache');
  writeProject(repoRoot, { project: 'apps/web' });
  let installs = 0;
  const options = {
    repoRoot,
    project: 'apps/web',
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, cachePath) {
      installs += 1;
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  };
  const first = ensureProject(options);
  invalidateProject({
    repoRoot,
    project: 'APPS/WEB',
    cacheRoot,
    runtime: runtime(),
    environment: {},
  });
  const second = ensureProject(options);

  assert.notEqual(second.key, first.key);
  assert.equal(installs, 2);
  assert.deepEqual(
    generationStates(cacheRoot).map((state) => state.packageRootIdentity),
    ['apps/web'],
  );
});

test('a corrupt persisted generation fails closed without running npm ci', () => {
  const repoRoot = resetScratch('corrupt-invalidation-generation');
  const cacheRoot = path.join(repoRoot, '.cache');
  writeProject(repoRoot);
  let installs = 0;
  const options = {
    repoRoot,
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, cachePath) {
      installs += 1;
      return writeFakeInstall(projectPath, npmArgs, cachePath);
    },
  };
  ensureProject(options);
  const generationRoot = path.join(cacheRoot, 'generations');
  const generationPath = path.join(generationRoot, readdirSync(generationRoot)[0]);
  writeFileSync(generationPath, '{"schema":1,"generation":""}');
  rmSync(path.join(repoRoot, 'node_modules'), { recursive: true, force: true });

  assert.throws(
    () => ensureProject(options),
    /invalid dependency invalidation generation/u,
  );
  assert.equal(installs, 1);
});

test('corrupt cache markers are quarantined before one clean install', () => {
  const repoRoot = resetScratch('corruption');
  const cacheRoot = path.join(repoRoot, '.cache');
  writeProject(repoRoot);
  let installs = 0;
  let cachePath;
  const options = {
    repoRoot,
    cacheRoot,
    runtime: runtime(),
    environment: {},
    runNpmCi(projectPath, npmArgs, currentCachePath) {
      installs += 1;
      cachePath = currentCachePath;
      return writeFakeInstall(projectPath, npmArgs, currentCachePath);
    },
  };
  ensureProject(options);
  rmSync(path.join(repoRoot, 'node_modules'), { recursive: true, force: true });
  writeFileSync(path.join(cachePath, 'cache.json'), '{broken');
  ensureProject(options);
  assert.equal(installs, 2);
  assert.equal(
    existsSync(path.join(cacheRoot, 'quarantine')),
    true,
  );
});

test('a failed cached install is quarantined, retried cold once, then fails closed', () => {
  const repoRoot = resetScratch('retry-cold');
  const cacheRoot = path.join(repoRoot, '.cache');
  writeProject(repoRoot);
  let attempts = 0;
  assert.throws(
    () => ensureProject({
      repoRoot,
      cacheRoot,
      runtime: runtime(),
      environment: {},
      runNpmCi() {
        attempts += 1;
        throw new Error(`install failure ${attempts}`);
      },
    }),
    /failed after quarantining/u,
  );
  assert.equal(attempts, 2);
});

test('100-cycle four-worktree soak has no cross-worktree writes or resolution', () => {
  const root = resetScratch('four-worktree-soak');
  const cacheRoot = path.join(root, '.cache');
  const worktrees = Array.from({ length: 4 }, (_, index) => path.join(root, `worktree-${index}`));
  let installs = 0;
  const originalLog = console.log;
  console.log = () => {};
  try {
    for (const worktree of worktrees) {
      writeProject(worktree);
      const options = {
        repoRoot: worktree,
        cacheRoot,
        runtime: runtime(),
        environment: {},
        repositoryIdentity: 'four-worktree-soak-repository',
        validationLockHeld: true,
        runNpmCi(projectPath, npmArgs, cachePath) {
          installs += 1;
          return writeFakeInstall(projectPath, npmArgs, cachePath);
        },
      };
      for (let cycle = 0; cycle < 25; cycle += 1) {
        ensureProject(options);
        verifyProjectResolution(worktree, JSON.parse(
          readFileSync(path.join(worktree, 'package.json'), 'utf8'),
        ));
      }
    }
  } finally {
    console.log = originalLog;
  }
  assert.equal(installs, 4);
  writeFileSync(path.join(worktrees[0], 'node_modules/example/local-only'), 'changed');
  for (const worktree of worktrees.slice(1)) {
    assert.equal(existsSync(path.join(worktree, 'node_modules/example/local-only')), false);
  }
});

test('Windows git common-dir paths normalize without following worktree git files', () => {
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

test('foreign MSBuild assets identify only the affected worktree project output', {
  skip: process.platform !== 'win32',
}, () => {
  const repoRoot = resetScratch('foreign-dotnet-output');
  const projectRoot = path.join(repoRoot, 'packages', 'Example');
  mkdirSync(path.join(projectRoot, 'obj'), { recursive: true });
  writeFileSync(path.join(projectRoot, 'obj', 'project.assets.json'), JSON.stringify({
    project: {
      restore: {
        projectPath: 'C:\\another-worktree\\packages\\Example\\Example.csproj',
      },
    },
  }));
  assert.deepEqual(foreignDotnetOutputRoots(repoRoot), [projectRoot]);
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
