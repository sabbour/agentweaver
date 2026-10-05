import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { writeFileSync, existsSync } from 'node:fs';
import { checkAksNetworkSecurity, checkBlobRoundtrip, checkDeployedSha, checkKeyVaultSecretVersion, checkMonitorTrace,
  checkServiceDigests, checkWorkloadIdentity, runAcceptance } from '../verify-acceptance.mjs';
import { fixture, source, fakeAzure, ids, deploymentOutputs, observedCluster } from './fixtures/target.mjs';
import { makeRuntimeFixture, completedAt as probeCompletedAt } from './fixtures/foundation-probe-runtime.mjs';
import { postDeploymentAzure } from './fixtures/post-deployment.mjs';

test('AKS network preflight reads the exact observed cluster and reports enabled security configuration', () => {
  const calls = [];
  const check = checkAksNetworkSecurity({ ...fixture, clusterId: observedCluster.id },
    fakeAzure({}, calls));
  assert.equal(check.status, 'passed');
  assert.deepEqual(check.evidence, {
    clusterId: observedCluster.id,
    publicApi: true,
    apiServerFqdn: 'api.example.azmk8s.io',
    managedEntra: true,
    azureRbac: true,
    localAccountsDisabled: true,
    kubernetesVersion: '1.29.7',
    networkDataplane: 'cilium',
    advancedNetworkingEnabled: true,
    advancedNetworkingSecurityEnabled: true,
    apiVersion: '2024-09-01',
  });
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0].slice(0, 4), ['rest', '--method', 'get', '--url']);
  assert.equal(calls[0][4], `https://management.azure.com${observedCluster.id}?api-version=2024-09-01`);
});

test('AKS network preflight fails closed for missing target, failed/malformed GET, or unmet observed requirements', () => {
  const altered = ({ properties = {}, networkProfile = {}, ...resource } = {}) => ({
    ...observedCluster,
    ...resource,
    properties: { ...observedCluster.properties, ...properties,
      networkProfile: { ...observedCluster.properties.networkProfile, ...networkProfile } },
  });
  const rejected = [
    { status: 1, stdout: JSON.stringify(observedCluster) },
    { status: 0, stdout: 'malformed' },
    { status: 0, stdout: 'null' },
    { status: 0, stdout: JSON.stringify({ ...observedCluster, id: `${observedCluster.id}-other` }) },
    { status: 0, stdout: JSON.stringify(altered({ properties: { kubernetesVersion: '1.28.9' } })) },
    ...['not-a-version', '1.29.0-preview'].map(kubernetesVersion =>
      ({ status: 0, stdout: JSON.stringify(altered({ properties: { kubernetesVersion } })) })),
    { status: 0, stdout: JSON.stringify(altered({ networkProfile: { networkDataplane: 'azure' } })) },
    { status: 0, stdout: JSON.stringify(altered({ networkProfile: { advancedNetworking: undefined } })) },
    { status: 0, stdout: JSON.stringify(altered({ networkProfile: {
      advancedNetworking: { enabled: false, security: { enabled: true } },
    } })) },
    { status: 0, stdout: JSON.stringify(altered({ networkProfile: {
      advancedNetworking: { enabled: true, security: { enabled: false } },
    } })) },
    { status: 0, stdout: JSON.stringify(altered({ networkProfile: {
      advancedNetworking: { enabled: true },
    } })) },
    { status: 0, stdout: JSON.stringify(altered({ properties: {
      apiServerAccessProfile: { enablePrivateCluster: true },
    } })) },
    { status: 0, stdout: JSON.stringify(altered({ properties: { aadProfile: { managed: false, enableAzureRBAC: true } } })) },
    { status: 0, stdout: JSON.stringify(altered({ properties: { aadProfile: { managed: true, enableAzureRBAC: false } } })) },
    { status: 0, stdout: JSON.stringify(altered({ properties: { disableLocalAccounts: false } })) },
    { status: 0, stdout: JSON.stringify(altered({ properties: { fqdn: null } })) },
  ];
  for (const result of rejected) {
    assert.equal(checkAksNetworkSecurity({ ...fixture, clusterId: observedCluster.id }, () => result).status, 'blocked');
  }

  let queried = false;
  const missingTarget = checkAksNetworkSecurity({ ...fixture }, () => { queried = true; });
  assert.equal(missingTarget.status, 'blocked');
  assert.equal(queried, false);
  assert.equal(checkAksNetworkSecurity({ ...fixture, clusterId: `${observedCluster.id}-other` },
    () => { queried = true; }).status, 'blocked');
  assert.equal(queried, false);
  assert.equal(checkAksNetworkSecurity({ ...fixture, clusterId: observedCluster.id },
    () => { throw new Error('AuthorizationFailed'); }).status, 'blocked');
});

