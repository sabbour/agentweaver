import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { loadCaptureConfig, loadJoinedCapturePlan } from './capture-config.mjs';
import { renderCaptureScript } from './capture-plan.mjs';
import {
  captureArtifactPaths,
  persistCaptureEvidence,
} from './capture-evidence.mjs';
import { writeSeedScript } from './auth.mjs';

export const DEFAULT_RECORDING_SESSION = 'agentweaver-demo';
export const DEFAULT_RECORDING_BASE_URL = 'https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io';
export const DEFAULT_RECORDING_AUTH_ROOT = 'scripts/demo-recording/.auth';
export const EDGE_DEFAULT_PROFILE_DIRECTORY = 'Default';
const RECORDING_AUTH_MODES = new Set(['auto', 'github-legacy', 'entra']);

const COMMAND_OPTIONS = {
  signin: new Set(['session', 'base-url', 'auth-root', 'wait-for-edge-ms', 'auth-mode', 'plan']),
  open: new Set(['session', 'base-url', 'auth-root', 'auth-mode', 'plan']),
  start: new Set(['session', 'base-url', 'auth-root', 'wait-for-edge-ms', 'auth-mode', 'plan', 'beat-plan', 'out-dir', 'beat']),
  prepare: new Set(['auth-root', 'plan', 'beat-plan', 'out-dir', 'beat']),
  capture: new Set(['session', 'base-url', 'auth-root', 'auth-mode', 'plan', 'beat-plan', 'out-dir', 'beat', 'all', 'resume']),
  status: new Set(['session', 'base-url', 'auth-root']),
  close: new Set(['session']),
};

const BOOLEAN_OPTIONS = new Set(['all', 'resume']);
const PROFILE_COPY_EXCLUDED_DIRECTORIES = new Set([
  'BrowserMetrics',
  'Cache',
  'Code Cache',
  'Crashpad',
  'DawnCache',
  'GPUCache',
  'GrShaderCache',
  'ShaderCache',
  'component_crx_cache',
  'extensions_crx_cache',
]);
const PROFILE_COPY_EXCLUDED_FILES = new Set([
  'LOCK',
  'SingletonCookie',
  'SingletonLock',
  'SingletonSocket',
]);
const PROFILE_COPY_EXCLUDED_SUFFIXES = ['.tmp'];
const PROFILE_COPY_MAX_ATTEMPTS = 3;
const PROFILE_COPY_RETRY_DELAY_MS = 250;

function toCamelCase(value) {
  return value.replace(/-([a-z])/g, (_match, letter) => letter.toUpperCase());
}

export function parseRecordingCommandOptions(command, argv) {
  const allowed = COMMAND_OPTIONS[command];
  if (!allowed) throw new Error(`Unknown recording command: ${command}`);

  const options = {
    session: DEFAULT_RECORDING_SESSION,
    baseUrl: DEFAULT_RECORDING_BASE_URL,
    authRoot: DEFAULT_RECORDING_AUTH_ROOT,
    authMode: 'auto',
    waitForEdgeMs: 300_000,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (!flag.startsWith('--')) throw new Error(`Unexpected argument: ${flag}`);
    const name = flag.slice(2);
    if (!allowed.has(name)) throw new Error(`Unknown option for ${command}: ${flag}`);
    if (BOOLEAN_OPTIONS.has(name)) {
      options[toCamelCase(name)] = true;
      continue;
    }
    const value = argv[index + 1];
    if (!value || value.startsWith('--')) throw new Error(`Expected a value after ${flag}.`);
    options[toCamelCase(name)] = value;
    index += 1;
  }

  if (!/^[a-zA-Z0-9_-]+$/.test(options.session)) {
    throw new Error('--session may contain only letters, numbers, underscores, and hyphens.');
  }
  if (!URL.canParse(options.baseUrl)) throw new Error('--base-url must be an absolute URL.');
  if (new URL(options.baseUrl).protocol !== 'https:') throw new Error('--base-url must use HTTPS.');
  if (!RECORDING_AUTH_MODES.has(options.authMode)) {
    throw new Error('--auth-mode must be auto, github-legacy, or entra.');
  }

  options.waitForEdgeMs = Number(options.waitForEdgeMs);
  if (!Number.isInteger(options.waitForEdgeMs) || options.waitForEdgeMs < 0) {
    throw new Error('--wait-for-edge-ms must be a non-negative integer.');
  }
  if (command === 'prepare' || command === 'capture') {
    if (!options.plan) throw new Error(`${command} requires --plan.`);
  }
  if (command === 'capture' && !options.beat && !options.all) {
    throw new Error('capture requires --beat <id> or --all.');
  }
  if (options.beat && options.all) throw new Error('Use either --beat or --all, not both.');
  return options;
}

