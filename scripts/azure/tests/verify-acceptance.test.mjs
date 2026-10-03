import { test } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, existsSync } from 'node:fs';
import { checkBlobRoundtrip, checkDeployedSha, checkKeyVaultSecretVersion, checkMonitorTrace,
  checkServiceDigests, checkWorkloadIdentity, runAcceptance } from '../verify-acceptance.mjs';
import { fixture, source, fakeAzure, ids } from './fixtures/target.mjs';

test('deployment reader requires exact successful Bicep output receipt, not fictitious deployment tags', () => {
  const config = { ...fixture, sourceHash: source.sourceHash };
  assert.equal(checkDeployedSha(config, fakeAzure()).status, 'passed');
  for (const create of [
    { status: 0, stdout: JSON.stringify({ tags: { sourceSha: source.sha } }) },
    { status: 0, stdout: 'malformed' },
    { status: 1, stdout: '' },
    { status: 0, stdout: '{"properties":{"provisioningState":"Failed"}}' },
  ]) assert.equal(checkDeployedSha(config, fakeAzure({ create })).status, 'blocked');
});

test('issuer and exact subject/issuer/audience configuration never imply verified token exchange', () => {
  const check = { ...fixture, clusterName: 'aw-v1-p0-aks', identityChecks: [{
    identityName: 'aw-v1-p0-id-foundation-probe', federatedCredentialName: 'foundation-probe-workload-identity',
    expectedSubject: 'system:serviceaccount:agentweaver-v1-p0:foundation-probe',
  }] };
  const execAz = args => args[0] === 'aks' ? { status: 0, stdout: 'https://issuer.example/' } :
    { status: 0, stdout: JSON.stringify({ issuer: 'https://issuer.example/',
      subject: check.identityChecks[0].expectedSubject, audiences: ['api://AzureADTokenExchange'] }) };
  const exact = checkWorkloadIdentity(check, execAz);
  assert.equal(exact.status, 'blocked');
  assert.equal(exact.evidence.exact, true);
  assert.equal(exact.evidence.tokenExchangeVerified, false);
  assert.equal(checkWorkloadIdentity({ ...check, identityChecks: [] }, execAz).status, 'blocked');
  for (const bad of [
    { subject: 'system:serviceaccount:wrong:other' },
    { issuer: 'https://wrong.example/' },
    { audiences: ['wrong'] },
  ]) {
    const result = checkWorkloadIdentity(check, args => args[0] === 'aks' ? execAz(args) : {
      status: 0, stdout: JSON.stringify({ issuer: 'https://issuer.example/',
        subject: check.identityChecks[0].expectedSubject, audiences: ['api://AzureADTokenExchange'], ...bad }),
    });
    assert.equal(result.status, 'blocked');
    assert.equal(result.evidence?.exact, undefined);
  }
});

test('latest KV, exact operator KV and caller image strings are not runtime evidence', () => {
  assert.equal(checkKeyVaultSecretVersion({}).status, 'blocked');
  assert.equal(checkKeyVaultSecretVersion({ secretVersion: 'v1' }).status, 'blocked');
  assert.equal(checkServiceDigests({ services: { identity: `sha256:${'a'.repeat(64)}` } }).status, 'blocked');
});

const nonce = '11111111-2222-3333-4444-555555555555';
const startedAt = '2026-10-03T12:00:00.000Z';
const monitorConfig = { workspaceId: 'workspace', runId: nonce, expectedSha: source.sha, startedAt };
const clock = { now: () => Date.parse('2026-10-03T12:01:00.000Z') };
test('Monitor rejects custom/historical/malformed/injected evidence and missing SHA/nonce', () => {
  const called = () => { throw new Error('must not query'); };
  for (const config of [
    { ...monitorConfig, query: 'AppTraces | take 1' }, { ...monitorConfig, runId: '" | take 1' },
    { ...monitorConfig, expectedSha: undefined }, { ...monitorConfig, runId: undefined },
    { ...monitorConfig, startedAt: '2025-01-01' },
  ]) assert.equal(checkMonitorTrace(config, called, clock).status, 'blocked');
  for (const row of [{ Message: 'old' }, { TimeGenerated: '2025-01-01', Properties: { sourceSha: source.sha, nonce } },
    { TimeGenerated: startedAt, Properties: { sourceSha: source.sha, nonce: 'other' } }]) {
    assert.equal(checkMonitorTrace(monitorConfig, () => ({ status: 0, stdout: JSON.stringify([row]) }), clock).status, 'blocked');
  }
});

test('Monitor fixes query to fresh SHA plus nonce and validates returned row correlation', () => {
  let query;
  const result = checkMonitorTrace(monitorConfig, args => {
    query = args[args.indexOf('--analytics-query') + 1];
    return { status: 0, stdout: JSON.stringify([{ TimeGenerated: startedAt,
      Properties: { sourceSha: source.sha, nonce } }]) };
  }, clock);
  assert.equal(result.status, 'passed');
  assert.ok(query.includes(source.sha) && query.includes(nonce) && query.includes('TimeGenerated'));
  assert.ok(!query.includes('take 1'));
});

