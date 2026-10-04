import assert from 'node:assert/strict';
import test from 'node:test';
import { collectFoundationProbeEvidence } from '../lib/foundation-probe-runtime.mjs';
import {
  completedAt, finishedAt, makeRuntimeFixture, probeNonce, startedAt,
} from './fixtures/foundation-probe-runtime.mjs';

function collect(runtime, overrides = {}) {
  return collectFoundationProbeEvidence(runtime.options, {
    execKubectl: runtime.execKubectl,
    verifyImage: runtime.verifyImage,
    now: () => Date.parse('2026-10-03T12:02:00.000Z'),
    ...overrides,
  });
}

function check(result, name) {
  return result.checks.find(item => item.name === name);
}

test('independent Job, pod, target, identity, registry, and native probe receipt form complete runtime evidence', () => {
  const runtime = makeRuntimeFixture();
  const result = collect(runtime);
  assert.deepEqual(result.checks.map(item => item.status),
    ['passed', 'passed', 'passed', 'passed', 'passed', 'passed', 'passed']);
  assert.equal(result.probeReceipt.nonce, probeNonce);
  assert.equal(result.completedAt, completedAt);
  assert.deepEqual(check(result, 'foundation-probe-job-pod').evidence, {
    jobUid: '11111111-1111-1111-1111-111111111111',
    podUid: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
    ownerJobUid: '11111111-1111-1111-1111-111111111111',
    namespace: 'agentweaver-v1-p0',
    serviceAccountName: 'foundation-probe',
    startedAt,
    completedAt,
    containerStartedAt: startedAt,
    containerFinishedAt: finishedAt,
    exitCode: 0,
    activeDeadlineSeconds: 420,
  });
  assert.equal(check(result, 'foundation-probe-registry-image').evidence.image,
    'registry.example/agentweaver/foundation-probe@sha256:' + 'd'.repeat(64));
  assert.equal(check(result, 'foundation-probe-registry-image').evidence.localConfigDigest,
    'sha256:' + 'c'.repeat(64));
  assert.equal(check(result, 'foundation-probe-registry-image').evidence.registryManifestDigest,
    'sha256:' + 'd'.repeat(64));
  assert.equal(check(result, 'foundation-probe-workload-identity').evidence.audience,
    'api://AzureADTokenExchange');
  assert.equal(check(result, 'foundation-probe-workload-identity').scope, 'configuration');
  assert.equal(check(result, 'foundation-probe-workload-identity-exchange').scope, 'integration');
  assert.ok(runtime.calls.every(({ args }) => args[0] === 'config' || args[0] === 'get' || args[0] === 'logs'));
  assert.ok(runtime.calls.every(({ args }) => args.includes('--context') && args.includes('aw-v1-p0')));
  assert.ok(runtime.calls.every(({ args }) => !args.includes('exec') && !args.includes('apply') &&
    !args.includes('delete') && !args.includes('create')));
});

test('incomplete or failed Job and nonzero process exit remain blocked', () => {
  for (const mutate of [
    runtime => { runtime.state.job.status.succeeded = 0; },
    runtime => { runtime.state.job.status.conditions[0].status = 'False'; },
    runtime => { runtime.state.job.status.failed = 1; },
    runtime => { runtime.state.pod.status.containerStatuses[0].state.terminated.exitCode = 17; },
    runtime => { runtime.state.pod.status.containerStatuses[0].state.terminated.reason = 'Error'; },
  ]) {
    const runtime = makeRuntimeFixture();
    mutate(runtime);
    const result = collect(runtime);
    assert.equal(check(result, 'foundation-probe-job-pod').status, 'blocked');
    assert.equal(check(result, 'foundation-probe-receipt').status, 'blocked');
    assert.equal(runtime.calls.some(({ args }) => args[0] === 'logs'), true);
    assert.equal(check(result, 'foundation-probe-failure-diagnostic').status, 'captured');
  }
  const wrongOwner = makeRuntimeFixture();
  wrongOwner.state.pod.metadata.ownerReferences[0].uid = 'other-job-uid';
  const result = collect(wrongOwner);
  assert.equal(check(result, 'foundation-probe-job-pod').status, 'blocked');
  assert.equal(wrongOwner.calls.some(({ args }) => args[0] === 'logs'), false);
});

