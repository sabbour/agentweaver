import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadDirConfig, resolveExternalStateDir } from '@bradygaster/squad-sdk';
import { KIND, resolveAuthoritativeState, runAdmissionPreflight, validateAdmissionPreflight, verifyTrustedValidator } from '../squad-admission-preflight.mjs';
import { launchTrustedAdmission } from '../squad-admission-launcher.mjs';

const SHA = 'a'.repeat(40);
const expected = { repository: 'sabbour/agentweaver', prNumber: 1489, headSha: SHA };
const event = (state, extra = {}) => ({ state, actor: 'ralph', at: '2026-09-21T00:00:00Z', headSha: SHA, evidence: state, ...extra });
const ledger = (findings = []) => ({ kind: KIND, ...expected, findings });

test('admits empty, advisory, and fully resolved authoritative ledgers', () => {
  assert.equal(validateAdmissionPreflight(ledger(), expected).admitted, true);
  assert.equal(validateAdmissionPreflight(ledger([{ id: 'F-0', policy: 'advisory' }]), expected).findings, 1);
  const finding = { id: 'F-1', policy: 'required', transitions: [
    event('recorded'), event('owned', { owner: 'neo', action: 'fix validator' }),
    event('corrected'), event('revalidated', { validation: 'node --test focused suite' }), event('resolved'),
  ] };
  assert.equal(validateAdmissionPreflight(ledger([finding]), expected).findings, 1);
});

test('blocks unknown policies and incomplete required findings', () => {
  assert.throws(() => validateAdmissionPreflight(ledger([{ id: 'F-1' }]), expected), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger([{ id: 'F-1', policy: 'blocking' }]), expected), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger([{ id: 'F-1', policy: 'required', transitions: [] }]), expected), /incomplete/u);
});

test('resolves and verifies external state with the pinned CLI configuration resolver', () => {
  const fixture = join(dirname(fileURLToPath(import.meta.url)), `.admission-state-${process.pid}-${Date.now()}`);
  const repositoryRoot = join(fixture, 'repository');
  const squadDir = join(repositoryRoot, '.squad');
  const environmentName = process.platform === 'win32' ? 'APPDATA' : 'XDG_CONFIG_HOME';
  const priorAppData = process.env[environmentName];
  try {
    mkdirSync(squadDir, { recursive: true });
    writeFileSync(join(squadDir, 'config.json'), JSON.stringify({ version: 1, teamRoot: '.', projectKey: 'admission-state-test', stateLocation: 'external' }));
    process.env[environmentName] = join(fixture, 'state-root');
    const cliResolvedPath = resolveExternalStateDir(loadDirConfig(squadDir).projectKey, false);
    mkdirSync(cliResolvedPath, { recursive: true });
    assert.equal(resolveAuthoritativeState(repositoryRoot), cliResolvedPath);
    assert.throws(() => resolveAuthoritativeState(join(fixture, 'missing-marker')), /config\.json is required/u);
  } finally {
    if (priorAppData === undefined) delete process.env[environmentName];
    else process.env[environmentName] = priorAppData;
    rmSync(fixture, { recursive: true, force: true });
  }
});

test('resolves the live PR head immediately before validating the external ledger', async () => {
  const calls = [];
  const result = await runAdmissionPreflight('sabbour/agentweaver', 1489, {
    repositoryRoot: 'C:\\repo',
    command: async (file, args) => {
      calls.push([file, args]);
      return { stdout: JSON.stringify({ headRefOid: SHA }) };
    },
    resolveState: () => 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver',
    validator: { path: 'scripts/ci/squad-admission-preflight.mjs', ref: 'origin/dev', blobSha: 'b'.repeat(40), version: '1' },
    openBridge: () => ({ read: async () => { calls.push(['bridge', []]); return JSON.stringify(ledger()); }, close() {} }),
  });
  assert.equal(result.headSha, SHA);
  assert.equal(result.validator.blobSha, 'b'.repeat(40));
  assert.deepEqual(calls[1], ['gh', ['pr', 'view', '1489', '--repo', 'sabbour/agentweaver', '--json', 'headRefOid']]);
  await assert.rejects(() => runAdmissionPreflight('sabbour/agentweaver', 1489, {
    repositoryRoot: 'C:\\repo',
    command: async () => ({ stdout: JSON.stringify({ headRefOid: 'b'.repeat(40) }) }),
    resolveState: () => 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver',
    openBridge: () => ({ read: async () => JSON.stringify(ledger()), close() {} }),
  }), /stale/u);
});

