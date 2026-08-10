import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { recordingHelp } from '../cli.mjs';
import {
  assertAuthenticatedSnapshot,
  assertAuthRootWithinRepository,
  assertIgnoredAuthRoot,
  buildEdgeLaunchOptions,
  parsePlaywrightSessionList,
  parseRecordingCommandOptions,
  refreshRecordingAuthentication,
  recordingAuthPaths,
  resolveLiteralEdgeDefaultProfile,
  resolveSafeAuthDestination,
  shouldCopyEdgeProfileEntry,
  validateLiteralEdgeDefaultProfile,
  waitForEdgeToClose,
} from '../lib/recording-session.mjs';

const packageRoot = path.dirname(fileURLToPath(new URL('../package.json', import.meta.url)));
const repositoryRoot = path.resolve(packageRoot, '..', '..');

test('recording commands use one canonical session and protected auth root', () => {
  assert.deepEqual(parseRecordingCommandOptions('open', []), {
    session: 'agentweaver-demo',
    baseUrl: 'https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io',
    authRoot: 'scripts/demo-recording/.auth',
    waitForEdgeMs: 300_000,
  });
});

test('capture requires an explicit beat selection or all', () => {
  assert.throws(
    () => parseRecordingCommandOptions('capture', ['--plan', 'demo.capture.json']),
    /--beat <id> or --all/,
  );
  assert.equal(
    parseRecordingCommandOptions('capture', ['--plan', 'demo.capture.json', '--beat', '1.1']).beat,
    '1.1',
  );
  assert.equal(
    parseRecordingCommandOptions('capture', ['--plan', 'demo.capture.json', '--all']).all,
    true,
  );
});

test('recording commands reject unsafe sessions, insecure URLs, and unknown options', () => {
  assert.throws(() => parseRecordingCommandOptions('open', ['--session', 'bad session']), /only letters/);
  assert.throws(() => parseRecordingCommandOptions('open', ['--base-url', 'http://example.test']), /HTTPS/);
  assert.throws(() => parseRecordingCommandOptions('open', ['--other', 'value']), /Unknown option/);
});

test('recording auth paths keep every authentication artifact under one root', () => {
  const paths = recordingAuthPaths('scripts/demo-recording/.auth');
  for (const candidate of [
    paths.storageStatePath,
    paths.sessionStoragePath,
    paths.automationUserDataDir,
    paths.generatedScriptsRoot,
  ]) {
    assert.equal(path.relative(paths.root, candidate).startsWith('..'), false);
  }
});

test('Edge sign-in is fixed to the literal Default profile and a disposable data root', () => {
  const profile = resolveLiteralEdgeDefaultProfile('C:\\Users\\tester\\AppData\\Local');
  assert.equal(profile.profileDirectory, 'Default');
  assert.equal(profile.profilePath.endsWith(path.join('Microsoft', 'Edge', 'User Data', 'Default')), true);

  const launch = buildEdgeLaunchOptions('scripts/demo-recording/.auth/edge-default-automation');
  assert.equal(launch.channel, 'msedge');
  assert.ok(launch.args.includes('--profile-directory=Default'));
  assert.match(launch.userDataDir, /edge-default-automation$/);
});

test('Edge sign-in validates the exact Default profile identity from Local State', async () => {
  const localAppData = 'C:\\Users\\tester\\AppData\\Local';
  const profile = resolveLiteralEdgeDefaultProfile(localAppData);
  const seen = [];
  const access = async (candidate) => { seen.push(candidate); };
  const readFile = async () => JSON.stringify({
    profile: { info_cache: { Default: { name: 'Work' } } },
  });

  await assert.doesNotReject(() => validateLiteralEdgeDefaultProfile(profile, {
    localAppData,
    access,
    readFile,
  }));
  assert.deepEqual(seen, [profile.profilePath, profile.localStatePath]);

  await assert.rejects(
    () => validateLiteralEdgeDefaultProfile(
      { ...profile, profileDirectory: 'Profile 1' },
      { localAppData, access, readFile },
    ),
    /except the literal Default profile/,
  );
  await assert.rejects(
    () => validateLiteralEdgeDefaultProfile(profile, {
      localAppData,
      access,
      readFile: async () => JSON.stringify({ profile: { info_cache: {} } }),
    }),
    /does not identify a Default profile/,
  );
});

