import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  buildCommands,
  childEnvironment,
  parseArgs,
  resolveCliPath,
  validateAuthStatus,
  validateInput,
} from '../read-only-gateway.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'read-only-gateway.mjs');
const inputPath = path.resolve(directory, '..', 'examples', 'read-only-gateway.json');
const input = JSON.parse(await readFile(inputPath, 'utf8'));

test('case input admits only the declared read-only target and identities', () => {
  const parsed = validateInput(input);
  assert.equal(parsed.caseId, 'read-only-gateway');
  assert.equal(parsed.target.gatewayUrl, 'https://gateway.example.invalid');
  assert.equal(parsed.target.brokerIssuer, 'https://identity.example.invalid/issuer-path');
  assert.throws(() => validateInput({ ...input, legacyManifest: { target: 'ignored' } }), /schema/);
  assert.throws(() => validateInput({ ...input, bearerToken: 'do-not-log-this' }), /schema/);
  assert.throws(() => validateInput({
    ...input,
    target: { ...input.target, gatewayUrl: 'https://user:pass@gateway.example.invalid' },
  }), /Gateway URL/);
});

test('commands use public CLI operations and keep target selectors as literal arguments', () => {
  const source = structuredClone(input);
  source.target.clientId = 'client; & echo not-a-shell-command';
  const commands = buildCommands(validateInput(source));
  assert.deepEqual(commands.map(command => command.id), [
    'version',
    'auth-status',
    'api-operations',
    'api-describe',
    'rest-authorization-context',
  ]);
  assert.deepEqual(commands.at(-1).args.slice(-6), [
    '--scope', 'api.read', '--method', 'GET', '--url', '/api/v1/authorization/context',
  ]);
  assert.equal(commands[1].args[commands[1].args.indexOf('--client-id') + 1], source.target.clientId);
  assert.ok(commands.every(command => !command.args.includes('test')));
});

test('CLI path must be absolute and name the native apphost', () => {
  const expectedName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  assert.throws(() => resolveCliPath('agentweaver.exe'), /absolute path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /native/);
  assert.equal(path.basename(resolveCliPath(path.resolve(directory, expectedName))), expectedName);
});

test('child environment removes target selectors without regard to key case', () => {
  const childEnv = childEnvironment({
    PATH: 'system-path',
    AGENTWEAVER_GATEWAY_URL: 'https://stale.example',
    agentweaver_project_id: 'stale-project',
    AgEnTwEaVeR_RuN_Id: 'stale-run',
    agentweaver_cli_path: 'stale-apphost',
  });
  assert.deepEqual(childEnv, { PATH: 'system-path' });
});

test('signed-in status may omit its process-cached actor; the live REST context checks the actor', () => {
  const parsed = validateInput(input);
  assert.equal(validateAuthStatus({
    isSignedIn: true,
    actorId: null,
    tenantId: parsed.target.tenantId,
  }, parsed), true);
  assert.equal(validateAuthStatus({ isSignedIn: false }, parsed), false);
  assert.throws(() => validateAuthStatus({
    isSignedIn: true,
    tenantId: 'other-tenant',
  }, parsed), /tenant/);
});

test('dry-run validates local input without starting the apphost or creating evidence', async () => {
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
    assert.equal(result.apphostSha256, apphostSha256);
    assert.match(result.scriptSha256, /^[a-f0-9]{64}$/);
    assert.equal(result.network, 'not-used');
    assert.equal(result.evidence, 'not-created');
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('dry-run rejects an apphost hash mismatch before creating evidence', async () => {
  const scratch = path.join(directory, `hash-mismatch-${randomUUID()}`);
  const cliPath = path.join(scratch, process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const casePath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'non-executable dry-run fixture');
    await writeFile(casePath, JSON.stringify({ ...input, apphostSha256: '0'.repeat(64) }));
    const run = spawnSync(process.execPath, [
      scriptPath, '--input', casePath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 2);
    assert.match(run.stderr, /hash does not match/);
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('normal execution refuses a missing apphost and leaves the new output path untouched', async () => {
  const outputPath = path.resolve(directory, 'missing-apphost-evidence-not-created');
  await assert.rejects(access(outputPath), { code: 'ENOENT' });
  const cliPath = path.resolve(directory, 'missing-apphost', process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const run = spawnSync(process.execPath, [
    scriptPath, '--input', inputPath, '--output', outputPath,
  ], {
    cwd: path.resolve(directory, '..', '..', '..', '..'),
    encoding: 'utf8',
    env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
  });
  assert.equal(run.status, 2);
  assert.match(run.stderr, /does not exist as a file/);
  await assert.rejects(access(outputPath), { code: 'ENOENT' });
});

test('existing output directory blocks before command execution', async () => {
  const outputPath = path.resolve(directory, '..');
  const cliPath = path.resolve(directory, 'missing-apphost', process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const run = spawnSync(process.execPath, [
    scriptPath, '--input', inputPath, '--output', outputPath,
  ], {
    cwd: path.resolve(directory, '..', '..', '..', '..'),
    encoding: 'utf8',
    env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
  });
  assert.equal(run.status, 2);
  assert.match(run.stderr, /already exists/);
});

test('argument parser rejects unknown and repeated options', () => {
  assert.throws(() => parseArgs(['--input', 'case.json', '--output', 'evidence', '--token', 'secret']), /Use --input/);
  assert.throws(() => parseArgs(['--input', 'case.json', '--input', 'other.json', '--output', 'evidence']), /Use --input/);
});
