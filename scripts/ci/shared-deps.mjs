import {
  constants,
  cpSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readlinkSync,
  readdirSync,
  renameSync,
  rmSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { hostname } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const CACHE_SCHEMA = 1;
export const DEFAULT_PROJECTS = ['.', 'apps/web', 'docs'];
const LOCAL_MARKER = '.agentweaver-shared-deps.json';
const DEFAULT_LOCK_TIMEOUT_MS = 10 * 60 * 1000;
const DEFAULT_STALE_LOCK_MS = 30 * 60 * 1000;
const POLL_INTERVAL_MS = 250;

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function atomicWriteJson(filePath, value) {
  mkdirSync(path.dirname(filePath), { recursive: true });
  const temporaryPath = `${filePath}.${process.pid}.${randomUUID()}.tmp`;
  writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
  renameSync(temporaryPath, filePath);
}

function sleep(milliseconds) {
  const buffer = new SharedArrayBuffer(4);
  Atomics.wait(new Int32Array(buffer), 0, 0, milliseconds);
}

function run(command, args, options = {}) {
  const startedAt = performance.now();
  const result = spawnSync(command, args, {
    cwd: options.cwd,
    env: options.env ?? process.env,
    encoding: 'utf8',
    shell: options.shell ?? false,
    stdio: options.capture ? 'pipe' : 'inherit',
  });
  const elapsedSeconds = ((performance.now() - startedAt) / 1000).toFixed(2);
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    const detail = options.capture ? `\n${result.stderr || result.stdout}` : '';
    throw new Error(`${command} ${args.join(' ')} failed with exit code ${result.status}${detail}`);
  }
  return {
    stdout: options.capture ? result.stdout.trim() : '',
    elapsedSeconds,
  };
}

export function resolveCommonDir(repoRoot, rawCommonDir, pathApi = path) {
  const normalized = rawCommonDir.replace(/[\\/]+/g, pathApi.sep);
  return pathApi.resolve(repoRoot, normalized);
}

function npmExecutable() {
  return process.platform === 'win32' ? 'npm.cmd' : 'npm';
}

export function npmInvocation(args, environment = process.env) {
  if (process.platform === 'win32') {
    const candidates = [
      environment.npm_execpath,
      path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js'),
    ].filter(Boolean);
    const npmCliPath = candidates.find((candidate) => existsSync(candidate));
    if (npmCliPath) {
      return { command: process.execPath, args: [npmCliPath, ...args], shell: false };
    }
  }
  return {
    command: npmExecutable(),
    args,
    shell: process.platform === 'win32',
  };
}

export function analyzeProject(packageJson, lockfile) {
  const workspaceNames = Array.isArray(packageJson.workspaces)
    ? packageJson.workspaces
    : packageJson.workspaces?.packages;
  if (Array.isArray(workspaceNames) && workspaceNames.length > 0) {
    return {
      reusable: false,
      reason: 'package.json declares workspaces; using isolated npm ci keeps workspace links local',
    };
  }

  const linkedEntry = Object.entries(lockfile.packages ?? {}).find(
    ([entryPath, metadata]) => entryPath !== '' && metadata?.link === true,
  );
  if (linkedEntry) {
    return {
      reusable: false,
      reason: `package-lock.json contains local link ${linkedEntry[0]}; using isolated npm ci`,
    };
  }

  return { reusable: true, reason: null };
}

export function computeCacheIdentity({
  packageJsonBytes,
  lockfileBytes,
  nodeVersion,
  npmVersion,
  platform,
  arch,
  installConfigHash = '',
  npmArgsHash = '',
  invalidationNonce = '',
}) {
  const baseKey = sha256(
    JSON.stringify({
      schema: CACHE_SCHEMA,
      packageJson: sha256(packageJsonBytes),
      lockfile: sha256(lockfileBytes),
      nodeVersion,
      npmVersion,
      platform,
      arch,
      installConfigHash,
      npmArgsHash,
    }),
  );
  const key = invalidationNonce
    ? sha256(JSON.stringify({ baseKey, invalidationNonce }))
    : baseKey;
  return { baseKey, key };
}

function processIsAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) {
    return false;
  }
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code === 'EPERM';
  }
}

function readJson(filePath) {
  return JSON.parse(readFileSync(filePath, 'utf8'));
}

function lockIsStale(lockPath, staleAfterMs) {
  try {
    const owner = readJson(path.join(lockPath, 'owner.json'));
    const age = Date.now() - Date.parse(owner.startedAt);
    if (owner.hostname === hostname() && !processIsAlive(owner.pid)) {
      return age > 1000;
    }
    return age > staleAfterMs;
  } catch {
    return Date.now() - statSync(lockPath).mtimeMs > staleAfterMs;
  }
}

export function acquireLock(
  lockPath,
  {
    timeoutMs = DEFAULT_LOCK_TIMEOUT_MS,
    staleAfterMs = DEFAULT_STALE_LOCK_MS,
    pollIntervalMs = POLL_INTERVAL_MS,
  } = {},
) {
  mkdirSync(path.dirname(lockPath), { recursive: true });
  const startedAt = Date.now();
  const token = randomUUID();

  while (true) {
    try {
      mkdirSync(lockPath);
      atomicWriteJson(path.join(lockPath, 'owner.json'), {
        token,
        pid: process.pid,
        hostname: hostname(),
        startedAt: new Date().toISOString(),
      });
      return () => {
        try {
          const owner = readJson(path.join(lockPath, 'owner.json'));
          if (owner.token === token) {
            rmSync(lockPath, { recursive: true, force: true });
          }
        } catch {
          // A recovered/replaced lock is owned by another process.
        }
      };
    } catch (error) {
      if (error?.code !== 'EEXIST') {
        throw error;
      }
    }

    if (lockIsStale(lockPath, staleAfterMs)) {
      const abandonedPath = `${lockPath}.stale-${Date.now()}-${randomUUID()}`;
      try {
        renameSync(lockPath, abandonedPath);
        rmSync(abandonedPath, { recursive: true, force: true });
        continue;
      } catch (error) {
        if (!['ENOENT', 'EACCES', 'EPERM'].includes(error?.code)) {
          throw error;
        }
      }
    }

    if (Date.now() - startedAt >= timeoutMs) {
      throw new Error(`timed out waiting for dependency cache lock ${lockPath}`);
    }
    sleep(pollIntervalMs);
  }
}

function packagePathsFromHiddenLock(hiddenLock) {
  return Object.entries(hiddenLock.packages ?? {})
    .filter(([entryPath, metadata]) => (
      entryPath.startsWith('node_modules/')
      && entryPath !== 'node_modules/.bin'
      && metadata?.link !== true
    ))
    .map(([entryPath]) => entryPath);
}

export function validateCacheEntry(entryPath, expectedKey) {
  try {
    const entryMetadata = lstatSync(entryPath);
    const nodeModulesPath = path.join(entryPath, 'node_modules');
    const nodeModulesMetadata = lstatSync(nodeModulesPath);
    if (
      entryMetadata.isSymbolicLink()
      || nodeModulesMetadata.isSymbolicLink()
      || !entryMetadata.isDirectory()
      || !nodeModulesMetadata.isDirectory()
    ) {
      return { valid: false, reason: 'cache entry contains an unsafe directory link' };
    }

    const marker = readJson(path.join(entryPath, 'ready.json'));
    if (marker.schema !== CACHE_SCHEMA || marker.key !== expectedKey) {
      return { valid: false, reason: 'completion marker does not match the cache key' };
    }

    const hiddenLockPath = path.join(nodeModulesPath, '.package-lock.json');
    const hiddenLockBytes = readFileSync(hiddenLockPath);
    if (sha256(hiddenLockBytes) !== marker.hiddenLockHash) {
      return { valid: false, reason: 'npm hidden lockfile is missing or changed' };
    }

    const hiddenLock = JSON.parse(hiddenLockBytes);
    const packagePaths = packagePathsFromHiddenLock(hiddenLock);
    if (packagePaths.length !== marker.packageCount) {
      return { valid: false, reason: 'installed package inventory count changed' };
    }
    for (const packagePath of packagePaths) {
      const installedPackagePath = path.join(entryPath, packagePath);
      const installedPackageMetadata = lstatSync(installedPackagePath);
      if (
        installedPackageMetadata.isSymbolicLink()
        || !installedPackageMetadata.isDirectory()
        || !existsSync(path.join(installedPackagePath, 'package.json'))
      ) {
        return { valid: false, reason: `installed package is incomplete: ${packagePath}` };
      }
    }
    return { valid: true, reason: null };
  } catch (error) {
    return { valid: false, reason: error?.message ?? String(error) };
  }
}