test('the materialized validator verifies its trusted source bytes', async () => {
  const trustedBlob = 'c'.repeat(40);
  const command = async (_file, args) => {
    if (args[0] === 'rev-parse') return { stdout: `${trustedBlob}\n` };
    return { stdout: `${trustedBlob}\n` };
  };
  const validator = await verifyTrustedValidator({
    command, repositoryRoot: 'C:\\repo', validatorPath: 'C:\\trusted\\squad-admission-preflight.mjs', trustedValidatorBlob: trustedBlob,
  });
  assert.deepEqual(validator, { path: 'scripts/ci/squad-admission-preflight.mjs', ref: 'origin/dev', blobSha: trustedBlob, version: '1' });
  await assert.rejects(() => verifyTrustedValidator({
    command: async (_file, args) => ({ stdout: `${args[0] === 'rev-parse' ? trustedBlob : 'd'.repeat(40)}\n` }),
    repositoryRoot: 'C:\\repo', validatorPath: 'C:\\candidate\\squad-admission-preflight.mjs', trustedValidatorBlob: trustedBlob,
  }), /materialized trusted validator bytes do not match origin\/dev/u);
});

test('launcher executes actual trusted git bytes, not a modified candidate validator, and cleans up', async () => {
  const fixture = join(dirname(fileURLToPath(import.meta.url)), `.admission-launcher-${process.pid}-${Date.now()}`);
  const candidateRoot = join(fixture, 'candidate');
  const validatorPath = 'scripts/ci/squad-admission-preflight.mjs';
  const traceFile = join(fixture, 'trusted-trace.json');
  const candidateMarker = join(fixture, 'candidate-ran');
  const headSha = 'e'.repeat(40);
  const previousTrace = process.env.ADMISSION_TRACE;
  try {
    mkdirSync(candidateRoot, { recursive: true });
    execFileSync('git', ['init'], { cwd: candidateRoot, stdio: 'ignore' });
    execFileSync('git', ['config', 'user.email', 'admission@example.test'], { cwd: candidateRoot });
    execFileSync('git', ['config', 'user.name', 'Admission test'], { cwd: candidateRoot });
    mkdirSync(dirname(join(candidateRoot, validatorPath)), { recursive: true });
    writeFileSync(join(candidateRoot, validatorPath), `
      import { execFileSync } from 'node:child_process';
      import { writeFileSync } from 'node:fs';
      const blob = execFileSync('git', ['hash-object', process.argv[1]], { encoding: 'utf8' }).trim();
      if (blob !== process.argv[5]) throw new Error('untrusted materialization');
      writeFileSync(process.env.ADMISSION_TRACE, JSON.stringify({ executable: process.argv[1], source: 'trusted-executed' }));
      console.log(JSON.stringify({ headSha: '${headSha}', validator: { path: '${validatorPath}', ref: 'origin/dev', blobSha: blob, version: 'fixture-1' } }));
    `);
    execFileSync('git', ['add', validatorPath], { cwd: candidateRoot });
    execFileSync('git', ['commit', '-m', 'trusted validator'], { cwd: candidateRoot, stdio: 'ignore' });
    execFileSync('git', ['branch', '-M', 'dev'], { cwd: candidateRoot });
    execFileSync('git', ['remote', 'add', 'origin', '.'], { cwd: candidateRoot });
    execFileSync('git', ['fetch', 'origin', 'dev:refs/remotes/origin/dev'], { cwd: candidateRoot, stdio: 'ignore' });
    writeFileSync(join(candidateRoot, validatorPath), `import { writeFileSync } from 'node:fs'; writeFileSync('${candidateMarker.replaceAll('\\', '\\\\')}', 'candidate-executed'); process.exit(88);`);
    process.env.ADMISSION_TRACE = traceFile;

    const result = await launchTrustedAdmission({
      repository: 'sabbour/agentweaver',
      prNumber: 1489,
      repositoryRoot: candidateRoot,
      command: async (file, args, options) => {
        if (file === 'gh') return { stdout: JSON.stringify({ headRefOid: headSha }) };
        return { stdout: execFileSync(file, args, { ...options, encoding: 'utf8' }) };
      },
    });

    const trace = JSON.parse(readFileSync(traceFile, 'utf8'));
    assert.equal(result.headSha, headSha);
    assert.equal(trace.source, 'trusted-executed');
    assert.equal(existsSync(candidateMarker), false);
    assert.equal(relative(candidateRoot, trace.executable).startsWith('..'), true);
    assert.equal(existsSync(trace.executable), false);
  } finally {
    if (previousTrace === undefined) delete process.env.ADMISSION_TRACE;
    else process.env.ADMISSION_TRACE = previousTrace;
    rmSync(fixture, { recursive: true, force: true });
  }
});
