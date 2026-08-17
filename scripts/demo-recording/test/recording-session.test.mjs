import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { recordingHelp, runRecordingCommand } from '../cli.mjs';
import {
  assertAuthenticatedSnapshot,
  assertAuthRootWithinRepository,
  assertResumableRecordingSession,
  assertIgnoredAuthRoot,
  buildEdgeLaunchOptions,
  parsePlaywrightSessionList,
  parseRecordingCommandOptions,
  openRecordingSession,
  refreshDisposableEdgeProfile,
  refreshRecordingAuthentication,
  recordingAuthPaths,
  resolvePlanAuthentication,
  resolveLiteralEdgeDefaultProfile,
  resolveSafeAuthDestination,
  signInButtonName,
  shouldCopyEdgeProfileEntry,
  validateLiteralEdgeDefaultProfile,
  waitForAuthenticatedSnapshot,
  waitForEdgeToClose,
} from '../lib/recording-session.mjs';

const packageRoot = path.dirname(fileURLToPath(new URL('../package.json', import.meta.url)));
const repositoryRoot = path.resolve(packageRoot, '..', '..');

test('recording commands use one canonical session and protected auth root', () => {
  assert.deepEqual(parseRecordingCommandOptions('open', []), {
    session: 'agentweaver-demo',
    baseUrl: 'https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io',
    authRoot: 'scripts/demo-recording/.auth',
    authMode: 'auto',
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
  assert.equal(
    parseRecordingCommandOptions('capture', ['--plan', 'demo.capture.json', '--beat', '1.2', '--resume']).resume,
    true,
  );
});

test('resumed capture requires the existing session to remain authenticated', () => {
  assert.doesNotThrow(() => assertResumableRecordingSession({
    sessionOpen: true,
    sessionAuthenticated: true,
  }));
  assert.throws(
    () => assertResumableRecordingSession({ sessionOpen: true, sessionAuthenticated: false }),
    /capture --resume requires an open, authenticated recording session/,
  );
});

test('recording commands reject unsafe sessions, insecure URLs, and unknown options', () => {
  assert.throws(() => parseRecordingCommandOptions('open', ['--session', 'bad session']), /only letters/);
  assert.throws(() => parseRecordingCommandOptions('open', ['--base-url', 'http://example.test']), /HTTPS/);
  assert.throws(() => parseRecordingCommandOptions('open', ['--other', 'value']), /Unknown option/);
  assert.throws(() => parseRecordingCommandOptions('open', ['--auth-mode', 'unknown']), /auth-mode/);
});

test('Azure capture plan requires Entra authentication', async () => {
  const options = await resolvePlanAuthentication(parseRecordingCommandOptions('open', [
    '--plan',
    'scripts/demo-recording/plans/azure-aks-demo.capture.json',
  ]));
  assert.equal(options.authMode, 'entra');
  assert.equal(
    options.plan,
    path.join(repositoryRoot, 'scripts', 'demo-recording', 'plans', 'azure-aks-demo.capture.json'),
  );
  await assert.rejects(
    resolvePlanAuthentication(parseRecordingCommandOptions('open', [
      '--plan',
      'scripts/demo-recording/plans/azure-aks-demo.capture.json',
      '--auth-mode',
      'github-legacy',
    ])),
    /conflicts with the entra authentication/,
  );
  await assert.rejects(
    resolvePlanAuthentication(parseRecordingCommandOptions('open', [
      '--plan',
      'scripts/demo-recording/plans/missing.capture.json',
    ])),
    /Use a path relative to the repository root or an absolute path/,
  );
});

test('Entra auth mode selects the Microsoft Entra sign-in button', () => {
  assert.equal(signInButtonName('entra'), 'Sign in with Microsoft Entra ID');
  assert.equal(signInButtonName('github-legacy'), 'Sign in with GitHub');
  assert.equal(signInButtonName('auto'), null);
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
  assert.equal(shouldCopyEdgeProfileEntry(path.join(root, 'DualEngine', 'transient.tmp'), root), false);
});

test('Edge profile refresh retries an EPERM into a new disposable destination', async () => {
  const id = `${process.pid}-${Date.now()}`;
  const authRoot = path.join(packageRoot, '.auth', `profile-refresh-test-${id}`);
  const paths = recordingAuthPaths(authRoot);
  const edgeProfile = {
    profilePath: path.resolve('edge-profile', 'Default'),
    localStatePath: path.resolve('edge-profile', 'Local State'),
  };
  const copiedTo = [];
  const removed = [];
  const waits = [];
  let copies = 0;
  try {
    const disposableRoot = await refreshDisposableEdgeProfile(paths, edgeProfile, repositoryRoot, {
      copyFile: async () => {},
      copy: async (_source, destination) => {
        copiedTo.push(destination);
        copies += 1;
        if (copies === 1) {
          const error = new Error('profile entry briefly locked');
          error.code = 'EPERM';
          error.path = edgeProfile.profilePath;
          error.syscall = 'copyfile';
          throw error;
        }
      },
      remove: async (candidate) => {
        removed.push(candidate);
        await fs.rm(candidate, { recursive: true, force: true });
      },
      wait: async (milliseconds) => { waits.push(milliseconds); },
    });

    assert.equal(copies, 2);
    assert.equal(waits.length, 1);
    assert.equal(waits[0], 250);
    assert.equal(removed.length, 1);
    assert.notEqual(path.dirname(copiedTo[0]), path.dirname(copiedTo[1]));
    assert.notEqual(disposableRoot, paths.automationUserDataDir);
    assert.equal(await fs.stat(disposableRoot).then(() => true, () => false), true);
    await fs.rm(disposableRoot, { recursive: true, force: true });
  } finally {
    await fs.rm(authRoot, { recursive: true, force: true }).catch(() => {});
  }
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

test('signin retains close-first Default-profile refresh behavior', async () => {
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

test('open restores protected auth without inspecting live Edge and preserves an authenticated session', async () => {
  const events = [];
  await openRecordingSession({
    session: 'agentweaver-demo',
    baseUrl: 'https://example.test',
    authRoot: 'scripts/demo-recording/.auth',
    authMode: 'auto',
  }, {
    resolveAuthentication: async (options) => options,
    getRepositoryRoot: async () => repositoryRoot,
    getAuth: async () => {
      events.push('read-protected-auth');
      return true;
    },
    getSessions: () => new Map([['agentweaver-demo', { status: 'open' }]]),
    run: (args) => {
      events.push(args.at(-1));
      return '- link "Overview"\n- link "Projects"';
    },
    waitForAuthentication: async (getSnapshot) => getSnapshot(),
    fileSystem: {
      mkdir: async () => { throw new Error('open session must not reset auth storage'); },
      chmod: async () => {},
      rm: async () => {},
    },
    write: () => {},
  });
  assert.deepEqual(events, ['read-protected-auth', 'snapshot']);
});

test('open fails closed instead of refreshing the live Edge profile when saved auth is unavailable', async () => {
  let opened = false;
  await assert.rejects(
    openRecordingSession({
      session: 'agentweaver-demo',
      baseUrl: 'https://example.test',
      authRoot: 'scripts/demo-recording/.auth',
      authMode: 'auto',
    }, {
      resolveAuthentication: async (options) => options,
      getRepositoryRoot: async () => repositoryRoot,
      getAuth: async () => false,
      getSessions: () => {
        opened = true;
        return new Map();
      },
    }),
    /Run "npm run demo:record -- signin"/,
  );
  assert.equal(opened, false);
});

test('signin CLI routing cannot bypass the close-first authentication helper', async () => {
  const calls = [];
  await runRecordingCommand(
    'signin',
    ['--session', 'owned-session'],
    {
      refreshAuthentication: async (options) => calls.push(options),
    },
  );
  assert.equal(calls.length, 1);
  assert.equal(calls[0].session, 'owned-session');
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

test('recording session waits for the authenticated shell after reload', async () => {
  let snapshots = 0;
  let waits = 0;
  const snapshot = await waitForAuthenticatedSnapshot(
    () => {
      snapshots += 1;
      return snapshots === 3 ? '- link "Overview"\n- link "Projects"' : '- heading "Loading"';
    },
    {
      attempts: 3,
      retryDelayMs: 0,
      wait: async () => { waits += 1; },
    },
  );
  assert.match(snapshot, /Overview/);
  assert.equal(snapshots, 3);
  assert.equal(waits, 2);
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
  assert.match(help, /open     Restore protected recording auth/);
  assert.match(help, /capture  Restore protected recording auth/);
});
