import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  COMMAND_TIMEOUT_MS,
  INTERACTIVE_COMMAND_TIMEOUT_MS,
  buildCommands,
  buildWorkspaceCommand,
  childEnvironment,
  parseArgs,
  runCase,
  runCommand,
  validateInput,
} from '../workspace-repository-read.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'workspace-repository-read.mjs');
const inputPath = path.resolve(directory, '..', 'examples', 'workspace-repository-read.json');
const input = JSON.parse(await readFile(inputPath, 'utf8'));
const apphostName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
const fileContent = 'workspace-file-content-canary\n';
const fileSha256 = createHash('sha256').update(fileContent).digest('hex');
const blobSha = createHash('sha1').update(`blob ${Buffer.byteLength(fileContent)}\0${fileContent}`).digest('hex');
const selectionCode = 'S'.repeat(80);
const fixture = {
  ...input.fixture,
  repository: 'octo/repo',
  commitSha: 'a'.repeat(40),
  filePath: 'docs/read me.md',
  byteLength: Buffer.byteLength(fileContent),
  sha256: fileSha256,
};

function ok(body) {
  return { exitCode: 0, signal: null, stdout: JSON.stringify(body), stderr: '', timedOut: false, error: null };
}

function fail(stderr, extra = {}) {
  return { exitCode: 1, signal: null, stdout: '', stderr, timedOut: false, error: null, ...extra };
}

function workspaceDocument(operation, result) {
  return {
    status: 'passed',
    operation,
    selection: {
      version: 2,
      connectionRevision: 1,
      installationId: 7,
      repositoryId: 9,
      repositoryFullName: 'octo/repo',
      expiresAt: '2026-11-14T00:00:00.0000000Z',
    },
    result,
    evidence: { status: 200 },
  };
}

function fakeApphost(caseInput, overrides = {}) {
  const calls = [];
  const runner = (cliPath, args, env, timeoutMs) => {
    const key = args[0] === 'workspace' ? `workspace ${args[1]}` : args[0] === '--version' ? 'version' : `${args[0]} ${args[1] ?? ''}`.trim();
    calls.push({ key, args, timeoutMs, env });
    if (overrides[key]) {
      const response = overrides[key](args, calls);
      if (response) return response;
    }
    switch (key) {
      case 'version':
        return { ...ok({}), stdout: '1.0.0\n' };
      case 'api operations':
        return ok([
          'getAuthorizationContext',
          'getProjectConfiguration',
          'listWorkspaceRepositoryRefs',
          'readWorkspaceRepositoryTree',
          'readWorkspaceRepositoryFile',
        ].map(operationId => ({ operationId })));
      case 'auth status':
        return ok({ isSignedIn: true, tenantId: caseInput.target.tenantId });
      case 'rest --gateway-url':
        return ok({ status: 200, body: { actorId: caseInput.expectedActorId, tenantId: caseInput.target.tenantId } });
      case 'workspace refs': {
        const cursor = args.includes('--cursor') ? args[args.indexOf('--cursor') + 1] : null;
        if (cursor === null) {
          return ok(workspaceDocument('listWorkspaceRepositoryRefs', {
            repository: 'octo/repo',
            items: [{ kind: 'branch', name: 'other', commitSha: 'b'.repeat(40) }],
            nextCursor: '2',
          }));
        }
        return ok(workspaceDocument('listWorkspaceRepositoryRefs', {
          repository: 'octo/repo',
          items: [{ kind: caseInput.fixture.refKind, name: caseInput.fixture.refName, commitSha: caseInput.fixture.commitSha }],
        }));
      }
      case 'workspace tree':
        return ok(workspaceDocument('readWorkspaceRepositoryTree', {
          repository: 'octo/repo',
          reference: caseInput.fixture.commitSha,
          commitSha: caseInput.fixture.commitSha,
          items: [{ path: caseInput.fixture.filePath, kind: 'file', blobSha, size: caseInput.fixture.byteLength }],
        }));
      case 'workspace file':
        return ok({
          ...workspaceDocument('readWorkspaceRepositoryFile', {
            path: caseInput.fixture.filePath,
            commitSha: caseInput.fixture.commitSha,
            size: caseInput.fixture.byteLength,
            isBinary: false,
            sha256: fileSha256,
            gitBlobSha1: blobSha,
            content: fileContent,
            selectionCode,
          }),
        });
      default:
        throw new Error(`Unexpected command: ${args.join(' ')}`);
    }
  };
  return { runner, calls };
}