function resolveCapturePlanPath(plan) {
  const workingDirectoryPath = path.resolve(plan);
  if (existsSync(workingDirectoryPath) || path.isAbsolute(plan)) return workingDirectoryPath;

  try {
    const repositoryRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], {
      encoding: 'utf8',
      windowsHide: true,
    }).trim();
    const repositoryPath = path.resolve(repositoryRoot, plan);
    if (existsSync(repositoryPath)) return repositoryPath;
  } catch {
    // Preserve the working-directory path so the loader reports the original failure.
  }

  return workingDirectoryPath;
}

export async function resolvePlanAuthentication(options) {
  if (!options.plan) return options;

  const planPath = resolveCapturePlanPath(options.plan);
  let captureConfig;
  try {
    captureConfig = await loadCaptureConfig(planPath);
  } catch (error) {
    if (error?.code === 'ENOENT') {
      throw new Error(
        `Capture plan "${options.plan}" was not found. Use a path relative to the repository root or an absolute path.`,
      );
    }
    throw error;
  }
  const planAuthMode = captureConfig.authentication?.mode;
  if (!planAuthMode) return { ...options, plan: planPath };
  if (options.authMode !== 'auto' && options.authMode !== planAuthMode) {
    throw new Error(
      `--auth-mode ${options.authMode} conflicts with the ${planAuthMode} authentication required by ${options.plan}.`,
    );
  }
  return { ...options, plan: planPath, authMode: planAuthMode };
}

export function recordingAuthPaths(authRoot = DEFAULT_RECORDING_AUTH_ROOT) {
  const root = path.resolve(authRoot);
  const storageStatePath = path.join(root, 'recording.storageState.json');
  return {
    root,
    storageStatePath,
    sessionStoragePath: `${storageStatePath}.sessionStorage.json`,
    automationUserDataDir: path.join(root, 'edge-default-automation'),
    generatedScriptsRoot: path.join(root, 'generated'),
  };
}

export function resolveLiteralEdgeDefaultProfile(localAppData = process.env.LOCALAPPDATA) {
  if (!localAppData) throw new Error('LOCALAPPDATA is not set. Microsoft Edge Default profile cannot be located.');
  const userDataDir = path.resolve(localAppData, 'Microsoft', 'Edge', 'User Data');
  return {
    userDataDir,
    profileDirectory: EDGE_DEFAULT_PROFILE_DIRECTORY,
    profilePath: path.join(userDataDir, EDGE_DEFAULT_PROFILE_DIRECTORY),
    localStatePath: path.join(userDataDir, 'Local State'),
  };
}

export async function validateLiteralEdgeDefaultProfile(
  edgeProfile,
  {
    localAppData = process.env.LOCALAPPDATA,
    access = fs.access,
    readFile = fs.readFile,
  } = {},
) {
  const expected = resolveLiteralEdgeDefaultProfile(localAppData);
  if (
    edgeProfile.profileDirectory !== EDGE_DEFAULT_PROFILE_DIRECTORY
    || path.resolve(edgeProfile.userDataDir) !== expected.userDataDir
    || path.resolve(edgeProfile.profilePath) !== expected.profilePath
    || path.resolve(edgeProfile.localStatePath) !== expected.localStatePath
  ) {
    throw new Error('Refusing to use any Microsoft Edge profile except the literal Default profile.');
  }

  try {
    await access(expected.profilePath);
    await access(expected.localStatePath);
  } catch {
    throw new Error('The literal Microsoft Edge Default work profile is unavailable. Close Edge and restore the Default profile before running signin.');
  }

  let localState;
  try {
    localState = JSON.parse(await readFile(expected.localStatePath, 'utf8'));
  } catch {
    throw new Error('The Microsoft Edge Default profile identity could not be validated from Local State. Run signin only after Edge has fully closed.');
  }
  if (!localState?.profile?.info_cache?.[EDGE_DEFAULT_PROFILE_DIRECTORY]) {
    throw new Error('The Microsoft Edge Local State file does not identify a Default profile. Refusing to use another Edge profile or a stale automation copy.');
  }
  return expected;
}

export function buildEdgeLaunchOptions(userDataDir) {
  return {
    channel: 'msedge',
    headless: false,
    viewport: null,
    args: [
      `--profile-directory=${EDGE_DEFAULT_PROFILE_DIRECTORY}`,
      '--no-first-run',
      '--no-default-browser-check',
    ],
    userDataDir: path.resolve(userDataDir),
  };
}

export function shouldCopyEdgeProfileEntry(sourcePath, profileRoot) {
  const relative = path.relative(profileRoot, sourcePath);
  if (!relative || relative.startsWith('..')) return true;
  const segments = relative.split(path.sep);
  if (segments.some((segment) => PROFILE_COPY_EXCLUDED_DIRECTORIES.has(segment))) return false;
  const basename = path.basename(sourcePath);
  return !PROFILE_COPY_EXCLUDED_FILES.has(basename)
    && !PROFILE_COPY_EXCLUDED_SUFFIXES.some((suffix) => basename.toLowerCase().endsWith(suffix));
}

