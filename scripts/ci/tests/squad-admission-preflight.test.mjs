import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveExternalStateDir } from '@bradygaster/squad-sdk';
import { KIND, runAdmissionPreflight, validateAdmissionPreflight } from '../squad-admission-preflight.mjs';
import { launchTrustedAdmission, resolveCanonicalExternalStateDir } from '../squad-admission-launcher.mjs';

const expected = { repository: 'sabbour/agentweaver', prNumber: 1489 };
const event = (state, headSha, extra = {}) => ({ state, actor: 'ralph', at: '2026-09-21T00:00:00Z', headSha, evidence: state, ...extra });
const ledger = (headSha, findings = []) => ({ kind: KIND, ...expected, headSha, findings });

test('admits empty, advisory, and fully resolved authoritative ledgers', () => {
  const headSha = 'a'.repeat(40);
  assert.equal(validateAdmissionPreflight(ledger(headSha), { ...expected, headSha }).admitted, true);
  assert.equal(validateAdmissionPreflight(ledger(headSha, [{ id: 'F-0', policy: 'advisory' }]), { ...expected, headSha }).findings, 1);
  const finding = { id: 'F-1', policy: 'required', transitions: [
    event('recorded', headSha), event('owned', headSha, { owner: 'neo', action: 'fix validator' }),
    event('corrected', headSha), event('revalidated', headSha, { validation: 'node --test focused suite' }), event('resolved', headSha),
  ] };
  assert.equal(validateAdmissionPreflight(ledger(headSha, [finding]), { ...expected, headSha }).findings, 1);
});

test('blocks unknown policies and incomplete required findings', () => {
  const headSha = 'a'.repeat(40);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [{ id: 'F-1' }]), { ...expected, headSha }), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [{ id: 'F-1', policy: 'blocking' }]), { ...expected, headSha }), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [{ id: 'F-1', policy: 'required', transitions: [] }]), { ...expected, headSha }), /incomplete/u);
});