function assertSafeDependencyLinks(nodeModulesPath) {
  const root = path.resolve(nodeModulesPath);
  const pending = [root];
  while (pending.length > 0) {
    const current = pending.pop();
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const entryPath = path.join(current, entry.name);
      const metadata = lstatSync(entryPath);
      if (metadata.isSymbolicLink()) {
        const target = readlinkSync(entryPath);
        const resolvedTarget = path.resolve(path.dirname(entryPath), target);
        const relativeTarget = path.relative(root, resolvedTarget);
        if (
          path.isAbsolute(target)
          || relativeTarget === '..'
          || relativeTarget.startsWith(`..${path.sep}`)
        ) {
          throw new Error(`dependency link escapes node_modules: ${entryPath} -> ${target}`);
        }
      } else if (metadata.isDirectory()) {
        pending.push(entryPath);
      }
    }
  }
}

function removeNodeModules(nodeModulesPath) {
  let metadata;
  try {
    metadata = lstatSync(nodeModulesPath);
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return;
    }
    throw error;
  }
  if (!metadata) {
    return;
  }
  if (metadata.isSymbolicLink()) {
    unlinkSync(nodeModulesPath);
    return;
  }
  rmSync(nodeModulesPath, { recursive: true, force: true });
}

function removePathWithoutFollowingLinks(targetPath) {
  try {
    const metadata = lstatSync(targetPath);
    if (metadata.isSymbolicLink()) {
      unlinkSync(targetPath);
    } else {
      rmSync(targetPath, { recursive: true, force: true });
    }
  } catch (error) {
    if (error?.code !== 'ENOENT') {
      throw error;
    }
  }
}

function validateLocalTree(entryPath, nodeModulesPath, expectedKey) {
  try {
    const metadata = lstatSync(nodeModulesPath);
    if (metadata.isSymbolicLink() || !metadata.isDirectory()) {
      return false;
    }
    const localMarker = readJson(path.join(nodeModulesPath, LOCAL_MARKER));
    const cacheMarker = readJson(path.join(entryPath, 'ready.json'));
    if (
      localMarker.schema !== CACHE_SCHEMA
      || localMarker.key !== expectedKey
      || cacheMarker.key !== expectedKey
    ) {
      return false;
    }
    const hiddenLockBytes = readFileSync(path.join(nodeModulesPath, '.package-lock.json'));
    if (sha256(hiddenLockBytes) !== cacheMarker.hiddenLockHash) {
      return false;
    }
    const hiddenLock = JSON.parse(hiddenLockBytes);
    const packagePaths = packagePathsFromHiddenLock(hiddenLock);
    if (packagePaths.length !== cacheMarker.packageCount) {
      return false;
    }
    return packagePaths.every((packagePath) => (
      existsSync(path.join(path.dirname(nodeModulesPath), packagePath, 'package.json'))
    ));
  } catch {
    return false;
  }
}

export function materializeDependencyTree(entryPath, nodeModulesPath, expectedKey) {
  if (validateLocalTree(entryPath, nodeModulesPath, expectedKey)) {
    return;
  }
  removeNodeModules(nodeModulesPath);
  const temporaryPath = `${nodeModulesPath}.cache-tmp-${process.pid}-${randomUUID()}`;
  removePathWithoutFollowingLinks(temporaryPath);
  try {
    cpSync(
      path.join(entryPath, 'node_modules'),
      temporaryPath,
      {
        recursive: true,
        dereference: false,
        verbatimSymlinks: true,
        mode: constants.COPYFILE_FICLONE,
      },
    );
    atomicWriteJson(path.join(temporaryPath, LOCAL_MARKER), {
      schema: CACHE_SCHEMA,
      key: expectedKey,
      materializedAt: new Date().toISOString(),
    });
    renameSync(temporaryPath, nodeModulesPath);
  } catch (error) {
    removePathWithoutFollowingLinks(temporaryPath);
    throw error;
  }
}

