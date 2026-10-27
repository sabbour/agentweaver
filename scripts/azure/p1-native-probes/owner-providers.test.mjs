import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { createP1NativeProviders } from './owner-providers.mjs';

const id = '11111111-1111-4111-8111-111111111111';
const hash = 'a'.repeat(64);
const past = '2026-01-01T00:00:00Z';
const future = '2099-01-01T00:00:00Z';
const actor = { issuer: 'https://issuer.example.test', subject: 'actor', tenantId: 'tenant' };
const input = { nativeOwner: { runtimeInstanceId: id }, pins: { context: { clusterIdentity: { sha256: hash } } } };

// Controlled owner responses exercise the real wire shapes; they are not live receipts.
function observations() {
  const binding = {
    actorIssuer: actor.issuer, actorId: actor.subject, tenantId: actor.tenantId,
    projectId: 'project', runId: 'run', sessionId: 'root', environmentId: 'environment',
    profileId: 'profile', placementUid: 'claim-uid', placementProviderId: 'agent-sandbox',
    executionFence: 1, placementGeneration: 2, environmentLifecycleGeneration: 3,
    environmentCurrentFencingGeneration: 4, environmentProviderFencingGeneration: 5,
    environmentLeaseRevision: 6, acceptedSelectionHash: hash,
    configureEndpoint: 'https://never-follow.example.test', modelCredentialReference: { secret: 'never-output' },
  };
  const registration = { runtimeInstanceId: id, revision: 1, binding, state: 'active', expiresAt: future };
  const provenance = {
    contractVersion: 1, coreInvocationId: id, setupInvocationId: id, setupReceiptHash: hash,
    tenantId: actor.tenantId, projectId: 'project', runId: 'run', rootSessionId: 'root',
    actorIssuer: actor.issuer, actorSubject: actor.subject, membershipId: id,
    membershipRevision: 1, acceptedSelectionHash: hash, executionFence: 1,
    rootStateVersion: 1, acceptedAt: past,
  };
  const pod = {
    contractVersion: 1, observedAt: past,
    environmentFence: {
      owner: { tenantId: 'tenant', projectId: 'project', runId: 'run', environmentId: 'environment' },
      lifecycleGeneration: 3,
    },
    resource: { seam: 'sandbox', providerId: 'agent-sandbox', resourceId: 'claim-uid', generation: 2 },
    fencingGeneration: 5, provisionOperationId: id, clusterIdentity: hash, namespace: 'namespace',
    claimUid: 'claim-uid', sandboxUid: 'sandbox-cr-uid', podName: 'host', podUid: 'actual-pod-uid',
    podResourceVersion: '77', podGeneration: 1, podPhase: 'Running',
    workspaceAttachment: {
      namespace: 'namespace', claimName: 'workspace', claimUid: 'pvc-uid', mountPath: '/workspace', readOnly: false,
    },
    isolationConfigurationVerified: true,
  };
  const evidence = {
    contractVersion: 1, observedAt: past,
    readiness: { contractVersion: 1, runtimeInstanceId: id, registrationRevision: 1, executionFence: 1 },
    environmentPodObservation: structuredClone(pod), environmentPodObservationHash: hash,
    workspaceMount: {
      contractVersion: 1, observedAt: past, podUid: pod.podUid, mountPath: '/workspace',
      mountId: 99, deviceMajor: 0, deviceMinor: 43, directoryInode: 100,
      fileSystemType: 'cifs', readOnly: false, mountInfoSha256: hash,
    },
    registrationHash: hash, sourceHash: hash, nativeSessionHash: hash,
  };
  return { registration, provenance, pod, evidence };
}