function blobFake({ upload = 'ok', cleanup = 'ok', generation = nonce } = {}, calls = []) {
  return args => {
    calls.push(args);
    if (args[2] === 'upload') {
      assert.ok(args.includes('--if-none-match') && args.includes('*'));
      assert.ok(args.includes(`generation=${nonce}`));
      if (upload === 'throw') throw new Error('upload timeout after write');
      return { status: upload === 'fail' ? 1 : 0, stdout: '', stderr: 'primary upload error' };
    }
    if (args[2] === 'download') {
      writeFileSync(args[args.indexOf('--file') + 1], `agentweaver-v1-p0:${nonce}`);
      return { status: 0 };
    }
    if (args[2] === 'show') return { status: 0, stdout: JSON.stringify({
      metadata: { generation }, properties: { etag: '"owned-etag"' },
    }) };
    if (args[2] === 'delete') {
      assert.equal(args[args.indexOf('--if-match') + 1], '"owned-etag"');
      return { status: cleanup === 'fail' ? 1 : 0, stderr: 'cleanup error' };
    }
    throw new Error('unexpected blob command');
  };
}

test('Blob cleanup runs in finally after upload failure/throw and preserves both errors', () => {
  for (const upload of ['fail', 'throw']) {
    const calls = [];
    const result = checkBlobRoundtrip({ accountName: 'owned', container: 'owned', executeProbes: true },
      blobFake({ upload, cleanup: 'fail' }, calls), { uuid: () => nonce });
    assert.equal(result.status, 'blocked');
    assert.ok(result.evidence.primaryFailure);
    assert.ok(result.evidence.cleanupFailure);
    assert.ok(calls.some(args => args[2] === 'show') && calls.some(args => args[2] === 'delete'));
    assert.ok(!calls.some(args => args[2] === 'download'));
    assert.equal(existsSync(`artifacts/azure/${nonce}`), false);
  }
});

test('Blob conditional cleanup never deletes another generation and cleanup failure cannot pass', () => {
  const calls = [];
  const mismatch = checkBlobRoundtrip({ executeProbes: true }, blobFake({ generation: 'other' }, calls), { uuid: () => nonce });
  assert.equal(mismatch.status, 'blocked');
  assert.ok(!calls.some(args => args[2] === 'delete'));
  const failure = checkBlobRoundtrip({ executeProbes: true }, blobFake({ cleanup: 'fail' }), { uuid: () => nonce });
  assert.equal(failure.status, 'blocked');
  assert.equal(failure.evidence.cleanupFailed, true);
  const good = checkBlobRoundtrip({ executeProbes: true }, blobFake(), { uuid: () => nonce });
  assert.equal(good.status, 'blocked');
  assert.equal(good.evidence.operatorOnly, true);
});

test('Blob default performs no write effects', () => {
  assert.equal(checkBlobRoundtrip({}, () => { throw new Error('unexpected'); }).status, 'blocked');
});

test('FULL REPORT regression: original false full success remains BLOCKED, even with plausible caller evidence', () => {
  const calls = [];
  const azure = fakeAzure({}, calls);
  const config = { ...fixture, deploymentName: 'candidate', clusterName: 'aw-v1-p0-aks',
    secretVersion: undefined, services: { broker: `sha256:${'a'.repeat(64)}` }, executeProbes: true,
    query: 'AppTraces | take 1', monitorQuery: 'AppTraces | take 1',
    runtimeEvidence: { verified: true, status: 'passed', sourceSha: source.sha } };
  const report = runAcceptance(config, { sourceResolver: () => source,
    uuid: () => nonce,
    execAz: args => args[0] === 'storage' ? blobFake({ cleanup: 'fail' })(args) : azure(args) });
  assert.equal(report.overall, 'blocked');
  assert.equal(report.scope, 'p0-integration');
  assert.equal(report.deployedAcceptance, false);
  assert.equal(report.candidate.sourceSha, source.sha);
  assert.equal(report.checks.find(check => check.name === 'blob-roundtrip').evidence.cleanupFailed, true);
  assert.match(report.checks.find(check => check.name === 'blob-roundtrip').evidence.cleanupFailure, /Conditional cleanup failed/);
  assert.equal(report.checks.find(check => check.name === 'workload-identity-oidc').evidence.exact, false);
  for (const name of ['key-vault-secret-version', 'service-image-digests', 'blob-roundtrip',
    'monitor-trace', 'runtime-workload-evidence']) assert.equal(report.checks.find(check => check.name === name).status, 'blocked');
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
});

test('acceptance account/source failure prevents probes and returns a structured block', () => {
  const calls = [];
  const report = runAcceptance(fixture, { sourceResolver: () => source,
    execAz: fakeAzure({ groupResult: { status: 1, stderr: '(AuthorizationFailed)' } }, calls) });
  assert.equal(report.overall, 'blocked');
  assert.ok(!calls.some(args => args[0] === 'deployment' || args[0] === 'storage'));
  assert.equal(report.checks[0].name, 'target-and-source');
});