async function withScratch(callback, inputOverrides = {}) {
  const scratch = path.join(directory, `workspace-case-${randomUUID()}`);
  await mkdir(scratch);
  try {
    const cliPath = path.join(scratch, apphostName);
    await writeFile(cliPath, 'fake apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    const caseInput = { ...input, apphostSha256, fixture, ...inputOverrides };
    const casePath = path.join(scratch, 'case.json');
    await writeFile(casePath, JSON.stringify(caseInput));
    const outputPath = path.join(scratch, 'evidence');
    await callback({
      scratch,
      cliPath,
      casePath,
      outputPath,
      caseInput: inputOverrides.approvedNonProduction === undefined ? validateInput(caseInput) : null,
      env: { PATH: process.env.PATH, AGENTWEAVER_CLI_PATH: cliPath, AGENTWEAVER_PROJECT_ID: 'stale-project' },
    });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
}

async function runFake(context, overrides, runOptions = {}) {
  const apphost = fakeApphost(context.caseInput, overrides);
  const exitCode = await runCase(
    { inputPath: context.casePath, outputPath: context.outputPath, interactive: true, dryRun: false },
    context.env,
    { runCommand: apphost.runner, ...runOptions },
  );
  const result = JSON.parse(await readFile(path.join(context.outputPath, 'result.json'), 'utf8'));
  const commands = await readFile(path.join(context.outputPath, 'commands.jsonl'), 'utf8');
  return { exitCode, result, commands, calls: apphost.calls };
}

test('case input requires the fixture and rejects extra fields', () => {
  const parsed = validateInput(input);
  assert.equal(parsed.caseId, 'workspace-repository-read');
  assert.equal(parsed.fixture.refKind, 'branch');
  assert.throws(() => validateInput({ ...input, bearerToken: 'do-not-log-this' }), /schema/);
  assert.throws(() => validateInput({ ...input, approvedNonProduction: false }), /schema/);
  assert.throws(() => validateInput({ ...input, fixture: { ...input.fixture, commitSha: 'main' } }), /schema/);
  assert.throws(() => validateInput({ ...input, fixture: { ...input.fixture, filePath: '/etc/passwd' } }), /schema/);
  const { fixture: _fixture, ...withoutFixture } = input;
  assert.throws(() => validateInput(withoutFixture), /schema/);
});

test('commands use an unbound projects.admin identity and only workspace commands wait for browser approval', () => {
  const parsed = validateInput(input);
  const commands = buildCommands(parsed);
  assert.deepEqual(commands.map(command => command.id), [
    'version', 'api-operations', 'auth-status', 'rest-authorization-context',
  ]);
  assert.ok(commands.every(command => command.timeoutMs === COMMAND_TIMEOUT_MS));
  assert.equal(COMMAND_TIMEOUT_MS, 60_000);
  assert.ok(INTERACTIVE_COMMAND_TIMEOUT_MS > 5 * 60_000);
  assert.deepEqual(commands[2].args.slice(0, 4), ['auth', 'status', '--scope', 'projects.admin']);
  assert.deepEqual(commands[3].args.slice(-6), ['--scope', 'projects.admin', '--method', 'GET', '--url', '/api/v1/authorization/context']);
  for (const kind of ['refs', 'tree', 'file']) {
    const command = buildWorkspaceCommand(parsed, kind);
    assert.deepEqual(command.args.slice(0, 2), ['workspace', kind]);
    assert.ok(command.args.includes('--interactive'));
    assert.equal(command.timeoutMs, INTERACTIVE_COMMAND_TIMEOUT_MS);
    assert.ok(!command.args.includes('--output'));
    assert.ok(!command.args.includes('--project-id'));
  }
  assert.deepEqual(buildWorkspaceCommand(parsed, 'refs', { cursor: '2' }).args.slice(0, 7),
    ['workspace', 'refs', '--project', parsed.fixture.projectId, '--interactive', '--cursor', '2']);
});

test('child environment removes project and run bindings', () => {
  assert.deepEqual(childEnvironment({
    PATH: 'system-path',
    agentweaver_project_id: 'stale-project',
    AGENTWEAVER_RUN_ID: 'stale-run',
  }), { PATH: 'system-path' });
});

test('argument parser accepts --interactive once and rejects unknown options', () => {
  assert.equal(parseArgs(['--input', 'a', '--output', 'b', '--interactive']).interactive, true);
  assert.equal(parseArgs(['--input', 'a', '--output', 'b']).interactive, false);
  assert.throws(() => parseArgs(['--input', 'a', '--output', 'b', '--interactive', '--interactive']), /once/);
  assert.throws(() => parseArgs(['--input', 'a', '--output', 'b', '--selection', 'x']), /Use --input/);
});

test('dry-run validates input without starting the apphost or creating evidence', async () => {
  await withScratch(async ({ cliPath, casePath, outputPath }) => {
    const run = spawnSync(process.execPath, [scriptPath, '--input', casePath, '--output', outputPath, '--dry-run'], {
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 0, run.stderr);
    const result = JSON.parse(run.stdout);
    assert.equal(result.status, 'validated-not-run');
    assert.equal(result.network, 'not-used');
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  });
});

test('a run without --interactive exits 2 before any command', async () => {
  await withScratch(async ({ cliPath, casePath, outputPath }) => {
    const run = spawnSync(process.execPath, [scriptPath, '--input', casePath, '--output', outputPath], {
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 2);
    assert.match(run.stderr, /--interactive/);
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  });
});

test('an input that is not approved for non-production use blocks before any command', async () => {
  await withScratch(async ({ cliPath, casePath, outputPath }) => {
    const run = spawnSync(process.execPath, [scriptPath, '--input', casePath, '--output', outputPath, '--interactive'], {
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 2);
    assert.match(run.stderr, /schema/);
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  }, { approvedNonProduction: 'yes' });
});

test('the journey passes after the actor check and keeps file content out of evidence', async () => {
  await withScratch(async context => {
    const { exitCode, result, commands, calls } = await runFake(context);
    assert.equal(exitCode, 0, result.failure);
    assert.equal(result.status, 'passed');
    assert.deepEqual(calls.map(call => call.key), [
      'version', 'api operations', 'auth status', 'rest --gateway-url',
      'workspace refs', 'workspace refs', 'workspace tree', 'workspace file',
    ]);
    assert.equal(calls[5].args[calls[5].args.indexOf('--cursor') + 1], '2');
    assert.ok(calls.every(call => !Object.hasOwn(call.env, 'AGENTWEAVER_PROJECT_ID')));
    assert.equal(result.observed.gitBlobSha1, blobSha);
    assert.equal(result.observed.sha256, fileSha256);
    for (const evidence of [commands, JSON.stringify(result)]) {
      assert.ok(!evidence.includes('workspace-file-content-canary'));
      assert.ok(!evidence.includes(selectionCode));
    }
  });
});

test('a signed-out CLI blocks before any workspace command', async () => {
  await withScratch(async context => {
    const { exitCode, result, calls } = await runFake(context, {
      'auth status': () => ok({ isSignedIn: false }),
    });
    assert.equal(exitCode, 2);
    assert.equal(result.status, 'blocked');
    assert.match(result.failure, /agentweaver auth login --scope projects\.admin/);
    assert.ok(calls.every(call => !call.key.startsWith('workspace')));
  });
});

test('a different actor or tenant fails before any workspace command', async () => {
  for (const body of [
    { actorId: 'other-actor', tenantId: input.target.tenantId },
    { actorId: input.expectedActorId, tenantId: 'other-tenant' },
  ]) {
    await withScratch(async context => {
      const { exitCode, result, calls } = await runFake(context, {
        'rest --gateway-url': () => ok({ status: 200, body }),
      });
      assert.equal(exitCode, 1);
      assert.equal(result.status, 'failed');
      assert.match(result.failure, /actor or tenant/);
      assert.ok(calls.every(call => !call.key.startsWith('workspace')));
    });
  }
});

test('a ref that is never found or a repeated cursor fails', async () => {
  const page = cursor => ok(workspaceDocument('listWorkspaceRepositoryRefs', {
    repository: 'octo/repo',
    items: [{ kind: 'branch', name: 'other', commitSha: 'b'.repeat(40) }],
    nextCursor: cursor,
  }));
  await withScratch(async context => {
    const { result, calls } = await runFake(context, {
      'workspace refs': args => page(String((args.includes('--cursor') ? Number(args[args.indexOf('--cursor') + 1]) : 1) + 1)),
    });
    assert.equal(result.status, 'failed');
    assert.match(result.failure, /not found in 100 pages/);
    assert.equal(calls.filter(call => call.key === 'workspace refs').length, 100);
  });
  await withScratch(async context => {
    const { result } = await runFake(context, { 'workspace refs': () => page('2') });
    assert.equal(result.status, 'failed');
    assert.match(result.failure, /repeated a page cursor/);
  });
});

test('a Broker failure blocks the case', async () => {
  await withScratch(async context => {
    const { exitCode, result, calls } = await runFake(context, {
      'workspace refs': () => fail('The Identity Broker could not complete sign-in.\n'),
    });
    assert.equal(exitCode, 2);
    assert.equal(result.status, 'blocked');
    assert.match(result.failure, /Identity Broker could not complete sign-in/);
    assert.equal(calls.at(-1).key, 'workspace refs');
  });
});

test('a workspace command that passes its timeout blocks with the approval message', async () => {
  await withScratch(async context => {
    const { exitCode, result, calls } = await runFake(context, {
      'workspace tree': () => fail('', { exitCode: null, signal: 'SIGTERM', timedOut: true, error: 'spawnSync ETIMEDOUT' }),
    }, { interactiveTimeoutMs: 25 });
    assert.equal(exitCode, 2);
    assert.equal(result.status, 'blocked');
    assert.equal(result.failure, 'The browser approval did not complete in time.');
    assert.equal(calls.find(call => call.key === 'workspace tree').timeoutMs, 25);
    assert.equal(calls.find(call => call.key === 'auth status').timeoutMs, COMMAND_TIMEOUT_MS);
  });
});

test('a timeout of a non-interactive command stays failed', async () => {
  await withScratch(async context => {
    const { result } = await runFake(context, {
      'auth status': () => fail('', { exitCode: null, signal: 'SIGTERM', timedOut: true, error: 'spawnSync ETIMEDOUT' }),
    });
    assert.equal(result.status, 'failed');
  });
});

test('runCommand reports a timeout from a real child process', () => {
  const record = runCommand(process.execPath, ['-e', 'setTimeout(() => {}, 5000)'], { PATH: process.env.PATH }, 100);
  assert.equal(record.timedOut, true);
});

test('a file that does not match the expected hash fails', async () => {
  await withScratch(async context => {
    const { result } = await runFake(context, {
      'workspace file': () => ok(workspaceDocument('readWorkspaceRepositoryFile', {
        path: fixture.filePath,
        commitSha: fixture.commitSha,
        size: fixture.byteLength,
        isBinary: false,
        sha256: '0'.repeat(64),
        gitBlobSha1: blobSha,
      })),
    });
    assert.equal(result.status, 'failed');
    assert.match(result.failure, /SHA-256/);
  });
});
