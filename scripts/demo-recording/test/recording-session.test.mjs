import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { recordingHelp, runRecordingCommand } from '../cli.mjs';
import {
  assertAuthenticatedSnapshot,
  assertAuthRootWithinRepository,
  assertIgnoredAuthRoot,
  buildEdgeLaunchOptions,
  captureRecordingPlan,
  openRecordingSession,
  parsePlaywrightSessionList,
  parseRecordingCommandOptions,
  pruneCacheDirectories,
  refreshRecordingAuthentication,
  recordingAuthPaths,
  resolveCaptureBeatPrerequisites,
  resolveLiteralEdgeDefaultProfile,
  resolveSafeAuthDestination,
  selectCaptureBeats,
  shouldCopyEdgeProfileEntry,
  presentInteractiveSignInShell,
  waitForAuthenticatedSnapshot,
  waitForInteractiveSignInCompletion,
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
  const unauthenticated = parseRecordingCommandOptions(
    'capture',
    ['--plan', 'demo.capture.json', '--beat', '0.0', '--unauthenticated'],
  );
  assert.equal(unauthenticated.unauthenticated, true);
  assert.equal(unauthenticated.session, 'agentweaver-demo-unauthenticated');
  assert.throws(
    () => parseRecordingCommandOptions('capture', ['--plan', 'demo.capture.json', '--all', '--unauthenticated']),
    /do not use --all/,
  );
  assert.throws(
    () => parseRecordingCommandOptions('capture', ['--plan', 'demo.capture.json', '--beat', '0.0', '--unauthenticated', '--session', 'other']),
    /own isolated recording session/,
  );
});

test('authenticated all capture skips handoff beats and modes cannot mix', () => {
  const beats = [
    { id: '0.0', captureMode: 'unauthenticated' },
    { id: '1.1', requiresPriorBeat: '0.0' },
    { id: '1.2', captureMode: 'authenticated', requiresPriorBeat: '1.1' },
  ];
  assert.deepEqual(
    selectCaptureBeats(beats, { all: true }).map((beat) => beat.id),
    ['1.1', '1.2'],
  );
  assert.deepEqual(
    selectCaptureBeats(beats, { beat: '0.0', unauthenticated: true }).map((beat) => beat.id),
    ['0.0'],
  );
  assert.throws(
    () => selectCaptureBeats(beats, { beat: '0.0' }),
    /requires --unauthenticated/,
  );
  assert.throws(
    () => selectCaptureBeats(beats, { beat: '1.1', unauthenticated: true }),
    /declared with captureMode "unauthenticated"/,
  );
  assert.throws(
    () => selectCaptureBeats(beats, { beat: '1.2' }),
    /Beat 1.2 requires prior beat 1.1.*--all/,
  );
});

test('full capture defers Bug Fix PR preparation until the preceding beats create it', async () => {
  const beats = ['4.1', '4.2', '4.3', '4.4', '4.5', '4.6', '4.7'];
  const preparations = [];
  const executions = [];
  let pullRequestExists = false;

  await captureRecordingPlan(
    { session: 'demo', all: true },
    {
      openSession: async () => {},
      prepareScripts: async (options, preparation = {}) => {
        preparations.push({ beat: options.beat, ...preparation });
        if (preparation.resolvePrerequisites === false) {
          assert.equal(pullRequestExists, false, 'global preparation must happen before the Bug Fix run');
          return {
            outputDirectory: 'generated',
            scripts: beats.map((beatId) => ({ beatId, scriptPath: `generated/beat-${beatId}.cjs` })),
          };
        }
        if (options.beat === '4.6') {
          assert.equal(pullRequestExists, true, 'Beat 4.6 must resolve the PR after Beats 4.1–4.5');
        }
        return {
          outputDirectory: 'generated',
          scripts: [{ beatId: options.beat, scriptPath: `generated/beat-${options.beat}.cjs` }],
        };
      },
      runScript: (args) => {
        const filename = args.find((arg) => arg.startsWith('--filename='));
        executions.push(filename);
        if (filename.endsWith('beat-4.5.cjs')) pullRequestExists = true;
      },
      write: () => {},
    },
  );

  assert.deepEqual(executions, beats.map((beat) => `--filename=generated/beat-${beat}.cjs`));
  assert.deepEqual(preparations, [
    { beat: undefined, resolvePrerequisites: false, writeScripts: false },
    ...beats.map((beat) => ({ beat, })),
  ]);
});

test('unauthenticated capture opens its isolated session without refreshing sign-in', async () => {
  const opened = [];

  await captureRecordingPlan(
    { session: 'agentweaver-demo-unauthenticated', beat: '0.0', unauthenticated: true },
    {
      openSession: async () => opened.push('authenticated'),
      openUnauthenticatedSession: async () => opened.push('unauthenticated'),
      prepareScripts: async (options, preparation = {}) => (
        preparation.resolvePrerequisites === false
          ? { outputDirectory: 'generated', scripts: [{ beatId: '0.0', scriptPath: 'generated/beat-0-0.cjs' }] }
          : { outputDirectory: 'generated', scripts: [{ beatId: options.beat, scriptPath: 'generated/beat-0-0.cjs' }] }
      ),
      runScript: () => {},
      write: () => {},
    },
  );

  assert.deepEqual(opened, ['unauthenticated']);
});

