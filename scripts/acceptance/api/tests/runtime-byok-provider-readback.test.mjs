import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  ALLOWLISTED_ERROR_CODES,
  buildCommands,
  buildListEgressCommand,
  childEnvironment,
  resolveCliPath,
  runCase,
  runCommand,
  validateInput,
} from '../runtime-byok-provider-readback.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'runtime-byok-provider-readback.mjs');
const examplePath = path.resolve(directory, '..', 'examples', 'runtime-byok-provider-readback.json');
const example = JSON.parse(await readFile(examplePath, 'utf8'));
const apphostName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';

const UNRELATED_TENANT_MARKER = 'SECRET_UNRELATED_TENANT_MARKER';
const UNRELATED_HOST_MARKER = 'secret-unrelated-host.example.invalid';
const UNRELATED_PROVIDER_MARKER = 'SECRET_UNRELATED_PROVIDER_MARKER';
const CURSOR_MARKER = 'CURSOR_MARKER_DO_NOT_LOG_12345';
const STDERR_MARKER = 'STDERR_SENSITIVE_MARKER_98765';
const HEADER_MARKER = 'X-Sensitive-Header-Marker';

function ok(body) {
  return {
    startedAt: '2026-11-25T10:00:00.000Z',
    completedAt: '2026-11-25T10:00:00.050Z',
    durationMs: 50,
    exitCode: 0,
    signal: null,
    stdout: typeof body === 'string' ? body : JSON.stringify(body),
    stderr: '',
    timedOut: false,
    spawnError: false,
  };
}

function envelope(status, body, headers = { 'Cache-Control': 'no-store', [HEADER_MARKER]: 'secret-header-value' }) {
  return {
    ...ok({ status, headers, body }),
    exitCode: status >= 200 && status < 300 ? 0 : 1,
  };
}

function destinationFromExpected(input, overrides = {}) {
  return {
    destinationId: '11111111-1111-4111-8111-111111111111',
    requestId: input.expectedRequest.requestId,
    host: input.expectedRequest.host,
    port: input.expectedRequest.port,
    state: 'requested',
    origin: 'modelProvider',
    scope: input.expectedRequest.scope,
    projectId: input.expectedRequest.projectId,
    tenantId: input.expectedRequest.tenantId,
    providerId: input.expectedRequest.providerId,
    providerRevision: input.expectedRequest.providerRevision,
    requestedBy: input.expectedActorId,
    requestedAt: '2026-11-25T09:55:00.000Z',
    ...overrides,
  };
}

function unrelatedDestination() {
  return {
    destinationId: '22222222-2222-4222-8222-222222222222',
    requestId: '33333333-3333-4333-8333-333333333333',
    host: UNRELATED_HOST_MARKER,
    port: 443,
    state: 'requested',
    origin: 'modelProvider',
    scope: 'platform',
    projectId: null,
    tenantId: UNRELATED_TENANT_MARKER,
    providerId: UNRELATED_PROVIDER_MARKER,
    providerRevision: 7,
    requestedBy: 'other-actor',
    requestedAt: '2026-11-25T09:50:00.000Z',
  };
}

function fakeApphost(caseInput, overrides = {}) {
  const calls = [];
  const runner = (cliPath, args, env, timeoutMs) => {
    const key = args[0] === '--version'
      ? 'version'
      : args[0] === 'api' && args[1] === 'invoke'
        ? `api invoke ${args[2]}`
        : `${args[0]} ${args[1] ?? ''}`.trim();
    calls.push({ key, args, env, timeoutMs });
    if (overrides[key]) {
      const custom = overrides[key](args, calls);
      if (custom) return custom;
    }
    switch (key) {
      case 'version':
        return ok('1.0.0\n');
      case 'api operations':
        return ok([
          { operationId: 'getAuthorizationContext' },
          { operationId: 'listPlatformEgressDestinations' },
        ]);
      case 'auth status':
        return ok({
          isSignedIn: true,
          actorId: caseInput.expectedActorId,
          tenantId: caseInput.target.tenantId,
        });
      case 'api invoke getAuthorizationContext':
        return envelope(200, {
          contractVersion: 1,
          issuer: caseInput.target.brokerIssuer,
          actorId: caseInput.expectedActorId,
          tenantId: caseInput.target.tenantId,
          membershipRevision: 1,
          boundProjectId: null,
          boundRunId: null,
          effectiveAuthority: [{
            resourceType: 'platform',
            resourceId: 'default',
            permissions: [{ permission: 'readPlatformRuntimeDefaults', roleRevision: 1 }],
          }],
        });
      case 'api invoke listPlatformEgressDestinations':
        return envelope(200, {
          destinations: [unrelatedDestination(), destinationFromExpected(caseInput)],
          nextCursor: null,
        });
      default:
        throw new Error(`Unexpected command: ${key}`);
    }
  };
  return { runner, calls };
}