function installIsolated(projectPath, npmArgs, runNpmCi) {
  removeNodeModules(path.join(projectPath, 'node_modules'));
  return runNpmCi(projectPath, npmArgs);
}

function defaultNpmInstall(projectPath, npmArgs) {
  const invocation = npmInvocation(['ci', ...npmArgs]);
  return run(invocation.command, invocation.args, {
    cwd: projectPath,
    shell: invocation.shell,
  });
}

function gitValue(repoRoot, args) {
  return run('git', args, { cwd: repoRoot, capture: true }).stdout;
}

export function runtimeInfo(repoRoot) {
  const invocation = npmInvocation(['--version']);
  const npmVersion = run(invocation.command, invocation.args, {
    cwd: repoRoot,
    capture: true,
    shell: invocation.shell,
  }).stdout;
  const configInvocation = npmInvocation(['config', 'list', '--json']);
  const npmConfig = JSON.parse(run(configInvocation.command, configInvocation.args, {
    cwd: repoRoot,
    capture: true,
    shell: configInvocation.shell,
  }).stdout);
  const installConfig = Object.fromEntries(
    [
      'bin-links',
      'cpu',
      'engine-strict',
      'force',
      'foreground-scripts',
      'global-style',
      'ignore-scripts',
      'include',
      'install-links',
      'install-strategy',
      'legacy-peer-deps',
      'legacy-bundling',
      'libc',
      'omit',
      'optional',
      'os',
      'prefer-dedupe',
      'production',
      'strict-peer-deps',
    ].map((name) => [name, npmConfig[name] ?? null]),
  );
  return {
    nodeVersion: process.version,
    npmVersion,
    platform: process.platform,
    arch: process.arch,
    installConfigHash: sha256(JSON.stringify({
      ...installConfig,
      NODE_ENV: process.env.NODE_ENV ?? null,
    })),
  };
}

function cacheRootForRepo(repoRoot, override) {
  if (override) {
    return path.resolve(repoRoot, override);
  }
  const commonDir = resolveCommonDir(
    repoRoot,
    gitValue(repoRoot, ['rev-parse', '--git-common-dir']),
  );
  return path.join(commonDir, 'agentweaver-cache', `npm-v${CACHE_SCHEMA}`);
}

function invalidationNonce(cacheRoot, baseKey) {
  try {
    return readJson(path.join(cacheRoot, 'invalidations', `${baseKey}.json`)).nonce ?? '';
  } catch {
    return '';
  }
}

function projectInputs(projectPath) {
  const packageJsonPath = path.join(projectPath, 'package.json');
  const lockfilePath = path.join(projectPath, 'package-lock.json');
  const packageJsonBytes = readFileSync(packageJsonPath);
  const lockfileBytes = readFileSync(lockfilePath);
  return {
    packageJsonBytes,
    lockfileBytes,
    packageJson: JSON.parse(packageJsonBytes),
    lockfile: JSON.parse(lockfileBytes),
  };
}

function fallback(projectPath, npmArgs, reason, runNpmCi) {
  console.warn(`[deps] shared dependency reuse unavailable for ${projectPath}: ${reason}`);
  console.warn('[deps] falling back to an isolated npm ci');
  const result = installIsolated(projectPath, npmArgs, runNpmCi);
  console.log(`[deps] isolated install completed in ${result.elapsedSeconds ?? '0.00'}s`);
  return { mode: 'isolated', reason };
}