export function assertIgnoredAuthRoot(authRoot, checkIgnore) {
  const probe = path.join(path.resolve(authRoot), '.recording-auth-probe');
  if (!checkIgnore(probe)) {
    throw new Error(`Refusing to use ${authRoot} because Git does not ignore it.`);
  }
}

export function assertAuthRootWithinRepository(authRoot, repositoryRoot) {
  const root = path.resolve(authRoot);
  const repository = path.resolve(repositoryRoot);
  const relative = path.relative(repository, root);
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) {
    throw new Error('The recording auth directory must be a Git-ignored child of the repository.');
  }
}

function isPathWithin(candidate, root, { allowEqual = false } = {}) {
  const relative = path.relative(root, candidate);
  return (allowEqual && relative === '')
    || (relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative));
}

function normalizedPath(candidate) {
  const resolved = path.resolve(candidate);
  return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

async function resolveWithoutReparse(candidate, repositoryRoot, {
  lstat = fs.lstat,
  realpath = fs.realpath,
} = {}) {
  const repository = path.resolve(repositoryRoot);
  const target = path.resolve(candidate);
  const relative = path.relative(repository, target);
  if (relative.startsWith('..') || path.isAbsolute(relative)) {
    throw new Error('The recording auth destination must remain inside the repository.');
  }

  const repositoryReal = await realpath(repository);
  let lexical = repository;
  let resolved = repositoryReal;
  const segments = relative ? relative.split(path.sep) : [];
  for (let index = 0; index < segments.length; index += 1) {
    const segment = segments[index];
    lexical = path.join(lexical, segment);
    let stats;
    try {
      stats = await lstat(lexical);
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
      resolved = path.join(resolved, ...segments.slice(index));
      break;
    }
    if (stats.isSymbolicLink()) {
      throw new Error('Refusing a recording auth path that crosses a junction, symlink, or reparse point.');
    }
    const actual = await realpath(lexical);
    const expected = path.join(resolved, segment);
    if (normalizedPath(actual) !== normalizedPath(expected)) {
      throw new Error('Refusing a recording auth path that crosses a junction, symlink, or reparse point.');
    }
    resolved = actual;
  }

  if (!isPathWithin(resolved, repositoryReal, { allowEqual: true })) {
    throw new Error('The resolved recording auth destination escapes the repository.');
  }
  return { resolved, repositoryReal };
}

export async function resolveSafeAuthDestination(candidate, {
  authRoot,
  repositoryRoot,
  checkIgnore = isIgnored,
  lstat = fs.lstat,
  realpath = fs.realpath,
} = {}) {
  const root = path.resolve(authRoot);
  const destination = path.resolve(candidate);
  assertAuthRootWithinRepository(root, repositoryRoot);
  if (!isPathWithin(destination, root, { allowEqual: true })) {
    throw new Error('The recording auth destination must remain inside the protected auth root.');
  }

  const rootResult = await resolveWithoutReparse(root, repositoryRoot, { lstat, realpath });
  const destinationResult = destination === root
    ? rootResult
    : await resolveWithoutReparse(destination, repositoryRoot, { lstat, realpath });
  if (!isPathWithin(destinationResult.resolved, rootResult.resolved, { allowEqual: true })) {
    throw new Error('The resolved recording auth destination escapes the protected auth root.');
  }

  const ignoreProbe = destination === root
    ? path.join(destinationResult.resolved, '.recording-auth-probe')
    : destinationResult.resolved;
  if (!checkIgnore(ignoreProbe)) {
    throw new Error(`Refusing to use ${destination} because its resolved destination is not Git-ignored.`);
  }
  return destinationResult.resolved;
}

export function signInButtonName(authMode) {
  if (authMode === 'github-legacy') return 'Sign in with GitHub';
  if (authMode === 'entra') return 'Sign in with Microsoft Entra ID';
  return null;
}

export function assertAuthenticatedSnapshot(snapshot) {
  if (/Sign in with (Microsoft Entra ID|GitHub)/i.test(snapshot)) {
    throw new Error('Recording authentication has expired. Run "npm run demo:record -- signin", then try again.');
  }
  if (!/\b(Overview|Projects|Sessions|Settings)\b/i.test(snapshot)) {
    throw new Error('The recording session opened, but Agentweaver authentication could not be verified.');
  }
}

export async function waitForAuthenticatedSnapshot(
  getSnapshot,
  {
    attempts = 5,
    retryDelayMs = 500,
    wait = delay,
  } = {},
) {
  let lastError;
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    try {
      const snapshot = await getSnapshot();
      assertAuthenticatedSnapshot(snapshot);
      return snapshot;
    } catch (error) {
      lastError = error;
    }
    if (attempt + 1 < attempts) await wait(retryDelayMs);
  }
  throw lastError;
}

