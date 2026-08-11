import {
  existsSync,
  linkSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';
import { hostname } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const CACHE_SCHEMA = 2;
export const DEFAULT_PROJECTS = ['.', 'apps/web', 'docs', 'scripts/ui-harness'];
const LOCAL_MARKER = '.agentweaver-deps.json';
const CACHE_MARKER = 'cache.json';
const GENERATION_SCHEMA = 2;
const INITIAL_GENERATION = '0';
const DEFAULT_LOCK_TIMEOUT_MS = 10 * 60 * 1000;
const DEFAULT_STALE_LOCK_MS = 30 * 60 * 1000;
const POLL_INTERVAL_MS = 250;
const DEFAULT_NPM_REGISTRY = 'https://registry.npmjs.org/';
let cachedCurrentProcessStartToken;

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function atomicWriteJson(filePath, value) {
  mkdirSync(path.dirname(filePath), { recursive: true });
  const temporaryPath = `${filePath}.${process.pid}.${randomUUID()}.tmp`;
  writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
  try {
    renameSync(temporaryPath, filePath);
  } finally {
    rmSync(temporaryPath, { force: true });
  }
}

function atomicCreateJson(filePath, value) {
  mkdirSync(path.dirname(filePath), { recursive: true });
  const temporaryPath = `${filePath}.${process.pid}.${randomUUID()}.tmp`;
  writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
  try {
    linkSync(temporaryPath, filePath);
    return true;
  } catch (error) {
    if (error?.code !== 'EEXIST') {
      throw error;
    }
    return false;
  } finally {
    rmSync(temporaryPath, { force: true });
  }
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
  packageRootIdentity,
  packageJsonBytes,
  lockfileBytes,
  nodeVersion,
  npmVersion,
  platform,
  arch,
  libc = 'none',
  installConfigHash = '',
  npmArgsHash = '',
  lifecycleEnvironmentHash = '',
  invalidationGeneration = INITIAL_GENERATION,
  repositoryCacheIdentity = '',
}) {
  const identity = {
    schema: CACHE_SCHEMA,
    packageRootIdentity,
    packageJson: sha256(packageJsonBytes),
    lockfile: sha256(lockfileBytes),
    nodeVersion,
    npmVersion,
    platform,
    arch,
    libc,
    installConfigHash,
    npmArgsHash,
    lifecycleEnvironmentHash,
  };
  if (repositoryCacheIdentity) {
    identity.repositoryCacheIdentity = repositoryCacheIdentity;
  }
  const baseKey = sha256(JSON.stringify(identity));
  const key = invalidationGeneration !== INITIAL_GENERATION
    ? sha256(JSON.stringify({ baseKey, invalidationGeneration }))
    : baseKey;
  return { baseKey, key, invalidationGeneration };
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

export function processStartToken(pid, platform = process.platform) {
  if (pid === process.pid && cachedCurrentProcessStartToken !== undefined) {
    return cachedCurrentProcessStartToken;
  }
  if (!processIsAlive(pid)) {
    return null;
  }
  let token = null;
  if (platform === 'linux') {
    try {
      const fields = readFileSync(`/proc/${pid}/stat`, 'utf8').trim().split(' ');
      token = `linux:${fields[21]}`;
    } catch {
      token = null;
    }
  } else if (platform === 'win32') {
    const result = spawnSync(
      'powershell.exe',
      [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        `(Get-Process -Id ${pid} -ErrorAction Stop).StartTime.ToUniversalTime().Ticks`,
      ],
      { encoding: 'utf8', windowsHide: true },
    );
    token = result.status === 0 ? `win32:${result.stdout.trim()}` : null;
  }
  if (pid === process.pid) {
    cachedCurrentProcessStartToken = token;
  }
  return token;
}

function readJson(filePath) {
  return JSON.parse(readFileSync(filePath, 'utf8'));
}

function lockIsStale(lockPath, staleAfterMs, processStartTokenFn) {
  try {
    const owner = readJson(path.join(lockPath, 'owner.json'));
    const age = Date.now() - Date.parse(owner.startedAt);
    if (owner.hostname !== hostname()) {
      return age > staleAfterMs;
    }
    if (!processIsAlive(owner.pid)) {
      return age > 1000;
    }
    const currentStartToken = processStartTokenFn(owner.pid);
    if (owner.processStartToken && currentStartToken) {
      return owner.processStartToken !== currentStartToken && age > 1000;
    }
    // Without a start-time identity, a live PID cannot be recovered safely.
    return false;
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
    processStartTokenFn = processStartToken,
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
        processStartToken: processStartTokenFn(process.pid),
        startedAt: new Date().toISOString(),
      });
      return () => {
        try {
          const owner = readJson(path.join(lockPath, 'owner.json'));
          if (owner.token === token) {
            rmSync(lockPath, { recursive: true, force: true });
          }
        } catch {
          // A recovered or replaced lock is owned by another process.
        }
      };
    } catch (error) {
      if (error?.code !== 'EEXIST') {
        throw error;
      }
    }

    if (lockIsStale(lockPath, staleAfterMs, processStartTokenFn)) {
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
      throw new Error(`timed out waiting for dependency lock ${lockPath}`);
    }
    sleep(pollIntervalMs);
  }
}

