import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { runP1DotnetCli } from '../p1-dotnet-cli.mjs';
import { runP1CliEntrypoint } from '../run-p1.mjs';
import { runP1SigninCli } from '../p1-signin-run.mjs';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

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

test('MCP selections are rejected explicitly without legacy or API delegation', async () => {
  for (const surface of ['mcp', 'api,mcp', 'mcp,api']) {
    let legacyCalled = false;
    let apphostCalled = false;
    const errors = [];
    const code = await runP1CliEntrypoint([
      '--surface', surface, '--input', 'plan.json', '--output', 'evidence.jsonl',
    ], {}, { error: message => errors.push(message) }, {
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
    assert.match(errors.join('\n'), /does not support an MCP surface/);
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

test('legacy proof paths are root-resolved in a temporary CLI manifest without changing envelope paths or input', async () => {
  const inputPath = resolve(repositoryRoot, 'releases', `.cli-input-${randomUUID()}.json`);
  const original = JSON.stringify({
    deploymentEvidence: {
      publicationPath: 'releases/foundation.json',
      manifestPath: 'releases/foundation.json',
      agentHostArchivePath: 'releases/foundation.json',
      integrationDeploymentReceiptPath: 'releases/foundation.json',
      releaseTag: 'v1.0.0',
    },
  });
  await writeFile(inputPath, original, 'utf8');
  let temporaryPath;
  try {
    const code = await runP1DotnetCli({
      input: relative(repositoryRoot, inputPath),
      output: 'artifacts/evidence.jsonl',
      nativeProbeEnvelope: 'scripts/harness-shared/test/p1-dotnet-cli.test.mjs',
      dispatchRecoveryEnvelope: 'scripts/harness-shared/test/p1-dotnet-cli.test.mjs',
    }, { AGENTWEAVER_CLI_PATH: process.execPath }, { error() {} }, (_file, args) => {
      temporaryPath = resolve(args[args.indexOf('--input') + 1]);
      assert.notEqual(temporaryPath, inputPath);
      assert.deepEqual(JSON.parse(readFileSync(temporaryPath, 'utf8')).deploymentEvidence, {
        publicationPath: resolve(repositoryRoot, 'releases/foundation.json'),
        manifestPath: resolve(repositoryRoot, 'releases/foundation.json'),
        agentHostArchivePath: resolve(repositoryRoot, 'releases/foundation.json'),
        integrationDeploymentReceiptPath: resolve(repositoryRoot, 'releases/foundation.json'),
        releaseTag: 'v1.0.0',
      });
      assert.deepEqual(args.slice(args.indexOf('--native-probe-envelope')), [
        '--native-probe-envelope', 'scripts/harness-shared/test/p1-dotnet-cli.test.mjs',
        '--dispatch-recovery-envelope', 'scripts/harness-shared/test/p1-dotnet-cli.test.mjs',
      ]);
      const child = new EventEmitter();
      queueMicrotask(() => child.emit('exit', 0, null));
      return child;
    });

    assert.equal(code, 0);
    assert.equal(await readFile(inputPath, 'utf8'), original);
    await assert.rejects(readFile(temporaryPath, 'utf8'), { code: 'ENOENT' });
    let unchangedInput;
    const overwriteCode = await runP1DotnetCli({
      input: relative(repositoryRoot, inputPath),
      output: relative(repositoryRoot, inputPath),
    }, { AGENTWEAVER_CLI_PATH: process.execPath }, { error() {} }, (_file, args) => {
      unchangedInput = args[args.indexOf('--input') + 1];
      const child = new EventEmitter();
      queueMicrotask(() => child.emit('exit', 2, null));
      return child;
    });
    assert.equal(overwriteCode, 2);
    assert.equal(resolve(unchangedInput), inputPath);
    assert.equal(await readFile(inputPath, 'utf8'), original);
  } finally {
    await rm(inputPath, { force: true });
    if (temporaryPath) await rm(temporaryPath, { force: true });
  }
});
