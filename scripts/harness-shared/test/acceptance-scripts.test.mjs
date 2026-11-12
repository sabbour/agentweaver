import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { runP1AcceptanceScripts } from '../acceptance-scripts.mjs';
import { runP1CliEntrypoint } from '../run-p1.mjs';
import { runP1SigninCli } from '../p1-signin-run.mjs';

const casePath = fileURLToPath(new URL('../../acceptance/api/read-only-gateway.mjs', import.meta.url));

test('API launcher starts only the checked-in case script with literal input arguments', async () => {
  let invocation;
  const code = await runP1AcceptanceScripts({
    input: 'case; echo not-a-shell-command.json',
    output: 'new-evidence',
    dryRun: true,
  }, {}, { error() {} }, (file, args, options) => {
    invocation = { file, args, options };
    const child = new EventEmitter();
    queueMicrotask(() => child.emit('exit', 0, null));
    return child;
  });

  assert.equal(code, 0);
  assert.equal(invocation.file, process.execPath);
  assert.deepEqual(invocation.args, [
    casePath,
    '--input', 'case; echo not-a-shell-command.json',
    '--output', 'new-evidence',
    '--dry-run',
  ]);
  assert.equal(invocation.options.shell, false);
  assert.equal(invocation.options.stdio, 'inherit');
});

test('legacy runner flags and browser interaction are rejected before the script starts', async () => {
  let spawned = false;
  const errors = [];
  for (const options of [
    { input: 'case.json', output: 'evidence', interactive: true },
    { input: 'case.json', output: 'evidence', nativeProbeEnvelope: 'proof.json' },
    { input: 'case.json', output: 'evidence', dispatchRecoveryEnvelope: 'proof.json' },
    { input: 'case.json', output: 'evidence', identityModule: 'module.mjs' },
    { input: 'case.json', output: 'evidence', pageModule: 'module.mjs' },
  ]) {
    assert.equal(await runP1AcceptanceScripts(options, {}, {
      error: message => errors.push(message),
    }, () => {
      spawned = true;
      return new EventEmitter();
    }), 2);
  }
  assert.equal(spawned, false);
  assert.match(errors.join('\n'), /does not open a browser/);
  assert.match(errors.join('\n'), /Legacy runner modules and proof envelopes/);
});

test('API harness routes to the script without falling back to the legacy runner', async () => {
  let invocation;
  const code = await runP1CliEntrypoint([
    '--surface', 'api', '--input', 'case.json', '--output', 'new-evidence',
  ], {}, { error() {} }, {
    runApiScripts: async options => {
      invocation = options;
      return 2;
    },
    runLegacy: () => assert.fail('API acceptance must not run through the legacy journey.'),
  });
  assert.equal(code, 2);
  assert.deepEqual(invocation, {
    surface: 'api',
    input: 'case.json',
    output: 'new-evidence',
  });
});

test('sign-in launcher routes API to the case script without opening a browser', async () => {
  let invocation;
  const code = await runP1SigninCli([
    '--surface', 'api', '--input', 'case.json', '--output', 'new-evidence',
  ], {}, { error() {}, log() {} }, {
    runApiScripts: async options => {
      invocation = options;
      return 0;
    },
    signIn: () => assert.fail('API cases use native CLI authentication.'),
    withBrowserContext: () => assert.fail('API cases do not open a browser.'),
  });

  assert.equal(code, 0);
  assert.deepEqual(invocation, {
    input: 'case.json',
    output: 'new-evidence',
    dryRun: undefined,
    interactive: false,
    nativeProbeEnvelope: undefined,
    dispatchRecoveryEnvelope: undefined,
  });
});

test('API launcher preserves the case exit code and reports spawn failure', async () => {
  const child = new EventEmitter();
  const result = runP1AcceptanceScripts({ input: 'case.json', output: 'evidence' }, {}, {
    error() {},
  }, () => child);
  child.emit('exit', 2, null);
  assert.equal(await result, 2);

  const errors = [];
  assert.equal(await runP1AcceptanceScripts({ input: 'case.json', output: 'evidence' }, {}, {
    error: message => errors.push(message),
  }, () => {
    throw new Error('spawn failed');
  }), 1);
  assert.match(errors.join('\n'), /could not be started/);
});