test('Job and observed pod cannot override the verified image entrypoint or execution arguments', () => {
  for (const mutate of [
    runtime => { runtime.state.job.spec.template.spec.containers[0].command = ['/bin/sh', '-c']; },
    runtime => { runtime.state.pod.spec.containers[0].command = ['/bin/sh', '-c']; },
    runtime => { runtime.state.pod.spec.containers[0].args = ['--plan']; },
  ]) {
    const runtime = makeRuntimeFixture();
    mutate(runtime);
    const result = collect(runtime);
    assert.equal(check(result, 'foundation-probe-job-pod').status, 'blocked');
    assert.equal(check(result, 'foundation-probe-receipt').status, 'blocked');
    assert.equal(runtime.calls.some(({ args }) => args[0] === 'logs'), false);
  }
});

test('pod UID and owner must independently bind exactly one pod to the completed Job', () => {
  for (const mutate of [
    runtime => { runtime.state.pod.metadata.ownerReferences[0].uid = 'other-job-uid'; },
    runtime => { runtime.state.pod.metadata.ownerReferences[0].name = 'other-job'; },
    runtime => { runtime.state.pod.metadata.uid = ''; },
    runtime => { runtime.state.pod.metadata.namespace = 'other'; },
    runtime => { runtime.state.pod.status.phase = 'Running'; },
    runtime => { runtime.state.pod.spec.serviceAccountName = 'other'; },
    runtime => { runtime.state.job.spec.activeDeadlineSeconds = 421; },
    runtime => { runtime.state.job.status.completionTime = '2026-10-03T12:08:00.000Z'; },
  ]) {
    const runtime = makeRuntimeFixture();
    mutate(runtime);
    const result = collect(runtime);
    assert.equal(check(result, 'foundation-probe-job-pod').status, 'blocked');
  }
});

test('target configuration and projected workload identity must match the admitted identity', () => {
  for (const mutate of [
    runtime => { runtime.state.configMap.immutable = false; },
    runtime => { runtime.state.configMap.data['target.json'] = JSON.stringify({
      ...JSON.parse(runtime.state.configMap.data['target.json']), sourceSha: 'f'.repeat(40),
    }); },
    runtime => { runtime.state.serviceAccount.metadata.annotations['azure.workload.identity/client-id'] = 'wrong'; },
    runtime => { runtime.state.pod.spec.volumes = runtime.state.pod.spec.volumes.filter(volume => !volume.projected); },
    runtime => { runtime.state.pod.spec.volumes.find(volume => volume.projected).projected.sources[0].serviceAccountToken.audience = 'wrong'; },
    runtime => { runtime.state.pod.spec.containers[0].env.find(entry => entry.name === 'AZURE_TENANT_ID').value = 'wrong'; },
  ]) {
    const runtime = makeRuntimeFixture();
    mutate(runtime);
    const result = collect(runtime);
    if (!runtime.state.configMap.immutable) {
      assert.equal(check(result, 'foundation-probe-target').status, 'blocked');
      assert.equal(check(result, 'foundation-probe-target').scope, 'configuration');
    } else if (runtime.state.configMap.data['target.json'].includes(`"${'f'.repeat(40)}"`)) {
      assert.equal(check(result, 'foundation-probe-target').status, 'blocked');
    } else {
      assert.equal(check(result, 'foundation-probe-workload-identity').status, 'blocked');
    }
  }
});

test('registry manifest image must match source labels, the Job, and the pod pulled image ID', () => {
  const wrongPodImage = makeRuntimeFixture();
  wrongPodImage.state.pod.status.containerStatuses[0].imageID = `containerd://sha256:${'c'.repeat(64)}`;
  let result = collect(wrongPodImage);
  assert.equal(check(result, 'foundation-probe-registry-image').status, 'blocked');
  assert.equal(result.probeReceipt, undefined);

  const wrongJobImage = makeRuntimeFixture();
  wrongJobImage.state.job.spec.template.spec.containers[0].image = 'registry.example/other@sha256:' + 'd'.repeat(64);
  result = collect(wrongJobImage);
  assert.equal(check(result, 'foundation-probe-registry-image').status, 'blocked');

  const wrongProvenance = makeRuntimeFixture();
  wrongProvenance.state.localImageReceipt.sourceTree = 'f'.repeat(40);
  result = collect(wrongProvenance);
  assert.equal(check(result, 'foundation-probe-registry-image').status, 'blocked');

  const credentialReference = makeRuntimeFixture();
  credentialReference.options.imageReference =
    'https://user:credential-sentinel@registry.example/agentweaver/foundation-probe:tag';
  result = collect(credentialReference);
  assert.equal(check(result, 'foundation-probe-registry-image').status, 'blocked');
  assert.doesNotMatch(JSON.stringify(check(result, 'foundation-probe-registry-image')), /credential-sentinel/);
  assert.equal(credentialReference.calls.some(({ args }) => args[0] === 'logs'), false);
});

