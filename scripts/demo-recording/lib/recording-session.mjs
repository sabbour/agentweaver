import { execFileSync } from 'node:child_process';
import {
  existsSync,
  openSync as fsOpenSync,
  closeSync as fsCloseSync,
  statSync as fsStatSync,
  readdirSync,
  rmSync,
} from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { joinCaptureConfig, loadCaptureConfig } from './capture-config.mjs';
import { loadBeatPlan } from './beats.mjs';
import { renderCaptureScript } from './capture-plan.mjs';
import { resolveCapturePreflight, verifyFixtureWorkflowRequirements } from './preflight.mjs';
import { writeSeedScript } from './auth.mjs';

export const DEFAULT_RECORDING_SESSION = 'agentweaver-demo';
export const UNAUTHENTICATED_RECORDING_SESSION = 'agentweaver-demo-unauthenticated';
export const DEFAULT_RECORDING_BASE_URL = 'https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io';
export const DEFAULT_RECORDING_AUTH_ROOT = 'scripts/demo-recording/.auth';
export const EDGE_DEFAULT_PROFILE_DIRECTORY = 'Default';

const COMMAND_OPTIONS = {
  signin: new Set(['session', 'base-url', 'auth-root', 'wait-for-edge-ms']),
  open: new Set(['session', 'base-url', 'auth-root']),
  start: new Set(['session', 'base-url', 'auth-root', 'wait-for-edge-ms', 'plan', 'beat-plan', 'out-dir', 'beat']),
  prepare: new Set(['auth-root', 'plan', 'beat-plan', 'out-dir', 'beat']),
  capture: new Set(['session', 'base-url', 'auth-root', 'plan', 'beat-plan', 'out-dir', 'beat', 'all', 'unauthenticated']),
  status: new Set(['session', 'base-url', 'auth-root']),
  close: new Set(['session']),
};

const BOOLEAN_OPTIONS = new Set(['all', 'unauthenticated']);
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
    waitForEdgeMs: 300_000,
  };
  let explicitSession = false;

  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (!flag.startsWith('--')) throw new Error(`Unexpected argument: ${flag}`);
    const name = flag.slice(2);
    if (!allowed.has(name)) throw new Error(`Unknown option for ${command}: ${flag}`);
    if (name === 'session') explicitSession = true;
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
  if (options.unauthenticated) {
    if (command !== 'capture') throw new Error('--unauthenticated is supported only by capture.');
    if (options.all) throw new Error('--unauthenticated captures one explicitly selected unauthenticated beat; do not use --all.');
    if (explicitSession) throw new Error('--unauthenticated uses its own isolated recording session; do not pass --session.');
    options.session = UNAUTHENTICATED_RECORDING_SESSION;
  }
  return options;
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
  return !PROFILE_COPY_EXCLUDED_FILES.has(path.basename(sourcePath));
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

export function assertAuthenticatedSnapshot(snapshot) {
  if (/Sign in with (Microsoft Entra ID|GitHub)/i.test(snapshot)) {
    throw new Error('Recording authentication has expired. Run "npm run demo:record -- signin", then try again.');
  }
  if (!/\b(Overview|Projects|Sessions|Settings)\b/i.test(snapshot)) {
    throw new Error('The recording session opened, but Agentweaver authentication could not be verified.');
  }
}