function setup(t, mutate = () => {}) {
  const values = observations();
  const calls = [];
  const counts = {};
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    calls.push({ url: url.href, init });
    const path = url.pathname;
    const kind = path.includes('/registrations/') ? 'registration'
      : path.endsWith('/p1-invocation-binding') ? 'provenance'
        : path.endsWith('/pod-observation') ? 'pod' : 'evidence';
    const count = counts[kind] = (counts[kind] ?? 0) + 1;
    const body = structuredClone(values[kind]);
    mutate(body, kind, count);
    return new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
  });
  const owner = name => ({
    origin: `https://${name}.example.test`, tenantId: actor.tenantId,
    async getAuthorization(origin) {
      assert.equal(origin, `https://${name}.example.test`);
      return 'Bearer in-memory-only';
    },
  });
  const trusted = {
    actor, core: owner('core'), environment: owner('environment'), host: owner('host'),
    authProvider: { async getValidatedToken() { throw new Error('must-not-acquire'); } },
  };
  return { providers: createP1NativeProviders({ input }, trusted), trusted, values, calls };
}

test('real owner route and C# record contracts stay aligned without database or cluster fixtures', async () => {
  const source = async path => readFile(new URL(`../../../${path}`, import.meta.url), 'utf8');
  const [core, environment, host, contracts, pod, provenance] = await Promise.all([
    source('services/orchestrator/Agentweaver.Orchestrator/ProjectRunEndpoints.cs'),
    source('services/environment/Agentweaver.Environment/EnvironmentEndpoints.cs'),
    source('packages/Agentweaver.AgentRuntime/RuntimeAgentHostEndpoints.cs'),
    source('packages/Agentweaver.Identity/RuntimeHostContracts.cs'),
    source('packages/Agentweaver.Abstractions/SandboxPodObservation.cs'),
    source('packages/Agentweaver.Abstractions/P1InvocationBinding.cs'),
  ]);
  assert.ok(core.includes('/internal/projects/{projectId}/runs/{runId}/p1-invocation-binding'));
  assert.ok(environment.includes('MapRuntimePlacement("/pod-observation")'));
  assert.ok(environment.includes('/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}'));
  assert.ok(host.includes('/runtime/v1/readiness/evidence'));
  for (const ownerProgram of [
    'services/orchestrator/Agentweaver.Orchestrator/Program.cs',
    'services/environment/Agentweaver.Environment/Program.cs',
    'services/agenthost/Agentweaver.AgentHost/Program.cs',
  ]) {
    assert.match(await source(ownerProgram), /JsonStringEnumConverter\(\s*System\.Text\.Json\.JsonNamingPolicy\.CamelCase/);
  }
  const values = observations();
  for (const [sourceText, name, value] of [
    [contracts, 'RuntimeHostReadinessEvidenceReceipt', values.evidence],
    [contracts, 'RuntimeHostWorkspaceMountEvidence', values.evidence.workspaceMount],
    [pod, 'SandboxPodObservation', values.pod],
    [provenance, 'P1InvocationBinding', values.provenance],
  ]) {
    const declaration = sourceText.match(new RegExp(`record ${name}\\(([\\s\\S]*?)\\)`))?.[1];
    assert.ok(declaration, name);
    const fields = declaration.split(',').map(field => {
      const property = field.trim().split(/\s+/).at(-1);
      return property[0].toLowerCase() + property.slice(1);
    });
    assert.deepEqual(Object.keys(value).sort(), fields.sort(), name);
  }
});

test('joins current owner observations but missing Environment creation read stays blocked', async t => {
  const { providers, calls } = setup(t);
  const result = await providers.nativeAuthorityProvider.readCurrent();
  assert.equal(result.status, 'blocked');
  assert.equal(result.code, 'environment_creation_binding_read_contract_missing');
  assert.notEqual(result.partialObservation.claimUid, result.partialObservation.podUid);
  assert.doesNotMatch(JSON.stringify(result), /never-output|in-memory-only|never-follow/);
  assert.equal(calls.length, 7);
  assert.deepEqual(calls.map(call => new URL(call.url).hostname), [
    'core.example.test', 'core.example.test', 'environment.example.test', 'host.example.test',
    'environment.example.test', 'core.example.test', 'core.example.test',
  ]);
  for (const { init } of calls) {
    assert.equal(init.method, 'GET');
    assert.equal(init.redirect, 'error');
    assert.equal(init.cache, 'no-store');
    assert.equal(init.headers['X-Agentweaver-Tenant'], 'tenant');
    assert.ok(init.signal instanceof AbortSignal);
  }
  assert.equal((await providers.journeyAuthorizer.authorize()).allowed, false);
  await assert.rejects(providers.runtimeReadinessProvider.readCurrent, /authority_join_incomplete/);
});

test('caller JSON cannot supply authoritative receipts or endpoints', async t => {
  const { trusted, calls } = setup(t);
  const caller = structuredClone(input);
  caller.pins.runtimeReceipt = observations();
  caller.environment = { baseUrl: 'https://attacker.example.test' };
  const result = await createP1NativeProviders({ input: caller }, trusted).nativeAuthorityProvider.readCurrent();
  assert.equal(result.status, 'blocked');
  assert.ok(calls.every(call => !call.url.includes('attacker')));
  caller.nativeOwner.provenance = observations().provenance;
  assert.throws(() => createP1NativeProviders({ input: caller }, trusted), /locator_required/);
  assert.throws(() => createP1NativeProviders({ input }), /trusted_actor_required/);
});

test('foreign, child, stale, replaced and claim-versus-Pod mismatches reject', async t => {
  const changes = [
    (body, kind) => { if (kind === 'provenance') body.rootSessionId = 'other-root'; },
    (body, kind) => { if (kind === 'provenance') body.acceptedSelectionHash = 'b'.repeat(64); },
    (body, kind) => { if (kind === 'registration') body.binding.actorId = 'other-actor'; },
    (body, kind) => { if (kind === 'registration') body.expiresAt = past; },
    (body, kind) => { if (kind === 'registration') body.state = 'revoked'; },
    (body, kind) => { if (kind === 'pod') body.resource.resourceId = body.podUid; },
    (body, kind) => { if (kind === 'pod') body.resource.seam = 'storage'; },
    (body, kind) => { if (kind === 'pod') body.environmentFence.owner.runId = 'other-run'; },
    (body, kind) => { if (kind === 'pod') body.fencingGeneration++; },
    (body, kind) => { if (kind === 'evidence') body.workspaceMount.podUid = 'replacement-pod'; },
    (body, kind) => { if (kind === 'evidence') body.readiness.registrationRevision++; },
    (body, kind, count) => { if (kind === 'pod' && count === 2) body.podResourceVersion = '78'; },
    (body, kind, count) => { if (kind === 'provenance' && count === 2) body.rootStateVersion++; },
    (body, kind, count) => { if (kind === 'registration' && count === 2) body.revision++; },
  ];
  for (const [index, change] of changes.entries()) {
    await t.test(`mismatch ${index + 1}`, async sub => {
      const { providers } = setup(sub, change);
      await assert.rejects(providers.nativeAuthorityProvider.readCurrent, /^Error: native_/);
    });
  }
});

test('owner failures, redirects, malformed and oversized bodies are bounded and redacted', async t => {
  const { providers, calls } = setup(t);
  for (const response of [
    () => new Response('private-owner-error', { status: 403 }),
    () => new Response('', { status: 302, headers: { Location: 'https://never-follow.example.test' } }),
    () => new Response('invalid-json-secret', { headers: { 'Content-Type': 'application/json' } }),
    () => new Response('x'.repeat(65537), { headers: { 'Content-Type': 'application/json' } }),
    () => { throw new Error('token-private-error'); },
  ]) {
    t.mock.method(globalThis, 'fetch', async () => response());
    await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
      { message: 'native_owner_read_unavailable' });
  }
  assert.equal(calls.length, 0);
});