export function ensureProject({
  repoRoot,
  project = '.',
  cacheRoot,
  isolated = false,
  requireShared = false,
  npmArgs = [],
  lockTimeoutMs = DEFAULT_LOCK_TIMEOUT_MS,
  staleLockMs = DEFAULT_STALE_LOCK_MS,
  runNpmCi = defaultNpmInstall,
  materialize = materializeDependencyTree,
  runtime,
  environment = process.env,
}) {
  const startedAt = performance.now();
  const projectPath = path.resolve(repoRoot, project);
  const resolvedRuntime = runtime ?? runtimeInfo(projectPath);
  const nodeModulesPath = path.join(projectPath, 'node_modules');
  const inputs = projectInputs(projectPath);
  const analysis = analyzeProject(inputs.packageJson, inputs.lockfile);
  const forcedIsolated = isolated
    || environment.CI === 'true'
    || environment.AGENTWEAVER_DISABLE_SHARED_DEPS === '1';

  if (forcedIsolated) {
    const reason = 'shared reuse was disabled for this environment';
    if (requireShared) {
      throw new Error(reason);
    }
    console.log(`[deps] running isolated npm ci for ${project}`);
    const result = installIsolated(projectPath, npmArgs, runNpmCi);
    console.log(`[deps] isolated install completed in ${result.elapsedSeconds ?? '0.00'}s`);
    return { mode: 'isolated', reason };
  }

  if (!analysis.reusable) {
    if (requireShared) {
      throw new Error(analysis.reason);
    }
    return fallback(projectPath, npmArgs, analysis.reason, runNpmCi);
  }

  const resolvedCacheRoot = cacheRootForRepo(
    repoRoot,
    cacheRoot ?? environment.AGENTWEAVER_DEPS_CACHE_DIR,
  );
  mkdirSync(resolvedCacheRoot, { recursive: true });
  const initialIdentity = computeCacheIdentity({
    ...inputs,
    ...resolvedRuntime,
    npmArgsHash: sha256(JSON.stringify(npmArgs)),
  });
  const nonce = invalidationNonce(resolvedCacheRoot, initialIdentity.baseKey);
  const identity = computeCacheIdentity({
    ...inputs,
    ...resolvedRuntime,
    npmArgsHash: sha256(JSON.stringify(npmArgs)),
    invalidationNonce: nonce,
  });
  const entryPath = path.join(resolvedCacheRoot, 'entries', identity.key);
  const lockPath = path.join(resolvedCacheRoot, 'locks', identity.key);

  let validation = validateCacheEntry(entryPath, identity.key);
  if (!validation.valid) {
    let release;
    try {
      release = acquireLock(lockPath, {
        timeoutMs: lockTimeoutMs,
        staleAfterMs: staleLockMs,
      });
      validation = validateCacheEntry(entryPath, identity.key);
      if (!validation.valid) {
        removeNodeModules(nodeModulesPath);
        removePathWithoutFollowingLinks(entryPath);
        const entriesPath = path.dirname(entryPath);
        mkdirSync(entriesPath, { recursive: true });
        for (const entry of readdirSync(entriesPath)) {
          if (entry.startsWith(`${identity.key}.tmp-`)) {
            removePathWithoutFollowingLinks(path.join(entriesPath, entry));
          }
        }

        console.log(`[deps] populating shared cache for ${project} (${identity.key.slice(0, 12)})`);
        const installResult = installIsolated(projectPath, npmArgs, runNpmCi);
        assertSafeDependencyLinks(nodeModulesPath);
        rmSync(path.join(nodeModulesPath, '.vite'), { recursive: true, force: true });
        rmSync(path.join(nodeModulesPath, '.vitepress'), { recursive: true, force: true });

        const temporaryEntry = `${entryPath}.tmp-${process.pid}-${randomUUID()}`;
        mkdirSync(temporaryEntry, { recursive: true });
        renameSync(nodeModulesPath, path.join(temporaryEntry, 'node_modules'));
        const hiddenLockPath = path.join(temporaryEntry, 'node_modules', '.package-lock.json');
        const hiddenLockBytes = readFileSync(hiddenLockPath);
        const hiddenLock = JSON.parse(hiddenLockBytes);
        atomicWriteJson(path.join(temporaryEntry, 'ready.json'), {
          schema: CACHE_SCHEMA,
          key: identity.key,
          baseKey: identity.baseKey,
          createdAt: new Date().toISOString(),
          nodeVersion: resolvedRuntime.nodeVersion,
          npmVersion: resolvedRuntime.npmVersion,
          platform: resolvedRuntime.platform,
          arch: resolvedRuntime.arch,
          installConfigHash: resolvedRuntime.installConfigHash,
          hiddenLockHash: sha256(hiddenLockBytes),
          packageCount: packagePathsFromHiddenLock(hiddenLock).length,
          installSeconds: Number(installResult.elapsedSeconds ?? 0),
        });
        renameSync(temporaryEntry, entryPath);
      }
    } catch (error) {
      if (requireShared) {
        throw error;
      }
      return fallback(projectPath, npmArgs, error?.message ?? String(error), runNpmCi);
    } finally {
      release?.();
    }
  }

  try {
    materialize(entryPath, nodeModulesPath, identity.key);
  } catch (error) {
    if (requireShared) {
      throw error;
    }
    return fallback(
      projectPath,
      npmArgs,
      `could not materialize the shared dependency tree: ${error?.message ?? error}`,
      runNpmCi,
    );
  }
  const elapsedSeconds = ((performance.now() - startedAt) / 1000).toFixed(2);
  console.log(
    `[deps] ${project} materialized from shared cache ${identity.key.slice(0, 12)} in ${elapsedSeconds}s`,
  );
  return { mode: 'shared', key: identity.key, elapsedSeconds };
}