export function parsePlaywrightSessionList(output) {
  const sessions = new Map();
  let current = null;
  for (const line of String(output).split(/\r?\n/)) {
    const sessionMatch = /^-\s+([^:]+):\s*$/.exec(line);
    if (sessionMatch) {
      current = sessionMatch[1];
      sessions.set(current, { name: current, status: 'unknown' });
      continue;
    }
    const statusMatch = /^\s+-\s+status:\s+(.+)\s*$/.exec(line);
    if (current && statusMatch) sessions.get(current).status = statusMatch[1];
  }
  return sessions;
}

function isIgnored(candidate) {
  try {
    execFileSync('git', ['check-ignore', '--no-index', '-q', '--', candidate], { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

async function assertProtectedAuthRoot(authRoot) {
  const repositoryRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], {
    encoding: 'utf8',
    windowsHide: true,
  }).trim();
  await resolveSafeAuthDestination(authRoot, {
    authRoot,
    repositoryRoot,
  });
  return repositoryRoot;
}

async function assertProtectedAuthDestination(candidate, authRoot, repositoryRoot) {
  await resolveSafeAuthDestination(candidate, {
    authRoot,
    repositoryRoot,
  });
}

let playwrightCliCommand;

function resolvePlaywrightCliCommand() {
  if (playwrightCliCommand) return playwrightCliCommand;
  if (process.platform === 'win32') {
    const launchers = execFileSync('where.exe', ['playwright-cli'], {
      encoding: 'utf8',
      windowsHide: true,
    }).split(/\r?\n/).filter(Boolean);
    for (const launcher of launchers) {
      const scriptPath = path.join(path.dirname(launcher), 'node_modules', '@playwright', 'cli', 'playwright-cli.js');
      if (existsSync(scriptPath)) {
        playwrightCliCommand = { file: process.execPath, prefix: [scriptPath] };
        return playwrightCliCommand;
      }
    }
  }
  playwrightCliCommand = { file: 'playwright-cli', prefix: [] };
  return playwrightCliCommand;
}

function runPlaywrightCli(args, { output = 'capture', sensitive = false } = {}) {
  try {
    const command = resolvePlaywrightCliCommand();
    return execFileSync(command.file, [...command.prefix, ...args], {
      encoding: 'utf8',
      stdio: output === 'inherit' ? 'inherit' : ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
    }) ?? '';
  } catch {
    if (sensitive) throw new Error('playwright-cli could not restore the protected recording session.');
    throw new Error(`playwright-cli command failed: ${args.find((arg) => !arg.startsWith('-')) ?? 'unknown'}.`);
  }
}

function sessionArgs(session, ...args) {
  return [`-s=${session}`, ...args];
}

async function writeProtectedJson(filePath, value, authRoot, repositoryRoot) {
  await assertProtectedAuthDestination(filePath, authRoot, repositoryRoot);
  await fs.writeFile(filePath, JSON.stringify(value, null, 2), { encoding: 'utf8', mode: 0o600 });
  await fs.chmod(filePath, 0o600).catch(() => {});
}

async function listEdgeProcessIds() {
  if (process.platform !== 'win32') {
    throw new Error('The Microsoft Edge Default work-profile sign-in flow is supported only on Windows.');
  }
  const command = [
    "$p = Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" -ErrorAction SilentlyContinue",
    "if ($p) { $p.ProcessId -join ',' }",
  ].join('; ');
  const output = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command], {
    encoding: 'utf8',
    windowsHide: true,
  });
  return String(output).trim().split(',').filter(Boolean);
}

export async function waitForEdgeToClose({
  timeoutMs = 300_000,
  pollMs = 1_000,
  getProcessIds = listEdgeProcessIds,
  write = (message) => process.stdout.write(message),
} = {}) {
  const startedAt = Date.now();
  let announced = false;
  while ((await getProcessIds()).length > 0) {
    if (!announced) {
      write('Close all Microsoft Edge windows. The sign-in tool is waiting for the Default profile to be released.\n');
      announced = true;
    }
    if (Date.now() - startedAt >= timeoutMs) {
      throw new Error('Microsoft Edge is still running. Close Edge, including background windows, then run signin again.');
    }
    await delay(pollMs);
  }
}

function isRetryableProfileCopyError(error) {
  return ['EACCES', 'EBUSY', 'EPERM'].includes(error?.code);
}