test('AKS resource IDs compare case-insensitively without widening target scope', () => {
  const id = observedCluster.id.replace('aw-v1-p0-aks', 'AW-V1-P0-AKS');
  const resource = { ...observedCluster, id: id.toUpperCase() };
  const result = checkAksNetworkSecurity({ ...fixture, clusterId: id },
    () => ({ status: 0, stdout: JSON.stringify(resource), stderr: '' }));
  assert.equal(result.status, 'passed');
  assert.equal(result.evidence.clusterId, resource.id);
});

test('deployment reader requires exact successful Bicep output receipt, not fictitious deployment tags', () => {
  const config = { ...fixture, sourceTree: source.sourceTree, sourceHash: source.sourceHash };
  assert.equal(checkDeployedSha(config, fakeAzure()).status, 'passed');
  for (const create of [
    { status: 0, stdout: JSON.stringify({ tags: { sourceSha: source.sha } }) },
    { status: 0, stdout: 'malformed' },
    { status: 1, stdout: '' },
    { status: 0, stdout: '{"properties":{"provisioningState":"Failed"}}' },
  ]) assert.equal(checkDeployedSha(config, fakeAzure({ create })).status, 'blocked');
});

test('acceptance receipt reader rejects missing tree, wrong deployment ID and cross-target output substitutions', () => {
  const valid = { id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${fixture.deploymentName}`,
    properties: { provisioningState: 'Succeeded', outputs: deploymentOutputs } };
  for (const change of [
    { sourceTree: { value: source.sourceHash } }, { sourceTree: undefined },
    { foundationProbeIdentity: undefined }, { monitorWorkspaceId: { value: fixture.groupId } },
    { foundationResources: { value: { ...deploymentOutputs.foundationResources.value,
      vaultUri: 'https://shared.vault.azure.net/' } } },
  ]) {
    const create = { status: 0, stdout: JSON.stringify({
      ...valid, properties: { ...valid.properties, outputs: { ...deploymentOutputs, ...change } },
    }) };
    assert.equal(checkDeployedSha({ ...fixture, ...source }, fakeAzure({ create })).status, 'blocked');
  }
  const create = { status: 0, stdout: JSON.stringify({ ...valid, id: `${valid.id}-other` }) };
  assert.equal(checkDeployedSha({ ...fixture, ...source }, fakeAzure({ create })).status, 'blocked');
});

test('acceptance rejects an AKS-only source-bound deployment receipt as a full-foundation receipt', () => {
  const valid = { id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${fixture.deploymentName}`,
    properties: { provisioningState: 'Succeeded', outputs: {
      sourceSha: { value: source.sha },
      sourceTree: { value: source.sourceTree },
      sourceHash: { value: source.sourceHash },
      clusterId: { value: observedCluster.id },
      clusterName: { value: 'aw-v1-p0-aks' },
      operatorRoleAssignmentId: { value: `${observedCluster.id}/providers/Microsoft.Authorization/roleAssignments/abc` },
      oidcIssuerUrl: { value: 'https://issuer.example/' },
    } } };
  const result = checkDeployedSha({ ...fixture, ...source }, fakeAzure({
    create: { status: 0, stdout: JSON.stringify(valid) },
  }));
  assert.equal(result.status, 'blocked');
  assert.match(result.reason, /AKS-only deployment receipt is not a full-foundation receipt/);
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
  assert.equal(exact.status, 'passed');
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

const nonce = '11111111222233334444555555555555';
const startedAt = '2026-10-03T12:00:00.000Z';
const completedAt = '2026-10-03T12:00:30.000Z';
const monitorConfig = { workspaceId: deploymentOutputs.monitorWorkspaceId.value,
  runId: nonce, expectedSha: source.sha, sourceTree: source.sourceTree,
  traceId: 'd'.repeat(32), spanId: 'e'.repeat(16), startedAt, completedAt };
const monitorRow = { TimeGenerated: startedAt, Name: 'foundation-probe',
  OperationId: monitorConfig.traceId, Id: monitorConfig.spanId,
  Properties: { 'probe.source_sha': source.sha, 'probe.source_tree': source.sourceTree, 'probe.nonce': nonce } };
const clock = { now: () => Date.parse('2026-10-03T12:01:00.000Z') };
test('Monitor rejects custom/historical/malformed/injected evidence and missing SHA/nonce', () => {
  const called = () => { throw new Error('must not query'); };
  for (const config of [
    { ...monitorConfig, query: 'AppTraces | take 1' }, { ...monitorConfig, runId: '" | take 1' },
    { ...monitorConfig, expectedSha: undefined }, { ...monitorConfig, runId: undefined },
    { ...monitorConfig, runId: '11111111-2222-3333-4444-555555555555' },
    { ...monitorConfig, sourceTree: source.sourceHash }, { ...monitorConfig, traceId: '0'.repeat(32) },
    { ...monitorConfig, spanId: '0'.repeat(16) }, { ...monitorConfig, workspaceId: fixture.groupId },
    { ...monitorConfig, startedAt: '2025-01-01' },
    { ...monitorConfig, completedAt: '2026-10-03T12:01:00.000Z' },
    { ...monitorConfig, completedAt: '2026-10-03T11:59:00.000Z' },
  ]) assert.equal(checkMonitorTrace(config, called, clock).status, 'blocked');
  for (const row of [{ Message: 'old' }, { ...monitorRow, TimeGenerated: '2025-01-01' },
    { ...monitorRow, TimeGenerated: '2026-10-03T12:02:00.000Z' },
    { ...monitorRow, Name: 'other' }, { ...monitorRow, OperationId: 'f'.repeat(32) },
    { ...monitorRow, Id: 'f'.repeat(16) }, { ...monitorRow, Properties: { sourceSha: source.sha, nonce } },
    { ...monitorRow, Properties: { ...monitorRow.Properties, 'probe.source_tree': 'f'.repeat(40) } },
    { ...monitorRow, Properties: { ...monitorRow.Properties, 'probe.nonce': 'other' } }]) {
    assert.equal(checkMonitorTrace(monitorConfig, () => ({ status: 0, stdout: JSON.stringify([row]) }), clock).status, 'blocked');
  }
});

test('Monitor fixes query to fresh SHA plus nonce and validates returned row correlation', () => {
  let query;
  const result = checkMonitorTrace(monitorConfig, args => {
    query = args[args.indexOf('--analytics-query') + 1];
    return { status: 0, stdout: JSON.stringify([monitorRow]) };
  }, clock);
  assert.equal(result.status, 'passed');
  assert.ok(query.includes(source.sha) && query.includes(nonce) && query.includes('TimeGenerated'));
  assert.ok(!query.includes('take 1'));
  assert.ok(query.startsWith('union AppDependencies, AppRequests'));
  assert.ok(query.includes('probe.source_tree') && query.includes(monitorConfig.traceId) && query.includes(monitorConfig.spanId));
  assert.doesNotMatch(query, /AppTraces|Properties\.sourceSha/);
});

test('Monitor transport failures remain structured and redact diagnostics', () => {
  const thrown = checkMonitorTrace(monitorConfig, () => {
    throw new Error('Bearer monitor-token-sentinel');
  }, clock);
  assert.equal(thrown.status, 'blocked');
  assert.equal(thrown.scope, 'integration');
  assert.doesNotMatch(JSON.stringify(thrown), /monitor-token-sentinel/);

  const failed = checkMonitorTrace(monitorConfig, () => ({
    status: 1, stdout: '', stderr: 'InstrumentationKey=monitor-secret-sentinel',
  }), clock);
  assert.equal(failed.status, 'blocked');
  assert.doesNotMatch(JSON.stringify(failed), /monitor-secret-sentinel/);
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
  const azure = fakeAzure(postDeploymentAzure, calls);
  const config = { ...fixture, clusterName: 'aw-v1-p0-aks',
    secretVersion: undefined, services: { broker: `sha256:${'a'.repeat(64)}` }, executeProbes: true,
    query: 'AppTraces | take 1', monitorQuery: 'AppTraces | take 1',
    runtimeEvidence: { verified: true, status: 'passed', sourceSha: source.sha } };
  const report = runAcceptance(config, { sourceResolver: () => source,
    uuid: () => nonce,
    execAz: (args, options) => args[0] === 'storage' ? blobFake({ cleanup: 'fail' })(args) : azure(args, options) });
  assert.equal(report.overall, 'blocked');
  assert.equal(report.scope, 'p0-integration');
  assert.equal(report.deployedAcceptance, false);
  assert.equal(report.candidate.sourceSha, source.sha);
  assert.equal(report.checks.find(check => check.name === 'blob-roundtrip').evidence.cleanupFailed, true);
  assert.match(report.checks.find(check => check.name === 'blob-roundtrip').evidence.cleanupFailure, /Conditional cleanup failed/);
  const identity = report.checks.find(check => check.name === 'workload-identity-oidc');
  assert.equal(report.checks.find(check => check.name === 'aks-observed-network-security').status, 'passed');
  assert.equal(identity.evidence.exact, true);
  assert.equal(identity.evidence.tokenExchangeVerified, false);
  assert.equal(identity.status, 'passed');
  for (const name of ['key-vault-secret-version', 'service-image-digests', 'blob-roundtrip',
    'monitor-trace', 'runtime-workload-evidence']) assert.equal(report.checks.find(check => check.name === name).status, 'blocked');
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
});

test('full report overrides caller federation checks with exact bound foundation-probe configuration', () => {
  const valid = { issuer: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/',
    subject: 'system:serviceaccount:agentweaver-v1-p0:foundation-probe', audiences: ['api://AzureADTokenExchange'] };
  const json = value => ({ status: 0, stdout: JSON.stringify(value), stderr: '' });
  for (const overrides of [
    {},
    { federationResult: json({ ...valid, issuer: 'https://wrong.example/' }) },
    { federationResult: json({ ...valid, issuer: undefined }) },
    { federationResult: json({ ...valid, subject: 'system:serviceaccount:wrong:other' }) },
    { federationResult: json({ ...valid, subject: undefined }) },
    { federationResult: json({ ...valid, audiences: ['wrong'] }) },
    { federationResult: json({ ...valid, audiences: ['api://AzureADTokenExchange', 'wrong'] }) },
    { federationResult: json({ ...valid, audiences: undefined }) },
    { federationResult: { status: 1, stdout: JSON.stringify(valid), stderr: 'AuthorizationFailed' } },
    { federationResult: { status: 0, stdout: 'malformed', stderr: '' } },
    { issuerResult: { status: 1, stdout: valid.issuer, stderr: 'AuthorizationFailed' } },
    { issuerResult: { status: 0, stdout: '', stderr: '' } },
  ]) {
    const calls = [];
    const report = runAcceptance({ ...fixture, identityChecks: [{ identityName: 'wrong', expectedSubject: 'wrong' }] },
      { sourceResolver: () => source, execAz: fakeAzure({ ...postDeploymentAzure, ...overrides }, calls) });
    const identity = report.checks.find(check => check.name === 'workload-identity-oidc');
    assert.equal(report.overall, 'blocked');
    assert.equal(report.deployedAcceptance, false);
    assert.equal(identity.status, Object.keys(overrides).length === 0 ? 'passed' : 'blocked');
    if (Object.keys(overrides).length === 0) {
      assert.equal(identity.evidence.exact, true);
      assert.equal(identity.evidence.tokenExchangeVerified, false);
    } else assert.notEqual(identity.evidence?.exact, true);
    const queries = calls.filter(args => args[0] === 'identity');
    if (overrides.issuerResult) assert.equal(queries.length, 0);
    else {
      assert.equal(queries.length, 1);
      const args = queries[0];
      assert.deepEqual(args.slice(0, 3), ['identity', 'federated-credential', 'show']);
      for (const [flag, value] of [['--identity-name', 'aw-v1-p0-id-foundation-probe'],
        ['--name', 'foundation-probe-workload-identity'], ['--resource-group', fixture.resourceGroup],
        ['--subscription', ids.subscriptionId]]) assert.equal(args[args.indexOf(flag) + 1], value);
    }
    assert.ok(!calls.some(args => args.includes('create') || args.includes('delete')));
  }
});

test('acceptance account/source failure prevents probes and returns a structured block', () => {
  const calls = [];
  const report = runAcceptance(fixture, { sourceResolver: () => source,
    execAz: fakeAzure({ groupResult: { status: 1, stderr: '(AuthorizationFailed)' } }, calls) });
  assert.equal(report.overall, 'blocked');
  assert.ok(!calls.some(args => args[0] === 'deployment' || args[0] === 'storage'));
  assert.equal(report.checks[0].name, 'target-and-source');
});

test('AKS preflight does not query an ID when the deployment receipt is not successful', () => {
  const calls = [];
  const report = runAcceptance(fixture, { sourceResolver: () => source,
    execAz: fakeAzure({ ...postDeploymentAzure,
      create: { status: 1, stdout: '', stderr: 'DeploymentNotFound' } }, calls) });
  assert.equal(report.overall, 'blocked');
  assert.equal(report.checks.find(check => check.name === 'target-inventory').status, 'passed');
  assert.equal(report.checks.find(check => check.name === 'deployed-sha').status, 'blocked');
  assert.equal(report.checks.find(check => check.name === 'aks-observed-network-security').status, 'blocked');
  assert.ok(!calls.some(args => args[0] === 'rest'));
  assert.equal(report.checks.find(check => check.name === 'runtime-workload-evidence').status, 'blocked');
});

test('only independently collected complete runtime evidence can pass deployed acceptance', () => {
  const collect = runtime => {
    const receipt = runtime.state.probeReceipt;
    const monitorRow = {
      TimeGenerated: receipt.telemetry.startedAt,
      Name: 'foundation-probe',
      OperationId: receipt.telemetry.traceId,
      Id: receipt.telemetry.spanId,
      Properties: {
        'probe.source_sha': source.sha,
        'probe.source_tree': source.sourceTree,
        'probe.nonce': receipt.nonce,
      },
    };
    const calls = [];
    const report = runAcceptance({
      ...fixture,
      expectedSha: source.sha,
      deploymentName: fixture.deploymentName,
      collectRuntimeEvidence: true,
      kubeContext: runtime.options.kubeContext,
      imageReference: runtime.options.imageReference,
      imageReceiptPath: runtime.options.imageReceiptPath,
      workspaceId: deploymentOutputs.monitorWorkspaceId.value,
      runtimeEvidence: { verified: true },
    }, {
      sourceResolver: () => source,
      execAz: fakeAzure({ ...postDeploymentAzure,
        monitorResult: { status: 0, stdout: JSON.stringify([monitorRow]), stderr: '' } }, calls),
      execKubectl: runtime.execKubectl,
      verifyImage: runtime.verifyImage,
      now: () => Date.parse('2026-10-03T12:02:00.000Z'),
    });
    return { report, calls };
  };
  const runtime = makeRuntimeFixture();
  const { report, calls } = collect(runtime);

  assert.equal(report.overall, 'passed');
  assert.equal(report.deployedAcceptance, true);
  assert.equal(report.checks.find(check => check.name === 'runtime-workload-evidence').status, 'passed');
  assert.equal(report.checks.find(check => check.name === 'monitor-trace').status, 'passed');
  assert.equal(report.checks.find(check => check.name === 'monitor-trace').evidence.completedAt, probeCompletedAt);
  assert.equal(report.checks.some(check => check.name === 'service-image-digests'), false);
  assert.equal(report.checks.find(check => check.name === 'blob-roundtrip').scope, 'diagnostic');
  assert.equal(report.checks.find(check => check.name === 'blob-roundtrip').status, 'not-run');
  assert.ok(calls.some(args => args[0] === 'monitor' && args.includes('--analytics-query')));

  const staleRun = makeRuntimeFixture();
  staleRun.state.probeReceipt.telemetry.startedAt = '2026-10-03T11:55:00.000Z';
  const stale = collect(staleRun);
  assert.equal(stale.report.deployedAcceptance, false);
  assert.equal(stale.report.checks.find(check => check.name === 'foundation-probe-receipt').status, 'blocked');
  assert.equal(stale.report.checks.find(check => check.name === 'monitor-trace').status, 'blocked');
  assert.equal(stale.calls.some(args => args.includes('--analytics-query')), false);
});

test('CLI rejects mutation and caller-asserted runtime proof options before any target command', () => {
  const script = fileURLToPath(new URL('../verify-acceptance.mjs', import.meta.url));
  for (const option of ['--execute', '--run-id=11111111222233334444555555555555', '--trace-id=' + 'd'.repeat(32)]) {
    const result = spawnSync(process.execPath, [script, option], { cwd: process.cwd(), encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Unknown option/);
    assert.doesNotMatch(result.stdout, /"deployedAcceptance": true/);
  }
});