function gitValue(repoRoot, args) {
  return run('git', args, { cwd: repoRoot, capture: true }).stdout;
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

function canonicalWorktree(repoRoot) {
  const canonical = realpathSync.native(repoRoot);
  return process.platform === 'win32' ? canonical.toLowerCase() : canonical;
}

function repositoryIdentityForRepo(repoRoot, override) {
  if (override) {
    return override;
  }
  if (!existsSync(path.join(repoRoot, '.git'))) {
    return canonicalWorktree(repoRoot);
  }
  const commonDir = resolveCommonDir(
    repoRoot,
    gitValue(repoRoot, ['rev-parse', '--git-common-dir']),
  );
  const canonical = realpathSync.native(commonDir);
  return process.platform === 'win32' ? canonical.toLowerCase() : canonical;
}

export function acquireValidationLock(repoRoot, {
  cacheRoot,
  timeoutMs = DEFAULT_LOCK_TIMEOUT_MS,
  staleAfterMs = DEFAULT_STALE_LOCK_MS,
} = {}) {
  const resolvedCacheRoot = cacheRootForRepo(repoRoot, cacheRoot);
  const worktreeKey = sha256(canonicalWorktree(repoRoot));
  return acquireLock(
    path.join(resolvedCacheRoot, 'validation-locks', worktreeKey),
    { timeoutMs, staleAfterMs },
  );
}

export function acquireCacheUse(cacheRoot, {
  timeoutMs = DEFAULT_LOCK_TIMEOUT_MS,
  staleAfterMs = DEFAULT_STALE_LOCK_MS,
  onAcquired,
} = {}) {
  const releaseGate = acquireLock(
    path.join(cacheRoot, 'maintenance.lock'),
    { timeoutMs, staleAfterMs },
  );
  let releaseUse;
  try {
    releaseUse = acquireLock(
      path.join(cacheRoot, 'active', randomUUID()),
      { timeoutMs, staleAfterMs },
    );
    return onAcquired ? onAcquired(releaseUse) : releaseUse;
  } catch (error) {
    releaseUse?.();
    throw error;
  } finally {
    releaseGate();
  }
}

function generationStatePath(
  cacheRoot,
  repositoryIdentity,
  packageRootIdentity,
) {
  return path.join(
    cacheRoot,
    'generations',
    `${sha256(JSON.stringify({ repositoryIdentity, packageRootIdentity }))}.json`,
  );
}

function validateGenerationState(
  state,
  repositoryIdentity,
  packageRootIdentity,
  statePath,
) {
  if (
    state?.schema !== GENERATION_SCHEMA
    || state.repositoryIdentity !== repositoryIdentity
    || state.packageRootIdentity !== packageRootIdentity
    || typeof state.generation !== 'string'
    || state.generation.length === 0
  ) {
    throw new Error(`invalid dependency invalidation generation at ${statePath}`);
  }
  return state;
}

function readOrCreateGenerationLocked(
  cacheRoot,
  repositoryIdentity,
  packageRootIdentity,
) {
  const statePath = generationStatePath(
    cacheRoot,
    repositoryIdentity,
    packageRootIdentity,
  );
  if (existsSync(statePath)) {
    return validateGenerationState(
      readJson(statePath),
      repositoryIdentity,
      packageRootIdentity,
      statePath,
    );
  }
  const state = {
    schema: GENERATION_SCHEMA,
    repositoryIdentity,
    packageRootIdentity,
    generation: INITIAL_GENERATION,
    updatedAt: new Date().toISOString(),
  };
  if (!atomicCreateJson(statePath, state)) {
    return validateGenerationState(
      readJson(statePath),
      repositoryIdentity,
      packageRootIdentity,
      statePath,
    );
  }
  return state;
}

function rotateGenerationLocked(
  cacheRoot,
  repositoryIdentity,
  packageRootIdentity,
) {
  const current = readOrCreateGenerationLocked(
    cacheRoot,
    repositoryIdentity,
    packageRootIdentity,
  );
  const next = {
    ...current,
    generation: randomUUID(),
    updatedAt: new Date().toISOString(),
  };
  atomicWriteJson(
    generationStatePath(cacheRoot, repositoryIdentity, packageRootIdentity),
    next,
  );
  return { current, next };
}

export function acquireProjectCacheUse(cacheRoot, packageRootIdentity, {
  repositoryIdentity = cacheRoot,
  timeoutMs = DEFAULT_LOCK_TIMEOUT_MS,
  staleAfterMs = DEFAULT_STALE_LOCK_MS,
} = {}) {
  return acquireCacheUse(cacheRoot, {
    timeoutMs,
    staleAfterMs,
    onAcquired(release) {
      const generationState = readOrCreateGenerationLocked(
        cacheRoot,
        repositoryIdentity,
        packageRootIdentity,
      );
      return {
        generation: generationState.generation,
        release,
      };
    },
  });
}

export function acquireMaintenanceLock(cacheRoot, {
  timeoutMs = DEFAULT_LOCK_TIMEOUT_MS,
  staleAfterMs = DEFAULT_STALE_LOCK_MS,
  pollIntervalMs = POLL_INTERVAL_MS,
} = {}) {
  const startedAt = Date.now();
  const releaseMaintenance = acquireLock(
    path.join(cacheRoot, 'maintenance.lock'),
    { timeoutMs, staleAfterMs, pollIntervalMs },
  );
  const activeRoot = path.join(cacheRoot, 'active');
  try {
    while (existsSync(activeRoot)) {
      const active = [];
      for (const entry of readdirSync(activeRoot)) {
        const leasePath = path.join(activeRoot, entry);
        if (lockIsStale(leasePath, staleAfterMs, processStartToken)) {
          const abandonedPath = `${leasePath}.stale-${Date.now()}-${randomUUID()}`;
          try {
            renameSync(leasePath, abandonedPath);
            rmSync(abandonedPath, { recursive: true, force: true });
            continue;
          } catch (error) {
            if (!['ENOENT', 'EACCES', 'EPERM'].includes(error?.code)) {
              throw error;
            }
          }
        }
        active.push(leasePath);
      }
      if (active.length === 0) {
        break;
      }
      if (Date.now() - startedAt >= timeoutMs) {
        throw new Error(`timed out waiting for active npm cache users under ${activeRoot}`);
      }
      sleep(pollIntervalMs);
    }
    return releaseMaintenance;
  } catch (error) {
    releaseMaintenance();
    throw error;
  }
}

function safeRegistry(registry) {
  try {
    const url = new URL(registry ?? DEFAULT_NPM_REGISTRY);
    url.username = '';
    url.password = '';
    return url.toString();
  } catch {
    return String(registry ?? DEFAULT_NPM_REGISTRY);
  }
}

function lifecycleEnvironment(environment) {
  return {
    CI: environment.CI === 'true',
    NODE_ENV: environment.NODE_ENV ?? null,
    NODE_OPTIONS: environment.NODE_OPTIONS ?? null,
  };
}

function hasNpmCredentials(environment, npmConfig) {
  const credentialEnvironment = [
    'NPM_TOKEN',
    'NODE_AUTH_TOKEN',
    'NPM_CONFIG__AUTH',
    'NPM_CONFIG__AUTHTOKEN',
    'npm_config__auth',
    'npm_config__authToken',
  ];
  if (credentialEnvironment.some((name) => Boolean(environment[name]))) {
    return true;
  }
  return Object.keys(npmConfig).some((name) => (
    /(?:^|:)_(?:auth|authToken)$/iu.test(name)
    || /(?:password|token)$/iu.test(name)
  ));
}

function runtimeLibc() {
  if (process.platform !== 'linux') {
    return 'none';
  }
  const header = process.report?.getReport?.().header;
  return header?.glibcVersionRuntime
    ? `glibc-${header.glibcVersionRuntime}`
    : `linux-${process.env.npm_config_libc ?? 'unknown'}`;
}

export function runtimeInfo(repoRoot, environment = process.env) {
  const invocation = npmInvocation(['--version'], environment);
  const npmVersion = run(invocation.command, invocation.args, {
    cwd: repoRoot,
    capture: true,
    shell: invocation.shell,
    env: environment,
  }).stdout;
  const configInvocation = npmInvocation(['config', 'list', '--json'], environment);
  const npmConfig = JSON.parse(run(configInvocation.command, configInvocation.args, {
    cwd: repoRoot,
    capture: true,
    shell: configInvocation.shell,
    env: environment,
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
  installConfig.registry = safeRegistry(npmConfig.registry);
  return {
    nodeVersion: process.version,
    npmVersion,
    platform: process.platform,
    arch: process.arch,
    libc: runtimeLibc(),
    installConfigHash: sha256(JSON.stringify(installConfig)),
    lifecycleEnvironmentHash: sha256(JSON.stringify(lifecycleEnvironment(environment))),
    hasCredentials: hasNpmCredentials(environment, npmConfig),
  };
}

function projectInputs(repoRoot, project) {
  const canonicalRepoRoot = realpathSync.native(path.resolve(repoRoot));
  const projectPath = realpathSync.native(path.resolve(repoRoot, project));
  const relativeProjectPath = path.relative(canonicalRepoRoot, projectPath);
  if (
    relativeProjectPath.startsWith('..')
    || path.isAbsolute(relativeProjectPath)
  ) {
    throw new Error(`dependency project must be within the repository: ${project}`);
  }
  const packageJsonPath = path.join(projectPath, 'package.json');
  const lockfilePath = path.join(projectPath, 'package-lock.json');
  const packageJsonBytes = readFileSync(packageJsonPath);
  const lockfileBytes = readFileSync(lockfilePath);
  return {
    projectPath,
    packageRootIdentity: (
      process.platform === 'win32'
        ? relativeProjectPath.toLowerCase()
        : relativeProjectPath
    ).replaceAll('\\', '/') || '.',
    packageJsonBytes,
    lockfileBytes,
    packageJson: JSON.parse(packageJsonBytes),
    lockfile: JSON.parse(lockfileBytes),
  };
}

function removeNodeModules(nodeModulesPath) {
  try {
    const metadata = lstatSync(nodeModulesPath);
    if (metadata.isSymbolicLink()) {
      rmSync(nodeModulesPath, { force: true });
    } else {
      rmSync(nodeModulesPath, { recursive: true, force: true });
    }
  } catch (error) {
    if (error?.code !== 'ENOENT') {
      throw error;
    }
  }
}

function hiddenLockHash(nodeModulesPath) {
  return sha256(readFileSync(path.join(nodeModulesPath, '.package-lock.json')));
}

function isWithin(parent, child) {
  const normalizedParent = process.platform === 'win32' ? parent.toLowerCase() : parent;
  const normalizedChild = process.platform === 'win32' ? child.toLowerCase() : child;
  const relative = path.relative(normalizedParent, normalizedChild);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

export function verifyProjectResolution(projectPath, packageJson) {
  const nodeModulesPath = path.join(projectPath, 'node_modules');
  const nodeModulesMetadata = lstatSync(nodeModulesPath);
  if (nodeModulesMetadata.isSymbolicLink() || !nodeModulesMetadata.isDirectory()) {
    throw new Error(`node_modules is not a physical directory under ${projectPath}`);
  }
  const canonicalNodeModules = realpathSync.native(nodeModulesPath);
  const requiredPackages = Object.keys({
    ...packageJson.dependencies,
    ...packageJson.devDependencies,
  });
  for (const packageName of requiredPackages) {
    const packagePath = path.join(nodeModulesPath, ...packageName.split('/'), 'package.json');
    if (!existsSync(packagePath)) {
      throw new Error(`required dependency is missing: ${packageName}`);
    }
    if (!isWithin(canonicalNodeModules, realpathSync.native(packagePath))) {
      throw new Error(`dependency resolves outside this package root: ${packageName}`);
    }
  }

  const resolvable = ['vite', 'vitepress', '@changesets/cli', 'playwright']
    .filter((name) => requiredPackages.includes(name));
  const requireFromProject = createRequire(path.join(projectPath, 'package.json'));
  for (const packageName of resolvable) {
    const resolved = realpathSync.native(requireFromProject.resolve(packageName));
    if (!isWithin(canonicalNodeModules, resolved)) {
      throw new Error(`require.resolve escaped this package root: ${packageName}`);
    }
  }
}

function validateLocalTree(
  projectPath,
  packageJson,
  expectedKey,
  expectedGeneration,
) {
  try {
    const nodeModulesPath = path.join(projectPath, 'node_modules');
    const marker = readJson(path.join(nodeModulesPath, LOCAL_MARKER));
    if (marker.schema !== CACHE_SCHEMA || marker.key !== expectedKey) {
      return { valid: false, reason: 'local dependency marker does not match the fingerprint' };
    }
    if (
      (marker.invalidationGeneration ?? INITIAL_GENERATION)
      !== expectedGeneration
    ) {
      return { valid: false, reason: 'local dependency marker has a stale generation' };
    }
    if (hiddenLockHash(nodeModulesPath) !== marker.hiddenLockHash) {
      return { valid: false, reason: 'local npm hidden lockfile changed' };
    }
    verifyProjectResolution(projectPath, packageJson);
    return { valid: true, reason: null };
  } catch (error) {
    return { valid: false, reason: error?.message ?? String(error) };
  }
}

function defaultNpmInstall(projectPath, npmArgs, cachePath, environment) {
  const cacheArgs = cachePath ? ['--cache', cachePath] : [];
  const invocation = npmInvocation(['ci', ...cacheArgs, ...npmArgs], environment);
  return run(invocation.command, invocation.args, {
    cwd: projectPath,
    env: environment,
    shell: invocation.shell,
  });
}

function installIsolated(projectPath, npmArgs, runNpmCi, environment) {
  removeNodeModules(path.join(projectPath, 'node_modules'));
  return runNpmCi(projectPath, npmArgs, null, environment);
}

function installFromDownloadCache(
  projectPath,
  npmArgs,
  cachePath,
  runNpmCi,
  environment,
) {
  removeNodeModules(path.join(projectPath, 'node_modules'));
  mkdirSync(cachePath, { recursive: true });
  return runNpmCi(projectPath, npmArgs, cachePath, environment);
}

function cacheNamespaceState(cachePath, expectedKey, expectedGeneration) {
  const markerPath = path.join(cachePath, CACHE_MARKER);
  if (!existsSync(markerPath)) {
    return { valid: true, populated: false, reason: null };
  }
  try {
    const marker = readJson(markerPath);
    if (
      marker.schema !== CACHE_SCHEMA
      || marker.key !== expectedKey
      || (marker.invalidationGeneration ?? INITIAL_GENERATION)
        !== expectedGeneration
    ) {
      return { valid: false, populated: true, reason: 'download cache marker is corrupt' };
    }
    return { valid: true, populated: true, reason: null };
  } catch (error) {
    return { valid: false, populated: true, reason: error?.message ?? String(error) };
  }
}

function quarantineCacheNamespaceLocked(cacheRoot, cachePath, reason) {
  if (!existsSync(cachePath)) {
    return null;
  }
  const quarantinePath = path.join(
    cacheRoot,
    'quarantine',
    `${path.basename(cachePath)}-${Date.now()}-${randomUUID()}`,
  );
  mkdirSync(path.dirname(quarantinePath), { recursive: true });
  renameSync(cachePath, quarantinePath);
  atomicWriteJson(path.join(quarantinePath, 'quarantine.json'), {
    reason,
    quarantinedAt: new Date().toISOString(),
  });
  return quarantinePath;
}

function quarantineCacheNamespace(cacheRoot, cachePath, reason, lockOptions = {}) {
  const release = acquireMaintenanceLock(cacheRoot, lockOptions);
  try {
    return quarantineCacheNamespaceLocked(cacheRoot, cachePath, reason);
  } finally {
    release();
  }
}

function fallback(projectPath, npmArgs, reason, runNpmCi, environment) {
  console.warn(`[deps] shared npm download cache unavailable for ${projectPath}: ${reason}`);
  console.warn('[deps] falling back to an isolated npm ci');
  const result = installIsolated(projectPath, npmArgs, runNpmCi, environment);
  console.log(`[deps] isolated install completed in ${result.elapsedSeconds ?? '0.00'}s`);
  return { mode: 'isolated', reason, elapsedSeconds: result.elapsedSeconds };
}

function ensureProjectUnlocked({
  repoRoot,
  project,
  cacheRoot,
  isolated,
  requireShared,
  npmArgs,
  lockTimeoutMs,
  staleLockMs,
  runNpmCi,
  runtime,
  environment,
  repositoryIdentity,
}) {
  const startedAt = performance.now();
  const inputs = projectInputs(repoRoot, project);
  const resolvedRuntime = runtime ?? runtimeInfo(inputs.projectPath, environment);
  const analysis = analyzeProject(inputs.packageJson, inputs.lockfile);
  const forcedIsolated = isolated
    || environment.CI === 'true'
    || environment.AGENTWEAVER_DISABLE_SHARED_DEPS === '1';
  const credentialed = resolvedRuntime.hasCredentials === true;

  if (forcedIsolated || credentialed || !analysis.reusable) {
    const reason = forcedIsolated
      ? 'shared reuse was disabled for this environment'
      : credentialed
        ? 'authenticated npm configuration is excluded from shared cache namespaces'
        : analysis.reason;
    if (requireShared) {
      throw new Error(reason);
    }
    return fallback(inputs.projectPath, npmArgs, reason, runNpmCi, environment);
  }

  const configuredCacheRoot = cacheRoot ?? environment.AGENTWEAVER_DEPS_CACHE_DIR;
  const resolvedCacheRoot = cacheRootForRepo(repoRoot, configuredCacheRoot);
  const resolvedRepositoryIdentity = repositoryIdentityForRepo(
    repoRoot,
    repositoryIdentity,
  );
  const repositoryCacheIdentity = configuredCacheRoot
    ? resolvedRepositoryIdentity
    : '';
  const sharedAttempt = () => {
    const lease = acquireProjectCacheUse(
      resolvedCacheRoot,
      inputs.packageRootIdentity,
      {
        repositoryIdentity: resolvedRepositoryIdentity,
        timeoutMs: lockTimeoutMs,
        staleAfterMs: staleLockMs,
      },
    );
    try {
      const identity = computeCacheIdentity({
        ...inputs,
        ...resolvedRuntime,
        npmArgsHash: sha256(JSON.stringify(npmArgs)),
        invalidationGeneration: lease.generation,
        repositoryCacheIdentity,
      });
      const cachePath = path.join(resolvedCacheRoot, 'downloads', identity.key);
      const localValidation = validateLocalTree(
        inputs.projectPath,
        inputs.packageJson,
        identity.key,
        lease.generation,
      );
      if (localValidation.valid) {
        const elapsedSeconds = ((performance.now() - startedAt) / 1000).toFixed(2);
        console.log(`[deps] ${project} local dependency tree is current in ${elapsedSeconds}s`);
        return {
          kind: 'complete',
          result: {
            mode: 'local',
            key: identity.key,
            generation: lease.generation,
            elapsedSeconds,
          },
        };
      }

      const cacheState = cacheNamespaceState(
        cachePath,
        identity.key,
        lease.generation,
      );
      if (!cacheState.valid) {
        return {
          kind: 'quarantine',
          cachePath,
          reason: cacheState.reason,
        };
      }
      try {
        const installResult = installFromDownloadCache(
          inputs.projectPath,
          npmArgs,
          cachePath,
          runNpmCi,
          environment,
        );
        verifyProjectResolution(inputs.projectPath, inputs.packageJson);
        atomicCreateJson(path.join(cachePath, CACHE_MARKER), {
          schema: CACHE_SCHEMA,
          key: identity.key,
          packageRootIdentity: inputs.packageRootIdentity,
          invalidationGeneration: lease.generation,
          populatedAt: new Date().toISOString(),
        });
        const installedCacheState = cacheNamespaceState(
          cachePath,
          identity.key,
          lease.generation,
        );
        if (!installedCacheState.valid) {
          throw new Error(installedCacheState.reason);
        }
        atomicWriteJson(path.join(inputs.projectPath, 'node_modules', LOCAL_MARKER), {
          schema: CACHE_SCHEMA,
          key: identity.key,
          invalidationGeneration: lease.generation,
          hiddenLockHash: hiddenLockHash(path.join(inputs.projectPath, 'node_modules')),
          installedAt: new Date().toISOString(),
        });
        const elapsedSeconds = ((performance.now() - startedAt) / 1000).toFixed(2);
        console.log(
          `[deps] ${project} installed with shared npm cache ${identity.key.slice(0, 12)} `
          + `in ${elapsedSeconds}s`,
        );
        return {
          kind: 'complete',
          result: {
            mode: 'shared-cache',
            key: identity.key,
            generation: lease.generation,
            elapsedSeconds,
            installSeconds: installResult.elapsedSeconds,
          },
        };
      } catch (error) {
        return { kind: 'install-error', cachePath, error };
      }
    } finally {
      lease.release();
    }
  };

  let outcome = sharedAttempt();
  if (outcome.kind === 'quarantine') {
    quarantineCacheNamespace(
      resolvedCacheRoot,
      outcome.cachePath,
      outcome.reason,
      { timeoutMs: lockTimeoutMs, staleAfterMs: staleLockMs },
    );
    outcome = sharedAttempt();
  }
  if (outcome.kind === 'complete') {
    return outcome.result;
  }
  if (outcome.kind !== 'install-error') {
    throw new Error(`shared cache remained invalid after quarantine for ${project}`);
  }

  const firstError = outcome.error;
  quarantineCacheNamespace(
    resolvedCacheRoot,
    outcome.cachePath,
    `npm ci failed: ${firstError?.message ?? firstError}`,
    { timeoutMs: lockTimeoutMs, staleAfterMs: staleLockMs },
  );
  const retryOutcome = sharedAttempt();
  if (retryOutcome.kind === 'complete') {
    return retryOutcome.result;
  }
  if (retryOutcome.kind === 'quarantine') {
    quarantineCacheNamespace(
      resolvedCacheRoot,
      retryOutcome.cachePath,
      retryOutcome.reason,
      { timeoutMs: lockTimeoutMs, staleAfterMs: staleLockMs },
    );
    throw new AggregateError(
      [firstError, new Error(retryOutcome.reason)],
      `npm ci failed after quarantining the shared cache for ${project}`,
    );
  }
  quarantineCacheNamespace(
    resolvedCacheRoot,
    retryOutcome.cachePath,
    `cold npm ci retry failed: ${retryOutcome.error?.message ?? retryOutcome.error}`,
    { timeoutMs: lockTimeoutMs, staleAfterMs: staleLockMs },
  );
  throw new AggregateError(
    [firstError, retryOutcome.error],
    `npm ci failed after quarantining the shared cache for ${project}`,
  );
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
  runtime,
  environment = process.env,
  repositoryIdentity,
  validationLockHeld = false,
}) {
  let release;
  if (!validationLockHeld) {
    release = acquireValidationLock(repoRoot, {
      cacheRoot,
      timeoutMs: lockTimeoutMs,
      staleAfterMs: staleLockMs,
    });
  }
  try {
    return ensureProjectUnlocked({
      repoRoot,
      project,
      cacheRoot,
      isolated,
      requireShared,
      npmArgs,
      lockTimeoutMs,
      staleLockMs,
      runNpmCi,
      runtime,
      environment,
      repositoryIdentity,
    });
  } finally {
    release?.();
  }
}

function identityForProject({
  repoRoot,
  project,
  runtime,
  environment,
  npmArgs = [],
  invalidationGeneration = INITIAL_GENERATION,
  repositoryCacheIdentity = '',
}) {
  const inputs = projectInputs(repoRoot, project);
  const resolvedRuntime = runtime ?? runtimeInfo(inputs.projectPath, environment);
  return {
    inputs,
    runtime: resolvedRuntime,
    identity: computeCacheIdentity({
      ...inputs,
      ...resolvedRuntime,
      npmArgsHash: sha256(JSON.stringify(npmArgs)),
      invalidationGeneration,
      repositoryCacheIdentity,
    }),
  };
}

export function invalidateProject({
  repoRoot,
  project,
  cacheRoot,
  runtime,
  environment = process.env,
  npmArgs = [],
  repositoryIdentity,
  validationLockHeld = false,
}) {
  let releaseValidation;
  if (!validationLockHeld) {
    releaseValidation = acquireValidationLock(repoRoot, { cacheRoot });
  }
  try {
    const configuredCacheRoot = cacheRoot ?? environment.AGENTWEAVER_DEPS_CACHE_DIR;
    const resolvedCacheRoot = cacheRootForRepo(repoRoot, configuredCacheRoot);
    const resolvedRepositoryIdentity = repositoryIdentityForRepo(
      repoRoot,
      repositoryIdentity,
    );
    const repositoryCacheIdentity = configuredCacheRoot
      ? resolvedRepositoryIdentity
      : '';
    const {
      inputs,
      runtime: resolvedRuntime,
      identity: baseIdentity,
    } = identityForProject({
      repoRoot,
      project,
      runtime,
      environment,
      npmArgs,
      repositoryCacheIdentity,
    });
    const releaseMaintenance = acquireMaintenanceLock(resolvedCacheRoot);
    let previousIdentity;
    let nextGeneration;
    try {
      const rotated = rotateGenerationLocked(
        resolvedCacheRoot,
        resolvedRepositoryIdentity,
        inputs.packageRootIdentity,
      );
      previousIdentity = computeCacheIdentity({
        ...inputs,
        ...resolvedRuntime,
        npmArgsHash: sha256(JSON.stringify(npmArgs)),
        invalidationGeneration: rotated.current.generation,
        repositoryCacheIdentity,
      });
      nextGeneration = rotated.next.generation;
      quarantineCacheNamespaceLocked(
        resolvedCacheRoot,
        path.join(resolvedCacheRoot, 'downloads', previousIdentity.key),
        'explicit invalidation',
      );
    } finally {
      releaseMaintenance();
    }
    removeNodeModules(path.join(inputs.projectPath, 'node_modules'));
    console.log(`[deps] invalidated ${project} dependency state`);
    return {
      previousKey: previousIdentity?.key ?? baseIdentity.key,
      generation: nextGeneration,
    };
  } finally {
    releaseValidation?.();
  }
}

export function verifyProjectCache({
  repoRoot,
  project,
  cacheRoot,
  runtime,
  environment = process.env,
  npmArgs = [],
  repositoryIdentity,
  validationLockHeld = false,
}) {
  let releaseValidation;
  if (!validationLockHeld) {
    releaseValidation = acquireValidationLock(repoRoot, { cacheRoot });
  }
  try {
    const configuredCacheRoot = cacheRoot ?? environment.AGENTWEAVER_DEPS_CACHE_DIR;
    const resolvedCacheRoot = cacheRootForRepo(repoRoot, configuredCacheRoot);
    const resolvedRepositoryIdentity = repositoryIdentityForRepo(
      repoRoot,
      repositoryIdentity,
    );
    const repositoryCacheIdentity = configuredCacheRoot
      ? resolvedRepositoryIdentity
      : '';
    const { inputs } = identityForProject({
      repoRoot,
      project,
      runtime,
      environment,
      npmArgs,
      repositoryCacheIdentity,
    });
    const releaseMaintenance = acquireMaintenanceLock(resolvedCacheRoot);
    let verifyError;
    let cachePath;
    try {
      const generation = readOrCreateGenerationLocked(
        resolvedCacheRoot,
        resolvedRepositoryIdentity,
        inputs.packageRootIdentity,
      ).generation;
      const { identity } = identityForProject({
        repoRoot,
        project,
        runtime,
        environment,
        npmArgs,
        invalidationGeneration: generation,
        repositoryCacheIdentity,
      });
      cachePath = path.join(resolvedCacheRoot, 'downloads', identity.key);
      if (!existsSync(cachePath)) {
        console.log(`[deps] no shared npm cache exists for ${project}`);
        return;
      }
      const invocation = npmInvocation(['cache', 'verify', '--cache', cachePath], environment);
      run(invocation.command, invocation.args, {
        cwd: path.resolve(repoRoot, project),
        env: environment,
        shell: invocation.shell,
      });
    } catch (error) {
      verifyError = error;
      if (cachePath) {
        quarantineCacheNamespaceLocked(
          resolvedCacheRoot,
          cachePath,
          `npm cache verify failed: ${error?.message ?? error}`,
        );
      }
    } finally {
      releaseMaintenance();
    }
    if (verifyError) {
      throw verifyError;
    }
  } finally {
    releaseValidation?.();
  }
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
    if (['ensure', 'invalidate', 'verify'].includes(argument)) {
      options.command = argument;
    } else if (argument === '--project') {
      options.projects.push(argv[++index]);
    } else if (argument === '--isolated') {
      options.isolated = true;
    } else if (argument === '--require-shared') {
      options.requireShared = true;
    } else if (argument === '--cache-root') {
      options.cacheRoot = argv[++index];
    } else if (argument === '--npm-arg') {
      options.npmArgs.push(argv[++index]);
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
  const releaseValidation = acquireValidationLock(repoRoot, { cacheRoot: options.cacheRoot });
  try {
    for (const project of options.projects) {
      if (options.command === 'invalidate') {
        invalidateProject({
          repoRoot,
          project,
          cacheRoot: options.cacheRoot,
          npmArgs: options.npmArgs,
          validationLockHeld: true,
        });
      } else if (options.command === 'verify') {
        verifyProjectCache({
          repoRoot,
          project,
          cacheRoot: options.cacheRoot,
          npmArgs: options.npmArgs,
          validationLockHeld: true,
        });
      } else {
        ensureProject({
          repoRoot,
          project,
          cacheRoot: options.cacheRoot,
          isolated: options.isolated,
          requireShared: options.requireShared,
          npmArgs: options.npmArgs,
          validationLockHeld: true,
        });
      }
    }
  } finally {
    releaseValidation();
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