function profileCopyError(error, edgeProfile, attempt) {
  const failedPath = String(error?.path ?? error?.dest ?? '');
  const sourceRoot = path.resolve(edgeProfile.profilePath);
  const localState = path.resolve(edgeProfile.localStatePath);
  const failedPathResolved = failedPath ? path.resolve(failedPath) : '';
  const pathClass = failedPathResolved === localState
    ? 'Microsoft Edge Local State source'
    : isPathWithin(failedPathResolved, sourceRoot, { allowEqual: true })
      ? 'Microsoft Edge Default-profile source'
      : 'disposable recording-profile destination';
  const operation = error?.syscall ?? 'copy';
  return new Error(
    `Could not refresh the disposable Microsoft Edge Default profile: ${pathClass} `
    + `was unavailable during ${operation} (attempt ${attempt}/${PROFILE_COPY_MAX_ATTEMPTS}; `
    + `${error?.code ?? 'copy failed'}). The protected recording authentication was not changed. `
    + 'Close Edge and any profile-inspecting tool, then run signin again.',
  );
}

function disposableProfileRoot(paths, attempt) {
  return `${paths.automationUserDataDir}.refresh-${process.pid}-${Date.now()}-${attempt}`;
}

async function removeDisposableProfile(root, remove = fs.rm) {
  await remove(root, {
    recursive: true,
    force: true,
    maxRetries: PROFILE_COPY_MAX_ATTEMPTS,
    retryDelay: PROFILE_COPY_RETRY_DELAY_MS,
  });
}

export async function refreshDisposableEdgeProfile(
  paths,
  edgeProfile,
  repositoryRoot,
  {
    copyFile = fs.copyFile,
    copy = fs.cp,
    createDirectory = fs.mkdir,
    remove = removeDisposableProfile,
    wait = delay,
  } = {},
) {
  let lastError;
  for (let attempt = 1; attempt <= PROFILE_COPY_MAX_ATTEMPTS; attempt += 1) {
    const refreshRoot = disposableProfileRoot(paths, attempt);
    const refreshDefault = path.join(refreshRoot, EDGE_DEFAULT_PROFILE_DIRECTORY);
    try {
      await assertProtectedAuthDestination(refreshRoot, paths.root, repositoryRoot);
      await assertProtectedAuthDestination(refreshDefault, paths.root, repositoryRoot);
      await assertProtectedAuthDestination(path.join(refreshRoot, 'Local State'), paths.root, repositoryRoot);
      await createDirectory(refreshDefault, { recursive: true, mode: 0o700 });
      await assertProtectedAuthDestination(refreshDefault, paths.root, repositoryRoot);
      await copyFile(edgeProfile.localStatePath, path.join(refreshRoot, 'Local State'));
      await copy(edgeProfile.profilePath, refreshDefault, {
        recursive: true,
        force: true,
        filter: (sourcePath) => shouldCopyEdgeProfileEntry(sourcePath, edgeProfile.profilePath),
      });
      return refreshRoot;
    } catch (error) {
      lastError = error;
      await remove(refreshRoot).catch(() => {});
      if (!isRetryableProfileCopyError(error) || attempt === PROFILE_COPY_MAX_ATTEMPTS) {
        throw profileCopyError(error, edgeProfile, attempt);
      }
      await wait(PROFILE_COPY_RETRY_DELAY_MS * attempt);
    }
  }
  throw profileCopyError(lastError, edgeProfile, PROFILE_COPY_MAX_ATTEMPTS);
}