test('capture prerequisites resolve only current GitHub issue and staging routes', () => {
  const beat = {
    prerequisites: [
      { environment: 'ISSUE_NUMBER', kind: 'github-issue-number', message: 'Set issue number.' },
      { environment: 'ISSUE_URL', kind: 'github-issue-url', matchesEnvironment: 'ISSUE_NUMBER', message: 'Set matching issue URL.' },
      { environment: 'ASSISTANT_URL', kind: 'app-url', message: 'Set staging assistant route.' },
    ],
    steps: [{ url: '{{ISSUE_URL}}' }],
  };
  const environment = {
    ISSUE_NUMBER: '42',
    ISSUE_URL: 'https://github.com/example/demo/issues/42',
    ASSISTANT_URL: 'https://staging.example/assistant',
  };
  assert.equal(resolveCaptureBeatPrerequisites(beat, { environment, baseUrl: 'https://staging.example' }).steps[0].url, environment.ISSUE_URL);
  assert.throws(() => resolveCaptureBeatPrerequisites(beat, {
    environment: { ...environment, ISSUE_NUMBER: '43' },
    baseUrl: 'https://staging.example',
  }), /matching issue URL/);
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

test('pruneCacheDirectories removes known-safe cache dirs but preserves identity data', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'agentweaver-prune-test-'));
  try {
    await fs.mkdir(path.join(root, 'BrowserMetrics'), { recursive: true });
    await fs.writeFile(path.join(root, 'BrowserMetrics', 'metrics.pma'), 'x');
    await fs.mkdir(path.join(root, 'Default', 'Cache', 'Cache_Data'), { recursive: true });
    await fs.writeFile(path.join(root, 'Default', 'Cache', 'Cache_Data', 'blob'), 'x');
    await fs.mkdir(path.join(root, 'Default', 'Network'), { recursive: true });
    await fs.writeFile(path.join(root, 'Default', 'Network', 'Cookies'), 'x');
    await fs.writeFile(path.join(root, 'Default', 'Preferences'), '{}');

    pruneCacheDirectories(root);

    assert.equal(existsSync(path.join(root, 'BrowserMetrics')), false);
    assert.equal(existsSync(path.join(root, 'Default', 'Cache')), false);
    assert.equal(existsSync(path.join(root, 'Default', 'Network', 'Cookies')), true);
    assert.equal(existsSync(path.join(root, 'Default', 'Preferences')), true);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('pruneCacheDirectories is a no-op when the profile root does not exist', () => {
  assert.doesNotThrow(() => pruneCacheDirectories(path.join(os.tmpdir(), 'agentweaver-prune-missing')));
});

test('recording session reports expired or unverifiable authentication', () => {
  assert.throws(
    () => assertAuthenticatedSnapshot('- heading "Sign in with Microsoft Entra ID"'),
    /authentication has expired/,
  );
  assert.throws(() => assertAuthenticatedSnapshot('- heading "Service unavailable"'), /could not be verified/);
  assert.doesNotThrow(() => assertAuthenticatedSnapshot('- link "Overview"\n- link "Projects"'));
});

test('recording session waits for the post-reload shell before verifying authentication', async () => {
  const snapshots = [
    '- heading "Agentweaver"',
    '- link "Overview"\n- link "Projects"',
  ];
  const delays = [];

  await waitForAuthenticatedSnapshot('agentweaver-demo', {
    snapshot: () => snapshots.shift(),
    delayFn: async (milliseconds) => { delays.push(milliseconds); },
  });

  assert.deepEqual(delays, [500]);
});

test('recording session does not wait through an expired post-reload authentication state', async () => {
  const delays = [];
  await assert.rejects(
    () => waitForAuthenticatedSnapshot('agentweaver-demo', {
      snapshot: () => '- heading "Sign in with Microsoft Entra ID"',
      delayFn: async (milliseconds) => { delays.push(milliseconds); },
    }),
    /authentication has expired/,
  );
  assert.deepEqual(delays, []);
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

test('open reuses an already-open verified recording session without refreshing Edge Default', async () => {
  const events = [];
  await openRecordingSession(
    { session: 'agentweaver-demo', authRoot: path.join(repositoryRoot, 'scripts', 'demo-recording', '.auth') },
    {
      listSessions: () => new Map([['agentweaver-demo', { status: 'open' }]]),
      verifyAuthenticatedSnapshot: async (session) => events.push(`verify:${session}`),
      refreshAuthentication: async () => events.push('refresh-default'),
    },
  );
  assert.deepEqual(events, ['verify:agentweaver-demo']);
});

test('open restores protected recording auth before refreshing the Edge Default profile', async () => {
  const events = [];
  await openRecordingSession(
    { session: 'agentweaver-demo', authRoot: path.join(repositoryRoot, 'scripts', 'demo-recording', '.auth') },
    {
      listSessions: () => new Map([['agentweaver-demo', { status: 'closed' }]]),
      hasAuthentication: async () => {
        events.push('has-protected-auth');
        return true;
      },
      restoreAuthentication: async () => events.push('restore-protected-auth'),
      refreshAuthentication: async () => events.push('refresh-default'),
    },
  );
  assert.deepEqual(events, ['has-protected-auth', 'restore-protected-auth']);
});

test('open refreshes Default-profile sign-in only after protected auth cannot restore', async () => {
  const events = [];
  await openRecordingSession(
    { session: 'agentweaver-demo', authRoot: path.join(repositoryRoot, 'scripts', 'demo-recording', '.auth') },
    {
      listSessions: () => new Map([['agentweaver-demo', { status: 'closed' }]]),
      hasAuthentication: async () => {
        events.push('has-protected-auth');
        return true;
      },
      restoreAuthentication: async () => {
        events.push('restore-protected-auth');
        if (events.length === 2) throw new Error('expired');
      },
      refreshAuthentication: async () => events.push('refresh-default'),
    },
  );
  assert.deepEqual(events, [
    'has-protected-auth',
    'restore-protected-auth',
    'refresh-default',
    'has-protected-auth',
    'restore-protected-auth',
  ]);
});

test('open uses the existing refresh flow when its open recording session cannot verify', async () => {
  const events = [];
  await assert.rejects(
    () => openRecordingSession(
      { session: 'agentweaver-demo', authRoot: path.join(repositoryRoot, 'scripts', 'demo-recording', '.auth') },
      {
        listSessions: () => new Map([['agentweaver-demo', { status: 'open' }]]),
        verifyAuthenticatedSnapshot: async () => {
          events.push('verify');
          throw new Error('expired');
        },
        refreshAuthentication: async () => {
          events.push('refresh-default');
          throw new Error('recovery stopped');
        },
        hasAuthentication: async () => false,
      },
    ),
    /recovery stopped/,
  );
  assert.deepEqual(events, ['verify', 'refresh-default']);
});

test('signin foregrounds, waits for, and clicks the Agentweaver Entra button', async () => {
  const events = [];
  const button = {
    async waitFor(options) {
      events.push(['waitFor', options]);
    },
    async isVisible() {
      events.push(['isVisible']);
      return true;
    },
    async click() {
      events.push(['click']);
    },
  };
  const page = {
    async bringToFront() {
      events.push(['bringToFront']);
    },
    getByRole(role, options) {
      events.push(['getByRole', role, options]);
      return button;
    },
  };

  await presentInteractiveSignInShell(page, {
    timeoutMs: 120_000,
    pollMs: 5_000,
    write: (message) => events.push(['write', message]),
  });

  assert.deepEqual(events, [
    ['bringToFront'],
    ['getByRole', 'button', { name: 'Sign in with Microsoft Entra ID', exact: true }],
    ['write', "Waiting up to 120 seconds for Agentweaver's visible Sign in with Microsoft Entra ID button.\n"],
    ['waitFor', { state: 'visible', timeout: 5_000 }],
    ['isVisible'],
    ['click'],
  ]);
});

test('signin detects the post-click Entra boundary without inspecting IdP data', async () => {
  let evaluations = 0;
  let output = '';
  await assert.rejects(
    () => waitForInteractiveSignInCompletion({
      url: () => 'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
      evaluate: async () => {
        evaluations += 1;
        return false;
      },
    }, {
      baseUrl: 'https://agentweaver.example.test',
      timeoutMs: 5,
      pollMs: 1,
      write: (message) => { output += message; },
    }),
    /sign-in did not complete/,
  );
  assert.equal(evaluations, 0);
  assert.match(output, /human-only step/);
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

test('open routes directly to the Agentweaver sign-in recovery session', async () => {
  const calls = [];
  await runRecordingCommand(
    'open',
    [],
    {
      openSession: async (options) => calls.push(options),
    },
  );
  assert.equal(calls.length, 1);
  assert.equal(calls[0].session, 'agentweaver-demo');
});

test('open invokes interactive refresh when protected authentication is unavailable', async () => {
  const events = [];
  await assert.rejects(
    () => openRecordingSession(
      { session: 'agentweaver-demo', authRoot: path.join(repositoryRoot, 'scripts', 'demo-recording', '.auth') },
      {
        listSessions: () => new Map(),
        hasAuthentication: async () => false,
        refreshAuthentication: async () => events.push('refresh'),
      },
    ),
    /could not be verified/,
  );
  assert.deepEqual(events, ['refresh']);
});

test('start self-directs recording session setup without a prior status or open command', async () => {
  const calls = [];
  await runRecordingCommand(
    'start',
    [],
    {
      openSession: async (options) => calls.push(options),
    },
  );
  assert.equal(calls.length, 1);
  assert.equal(calls[0].session, 'agentweaver-demo');
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
  assert.match(help, /Reuse or restore recording auth/);
  assert.match(help, /capture  Self-direct authenticated setup/);
});
