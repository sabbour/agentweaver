import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  apiResponseBody,
  buildCommands,
  parseArgs,
  resolveCliPath,
  validateInput,
  validateObservation,
} from '../numeric-runtime-observation.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'numeric-runtime-observation.mjs');
const inputPath = path.resolve(directory, '..', 'examples', 'numeric-runtime-observation.json');
const input = JSON.parse(await readFile(inputPath, 'utf8'));

function observation() {
  const reservation = {
    factId: '00000000-0000-4000-8000-000000000010',
    kind: 'ToolInvocationReserved',
    limitKind: 'ToolCalls',
    sessionId: input.target.sessionId,
    attemptId: '00000000-0000-4000-8000-000000000011',
  };
  const rejection = {
    factId: '00000000-0000-4000-8000-000000000012',
    kind: 'ToolPermissionRejected',
    limitKind: 'ToolCalls',
    stage: 'toolPermission',
    reasonCode: 'runtime_tool_call_limit_exhausted',
    sessionId: input.target.sessionId,
    attemptId: '00000000-0000-4000-8000-000000000013',
    dispatchId: '00000000-0000-4000-8000-000000000014',
    ownerDecision: { state: 'Available' },
    toolPermission: { state: 'Available' },
    nativeNoSend: { state: 'Unavailable' },
    toolExecution: { state: 'Unavailable' },
  };
  const body = {
    contractVersion: 2,
    binding: {
      projectId: input.target.projectId,
      runId: input.target.runId,
      sessionId: input.target.sessionId,
      isRoot: false,
      currentExecutionFence: 2,
      sessionExecutionFence: 2,
    },
    acceptedLimits: { maxToolCalls: 1 },
    totals: { toolSlotsReserved: { state: 'Available', value: 1 } },
    history: {
      snapshotId: '00000000-0000-4000-8000-000000000015',
      highWatermark: 2,
      complete: true,
      items: [reservation, rejection],
      nextCursor: null,
    },
  };
  const exact = {
    history: { exactAttempt: true, items: [rejection] },
  };
  return { body, exact, restExact: structuredClone(exact) };
}

test('case input accepts only approved target metadata and safe resource IDs', () => {
  const parsed = validateInput(input);
  assert.equal(parsed.caseId, 'numeric-runtime-observation');
  assert.equal(parsed.expectedMaxToolCalls, 1);
  assert.throws(() => validateInput({ ...input, bearerToken: 'do-not-log-this' }), /schema/);
  assert.throws(() => validateInput({
    ...input,
    target: { ...input.target, sessionId: 'bad/session/id' },
  }), /schema/);
  assert.throws(() => validateInput({ ...input, approvedNonProduction: false }), /schema/);
});

test('commands use the normal CLI and REST surfaces without passing run text', () => {
  const commands = buildCommands(validateInput(input));
  assert.deepEqual(commands.map(command => command.id), [
    'version',
    'auth-status',
    'api-operations',
    'api-describe-runtime-observation',
    'rest-authorization-context',
    'run-status',
    'project-usage',
    'observation-page-1',
  ]);
  assert.ok(commands.some(command => command.args.includes('rest')));
  assert.ok(commands.some(command => command.args.includes('readRuntimeObservationV2')));
  assert.ok(commands.every(command => !command.args.some(argument => argument.includes('prompt'))));
  assert.ok(commands[7].args.includes('--query'));
  assert.ok(commands[7].args.includes('pageSize=100'));
  for (const command of commands.slice(1, 5)) {
    assert.ok(!command.args.includes('--project-id'));
    assert.ok(!command.args.includes('--run-id'));
  }
  assert.ok(commands[6].args.includes('--project-id'));
  assert.ok(commands[6].args.includes('--run-id'));
  assert.ok(commands[6].args.includes('projects.orchestrator'));
});

test('API invocation response bodies require HTTP 200 and unwrap the CLI envelope', () => {
  const { body } = observation();
  const response = { status: 200, headers: {}, evidence: {}, body };
  assert.equal(apiResponseBody(response, 'read'), body);
  assert.throws(() => apiResponseBody({ ...response, status: 403 }, 'read'), /HTTP 200/);
  assert.throws(() => apiResponseBody({ ...response, body: [] }, 'read'), /JSON body/);
});