test('reads only the launcher-attested canonical external state directory', async () => {
  const headSha = 'a'.repeat(40);
  const result = await runAdmissionPreflight('sabbour/agentweaver', 1489, {
    stateDirectory: 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver',
    headSha,
    readLedger: async (stateDirectory) => {
      assert.equal(stateDirectory, 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver');
      return ledger(headSha);
    },
    validator: { path: 'scripts/ci/squad-admission-preflight.mjs', ref: 'origin/dev', blobSha: 'b'.repeat(40), version: '2' },
  });
  assert.equal(result.headSha, headSha);
  await assert.rejects(() => runAdmissionPreflight('sabbour/agentweaver', 1489, {
    stateDirectory: 'C:\\candidate\\.squad',
    headSha,
    readLedger: async () => ledger('b'.repeat(40)),
  }), /stale/u);
});

test('materializes actual trusted Git bytes and ignores candidate validator, SDK, and local ledger', async () => {
  const fixture = join(dirname(fileURLToPath(import.meta.url)), `.admission-launcher-${process.pid}-${Date.now()}`);
  const candidateRoot = join(fixture, 'candidate');
  const stateRoot = join(fixture, 'state-root');
  const validatorPath = 'scripts/ci/squad-admission-preflight.mjs';
  const launcherPath = 'scripts/ci/squad-admission-launcher.mjs';
  const candidateMarker = join(fixture, 'candidate-validator-ran');
  const sdkMarker = join(fixture, 'candidate-sdk-ran');
  const previousAppData = process.env.APPDATA;
  try {
    mkdirSync(candidateRoot, { recursive: true });
    execFileSync('git', ['init'], { cwd: candidateRoot, stdio: 'ignore' });
    execFileSync('git', ['config', 'user.email', 'admission@example.test'], { cwd: candidateRoot });
    execFileSync('git', ['config', 'user.name', 'Admission test'], { cwd: candidateRoot });
    mkdirSync(dirname(join(candidateRoot, validatorPath)), { recursive: true });
    mkdirSync(dirname(join(candidateRoot, launcherPath)), { recursive: true });
    mkdirSync(join(candidateRoot, '.squad'), { recursive: true });
    writeFileSync(join(candidateRoot, validatorPath), readFileSync(new URL('../squad-admission-preflight.mjs', import.meta.url), 'utf8'));
    writeFileSync(join(candidateRoot, launcherPath), readFileSync(new URL('../squad-admission-launcher.mjs', import.meta.url), 'utf8'));
    writeFileSync(join(candidateRoot, '.squad', 'config.json'), JSON.stringify({
      version: 1, teamRoot: '.', projectKey: 'trusted-admission-state', stateLocation: 'external',
    }), { encoding: 'utf8' });
    execFileSync('git', ['add', validatorPath, launcherPath, '.squad/config.json'], { cwd: candidateRoot });
    execFileSync('git', ['commit', '-m', 'trusted admission sources'], { cwd: candidateRoot, stdio: 'ignore' });
    execFileSync('git', ['branch', '-M', 'dev'], { cwd: candidateRoot });
    execFileSync('git', ['remote', 'add', 'origin', '.'], { cwd: candidateRoot });
    execFileSync('git', ['fetch', 'origin', 'dev:refs/remotes/origin/dev'], { cwd: candidateRoot, stdio: 'ignore' });
    const headSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: candidateRoot, encoding: 'utf8' }).trim();

    writeFileSync(join(candidateRoot, validatorPath), `import { writeFileSync } from 'node:fs'; writeFileSync(${JSON.stringify(candidateMarker)}, 'executed'); process.exit(88);`);
    mkdirSync(join(candidateRoot, 'node_modules', '@bradygaster', 'squad-sdk'), { recursive: true });
    writeFileSync(join(candidateRoot, 'node_modules', '@bradygaster', 'squad-sdk', 'index.js'), `import { writeFileSync } from 'node:fs'; writeFileSync(${JSON.stringify(sdkMarker)}, 'executed');`);
    const candidateLedger = join(candidateRoot, '.squad', 'admission', 'findings', 'sabbour', 'agentweaver');
    mkdirSync(candidateLedger, { recursive: true });
    writeFileSync(join(candidateLedger, '1489.json'), JSON.stringify(ledger(headSha)));

    process.env.APPDATA = stateRoot;
    assert.equal(
      resolveCanonicalExternalStateDir('trusted-admission-state'),
      resolveExternalStateDir('trusted-admission-state', false),
      'the launcher must match the pinned SDK external-state resolver',
    );
    await assert.rejects(() => launchTrustedAdmission({
      repository: 'sabbour/agentweaver',
      prNumber: 1489,
      repositoryRoot: candidateRoot,
      command: async (file, args, options) => file === 'gh'
        ? { stdout: JSON.stringify({ headRefOid: headSha }) }
        : { stdout: execFileSync(file, args, { ...options, encoding: 'utf8' }) },
    }), /ENOENT/u, 'a candidate-local ledger cannot satisfy admission');

    const canonicalLedger = join(stateRoot, 'squad', 'projects', 'trusted-admission-state', 'admission', 'findings', 'sabbour', 'agentweaver');
    mkdirSync(canonicalLedger, { recursive: true });
    writeFileSync(join(canonicalLedger, '1489.json'), JSON.stringify(ledger(headSha)));
    const result = await launchTrustedAdmission({
      repository: 'sabbour/agentweaver',
      prNumber: 1489,
      repositoryRoot: candidateRoot,
      command: async (file, args, options) => file === 'gh'
        ? { stdout: JSON.stringify({ headRefOid: headSha }) }
        : { stdout: execFileSync(file, args, { ...options, encoding: 'utf8' }) },
    });

    assert.equal(result.headSha, headSha);
    assert.equal(result.validator.version, '2');
    assert.equal(existsSync(candidateMarker), false);
    assert.equal(existsSync(sdkMarker), false);
    assert.equal(relative(candidateRoot, result.stateDirectory).startsWith('..'), true);
  } finally {
    if (previousAppData === undefined) delete process.env.APPDATA;
    else process.env.APPDATA = previousAppData;
    rmSync(fixture, { recursive: true, force: true });
  }
});