async function withFixture(runFn) {
  const scratch = path.join(directory, `readback-${randomUUID()}`);
  const cliPath = path.join(scratch, apphostName);
  const casePath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch, { recursive: true });
  try {
    await writeFile(cliPath, 'fake native apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    const caseInput = { ...structuredClone(example), apphostSha256 };
    await writeFile(casePath, JSON.stringify(caseInput));
    return await runFn({ scratch, cliPath, casePath, outputPath, caseInput });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
}

test('case input validates exact keys, approved target, and expectedRequest scope rules', () => {
  const parsed = validateInput(example);
  assert.equal(parsed.caseId, 'runtime-byok-provider-readback');
  assert.throws(() => validateInput({ ...example, extraField: true }), /invalid_input/);
  assert.throws(() => validateInput({ ...example, approvedNonProduction: false }), /invalid_input/);
  assert.throws(() => validateInput({
    ...example,
    expectedRequest: { ...example.expectedRequest, projectId: 'proj-1' },
  }), /invalid_input/);
  const projectScoped = validateInput({
    ...example,
    expectedRequest: { ...example.expectedRequest, scope: 'project', projectId: 'proj-1' },
  });
  assert.equal(projectScoped.expectedRequest.projectId, 'proj-1');
  assert.throws(() => validateInput({
    ...example,
    expectedRequest: { ...example.expectedRequest, scope: 'project', projectId: null },
  }), /invalid_input/);
});

test('commands use explicit target options, unbound projects.admin scope, and literal operation', () => {
  const parsed = validateInput(example);
  const commands = buildCommands(parsed);
  assert.deepEqual(commands.map(c => c.id), [
    'version', 'api-operations', 'auth-status', 'api-invoke-authorization-context',
  ]);
  for (const cmd of commands.slice(1)) {
    assert.ok(!cmd.args.includes('--project-id'));
    assert.ok(!cmd.args.includes('--run-id'));
  }
  assert.ok(commands[2].args.includes('--scope'));
  assert.equal(commands[2].args[commands[2].args.indexOf('--scope') + 1], 'projects.admin');

  const listCmd = buildListEgressCommand(parsed, 1, { cursor: CURSOR_MARKER });
  assert.deepEqual(listCmd.args.slice(0, 5), [
    'api', 'invoke', 'listPlatformEgressDestinations', '--scope', 'projects.admin',
  ]);
  assert.ok(listCmd.args.includes('state=requested'));
  assert.ok(listCmd.args.includes(`cursor=${CURSOR_MARKER}`));

  assert.throws(() => resolveCliPath('agentweaver.exe'), /invalid_cli_path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /invalid_cli_path/);
  const env = childEnvironment({
    PATH: 'keep',
    AGENTWEAVER_PROJECT_ID: 'drop',
    agentweaver_run_id: 'drop',
    AGENTWEAVER_CLI_PATH: 'drop',
  });
  assert.deepEqual(env, { PATH: 'keep' });
});

test('dry-run validates local input without starting apphost or creating evidence', async () => {
  await withFixture(async ({ cliPath, casePath, outputPath }) => {
    const run = spawnSync(process.execPath, [
      scriptPath, '--input', casePath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 0, run.stderr);
    const parsed = JSON.parse(run.stdout);
    assert.equal(parsed.status, 'validated-not-run');
    assert.equal(parsed.network, 'not-used');
    assert.equal(parsed.evidence, 'not-created');
    await assert.rejects(access(outputPath), { code: 'ENOENT' });

    await writeFile(cliPath, 'tampered apphost');
    const badHash = spawnSync(process.execPath, [
      scriptPath, '--input', casePath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(badHash.status, 1);
    assert.equal(JSON.parse(badHash.stderr).errorCode, ALLOWLISTED_ERROR_CODES.apphostHashMismatch);
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  });
});

test('passes across empty-page pagination and excludes unrelated rows, cursors, headers, and args from evidence', async () => {
  await withFixture(async ({ cliPath, casePath, outputPath, caseInput }) => {
    let pageCalls = 0;
    const { runner } = fakeApphost(caseInput, {
      'api invoke listPlatformEgressDestinations': (args) => {
        pageCalls += 1;
        if (pageCalls === 1) {
          assert.ok(!args.some(arg => arg.startsWith('cursor=')));
          return envelope(200, { destinations: [], nextCursor: CURSOR_MARKER });
        }
        assert.ok(args.includes(`cursor=${CURSOR_MARKER}`));
        return envelope(200, {
          destinations: [unrelatedDestination(), destinationFromExpected(caseInput)],
          nextCursor: null,
        });
      },
    });
    const code = await runCase(
      { inputPath: casePath, outputPath, dryRun: false },
      { PATH: process.env.PATH, AGENTWEAVER_CLI_PATH: cliPath },
      { runCommand: runner },
    );
    assert.equal(code, 0);
    const resultText = await readFile(path.join(outputPath, 'result.json'), 'utf8');
    const commandsText = await readFile(path.join(outputPath, 'commands.jsonl'), 'utf8');
    const result = JSON.parse(resultText);
    assert.equal(result.status, 'passed');
    assert.equal(result.pagesRead, 2);
    assert.equal(result.matchedDestination.requestId, caseInput.expectedRequest.requestId);
    assert.equal(result.matchedDestination.host, caseInput.expectedRequest.host);
    for (const marker of [UNRELATED_TENANT_MARKER, UNRELATED_HOST_MARKER, UNRELATED_PROVIDER_MARKER, CURSOR_MARKER, HEADER_MARKER, 'secret-header-value']) {
      assert.ok(!resultText.includes(marker), `result.json leaked ${marker}`);
      assert.ok(!commandsText.includes(marker), `commands.jsonl leaked ${marker}`);
    }
  });
});

test('rejects identity, binding, and platform admin authorization failures', async () => {
  for (const [name, overrides, expectedStatus, expectedCode] of [
    ['not signed in', { 'auth status': () => ok({ isSignedIn: false }) }, 2, ALLOWLISTED_ERROR_CODES.authRequired],
    ['tenant mismatch', { 'auth status': () => ok({ isSignedIn: true, tenantId: 'other-tenant' }) }, 1, ALLOWLISTED_ERROR_CODES.tenantMismatch],
    ['actor mismatch', {
      'api invoke getAuthorizationContext': () => envelope(200, {
        actorId: 'wrong-actor',
        tenantId: example.target.tenantId,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [{ resourceType: 'platform', resourceId: 'default', permissions: [{ permission: 'readPlatformRuntimeDefaults', roleRevision: 1 }] }],
      }),
    }, 1, ALLOWLISTED_ERROR_CODES.actorMismatch],
    ['bound project', {
      'api invoke getAuthorizationContext': () => envelope(200, {
        actorId: example.expectedActorId,
        tenantId: example.target.tenantId,
        boundProjectId: 'proj-1',
        boundRunId: null,
        effectiveAuthority: [{ resourceType: 'platform', resourceId: 'default', permissions: [{ permission: 'readPlatformRuntimeDefaults', roleRevision: 1 }] }],
      }),
    }, 1, ALLOWLISTED_ERROR_CODES.authContextBound],
    ['missing platform read permission', {
      'api invoke getAuthorizationContext': () => envelope(200, {
        actorId: example.expectedActorId,
        tenantId: example.target.tenantId,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [{ resourceType: 'tenant', resourceId: example.target.tenantId, permissions: [{ permission: 'readProjects', roleRevision: 1 }] }],
      }),
    }, 1, ALLOWLISTED_ERROR_CODES.platformAdminRequired],
  ]) {
    await withFixture(async ({ cliPath, casePath, outputPath, caseInput }) => {
      const { runner } = fakeApphost(caseInput, overrides);
      const code = await runCase(
        { inputPath: casePath, outputPath, dryRun: false },
        { PATH: process.env.PATH, AGENTWEAVER_CLI_PATH: cliPath },
        { runCommand: runner },
      );
      assert.equal(code, expectedStatus, name);
      const result = JSON.parse(await readFile(path.join(outputPath, 'result.json'), 'utf8'));
      assert.equal(result.errorCode, expectedCode, name);
    });
  }
});

test('rejects wrong request, missing request, repeated cursor, malformed cursor, and page limit exhaustion', async () => {
  for (const [name, overrides, expectedCode, extraRunOptions] of [
    ['wrong request host', {
      'api invoke listPlatformEgressDestinations': (_args, calls) => envelope(200, {
        destinations: [destinationFromExpected(validateInput(example), { host: 'wrong.example.invalid' })],
        nextCursor: null,
      }),
    }, ALLOWLISTED_ERROR_CODES.requestMismatch],
    ['missing request', {
      'api invoke listPlatformEgressDestinations': () => envelope(200, {
        destinations: [unrelatedDestination()],
        nextCursor: null,
      }),
    }, ALLOWLISTED_ERROR_CODES.requestNotFound],
    ['repeated cursor', {
      'api invoke listPlatformEgressDestinations': () => envelope(200, {
        destinations: [],
        nextCursor: 'page_cursor_1',
      }),
    }, ALLOWLISTED_ERROR_CODES.repeatedCursor],
    ['malformed cursor', {
      'api invoke listPlatformEgressDestinations': () => envelope(200, {
        destinations: [],
        nextCursor: 'bad cursor with spaces!',
      }),
    }, ALLOWLISTED_ERROR_CODES.malformedCursor],
    ['page limit exceeded', {
      'api invoke listPlatformEgressDestinations': (_args, calls) => envelope(200, {
        destinations: [],
        nextCursor: `cursor_${calls.length}`,
      }),
    }, ALLOWLISTED_ERROR_CODES.pageLimitExceeded, { maxPages: 2 }],
  ]) {
    await withFixture(async ({ cliPath, casePath, outputPath, caseInput }) => {
      const { runner } = fakeApphost(caseInput, overrides);
      const code = await runCase(
        { inputPath: casePath, outputPath, dryRun: false },
        { PATH: process.env.PATH, AGENTWEAVER_CLI_PATH: cliPath },
        { runCommand: runner, ...extraRunOptions },
      );
      assert.equal(code, 1, name);
      const resultText = await readFile(path.join(outputPath, 'result.json'), 'utf8');
      const commandsText = await readFile(path.join(outputPath, 'commands.jsonl'), 'utf8');
      const result = JSON.parse(resultText);
      assert.equal(result.errorCode, expectedCode, name);
      assert.ok(!resultText.includes(UNRELATED_TENANT_MARKER));
      assert.ok(!commandsText.includes(UNRELATED_TENANT_MARKER));
    });
  }
});

test('maps 403, 503, malformed output, abnormal exits, and timeouts to static allowlisted codes without leaking stderr or raw output', async () => {
  for (const [name, overrides, expectedCode, expectedHttpStatus = null] of [
    ['auth context 403', {
      'api invoke getAuthorizationContext': () => envelope(403, { code: 'forbidden', detail: STDERR_MARKER }),
    }, ALLOWLISTED_ERROR_CODES.authContextHttpError, 403],
    ['403 forbidden', {
      'api invoke listPlatformEgressDestinations': () => envelope(403, { code: 'forbidden', detail: STDERR_MARKER }),
    }, ALLOWLISTED_ERROR_CODES.egressListForbidden, 403],
    ['503 unavailable', {
      'api invoke listPlatformEgressDestinations': () => envelope(503, { code: 'egress_proof_unavailable', detail: STDERR_MARKER }),
    }, ALLOWLISTED_ERROR_CODES.egressListUnavailable, 503],
    ['400 bad request', {
      'api invoke listPlatformEgressDestinations': () => envelope(400, { code: 'invalid_pagination', detail: STDERR_MARKER }),
    }, ALLOWLISTED_ERROR_CODES.egressListBadRequest, 400],
    ['malformed json on exit 0', {
      'api invoke listPlatformEgressDestinations': () => ok(`not-json-${STDERR_MARKER}`),
    }, ALLOWLISTED_ERROR_CODES.malformedJson],
    ['invalid stdout on exit 1', {
      'api invoke listPlatformEgressDestinations': () => ({
        ...ok(`not-json-${UNRELATED_TENANT_MARKER}`),
        exitCode: 1,
        stderr: STDERR_MARKER,
      }),
    }, ALLOWLISTED_ERROR_CODES.commandFailed],
    ['abnormal exit 2 with envelope', {
      'api invoke listPlatformEgressDestinations': () => ({
        ...envelope(403, { code: 'forbidden', detail: UNRELATED_TENANT_MARKER }),
        exitCode: 2,
        stderr: STDERR_MARKER,
      }),
    }, ALLOWLISTED_ERROR_CODES.commandFailed],
    ['command failure with sensitive stderr', {
      'api invoke listPlatformEgressDestinations': () => ({
        ...ok(''),
        exitCode: 1,
        stdout: UNRELATED_TENANT_MARKER,
        stderr: STDERR_MARKER,
      }),
    }, ALLOWLISTED_ERROR_CODES.commandFailed],
    ['command timeout', {
      'api invoke listPlatformEgressDestinations': () => ({
        ...ok(''),
        exitCode: null,
        signal: 'SIGTERM',
        timedOut: true,
        stderr: STDERR_MARKER,
      }),
    }, ALLOWLISTED_ERROR_CODES.commandTimeout],
  ]) {
    await withFixture(async ({ cliPath, casePath, outputPath, caseInput }) => {
      const { runner } = fakeApphost(caseInput, overrides);
      const code = await runCase(
        { inputPath: casePath, outputPath, dryRun: false },
        { PATH: process.env.PATH, AGENTWEAVER_CLI_PATH: cliPath },
        { runCommand: runner },
      );
      assert.equal(code, 1, name);
      const resultText = await readFile(path.join(outputPath, 'result.json'), 'utf8');
      const commandsText = await readFile(path.join(outputPath, 'commands.jsonl'), 'utf8');
      assert.equal(JSON.parse(resultText).errorCode, expectedCode, name);
      if (expectedHttpStatus !== null) {
        const lastCommand = JSON.parse(commandsText.trim().split('\n').at(-1));
        assert.equal(lastCommand.httpStatus, expectedHttpStatus, name);
      }
      assert.ok(!resultText.includes(STDERR_MARKER), `${name} leaked stderr to result.json`);
      assert.ok(!commandsText.includes(STDERR_MARKER), `${name} leaked stderr to commands.jsonl`);
      assert.ok(!resultText.includes(UNRELATED_TENANT_MARKER));
      assert.ok(!commandsText.includes(UNRELATED_TENANT_MARKER));
    });
  }
});

test('runCommand enforces strict child timeout on a real subprocess', () => {
  const record = runCommand(
    process.execPath,
    ['-e', 'setTimeout(() => {}, 5000)'],
    process.env,
    100,
  );
  assert.equal(record.timedOut, true);
});