test('case passes only when paged history and both exact lookups retain rejection evidence', () => {
  const { body, exact, restExact } = observation();
  const cliResponse = value => ({ status: 200, headers: {}, evidence: {}, body: value });
  const summary = validateObservation(
    validateInput(input),
    [apiResponseBody(cliResponse(body), 'observation page')],
    apiResponseBody(cliResponse(exact), 'exact lookup'),
    restExact,
  );
  assert.equal(summary.attemptId, '00000000-0000-4000-8000-000000000013');
  assert.match(summary.conclusion, /does not prove/);

  const unavailable = structuredClone(body);
  unavailable.history.complete = false;
  assert.throws(() => validateObservation(validateInput(input), [unavailable], exact, restExact), /incomplete/);

  const missingPermission = structuredClone(exact);
  missingPermission.history.items[0].toolPermission.state = 'Unavailable';
  assert.throws(() => validateObservation(validateInput(input), [body], missingPermission, restExact), /Exact attempt lookup/);

  const mismatchedAttempt = structuredClone(exact);
  mismatchedAttempt.history.items[0].attemptId = '00000000-0000-4000-8000-000000000099';
  assert.throws(() => validateObservation(
    validateInput(input), [body], mismatchedAttempt, restExact,
  ), /Exact attempt lookup/);

  const mismatchedRestEvidence = structuredClone(restExact);
  mismatchedRestEvidence.history.items[0].dispatchId = '00000000-0000-4000-8000-000000000099';
  mismatchedRestEvidence.history.items[0].toolPermission.payloadHash = 'different-hash';
  assert.throws(() => validateObservation(
    validateInput(input), [body], exact, mismatchedRestEvidence,
  ), /REST exact lookup/);
});

test('case rejects a changed snapshot and repeated facts across history pages', () => {
  const { body, exact, restExact } = observation();
  const secondPage = structuredClone(body);
  secondPage.history.items = [secondPage.history.items[1]];
  secondPage.history.nextCursor = null;
  body.history.items = [body.history.items[0]];
  body.history.nextCursor = 'opaque-cursor';
  assert.equal(validateObservation(validateInput(input), [body, secondPage], exact, restExact).pagesRead, 2);

  const changedSnapshot = structuredClone(secondPage);
  changedSnapshot.history.snapshotId = '00000000-0000-4000-8000-000000000016';
  assert.throws(() => validateObservation(
    validateInput(input), [body, changedSnapshot], exact, restExact,
  ), /snapshot/);

  const duplicate = structuredClone(body);
  duplicate.history.items = [body.history.items[0]];
  assert.throws(() => validateObservation(
    validateInput(input), [body, duplicate], exact, restExact,
  ), /duplicate fact/);
});

test('native CLI path must be absolute and name the apphost', () => {
  const expectedName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  assert.throws(() => resolveCliPath('agentweaver.exe'), /absolute path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /native/);
  assert.equal(path.basename(resolveCliPath(path.resolve(directory, expectedName))), expectedName);
});

test('dry-run validates the apphost hash and creates no evidence or network request', async () => {
  const scratch = path.join(directory, `dry-run-${randomUUID()}`);
  const cliPath = path.join(scratch, process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const casePath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'non-executable dry-run fixture');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(casePath, JSON.stringify({ ...input, apphostSha256 }));
    const run = spawnSync(process.execPath, [
      scriptPath, '--input', casePath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 0, run.stderr);
    const result = JSON.parse(run.stdout);
    assert.equal(result.status, 'validated-not-run');
    assert.equal(result.network, 'not-used');
    assert.equal(result.evidence, 'not-created');
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('argument parser rejects unknown and repeated options', () => {
  assert.throws(() => parseArgs(['--input', 'case.json', '--output', 'evidence', '--token', 'secret']), /Use --input/);
  assert.throws(() => parseArgs(['--input', 'case.json', '--input', 'other.json', '--output', 'evidence']), /Use --input/);
});