export async function signInRecordingSession(options) {
  const paths = recordingAuthPaths(options.authRoot);
  const edgeProfile = resolveLiteralEdgeDefaultProfile();
  const repositoryRoot = await assertProtectedAuthRoot(paths.root);
  await fs.mkdir(paths.root, { recursive: true, mode: 0o700 });
  await assertProtectedAuthRoot(paths.root);
  await waitForEdgeToClose({ timeoutMs: options.waitForEdgeMs });
  await validateLiteralEdgeDefaultProfile(edgeProfile);
  let disposableProfileRoot;

  let context;
  try {
    disposableProfileRoot = await refreshDisposableEdgeProfile(paths, edgeProfile, repositoryRoot);
    const { chromium } = await import('playwright');
    const launch = buildEdgeLaunchOptions(disposableProfileRoot);
    const { userDataDir, ...launchOptions } = launch;
    context = await chromium.launchPersistentContext(userDataDir, launchOptions);
    const page = context.pages()[0] ?? await context.newPage();
    await page.goto(options.baseUrl, { waitUntil: 'commit' });
    await page.waitForLoadState('domcontentloaded', { timeout: 30_000 }).catch(() => {});

    let hasSession = await page.evaluate(() => window.sessionStorage.getItem('agentweaver.sessionToken') !== null).catch(() => false);
    if (!hasSession) {
      const expectedSignInButton = signInButtonName(options.authMode);
      const configuredSignInButton = expectedSignInButton
        ? page.getByRole('button', { name: expectedSignInButton, exact: true })
        : page.getByRole('button', { name: /^Sign in with (Microsoft Entra ID|GitHub)$/ }).first();
      const appSignInControl = page.getByRole('button', { name: 'Sign in', exact: true })
        .or(page.getByRole('link', { name: 'Sign in', exact: true }));
      const signInButton = configuredSignInButton.or(appSignInControl);
      await signInButton.waitFor({ state: 'visible', timeout: 30_000 }).catch(() => {});
      if (!await signInButton.isVisible().catch(() => false)) {
        throw new Error(
          expectedSignInButton
            ? `The staging sign-in page did not offer the required "${expectedSignInButton}" or "Sign in" flow.`
            : 'The staging sign-in page did not offer a supported Agentweaver sign-in flow.',
        );
      }
      await signInButton.click();
      process.stdout.write(
        `Complete the ${expectedSignInButton ?? 'displayed'} sign-in flow in Microsoft Edge. `
        + 'It uses a freshly refreshed disposable copy of the literal Default work profile.\n',
      );
      await page.waitForFunction(
        () => window.sessionStorage.getItem('agentweaver.sessionToken') !== null,
        undefined,
        { timeout: 900_000 },
      );
      hasSession = true;
    }

    if (!hasSession) throw new Error('Agentweaver did not create an authenticated session.');
    const origin = await page.evaluate(() => window.location.origin);
    if (origin !== new URL(options.baseUrl).origin) {
      throw new Error('Sign-in did not return to the configured Agentweaver origin.');
    }
    const entries = await page.evaluate(() => ({ ...window.sessionStorage }));
    await assertProtectedAuthDestination(paths.storageStatePath, paths.root, repositoryRoot);
    await context.storageState({ path: paths.storageStatePath });
    await fs.chmod(paths.storageStatePath, 0o600).catch(() => {});
    await writeProtectedJson(paths.sessionStoragePath, { origin, entries }, paths.root, repositoryRoot);
    process.stdout.write('Recording sign-in is saved locally. Authentication values were not printed.\n');
  } finally {
    await context?.close().catch(() => {});
    if (disposableProfileRoot) {
      await assertProtectedAuthDestination(disposableProfileRoot, paths.root, repositoryRoot);
      await removeDisposableProfile(disposableProfileRoot).catch(() => {});
    }
  }
}

export async function hasRecordingAuth(authRoot = DEFAULT_RECORDING_AUTH_ROOT) {
  const paths = recordingAuthPaths(authRoot);
  try {
    const [storageStateText, sessionStorageText] = await Promise.all([
      fs.readFile(paths.storageStatePath, 'utf8'),
      fs.readFile(paths.sessionStoragePath, 'utf8'),
    ]);
    const storageState = JSON.parse(storageStateText);
    const sessionStorage = JSON.parse(sessionStorageText);
    return Array.isArray(storageState.cookies)
      && Array.isArray(storageState.origins)
      && URL.canParse(sessionStorage.origin)
      && typeof sessionStorage.entries?.['agentweaver.sessionToken'] === 'string'
      && sessionStorage.entries['agentweaver.sessionToken'].length > 0;
  } catch {
    return false;
  }
}

export function listPlaywrightSessions() {
  return parsePlaywrightSessionList(runPlaywrightCli(['list']));
}

export async function refreshRecordingAuthentication(options, {
  closeSession = closeRecordingSession,
  signIn = signInRecordingSession,
} = {}) {
  closeSession(options.session);
  await signIn(options);
}

function recordingAuthRequiredError() {
  return new Error(
    'Protected recording authentication is missing or expired. '
    + 'Run "npm run demo:record -- signin", then run open again.',
  );
}

async function verifyRecordingSession(getSnapshot, waitForAuthentication) {
  try {
    await waitForAuthentication(getSnapshot);
  } catch {
    throw recordingAuthRequiredError();
  }
}