test('Edge profile copy retains identity data but excludes caches and lock files', () => {
  const root = path.resolve('edge-profile', 'Default');
  assert.equal(shouldCopyEdgeProfileEntry(path.join(root, 'Network', 'Cookies'), root), true);
  assert.equal(shouldCopyEdgeProfileEntry(path.join(root, 'Preferences'), root), true);
  assert.equal(shouldCopyEdgeProfileEntry(path.join(root, 'Cache', 'cache.bin'), root), false);
  assert.equal(shouldCopyEdgeProfileEntry(path.join(root, 'LOCK'), root), false);
});

test('recording session reports expired or unverifiable authentication', () => {
  assert.throws(
    () => assertAuthenticatedSnapshot('- heading "Sign in with Microsoft Entra ID"'),
    /authentication has expired/,
  );
  assert.throws(() => assertAuthenticatedSnapshot('- heading "Service unavailable"'), /could not be verified/);
  assert.doesNotThrow(() => assertAuthenticatedSnapshot('- link "Overview"\n- link "Projects"'));
});

test('recording session requires Git to ignore the auth folder', () => {
  assert.doesNotThrow(() => assertIgnoredAuthRoot('scripts/demo-recording/.auth', () => true));
  assert.throws(() => assertIgnoredAuthRoot('recordings/auth', () => false), /Git does not ignore/);
});

test('recording auth must stay inside the repository', () => {
  const repository = path.resolve('repository');
  assert.doesNotThrow(() => assertAuthRootWithinRepository(path.join(repository, 'scripts', '.auth'), repository));
  assert.throws(() => assertAuthRootWithinRepository(repository, repository), /child of the repository/);
  assert.throws(
    () => assertAuthRootWithinRepository(path.resolve(repository, '..', 'outside'), repository),
    /child of the repository/,
  );
});

test('auth refresh closes only the owned Playwright session before inspecting Edge', async () => {
  const events = [];
  await refreshRecordingAuthentication(
    { session: 'agentweaver-demo' },
    {
      closeSession: (session) => events.push(`close:${session}`),
      signIn: async () => events.push('refresh-default'),
    },
  );
  assert.deepEqual(events, ['close:agentweaver-demo', 'refresh-default']);
});

test('auth destinations reject a junction that escapes the ignored auth root', async () => {
  const id = `${process.pid}-${Date.now()}`;
  const authRoot = path.join(packageRoot, '.auth', `junction-test-${id}`);
  const outsideTarget = path.join(packageRoot, 'test', `.auth-escape-target-${id}`);
  const junction = path.join(authRoot, 'edge-default-automation');
  await fs.mkdir(authRoot, { recursive: true });
  await fs.mkdir(outsideTarget, { recursive: true });
  try {
    await fs.symlink(outsideTarget, junction, process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(
      () => resolveSafeAuthDestination(junction, {
        authRoot,
        repositoryRoot,
      }),
      /junction, symlink, or reparse point/,
    );
  } finally {
    await fs.rm(junction, { force: true }).catch(() => {});
    await fs.rm(authRoot, { recursive: true, force: true }).catch(() => {});
    await fs.rm(outsideTarget, { recursive: true, force: true }).catch(() => {});
  }
});

test('playwright-cli session status parsing finds named open sessions', () => {
  const sessions = parsePlaywrightSessionList(`### Browsers
- agentweaver-demo:
  - status: open
  - browser-type: msedge
- other:
  - status: closed
`);
  assert.equal(sessions.get('agentweaver-demo').status, 'open');
  assert.equal(sessions.get('other').status, 'closed');
});

test('Edge process wait gives a clear close flow without terminating processes', async () => {
  let calls = 0;
  let message = '';
  await waitForEdgeToClose({
    timeoutMs: 100,
    pollMs: 1,
    getProcessIds: async () => (++calls === 1 ? ['10'] : []),
    write: (value) => { message += value; },
  });
  assert.match(message, /Close all Microsoft Edge windows/);
});

test('top-level help documents the complete recording workflow', () => {
  const help = recordingHelp();
  for (const command of ['signin', 'open', 'start', 'prepare', 'capture', 'status', 'close']) {
    assert.match(help, new RegExp(`\\b${command}\\b`));
  }
  assert.match(help, /Microsoft Edge Default work profile/);
  assert.match(help, /Refresh Default-profile sign-in/);
  assert.match(help, /capture  Refresh Default-profile sign-in/);
});
