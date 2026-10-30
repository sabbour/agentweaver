import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import test from 'node:test';
import { runP1DotnetCli } from '../p1-dotnet-cli.mjs';
import { runP1CliEntrypoint } from '../run-p1.mjs';
import { runP1SigninCli } from '../p1-signin-run.mjs';

test('API legacy entrypoint delegates explicit options to the configured apphost', async () => {
  let invocation;
  const code = await runP1CliEntrypoint([
    '--surface', 'api', '--input', 'plan.json', '--output', 'evidence.jsonl',
    '--native-probe-envelope', 'native.json', '--dry-run', '--interactive',
  ], {}, { error() {} }, {
    runDotnetCli: async (...args) => {
      invocation = args;
      return 2;
    },
    runLegacy: () => assert.fail('API acceptance must not run through the Node journey.'),
  });

  assert.equal(code, 2);
  assert.equal(invocation[0].input, 'plan.json');
  assert.equal(invocation[0].output, 'evidence.jsonl');
  assert.equal(invocation[0].nativeProbeEnvelope, 'native.json');
  assert.equal(invocation[0].dryRun, true);
  assert.equal(invocation[0].interactive, true);
});

test('malformed or implicit API entrypoints never fall back to Node execution', async () => {
  for (const argv of [
    ['--surface', 'api', '--input', 'plan.json', '--output', 'evidence.jsonl', '--unknown'],
    ['--input', 'plan.json', '--output', 'evidence.jsonl'],
  ]) {
    let legacyCalled = false;
    let apphostCalled = false;
    const errors = [];
    const code = await runP1CliEntrypoint(argv, {}, { error: message => errors.push(message) }, {
      runDotnetCli: async () => {
        apphostCalled = true;
        return 0;
      },
      runLegacy: async () => {
        legacyCalled = true;
        return 0;
      },
    });

    assert.equal(code, 2);
    assert.equal(apphostCalled, false);
    assert.equal(legacyCalled, false);
    assert.notEqual(errors.length, 0);
  }
});

test('API sign-in launcher delegates without browser auth unless --interactive is explicit', async () => {
  for (const [extra, expectedInteractive] of [[[], false], [['--interactive'], true]]) {
    let invocation;
    const code = await runP1SigninCli([
      '--surface', 'api', '--input', 'plan.json', '--output', 'evidence.jsonl', ...extra,
    ], {}, { error() {}, log() {} }, {
      runDotnetCli: async options => {
        invocation = options;
        return 0;
      },
      readInput: async () => ({ fixturePlan: {} }),
      signIn: () => assert.fail('The .NET API apphost owns authentication.'),
      withBrowserContext: () => assert.fail('API delegation must not create a Node browser session.'),
    });

    assert.equal(code, 0);
    assert.equal(invocation.interactive, expectedInteractive);
  }
});

test('apphost execution is exact-path only, strips the legacy setup token, and preserves exit status', async () => {
  let invocation;
  const errors = [];
  const code = await runP1DotnetCli({
    input: 'plan.json',
    output: 'evidence.jsonl',
    dispatchRecoveryEnvelope: 'recovery.json',
    interactive: true,
  }, {
    AGENTWEAVER_CLI_PATH: process.execPath,
    AGENTWEAVER_P1_SETUP_TOKEN: 'must-not-cross-process-boundary',
  }, { error: message => errors.push(message) }, (file, args, options) => {
    invocation = { file, args, options };
    const child = new EventEmitter();
    queueMicrotask(() => child.emit('exit', 2, null));
    return child;
  });

  assert.equal(code, 2);
  assert.equal(invocation.file, process.execPath);
  assert.deepEqual(invocation.args, [
    'test', '--input', 'plan.json', '--output', 'evidence.jsonl',
    '--dispatch-recovery-envelope', 'recovery.json', '--interactive',
  ]);
  assert.equal(invocation.options.env.AGENTWEAVER_P1_SETUP_TOKEN, undefined);
  assert.equal(invocation.options.stdio, 'inherit');
  assert.deepEqual(errors, []);
});

test('missing apphost is blocked without fallback', async () => {
  let spawned = false;
  const errors = [];
  const code = await runP1DotnetCli({ input: 'plan.json', output: 'evidence.jsonl' }, {}, {
    error: message => errors.push(message),
  }, () => {
    spawned = true;
    return { status: 0 };
  });

  assert.equal(code, 2);
  assert.equal(spawned, false);
  assert.match(errors.join('\n'), /AGENTWEAVER_CLI_PATH/);
});

test('mixed API and MCP sign-in runs are rejected instead of using the Node API runner', async () => {
  let delegated = false;
  const errors = [];
  const code = await runP1SigninCli([
    '--surface', 'api,mcp', '--input', 'plan.json', '--output', 'evidence.jsonl',
  ], {}, { error: message => errors.push(message), log() {} }, {
    runDotnetCli: async () => {
      delegated = true;
      return 0;
    },
    signIn: () => assert.fail('A mixed API/MCP request must not fall through to Node sign-in.'),
  });

  assert.equal(code, 2);
  assert.equal(delegated, false);
  assert.match(errors.join('\n'), /separately/);
});