export function invalidateProject({
  repoRoot,
  project,
  cacheRoot,
  runtime,
  environment = process.env,
}) {
  const projectPath = path.resolve(repoRoot, project);
  const resolvedRuntime = runtime ?? runtimeInfo(projectPath);
  const inputs = projectInputs(projectPath);
  const resolvedCacheRoot = cacheRootForRepo(
    repoRoot,
    cacheRoot ?? environment.AGENTWEAVER_DEPS_CACHE_DIR,
  );
  const identity = computeCacheIdentity({
    ...inputs,
    ...resolvedRuntime,
    npmArgsHash: sha256(JSON.stringify([])),
  });
  const invalidationPath = path.join(
    resolvedCacheRoot,
    'invalidations',
    `${identity.baseKey}.json`,
  );
  atomicWriteJson(invalidationPath, {
    nonce: randomUUID(),
    invalidatedAt: new Date().toISOString(),
  });
  console.log(`[deps] invalidated future materializations for ${project}`);
}

function parseArgs(argv) {
  const options = {
    command: 'ensure',
    projects: [],
    isolated: false,
    requireShared: false,
    cacheRoot: null,
    npmArgs: [],
  };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === 'ensure' || argument === 'invalidate') {
      options.command = argument;
    } else if (argument === '--project') {
      options.projects.push(argv[++index]);
    } else if (argument === '--isolated') {
      options.isolated = true;
    } else if (argument === '--require-shared') {
      options.requireShared = true;
    } else if (argument === '--cache-root') {
      options.cacheRoot = argv[++index];
    } else {
      throw new Error(`unknown argument: ${argument}`);
    }
  }
  if (options.projects.length === 0) {
    options.projects = DEFAULT_PROJECTS;
  }
  return options;
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  const scriptPath = fileURLToPath(import.meta.url);
  const repoRoot = path.resolve(path.dirname(scriptPath), '..', '..');
  for (const project of options.projects) {
    if (options.command === 'invalidate') {
      invalidateProject({
        repoRoot,
        project,
        cacheRoot: options.cacheRoot,
      });
    } else {
      ensureProject({
        repoRoot,
        project,
        cacheRoot: options.cacheRoot,
        isolated: options.isolated,
        requireShared: options.requireShared,
        npmArgs: options.npmArgs,
      });
    }
  }
}

const isMain = process.argv[1]
  && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url));
if (isMain) {
  try {
    main();
  } catch (error) {
    console.error(`[deps] ${error?.stack ?? error}`);
    process.exitCode = 1;
  }
}