export async function openRecordingSession(options, {
  resolveAuthentication = resolvePlanAuthentication,
  getAuth = hasRecordingAuth,
  getSessions = listPlaywrightSessions,
  run = runPlaywrightCli,
  writeSeed = writeSeedScript,
  waitForAuthentication = waitForAuthenticatedSnapshot,
  fileSystem = fs,
  getRepositoryRoot = assertProtectedAuthRoot,
  assertDestination = assertProtectedAuthDestination,
  write = (message) => process.stdout.write(message),
} = {}) {
  options = await resolveAuthentication(options);
  const paths = recordingAuthPaths(options.authRoot);
  const repositoryRoot = await getRepositoryRoot(paths.root);
  if (!await getAuth(paths.root)) throw recordingAuthRequiredError();

  const sessions = getSessions();
  if (sessions.get(options.session)?.status === 'open') {
    await verifyRecordingSession(
      () => run(sessionArgs(options.session, 'snapshot'), { sensitive: true }),
      waitForAuthentication,
    );
    write(`Recording session "${options.session}" is authenticated and ready.\n`);
    return;
  }

  await fileSystem.mkdir(paths.root, { recursive: true, mode: 0o700 });
  run(sessionArgs(options.session, 'open', '--persistent', '--browser=msedge'));
  const seedScriptPath = path.join(paths.root, `.seed-${options.session}.cjs`);
  try {
    await assertDestination(seedScriptPath, paths.root, repositoryRoot);
    run(sessionArgs(options.session, 'state-load', paths.storageStatePath), { sensitive: true });
    run(sessionArgs(options.session, 'goto', options.baseUrl), { sensitive: true });
    await writeSeed(seedScriptPath, {
      sessionStoragePath: paths.sessionStoragePath,
      targetOrigin: options.baseUrl,
    });
    await fileSystem.chmod(seedScriptPath, 0o600).catch(() => {});
    run(sessionArgs(options.session, '--raw', 'run-code', `--filename=${seedScriptPath}`), { sensitive: true });
    run(sessionArgs(options.session, 'reload'), { sensitive: true });
    await verifyRecordingSession(
      () => run(sessionArgs(options.session, 'snapshot'), { sensitive: true }),
      waitForAuthentication,
    );
    write(`Recording session "${options.session}" is authenticated and ready.\n`);
  } finally {
    await fileSystem.rm(seedScriptPath, { force: true }).catch(() => {});
  }
}

function planName(planPath) {
  return path.basename(planPath).replace(/\.capture\.json$/i, '').replace(/\.json$/i, '');
}

export async function prepareCaptureScripts(options) {
  const paths = recordingAuthPaths(options.authRoot);
  const repositoryRoot = await assertProtectedAuthRoot(paths.root);
  const planPath = path.resolve(options.plan);
  const captureConfig = await loadCaptureConfig(planPath);
  const configuredBeatIds = new Set(captureConfig.beats.map((beat) => beat.id));
  const beats = options.beatPlan
    ? (await loadJoinedCapturePlan({
      beatPlanPath: path.resolve(options.beatPlan),
      capturePlanPath: planPath,
    })).filter((beat) => configuredBeatIds.has(beat.id))
    : captureConfig.beats;
  const selected = options.beat ? beats.filter((beat) => beat.id === options.beat) : beats;
  if (options.beat && selected.length === 0) throw new Error(`Capture plan does not contain beat ${options.beat}.`);

  const outputDirectory = path.resolve(
    options.outDir ?? path.join(paths.generatedScriptsRoot, planName(planPath)),
  );
  const protectedOutput = isPathWithin(outputDirectory, paths.root, { allowEqual: true });
  if (protectedOutput) {
    await assertProtectedAuthDestination(outputDirectory, paths.root, repositoryRoot);
  }
  await fs.mkdir(outputDirectory, { recursive: true, mode: 0o700 });
  const scripts = [];
  for (const beat of selected) {
    if (!beat.videoPath) throw new Error(`Capture plan beat ${beat.id} requires videoPath.`);
    const scriptPath = path.join(outputDirectory, `beat-${beat.id.replace(/\./g, '-')}.cjs`);
    if (protectedOutput) {
      await assertProtectedAuthDestination(scriptPath, paths.root, repositoryRoot);
    }
    await fs.writeFile(scriptPath, renderCaptureScript(beat), { encoding: 'utf8', mode: 0o600 });
    scripts.push({
      beatId: beat.id,
      scriptPath,
      beat,
      capturePlanPath: planPath,
      ...captureArtifactPaths(beat.videoPath),
    });
  }
  return { outputDirectory, scripts };
}

export function assertResumableRecordingSession(status) {
  if (!status.sessionOpen || !status.sessionAuthenticated) {
    throw new Error('capture --resume requires an open, authenticated recording session. Run open or start first.');
  }
}

export function parseCaptureResult(output) {
  const marker = '__AGENTWEAVER_CAPTURE_RESULT__';
  const markerIndex = output.lastIndexOf(marker);
  if (markerIndex >= 0) {
    const line = output.slice(markerIndex + marker.length).split(/\r?\n/, 1)[0];
    return JSON.parse(line);
  }

  const trimmed = output.trim();
  if (trimmed) {
    try {
      const parsed = JSON.parse(trimmed);
      if (parsed?.cueLog && parsed?.activityLog) return parsed;
      if (parsed?.result?.cueLog && parsed.result?.activityLog) return parsed.result;
    } catch {}
  }
  for (let start = output.indexOf('{'); start >= 0; start = output.indexOf('{', start + 1)) {
    let depth = 0;
    let quoted = false;
    let escaped = false;
    for (let end = start; end < output.length; end += 1) {
      const character = output[end];
      if (quoted) {
        if (escaped) escaped = false;
        else if (character === '\\') escaped = true;
        else if (character === '"') quoted = false;
        continue;
      }
      if (character === '"') quoted = true;
      else if (character === '{') depth += 1;
      else if (character === '}') {
        depth -= 1;
        if (depth === 0) {
          try {
            const parsed = JSON.parse(output.slice(start, end + 1));
            if (parsed?.cueLog && parsed?.activityLog) return parsed;
            if (parsed?.result?.cueLog && parsed.result?.activityLog) return parsed.result;
          } catch {}
          break;
        }
      }
    }
  }
  throw new Error('Capture command completed without a parseable cue/activity result.');
}