export async function waitForAuthenticatedSnapshot(session, {
  timeoutMs = 120_000,
  pollMs = 500,
  snapshot = () => runPlaywrightCli(sessionArgs(session, 'snapshot'), { sensitive: true }),
  delayFn = delay,
  now = () => Date.now(),
} = {}) {
  const deadline = now() + timeoutMs;
  let lastError;
  while (true) {
    try {
      assertAuthenticatedSnapshot(snapshot());
      return;
    } catch (error) {
      if (/authentication has expired/i.test(error.message)) throw error;
      lastError = error;
    }
    const remainingMs = deadline - now();
    if (remainingMs <= 0) throw lastError;
    await delayFn(Math.min(pollMs, remainingMs));
  }
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

export async function presentInteractiveSignInShell(page, {
  timeoutMs = 120_000,
  pollMs = 5_000,
  write = (message) => process.stdout.write(message),
} = {}) {
  await page.bringToFront();
  const signInButton = page.getByRole('button', {
    name: 'Sign in with Microsoft Entra ID',
    exact: true,
  });

  const deadline = Date.now() + timeoutMs;
  write(`Waiting up to ${Math.ceil(timeoutMs / 1_000)} seconds for Agentweaver's visible Sign in with Microsoft Entra ID button.\n`);
  while (Date.now() < deadline) {
    try {
      await signInButton.waitFor({
        state: 'visible',
        timeout: Math.min(pollMs, deadline - Date.now()),
      });
    } catch {
      if (Date.now() >= deadline) break;
      write('Agentweaver sign-in button is not visible yet; continuing to wait for the app shell.\n');
      continue;
    }

    if (await signInButton.isVisible()) {
      await signInButton.click();
      return;
    }
  }

  throw new Error('The Agentweaver Sign in with Microsoft Entra ID button did not become visible before the bounded wait elapsed.');
}

export async function waitForInteractiveSignInCompletion(page, {
  baseUrl,
  timeoutMs = 900_000,
  pollMs = 250,
  delayFn = delay,
  write = (message) => process.stdout.write(message),
} = {}) {
  const expectedOrigin = new URL(baseUrl).origin;
  const deadline = Date.now() + timeoutMs;
  let reachedIdentityProvider = false;

  while (Date.now() < deadline) {
    const currentUrl = page.url();
    if (currentUrl !== 'about:blank' && currentUrl !== '') {
      const currentOrigin = new URL(currentUrl).origin;
      if (currentOrigin !== expectedOrigin) {
        if (!reachedIdentityProvider) {
          reachedIdentityProvider = true;
          write('Microsoft Entra sign-in is now a human-only step. Complete it privately in the displayed Edge window.\n');
        }
        await delayFn(pollMs);
        continue;
      }
    }

    const hasSession = await page.evaluate(
      () => window.sessionStorage.getItem('agentweaver.sessionToken') !== null,
    ).catch(() => false);
    if (hasSession) return;

    await delayFn(pollMs);
  }

  throw new Error('Agentweaver sign-in did not complete before the interactive sign-in window timed out.');
}

export async function refreshDisposableEdgeProfile(paths, edgeProfile, repositoryRoot) {
  const refreshRoot = `${paths.automationUserDataDir}.refresh-${process.pid}-${Date.now()}`;
  const refreshDefault = path.join(refreshRoot, EDGE_DEFAULT_PROFILE_DIRECTORY);
  try {
    await assertProtectedAuthDestination(refreshRoot, paths.root, repositoryRoot);
    await assertProtectedAuthDestination(refreshDefault, paths.root, repositoryRoot);
    await assertProtectedAuthDestination(path.join(refreshRoot, 'Local State'), paths.root, repositoryRoot);
    await assertProtectedAuthDestination(paths.automationUserDataDir, paths.root, repositoryRoot);
    await fs.mkdir(refreshDefault, { recursive: true, mode: 0o700 });
    await assertProtectedAuthDestination(refreshDefault, paths.root, repositoryRoot);
    await fs.copyFile(edgeProfile.localStatePath, path.join(refreshRoot, 'Local State'));
    await fs.cp(edgeProfile.profilePath, refreshDefault, {
      recursive: true,
      force: true,
      filter: (sourcePath) => {
        if (!shouldCopyEdgeProfileEntry(sourcePath, edgeProfile.profilePath)) return false;
        // Skip files locked by other processes (e.g. WebView2) — they aren't needed for SSO replay
        try {
          if (!fsStatSync(sourcePath).isDirectory()) {
            const fd = fsOpenSync(sourcePath, 'r');
            fsCloseSync(fd);
          }
          return true;
        } catch {
          return false;
        }
      },
    });
    await fs.rm(paths.automationUserDataDir, { recursive: true, force: true });
    await fs.rename(refreshRoot, paths.automationUserDataDir);
  } catch (error) {
    await fs.rm(refreshRoot, { recursive: true, force: true }).catch(() => {});
    throw new Error(`Could not refresh the disposable Microsoft Edge Default profile from the exact Default source. Close Edge and run signin again. (${error.code ?? 'copy failed'})`);
  }
}

export async function signInRecordingSession(options) {
  const paths = recordingAuthPaths(options.authRoot);
  const edgeProfile = resolveLiteralEdgeDefaultProfile();
  const repositoryRoot = await assertProtectedAuthRoot(paths.root);
  await fs.mkdir(paths.root, { recursive: true, mode: 0o700 });
  await assertProtectedAuthRoot(paths.root);
  await waitForEdgeToClose({ timeoutMs: options.waitForEdgeMs });
  await validateLiteralEdgeDefaultProfile(edgeProfile);
  await refreshDisposableEdgeProfile(paths, edgeProfile, repositoryRoot);

  let context;
  try {
    const { chromium } = await import('playwright');
    const launch = buildEdgeLaunchOptions(paths.automationUserDataDir);
    const { userDataDir, ...launchOptions } = launch;
    context = await chromium.launchPersistentContext(userDataDir, launchOptions);
    const page = context.pages()[0] ?? await context.newPage();
    await page.goto(options.baseUrl, { waitUntil: 'commit' });
    await page.waitForLoadState('domcontentloaded', { timeout: 30_000 }).catch(() => {});

    let hasSession = await page.evaluate(() => window.sessionStorage.getItem('agentweaver.sessionToken') !== null).catch(() => false);
    if (!hasSession) {
      await presentInteractiveSignInShell(page);
      process.stdout.write(
        'Agentweaver sign-in is ready in Microsoft Edge. The recorder clicked Agentweaver’s Sign in with Microsoft Entra ID button and will not interact with Microsoft Entra.\n',
      );
      await waitForInteractiveSignInCompletion(page, { baseUrl: options.baseUrl });
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
    await assertProtectedAuthDestination(paths.automationUserDataDir, paths.root, repositoryRoot);
    await fs.rm(paths.automationUserDataDir, { recursive: true, force: true }).catch(() => {});
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

export async function restoreRecordingAuthentication(options, {
  listSessions = listPlaywrightSessions,
  closeSession = closeRecordingSession,
  verifyAuthenticatedSnapshot = waitForAuthenticatedSnapshot,
} = {}) {
  const paths = recordingAuthPaths(options.authRoot);
  const repositoryRoot = await assertProtectedAuthRoot(paths.root);
  if (!await hasRecordingAuth(paths.root)) {
    throw new Error('Protected recording authentication is unavailable.');
  }

  if (listSessions().get(options.session)?.status === 'open') {
    closeSession(options.session);
  }
  runPlaywrightCli(sessionArgs(options.session, 'open', '--persistent', '--browser=msedge'));

  const seedScriptPath = path.join(paths.root, `.seed-${options.session}.cjs`);
  try {
    await assertProtectedAuthDestination(seedScriptPath, paths.root, repositoryRoot);
    runPlaywrightCli(sessionArgs(options.session, 'state-load', paths.storageStatePath), { sensitive: true });
    runPlaywrightCli(sessionArgs(options.session, 'goto', options.baseUrl), { sensitive: true });
    await writeSeedScript(seedScriptPath, {
      sessionStoragePath: paths.sessionStoragePath,
      targetOrigin: options.baseUrl,
    });
    await fs.chmod(seedScriptPath, 0o600).catch(() => {});
    runPlaywrightCli(sessionArgs(options.session, '--raw', 'run-code', `--filename=${seedScriptPath}`), { sensitive: true });
    runPlaywrightCli(sessionArgs(options.session, 'reload'), { sensitive: true });
    await verifyAuthenticatedSnapshot(options.session);
    process.stdout.write(`Recording session "${options.session}" is authenticated and ready.\n`);
  } finally {
    await fs.rm(seedScriptPath, { force: true }).catch(() => {});
  }
}

export async function openRecordingSession(options, {
  listSessions = listPlaywrightSessions,
  verifyAuthenticatedSnapshot = waitForAuthenticatedSnapshot,
  refreshAuthentication = refreshRecordingAuthentication,
  hasAuthentication = hasRecordingAuth,
  restoreAuthentication = restoreRecordingAuthentication,
} = {}) {
  const paths = recordingAuthPaths(options.authRoot);
  await assertProtectedAuthRoot(paths.root);
  const existingSessions = listSessions();
  if (existingSessions.get(options.session)?.status === 'open') {
    try {
      await verifyAuthenticatedSnapshot(options.session);
      process.stdout.write(`Recording session "${options.session}" is already authenticated and ready.\n`);
      return;
    } catch {
      // The owned session is not usable, so use the existing close-first recovery flow.
    }
  }

  if (await hasAuthentication(paths.root)) {
    try {
      await restoreAuthentication(options);
      return;
    } catch {
      // Expired or otherwise unverifiable protected auth must be refreshed safely.
    }
  }

  process.stdout.write(
    'Protected recording authentication is unavailable or expired. Starting the safe interactive sign-in path; Microsoft Entra interaction, if shown, requires a human.\n',
  );
  await refreshAuthentication(options);
  if (!await hasAuthentication(paths.root)) throw new Error('The refreshed Microsoft Edge Default sign-in could not be verified.');
  await restoreAuthentication(options);
}

export function selectCaptureBeats(beats, options) {
  const selected = options.beat
    ? beats.filter((beat) => beat.id === options.beat)
    : beats.filter((beat) => beat.captureMode !== 'unauthenticated');
  if (options.beat && selected.length === 0) throw new Error(`Capture plan does not contain beat ${options.beat}.`);

  const hasUnauthenticatedBeat = selected.some((beat) => beat.captureMode === 'unauthenticated');
  if (options.unauthenticated && (!options.beat || selected.length !== 1 || !hasUnauthenticatedBeat)) {
    throw new Error('--unauthenticated requires exactly one beat declared with captureMode "unauthenticated".');
  }
  if (!options.unauthenticated && hasUnauthenticatedBeat) {
    throw new Error(`Beat ${selected.find((beat) => beat.captureMode === 'unauthenticated').id} requires --unauthenticated and cannot use restored authentication.`);
  }
  const selectedIds = new Set(selected.map((beat) => beat.id));
  const beatById = new Map(beats.map((beat) => [beat.id, beat]));
  for (const beat of selected) {
    const priorBeatId = beat.requiresPriorBeat;
    if (!priorBeatId || selectedIds.has(priorBeatId)) continue;
    const priorBeat = beatById.get(priorBeatId);
    if (options.all && priorBeat?.captureMode === 'unauthenticated') continue;
    throw new Error(
      `Beat ${beat.id} requires prior beat ${priorBeatId}. Capture the serial sequence with --all before recording this beat.`,
    );
  }
  return selected;
}

export function openUnauthenticatedRecordingSession(options) {
  // This session intentionally has no persistent context or loaded storage state. It is
  // closed after the one safe handoff beat so no browser data is retained for later runs.
  closeRecordingSession(options.session);
  runPlaywrightCli(sessionArgs(options.session, 'open', '--browser=msedge'));
  process.stdout.write(`Unauthenticated recording session "${options.session}" is ready without restored storage.\n`);
}

function planName(planPath) {
  return path.basename(planPath).replace(/\.capture\.json$/i, '').replace(/\.json$/i, '');
}

function prerequisiteError(prerequisite) {
  return new Error(`Capture prerequisite ${prerequisite.environment} is unavailable. ${prerequisite.message}`);
}

function validatePrerequisite(prerequisite, environment, baseUrl) {
  const value = environment[prerequisite.environment]?.trim();
  if (!value) throw prerequisiteError(prerequisite);
  if (prerequisite.kind === 'github-issue-number' && !/^[1-9][0-9]*$/.test(value)) throw prerequisiteError(prerequisite);
  if (prerequisite.kind === 'github-issue-url'
    && !/^https:\/\/github\.com\/[^/\s]+\/[^/\s]+\/issues\/[1-9][0-9]*\/?$/u.test(value)) throw prerequisiteError(prerequisite);
  if (prerequisite.kind === 'github-issue-url' && prerequisite.matchesEnvironment) {
    const issueNumber = /\/issues\/([1-9][0-9]*)\/?$/u.exec(value)?.[1];
    if (!issueNumber || environment[prerequisite.matchesEnvironment]?.trim() !== issueNumber) throw prerequisiteError(prerequisite);
  }
  if (prerequisite.kind === 'github-pr-url'
    && !/^https:\/\/github\.com\/[^/\s]+\/[^/\s]+\/pull\/[1-9][0-9]*\/?$/u.test(value)) throw prerequisiteError(prerequisite);
  if (prerequisite.kind === 'app-url'
    && (!URL.canParse(value) || new URL(value).origin !== new URL(baseUrl).origin)) throw prerequisiteError(prerequisite);
  return value;
}

function replaceRuntimeTemplates(value, environment) {
  if (typeof value === 'string') {
    return value.replace(/\{\{([A-Z][A-Z0-9_]+)\}\}/gu, (_template, name) => {
      const replacement = environment[name];
      if (!replacement) throw new Error(`Capture plan references ${name}, but it is not configured as a satisfied prerequisite.`);
      return replacement;
    });
  }
  if (Array.isArray(value)) return value.map((item) => replaceRuntimeTemplates(item, environment));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, replaceRuntimeTemplates(item, environment)]));
  return value;
}

export function resolveCaptureBeatPrerequisites(beat, {
  environment = process.env,
  baseUrl = DEFAULT_RECORDING_BASE_URL,
} = {}) {
  const values = {};
  for (const prerequisite of beat.prerequisites ?? []) {
    values[prerequisite.environment] = validatePrerequisite(prerequisite, environment, baseUrl);
  }
  return replaceRuntimeTemplates(beat, values);
}

export async function prepareCaptureScripts(options, {
  resolvePrerequisites = true,
  writeScripts = true,
} = {}) {
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
  for (const configuredBeat of selected) {
    const beat = resolvePrerequisites
      ? resolveCaptureBeatPrerequisites(configuredBeat, { baseUrl: options.baseUrl })
      : configuredBeat;
    const scriptPath = path.join(outputDirectory, `beat-${beat.id.replace(/\./g, '-')}.cjs`);
    if (writeScripts && protectedOutput) {
      await assertProtectedAuthDestination(scriptPath, paths.root, repositoryRoot);
    }
    if (writeScripts) {
      await fs.writeFile(scriptPath, renderCaptureScript(beat), { encoding: 'utf8', mode: 0o600 });
    }
    scripts.push({ beatId: beat.id, scriptPath, videoPath: beat.videoPath });
  }
  return { outputDirectory, scripts };
}

export async function captureRecordingPlan(options, {
  openSession = openRecordingSession,
  openUnauthenticatedSession = openUnauthenticatedRecordingSession,
  prepareScripts = prepareCaptureScripts,
  runScript = runPlaywrightCli,
  makeDirectory = fs.mkdir,
  write = (message) => process.stdout.write(message),
} = {}) {
  await (options.unauthenticated ? openUnauthenticatedSession : openSession)(options);
  // Discover the ordered beat list without resolving its runtime prerequisites.
  // In particular, Blueprint 4.1–4.5 create the Bug Fix PR that 4.6 verifies,
  // so an --all preparation must not demand that identity before those beats run.
  const queue = await prepareScripts(options, {
    resolvePrerequisites: false,
    writeScripts: false,
  });
  let lastPrepared;
  for (const queued of queue.scripts) {
    const prepared = await prepareScripts({ ...options, beat: queued.beatId, all: false });
    const item = prepared.scripts[0];
    if (!item) throw new Error(`Capture preparation did not produce beat ${queued.beatId}.`);
    if (item.videoPath) await makeDirectory(path.dirname(path.resolve(item.videoPath)), { recursive: true });
    write(`Capturing beat ${item.beatId}.\n`);
    runScript(
      sessionArgs(options.session, '--raw', 'run-code', `--filename=${item.scriptPath}`),
      { output: 'inherit' },
    );
    lastPrepared = { outputDirectory: prepared.outputDirectory, scripts: [...(lastPrepared?.scripts ?? []), item] };
  }
  return lastPrepared ?? { outputDirectory: queue.outputDirectory, scripts: [] };
}

export async function recordingStatus(options) {
  const paths = recordingAuthPaths(options.authRoot);
  const edgeProfile = resolveLiteralEdgeDefaultProfile();
  const status = {
    edgeDefaultProfile: false,
    authIgnored: false,
    authReady: false,
    sessionOpen: false,
    sessionAuthenticated: false,
  };
  status.edgeDefaultProfile = await validateLiteralEdgeDefaultProfile(edgeProfile)
    .then(() => true, () => false);
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
  pruneCacheDirectories(path.join(process.cwd(), '.auth', 'sessions', session));
}

// playwright-cli manages its own persistent browser profile per session under
// `.auth/sessions/<session>` (relative to this package's cwd) and never cleans it up itself.
// Once the browser has released the profile (after the `close` command above returns), it is
// safe to prune the same known-safe cache directories we already exclude when copying Edge
// profiles elsewhere in this file (see PROFILE_COPY_EXCLUDED_DIRECTORIES). This keeps the
// persistent profile working (auth/cookies are preserved) while discarding regenerable cache
// data that otherwise accumulates unbounded (tens of MB per session).
export function pruneCacheDirectories(root) {
  if (!existsSync(root)) return;
  const stack = [root];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries;
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      if (!entry.isDirectory()) continue;
      const entryPath = path.join(dir, entry.name);
      if (PROFILE_COPY_EXCLUDED_DIRECTORIES.has(entry.name)) {
        rmSync(entryPath, { recursive: true, force: true });
      } else {
        stack.push(entryPath);
      }
    }
  }
}