test('probe receipt must correlate source, Git tree, nonce, issuer, and audience', () => {
  for (const mutate of [
    runtime => { runtime.state.probeReceipt.sourceSha = 'f'.repeat(40); },
    runtime => { runtime.state.probeReceipt.sourceTree = 'f'.repeat(40); },
    runtime => { runtime.state.probeReceipt.nonce = 'f'.repeat(32); },
    runtime => { runtime.state.probeReceipt.telemetry.startedAt = '2026-10-03T11:55:00.000Z'; },
    runtime => { runtime.state.probeReceipt.workloadIdentity.issuer = 'https://wrong.example/'; },
    runtime => { runtime.state.probeReceipt.workloadIdentity.audience = 'wrong'; },
  ]) {
    const runtime = makeRuntimeFixture();
    mutate(runtime);
    const result = collect(runtime);
    assert.equal(check(result, 'foundation-probe-receipt').status, 'blocked');
  }
});

test('exact Key Vault version, provider pins, owned Blob cleanup, and PG outcomes are mandatory', () => {
  for (const mutate of [
    runtime => { runtime.state.probeReceipt.keyVault.redeemed = false; },
    runtime => { runtime.state.probeReceipt.keyVault.secretVersion = 'latest'; },
    runtime => { runtime.state.probeReceipt.providerBindings[0].resourceId = 'other'; },
    runtime => { runtime.state.probeReceipt.blob.cleanupConfirmed = false; },
    runtime => { runtime.state.probeReceipt.blob.eTag = ''; },
    runtime => { runtime.state.probeReceipt.postgres.inboxDisposition = 'Duplicate'; },
    runtime => { runtime.state.probeReceipt.postgres.transactionCommitted = false; },
    runtime => { runtime.state.probeReceipt.postgres.outboxSequence = 0; },
  ]) {
    const runtime = makeRuntimeFixture();
    mutate(runtime);
    const result = collect(runtime);
    assert.equal(check(result, 'foundation-probe-receipt').status, 'blocked');
    assert.equal(check(result, 'foundation-probe-receipt').scope, 'integration');
  }
});

test('failed probe logs retain safe operation and cleanup codes without including credential text', () => {
  const runtime = makeRuntimeFixture();
  runtime.state.job.status.succeeded = 0;
  runtime.state.job.status.failed = 1;
  runtime.state.job.status.conditions = [{ type: 'Failed', status: 'True', reason: 'BackoffLimitExceeded' }];
  runtime.state.pod.status.phase = 'Failed';
  runtime.state.pod.status.containerStatuses[0].state.terminated.exitCode = 1;
  runtime.state.pod.status.containerStatuses[0].state.terminated.reason = 'Error';
  runtime.state.probeReceipt = {
    status: 'failed',
    code: 'blob_operation_and_cleanup_failed',
    failureType: 'ProbeException',
    diagnostic: 'Password=do-not-report',
  };

  const result = collect(runtime);
  const diagnostic = check(result, 'foundation-probe-failure-diagnostic');
  assert.equal(check(result, 'foundation-probe-job-pod').status, 'blocked');
  assert.equal(check(result, 'foundation-probe-receipt').status, 'blocked');
  assert.deepEqual(diagnostic.evidence.failures, [{
    code: 'blob_operation_and_cleanup_failed',
    failureType: 'ProbeException',
  }]);
  assert.doesNotMatch(JSON.stringify(diagnostic), /do-not-report/);
});

test('explicit context must resolve to the observed AKS API server and failures are redacted', () => {
  const wrongContext = makeRuntimeFixture();
  wrongContext.state.kubeConfig.clusters[0].cluster.server = 'https://other.example/';
  let result = collect(wrongContext);
  assert.equal(check(result, 'kubernetes-target').status, 'blocked');
  assert.equal(wrongContext.calls.filter(({ args }) => args[0] === 'get').length, 0);

  const missingContext = makeRuntimeFixture();
  missingContext.options.kubeContext = undefined;
  result = collect(missingContext);
  assert.equal(check(result, 'kubernetes-target').status, 'blocked');

  const failure = makeRuntimeFixture();
  failure.execKubectl = () => ({ status: 1, stderr: 'Bearer eyJabcdefghij.eyJabcdefghij.abcdefghijklmno InstrumentationKey=private-key' });
  result = collect(failure);
  const reason = check(result, 'kubernetes-target').reason;
  assert.doesNotMatch(reason, /eyJabcdefghij|private-key/);
  assert.match(reason, /\[redacted\]/);
});