function failureDetails(error) {
  return { name: error?.name ?? 'Error', message: error?.message ?? String(error) };
}

function captureGateFailureMessage(gateManifest) {
  const reasons = gateManifest.failureProvenance ?? [];
  return reasons.map((reason) => reason.message).filter(Boolean).join(' ') || 'The capture gate did not pass.';
}

export async function captureRecordingPlan(options, dependencies = {}) {
  const resolveAuthentication = dependencies.resolvePlanAuthentication
    ?? (dependencies.prepareCaptureScripts ? async (input) => input : resolvePlanAuthentication);
  const resolvedOptions = await resolveAuthentication(options);
  const openSession = dependencies.openRecordingSession ?? openRecordingSession;
  const getStatus = dependencies.recordingStatus ?? recordingStatus;
  const prepareScripts = dependencies.prepareCaptureScripts ?? prepareCaptureScripts;
  const runCapture = dependencies.runCapture ?? ((item) => runPlaywrightCli(
    sessionArgs(options.session, '--raw', 'run-code', `--filename=${item.scriptPath}`),
  ));
  const persistEvidence = dependencies.persistCaptureEvidence ?? persistCaptureEvidence;
  const write = dependencies.write ?? ((message) => process.stdout.write(message));
  if (resolvedOptions.resume) {
    assertResumableRecordingSession(await getStatus(resolvedOptions));
  } else {
    await openSession(resolvedOptions);
  }
  const prepared = await prepareScripts(resolvedOptions);
  const captures = [];
  for (const item of prepared.scripts) {
    await fs.mkdir(path.dirname(path.resolve(item.videoPath)), { recursive: true });
    write(`Capturing beat ${item.beatId}.\n`);
    let result = {};
    let failure = null;
    try {
      const output = await runCapture(item);
      if (typeof output === 'string' && output) write(output.endsWith('\n') ? output : `${output}\n`);
      result = typeof output === 'string' ? parseCaptureResult(output) : output;
      if (result?.failure) failure = result.failure;
    } catch (error) {
      failure = failureDetails(error);
    }
    const artifacts = {
      videoPath: item.videoPath,
      cueManifestPath: item.cueManifestPath,
      activityLogPath: item.activityLogPath,
      gateManifestPath: item.gateManifestPath,
    };
    const gateManifest = await persistEvidence({
      capturePlanPath: item.capturePlanPath ?? path.resolve(resolvedOptions.plan),
      beat: item.beat ?? { id: item.beatId },
      artifacts,
      result,
      failure,
    });
    const capture = {
      ...artifacts,
      beatId: item.beatId,
      cueCount: gateManifest.cues.observed.length,
      activityCount: gateManifest.activity.eventCount,
      meaningfulActivityCount: gateManifest.activity.meaningfulEventCount,
      passed: gateManifest.pass,
    };
    captures.push(capture);
    write(`Capture evidence for beat ${item.beatId}: ${capture.gateManifestPath} (${capture.passed ? 'PASS' : 'FAIL'}).\n`);
    if (!gateManifest.pass) {
      const error = new Error(
        `Capture beat ${item.beatId} failed its capture gate after evidence was persisted to `
        + `${capture.gateManifestPath}: ${captureGateFailureMessage(gateManifest)}`,
      );
      error.captures = [...captures];
      error.gateManifestPath = capture.gateManifestPath;
      throw error;
    }
  }
  return { ...prepared, captures };
}

export async function recordingStatus(options) {
  const paths = recordingAuthPaths(options.authRoot);
  const status = {
    authIgnored: false,
    authReady: false,
    sessionOpen: false,
    sessionAuthenticated: false,
  };
  status.authIgnored = await assertProtectedAuthRoot(paths.root).then(() => true, () => false);
  status.authReady = status.authIgnored && await hasRecordingAuth(paths.root);
  const sessions = listPlaywrightSessions();
  status.sessionOpen = sessions.get(options.session)?.status === 'open';
  if (status.sessionOpen) {
    try {
      assertAuthenticatedSnapshot(runPlaywrightCli(sessionArgs(options.session, 'snapshot'), { sensitive: true }));
      status.sessionAuthenticated = true;
    } catch {
      status.sessionAuthenticated = false;
    }
  }
  return status;
}

export function closeRecordingSession(session) {
  const sessions = listPlaywrightSessions();
  if (sessions.get(session)?.status === 'open') {
    runPlaywrightCli(sessionArgs(session, 'close'));
  }
}
