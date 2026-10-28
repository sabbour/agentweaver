import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { createP1NativeProviders } from './owner-providers.mjs';

const id = '11111111-1111-4111-8111-111111111111';
const hash = 'a'.repeat(64);
const past = '2026-01-01T00:00:00Z';
const future = '2099-01-01T00:00:00Z';
const actor = { issuer: 'https://issuer.example.test', subject: 'actor', tenantId: 'tenant' };
const input = { nativeOwner: { runtimeInstanceId: id }, pins: { context: { clusterIdentity: { sha256: hash } } } };
const sandboxOperationId = '55555555-5555-4555-8555-555555555555';

test('fixed-credit no-send owner read contract is exact and fails closed on missing, foreign, or stale evidence', async () => {
  const source = async path => readFile(new URL(`../../../${path}`, import.meta.url), 'utf8');
  const [
    hostEndpoints, identityContracts, host, sourceEndpoints, sourceOwner, sourceContracts,
    sourceStore, sourceClient,
  ] = await Promise.all([
    source('packages/Agentweaver.AgentRuntime/RuntimeAgentHostEndpoints.cs'),
    source('packages/Agentweaver.Identity/RuntimeHostContracts.cs'),
    source('packages/Agentweaver.AgentRuntime/RuntimeAgentHost.cs'),
    source('services/orchestrator/Agentweaver.Orchestrator/RuntimeUsageSourceEndpoints.cs'),
    source('services/orchestrator/Agentweaver.Orchestrator/RuntimeUsageSourceOwner.cs'),
    source('packages/Agentweaver.Identity/RuntimeNativeDenialContracts.cs'),
    source('services/orchestrator/Agentweaver.Orchestrator/RuntimeUsageSourceStore.cs'),
    source('packages/Agentweaver.AgentRuntime/RuntimeUsageSourceHttpClient.cs'),
  ]);
  const recordFields = (sourceText, name) => {
    const declaration = sourceText.match(new RegExp(`record ${name}\\(([\\s\\S]*?)\\)`))?.[1];
    assert.ok(declaration, name);
    return declaration.split(',').map(field => {
      const property = field.trim().split(/\s+/).at(-1);
      return property[0].toLowerCase() + property.slice(1);
    }).sort();
  };
  assert.match(hostEndpoints,
    /MapPost\("\/runtime\/v1\/turns\/fixed-credit-denials\/read",\s*\(HttpContext context, RuntimeHostFixedCreditGuardReadRequest request/);
  assert.match(hostEndpoints,
    /MapPost\("\/runtime\/v1\/turns\/fixed-credit-denials\/select-current",\s*\(HttpContext context, RuntimeHostFixedCreditGuardSelectionRequest request/);
  assert.match(hostEndpoints,
    /host\.ReadFixedCreditGuardAsync\(request, actor, token\)\)\)\.RequireAuthorization\(\)/);
  assert.match(hostEndpoints,
    /host\.SelectCurrentFixedCreditGuardAsync\(request, actor, token\)\)\)\.RequireAuthorization\(\)/);
  assert.match(hostEndpoints,
    /record RuntimeHostFixedCreditGuardReadRequest\(\s*RuntimeHostSessionProof\? Proof, Guid RequestId, string RequestHash\)/);
  assert.match(hostEndpoints,
    /record RuntimeHostFixedCreditGuardSelectionRequest\(Guid RuntimeInstanceId\)/);
  assert.match(identityContracts,
    /record RuntimeHostSessionProof\(\s*int ContractVersion,\s*RuntimeRegistration Registration,\s*Guid SourceGrantId,\s*long SourceGrantRevision,[\s\S]*?RuntimeCredentialPurpose Purpose\)/);
  assert.match(sourceContracts,
    /record RuntimeFixedCreditGuardReadRequest\(\s*RuntimeCredentialHttpRequest\? Authorization,\s*Guid RuntimeInstanceId,\s*long RegistrationRevision,\s*long ExecutionFence,\s*RuntimeFixedCreditGuardStage Stage,\s*Guid RequestId,\s*string RequestHash\)/);
  assert.match(sourceContracts,
    /record RuntimeFixedCreditGuardReadResult\(\s*RuntimeFixedCreditGuardReceipt\? Receipt,\s*RuntimeFixedCreditNoSendReceipt\? Evidence\)/);
  assert.match(sourceContracts,
    /record RuntimeFixedCreditGuardSelectionRequest\(\s*RuntimeCredentialHttpRequest\? Authorization,\s*Guid RuntimeInstanceId,\s*long RegistrationRevision,\s*long ExecutionFence\)/);
  assert.match(sourceContracts,
    /record RuntimeFixedCreditGuardSelectionResult\(\s*RuntimeFixedCreditGuardReceipt\? Receipt,\s*RuntimeFixedCreditNoSendReceipt\? Evidence\)/);
  assert.deepEqual(recordFields(sourceContracts, 'RuntimeFixedCreditGuardReadResult'),
    ['evidence', 'receipt']);
  assert.deepEqual(recordFields(sourceContracts, 'RuntimeFixedCreditGuardReceipt'), [
    'canonicalPayloadHash', 'code', 'contractVersion', 'recordedAt', 'registration',
    'registrationHash', 'requestHash', 'requestId', 'sourceGrantId', 'sourceGrantRevision',
    'sourceHash', 'stage',
  ].sort());
  assert.deepEqual(recordFields(sourceContracts, 'RuntimeFixedCreditNoSendReceipt'), [
    'afterCursorHash', 'beforeCursorHash', 'canonicalPayloadHash', 'contractVersion',
    'executionFence', 'guardReceiptHash', 'nativeSessionHash', 'observedEventCount',
    'rangeComplete', 'registrationHash', 'registrationRevision', 'requestId', 'runtimeInstanceId',
    'sourceGrantId', 'sourceGrantRevision', 'sourceHash', 'stage', 'verifiedAt',
  ].sort());
  assert.match(host,
    /var session = await RequireSessionAsync\(proof, actor, token\)[\s\S]*?source\.ReadFixedCreditGuardAsync\(\s*session, request\.RequestId, request\.RequestHash, token\)[\s\S]*?await RequireSessionAsync\(proof, actor, token\)/);
  assert.match(sourceEndpoints,
    /MapPost\("\/internal\/runtime\/turns\/fixed-credit-denials\/read",[\s\S]*?owner\.ReadFixedCreditGuardAsync\(context, request, cancellationToken\)\)\)\s*\.RequireAuthorization\(\)/);
  assert.match(sourceOwner,
    /RuntimeFixedCreditGuardContract\.ValidateReadRequest\(request\)[\s\S]*?store\.ReadCurrentSourceForGrantAsync\([\s\S]*?ReadFixedCreditGuardResultWithinTransactionAsync\([\s\S]*?includeEvidence: true[\s\S]*?\?\? throw new CoordinationException\(\s*"runtime_fixed_credit_guard_unknown", StatusCodes\.Status404NotFound\)[\s\S]*?RuntimeFixedCreditGuardContract\.ValidateReadResult\(/);
  assert.match(sourceOwner,
    /registrations\.ExecuteCurrentAsync\([\s\S]*?var grant = await broker\.VerifyAsync[\s\S]*?var finalGrant = await broker\.VerifyAsync[\s\S]*?await transaction\.CommitAsync/);
  assert.match(sourceOwner,
    /SelectCurrentFixedCreditGuardAsync\([\s\S]*?ReadCurrentSourceForGrantAsync\([\s\S]*?SelectFixedCreditGuardWithinTransactionAsync\([\s\S]*?ValidateSelectionResult\(/);
  assert.match(sourceStore,
    /AND payload->>'sourceHash' = @sourceHash[\s\S]*?AND payload->>'sourceGrantId' = @grantId[\s\S]*?AND payload->>'sourceGrantRevision' = @grantRevision/);
  assert.match(sourceStore,
    /if \(selectedReceipt is not null\)\s*return new\(null, null\)/);
  assert.match(sourceClient,
    /fixed-credit-denials\/select-current[\s\S]*?ValidateSelectionResult\(/);
  assert.match(sourceContracts,
    /if \(receipt\.Registration != registration \|\|[\s\S]*?receipt\.Registration\.Revision != request\.RegistrationRevision \|\|[\s\S]*?receipt\.Registration\.Binding\.ExecutionFence != request\.ExecutionFence \|\|[\s\S]*?receipt\.RequestId != request\.RequestId \|\|[\s\S]*?receipt\.RequestHash != request\.RequestHash\)[\s\S]*?runtime_fixed_credit_guard_conflict/);
});

// Controlled owner responses exercise the real wire shapes; they are not live receipts.
function observations() {
  const binding = {
    actorIssuer: actor.issuer, actorId: actor.subject, tenantId: actor.tenantId,
    projectId: 'project', runId: 'run', sessionId: 'root', environmentId: 'environment',
    profileId: 'profile', placementUid: 'claim-uid', placementProviderId: 'agent-sandbox',
    executionFence: 1, modelSourceMode: 'hostedCopilot', copilotHardCreditLimit: 0,
    modelSelectionReference: 'selection', reasoningEffort: 'high',
    placementGeneration: 2, environmentLifecycleGeneration: 3,
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
    fencingGeneration: 5, provisionOperationId: sandboxOperationId, clusterIdentity: hash, namespace: 'namespace',
    claimUid: 'claim-uid', sandboxUid: 'sandbox-cr-uid', podName: 'host', podUid: 'actual-pod-uid',
    podResourceVersion: '77', podGeneration: 1, podPhase: 'Running',
    workspaceAttachment: {
      namespace: 'namespace', claimName: 'workspace', claimUid: 'pvc-uid', mountPath: '/workspace', readOnly: false,
    },
    isolationConfigurationVerified: true,
  };
  const receipt = (operationId, resourceKind, resourceId, options = {}) => ({
    operationId, resourceKind, resourceId,
    resourceGeneration: null, providerFencingGeneration: null,
    transitionRevision: null, leaseRevision: null, providerResource: null,
    createdAt: past, completedAt: past, ...options,
  });
  const creation = {
    contractVersion: 1, binding: structuredClone(provenance),
    environmentFence: {
      owner: { tenantId: 'tenant', projectId: 'project', runId: 'run', environmentId: 'environment' },
      lifecycleGeneration: 3,
    },
    environment: receipt('22222222-2222-4222-8222-222222222222', 'Environment', 'environment'),
    workspaceMetadata: receipt('33333333-3333-4333-8333-333333333333', 'WorkspaceVolumeMetadata', 'workspace-volume', {
      resourceGeneration: 0, transitionRevision: 7,
      createdAt: '2026-01-02T00:00:00Z', completedAt: '2026-01-02T00:00:00Z',
    }),
    workspaceProvision: receipt('44444444-4444-4444-8444-444444444444', 'WorkspaceVolume', 'workspace-volume', {
      resourceGeneration: 8, transitionRevision: 9,
      providerResource: { seam: 'storage', providerId: 'azure-files', resourceId: 'share-uid', generation: 8 },
      createdAt: '2026-01-03T00:00:00Z', completedAt: '2026-01-03T00:00:00Z',
    }),
    sandboxProvision: receipt(sandboxOperationId, 'Sandbox', sandboxOperationId, {
      resourceGeneration: 2, providerFencingGeneration: 5, leaseRevision: 6,
      providerResource: { seam: 'sandbox', providerId: 'agent-sandbox', resourceId: 'claim-uid', generation: 2 },
      createdAt: '2026-01-04T00:00:00Z', completedAt: '2026-01-04T00:00:00Z',
    }),
    podObservation: structuredClone(pod),
  };
  const source = {
    runtimeInstanceId: id, sdkSessionId: 'native-session', sdkVersion: '1.0',
    runtimeVersion: '1.0', modelSelectionReference: 'selection', modelId: 'model',
    catalogHash: hash, modelMultiplier: null, sourceMode: 'hosted-copilot',
    meterSource: 'copilot.nano_aiu', acceptedSelectionHash: hash,
    registrationRevision: 1, reasoningEffort: 'high',
  };
  const sourceGrant = {
    grantId: '66666666-6666-4666-8666-666666666666', runtimeInstanceId: id,
    registrationRevision: 1, revision: 1, issuer: actor.issuer, purpose: 'observe',
    audience: 'https://runtime.example.test/internal/runtime/observations',
    state: 'active', configurationHash: hash, expiresAt: future, recordedAt: past,
  };
  const evidence = {
    contractVersion: 1, observedAt: past,
    readiness: {
      contractVersion: 1, runtimeInstanceId: id, registrationRevision: 1, executionFence: 1,
      image: { digest: `sha256:${hash}`, platform: 'linux/amd64', compressedPullBytes: 1 },
      startupPhases: [], sourceGrant,
    },
    environmentPodObservation: structuredClone(pod), environmentPodObservationHash: hash,
    workspaceMount: {
      contractVersion: 1, observedAt: past, podUid: pod.podUid, mountPath: '/workspace',
      mountId: 99, deviceMajor: 0, deviceMinor: 43, directoryInode: 100,
      fileSystemType: 'cifs', readOnly: false, mountInfoSha256: hash,
    },
    registrationHash: hash, sourceHash: hash,
    nativeSessionHash: createHash('sha256').update(source.sdkSessionId, 'utf8').digest('hex'),
  };
  const p1Evidence = {
    contractVersion: 1, observedAt: past, creation: structuredClone(creation), host: evidence,
  };
  const sourceReceipt = {
    runtimeInstanceId: id, registrationRevision: 1, sourceGrantId: sourceGrant.grantId,
    source, canonicalPayloadHash: hash, recordedAt: past,
  };
  const selection = { receipt: null, evidence: null };
  const fixedReceipt = {
    contractVersion: 1, stage: 'hostPreQueue', code: 'hardLimitZero',
    registration: structuredClone(registration), registrationHash: hash,
    requestId: '77777777-7777-4777-8777-777777777777', requestHash: 'b'.repeat(64),
    sourceHash: hash, sourceGrantId: sourceGrant.grantId, sourceGrantRevision: sourceGrant.revision,
    recordedAt: past, canonicalPayloadHash: 'c'.repeat(64),
  };
  const fixedEvidence = {
    contractVersion: 1, runtimeInstanceId: id, registrationRevision: 1,
    executionFence: 1, requestId: fixedReceipt.requestId, stage: 'hostPreQueue',
    guardReceiptHash: fixedReceipt.canonicalPayloadHash, registrationHash: hash,
    sourceHash: hash, sourceGrantId: sourceGrant.grantId,
    sourceGrantRevision: sourceGrant.revision,
    nativeSessionHash: evidence.nativeSessionHash,
    beforeCursorHash: 'd'.repeat(64), afterCursorHash: 'd'.repeat(64),
    observedEventCount: 0, rangeComplete: true, verifiedAt: past,
    canonicalPayloadHash: 'e'.repeat(64),
  };
  const fixedPair = { receipt: fixedReceipt, evidence: fixedEvidence };
  return {
    registration, provenance, pod, creation, evidence, p1Evidence, sourceReceipt,
    selection, fixedPair,
  };
}

function setup(t, mutate = () => {}, responseHeaders = {
  'Content-Type': 'application/json', 'Cache-Control': 'no-store',
}, responseStatus = () => 200) {
  const values = observations();
  const calls = [];
  const counts = {};
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    calls.push({ url: url.href, init });
    const path = url.pathname;
    const kind = path.includes('/registrations/') ? 'registration'
      : path.endsWith('/p1-invocation-binding') ? 'provenance'
        : path.endsWith('/p1-creation-binding') ? 'creation'
          : path.endsWith('/sources/' + id) ? 'sourceReceipt'
            : path.endsWith('/select-current') ? 'selection'
              : path.endsWith('/p1-evidence') ? 'p1Evidence' : 'evidence';
      const count = counts[kind] = (counts[kind] ?? 0) + 1;
      const body = structuredClone(values[kind]);
      mutate(body, kind, count);
      return new Response(JSON.stringify(body), {
        status: responseStatus(kind), headers: responseHeaders,
      });
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

test('real owner routes and C# record contracts stay aligned without database or cluster fixtures', async () => {
  const source = async path => readFile(new URL(`../../../${path}`, import.meta.url), 'utf8');
  const [
    core, environment, host, contracts, pod, provenance, creationContracts, hostReceipt,
    providerContracts, environmentContracts,
  ] = await Promise.all([
    source('services/orchestrator/Agentweaver.Orchestrator/ProjectRunEndpoints.cs'),
    source('services/environment/Agentweaver.Environment/EnvironmentEndpoints.cs'),
    source('packages/Agentweaver.AgentRuntime/RuntimeAgentHostEndpoints.cs'),
    source('packages/Agentweaver.Identity/RuntimeHostContracts.cs'),
    source('packages/Agentweaver.Abstractions/SandboxPodObservation.cs'),
    source('packages/Agentweaver.Abstractions/P1InvocationBinding.cs'),
    source('packages/Agentweaver.Abstractions/EnvironmentP1CreationBinding.cs'),
    source('packages/Agentweaver.AgentRuntime/RuntimeHostP1EvidenceReceipt.cs'),
    source('packages/Agentweaver.Abstractions/ProviderContracts.cs'),
    source('packages/Agentweaver.Abstractions/EnvironmentContracts.cs'),
  ]);
  assert.ok(core.includes('/internal/projects/{projectId}/runs/{runId}/p1-invocation-binding'));
  assert.ok(environment.includes('MapRuntimePlacement("/p1-creation-binding")'));
  assert.ok(environment.includes('/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}'));
  assert.ok(host.includes('/runtime/v1/readiness/p1-evidence'));
  assert.ok((await source('packages/Agentweaver.AgentRuntime/RuntimeP1CreationBindingHttpClient.cs'))
    .includes('JsonUnmappedMemberHandling.Disallow'));
  assert.ok((await source('services/environment/Agentweaver.Environment/EnvironmentP1CreationReader.cs'))
    .includes('new(1, binding, fence, environment, metadata, volume, provision, pod)'));
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
    [creationContracts, 'EnvironmentP1CreationBinding', values.creation],
    [creationContracts, 'EnvironmentP1CreationReceipt', values.creation.environment],
    [hostReceipt, 'RuntimeHostP1EvidenceReceipt', values.p1Evidence],
    [providerContracts, 'ProviderResourceRef', values.creation.sandboxProvision.providerResource],
    [pod, 'SandboxPodWorkspaceAttachment', values.pod.workspaceAttachment],
  ]) {
    const declaration = sourceText.match(new RegExp(`record ${name}\\(([\\s\\S]*?)\\)`))?.[1];
    assert.ok(declaration, name);
    const fields = declaration.split(',').map(field => {
      const property = field.trim().split(/\s+/).at(-1);
      return property[0].toLowerCase() + property.slice(1);
    });
    assert.deepEqual(Object.keys(value).sort(), fields.sort(), name);
  }
  assert.match(environmentContracts, /public EnvironmentOwnerIdentity Owner \{ get; \}/);
  assert.match(environmentContracts, /public long LifecycleGeneration \{ get; \}/);
  assert.deepEqual(Object.keys(values.creation.environmentFence).sort(), ['lifecycleGeneration', 'owner']);
  assert.deepEqual(Object.keys(values.creation.environmentFence.owner).sort(),
    ['environmentId', 'projectId', 'runId', 'tenantId']);
});

test('joins current owner observations and stays blocked on the missing native proof slot', async t => {
  const { providers, calls } = setup(t);
  const result = await providers.nativeAuthorityProvider.readCurrent();
  assert.equal(result.status, 'blocked');
  assert.equal(result.code, 'native_no_send_effect_proof_missing');
  assert.equal(result.acceptanceEvidence, false);
  assert.equal(result.nativeNoSendProven, false);
  assert.equal(result.partialObservation.sandboxProvisionOperationId, sandboxOperationId);
  assert.equal(result.partialObservation.sandboxClaimUid, 'claim-uid');
  assert.notEqual(result.partialObservation.sandboxClaimUid, result.partialObservation.podUid);
  assert.doesNotMatch(JSON.stringify(result), /never-output|in-memory-only|never-follow/);
  assert.equal(calls.length, 12);
  assert.deepEqual(calls.map(call => new URL(call.url).hostname), [
    'core.example.test', 'core.example.test', 'environment.example.test', 'host.example.test',
    'host.example.test', 'core.example.test', 'environment.example.test', 'core.example.test',
    'core.example.test', 'core.example.test', 'host.example.test', 'host.example.test',
  ]);
  assert.deepEqual(calls.map(call => new URL(call.url).pathname.split('/').at(-1)), [
    id, 'p1-invocation-binding', 'p1-creation-binding', 'select-current', 'p1-evidence',
    id, 'p1-creation-binding', 'p1-invocation-binding', id, id, 'select-current', 'p1-evidence',
  ]);
  for (const { init } of calls) {
    assert.equal(init.redirect, 'error');
    assert.equal(init.cache, 'no-store');
    assert.equal(init.headers['X-Agentweaver-Tenant'], 'tenant');
    assert.ok(init.signal instanceof AbortSignal);
  }
  assert.equal(calls[3].init.method, 'POST');
  assert.deepEqual(JSON.parse(calls[3].init.body), { runtimeInstanceId: id });
  assert.ok(calls.every((call, index) =>
    call.init.method === ([3, 10].includes(index) ? 'POST' : 'GET')));
  assert.equal(result.partialObservation.fixedCreditNoSendPair, null);
  assert.equal((await providers.journeyAuthorizer.authorize()).allowed, false);
  await assert.rejects(providers.runtimeReadinessProvider.readCurrent, /authority_join_incomplete/);
});

test('selects only a unique current fixed-credit pair and remains blocked', async t => {
  const current = observations();
  const { providers, calls } = setup(t, (body, kind) => {
    if (kind === 'selection') {
      body.receipt = structuredClone(current.fixedPair.receipt);
      body.evidence = structuredClone(current.fixedPair.evidence);
    }
  });
  const result = await providers.nativeAuthorityProvider.readCurrent();
  assert.equal(result.status, 'blocked');
  assert.equal(result.code, 'native_no_send_effect_proof_missing');
  assert.equal(result.acceptanceEvidence, false);
  assert.equal(result.nativeNoSendProven, false);
  assert.deepEqual(result.partialObservation.fixedCreditNoSendPair, {
    requestId: current.fixedPair.receipt.requestId,
    requestHash: current.fixedPair.receipt.requestHash,
    registrationRevision: 1,
    executionFence: 1,
    sourceGrantId: current.fixedPair.receipt.sourceGrantId,
    sourceGrantRevision: 1,
    beforeCursorHash: current.fixedPair.evidence.beforeCursorHash,
    afterCursorHash: current.fixedPair.evidence.afterCursorHash,
    observedEventCount: 0,
    rangeComplete: true,
    verifiedAt: past,
  });
  const selectCall = calls.find(call => new URL(call.url).pathname.endsWith('/select-current'));
  assert.equal(selectCall.init.method, 'POST');
  assert.deepEqual(JSON.parse(selectCall.init.body), { runtimeInstanceId: id });
  assert.deepEqual(calls.filter(call =>
    new URL(call.url).pathname.endsWith('/select-current')).map(call => JSON.parse(call.init.body)),
  [{ runtimeInstanceId: id }, { runtimeInstanceId: id }]);
  assert.doesNotMatch(JSON.stringify(result), /canonicalPayloadHash|registrationHash|nativeSessionHash/);
});

test('rejects source metadata that violates the owner source contract even when a pair is selected', async t => {
  const acceptedPin = {
    contractVersion: 1, modelSelectionReference: 'selection', modelId: 'accepted-model',
    sourceMode: 'hostedCopilot', configurationRevision: 'revision', configurationHash: hash,
    reasoningEffort: 'high',
  };
  const cases = [
    {
      name: 'model selection reference mismatch',
      mutate(source) { source.modelSelectionReference = 'foreign-selection'; },
    },
    {
      name: 'prompt limit without a registered limit',
      mutate(source) { source.maxPromptTokens = 100; },
    },
    {
      name: 'prompt limit omitted when registration bounds it',
      mutate(source) { source.maxPromptTokens = null; },
      registration: binding => { binding.maxPromptTokens = 10; },
    },
    {
      name: 'prompt limit exceeds registration bound',
      mutate(source) { source.maxPromptTokens = 11; },
      registration: binding => { binding.maxPromptTokens = 10; },
    },
    {
      name: 'source model pin is not the registered pin',
      mutate(source) { source.modelBindingPin = { arbitrary: 'record' }; },
    },
    {
      name: 'resolved model pin has no contract shape',
      mutate(source) { source.resolvedModelBindingPin = { arbitrary: 'record' }; },
    },
    {
      name: 'resolved model identity differs from source',
      mutate(source) {
        source.modelBindingPin = structuredClone(acceptedPin);
        source.modelId = 'accepted-model';
        source.resolvedModelBindingPin = { ...acceptedPin, modelId: 'different-model' };
      },
      registration(binding) {
        binding.modelBindingPin = structuredClone(acceptedPin);
      },
    },
    {
      name: 'hosted source contains BYOK metadata',
      mutate(source) { source.byokProvider = { type: 'azure' }; },
    },
  ];

  for (const { name, mutate, registration } of cases) {
    await t.test(name, async sub => {
      const current = observations();
      const { providers } = setup(sub, (body, kind) => {
        if (kind === 'registration' && registration) registration(body.binding);
        if (kind === 'sourceReceipt') mutate(body.source, current.registration.binding);
        if (kind === 'selection') {
          body.receipt = structuredClone(current.fixedPair.receipt);
          body.evidence = structuredClone(current.fixedPair.evidence);
        }
      });
      await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
        /^Error: native_fixed_credit_source_mismatch$/);
    });
  }
});

test('accepts a source matching the registered model pin and prompt-token bound', async t => {
  const pin = {
    contractVersion: 1, modelSelectionReference: 'selection', modelId: 'accepted-model',
    sourceMode: 'hostedCopilot', configurationRevision: 'revision', configurationHash: hash,
    reasoningEffort: 'high',
  };
  const { providers } = setup(t, (body, kind) => {
    if (kind === 'registration') {
      body.binding.maxPromptTokens = 100;
      body.binding.modelBindingPin = structuredClone(pin);
    }
    if (kind === 'sourceReceipt') {
      body.source.maxPromptTokens = 80;
      body.source.modelId = 'accepted-model';
      body.source.modelBindingPin = structuredClone(pin);
      body.source.resolvedModelBindingPin = structuredClone(pin);
    }
  });
  const result = await providers.nativeAuthorityProvider.readCurrent();
  assert.equal(result.status, 'blocked');
  assert.equal(result.acceptanceEvidence, false);
  assert.equal(result.nativeNoSendProven, false);
  assert.equal(result.partialObservation.fixedCreditNoSendPair, null);
});

test('closing Host reread rejects grant rotation while source and registration remain unchanged', async t => {
  const { providers, values, calls } = setup(t, (body, kind, count) => {
    if (kind === 'p1Evidence' && count === 2)
      body.host.readiness.sourceGrant.revision++;
  });
  await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
    /^Error: native_fixed_credit_authority_changed$/);
  assert.equal(calls.filter(call =>
    new URL(call.url).pathname.includes('/registrations/')).length, 2);
  assert.equal(calls.filter(call =>
    new URL(call.url).pathname.includes('/sources/')).length, 2);
  assert.equal(values.sourceReceipt.sourceGrantId, values.evidence.readiness.sourceGrant.grantId);
});

test('closing Host reread rejects a changed fixed-credit pair', async t => {
  const current = observations();
  const { providers } = setup(t, (body, kind, count) => {
    if (kind !== 'selection') return;
    if (count === 1) {
      body.receipt = structuredClone(current.fixedPair.receipt);
      body.evidence = structuredClone(current.fixedPair.evidence);
    }
  });
  await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
    /^Error: native_fixed_credit_selection_changed$/);
});

test('closing Host reread fails closed when the current grant expires after the first Host read', async t => {
  const clock = { now: Date.parse('2026-01-05T00:00:00Z') + 100 };
  const expiresAt = new Date(clock.now + 1_000).toISOString();
  t.mock.method(Date, 'now', () => clock.now);
  const { providers } = setup(t, (body, kind, count) => {
    if (kind === 'p1Evidence' && count === 1)
      body.host.readiness.sourceGrant.expiresAt = expiresAt;
    if (kind === 'p1Evidence' && count === 2) {
      body.host.readiness.sourceGrant.expiresAt = expiresAt;
      clock.now = Date.parse(expiresAt) + 1;
    }
  });
  await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
    /^Error: native_host_observation_mismatch$/);
});

test('missing, ambiguous, stale, foreign and incomplete owner pairs fail closed', async t => {
  const cases = [
    { name: 'missing', modify: result => result },
    { name: 'ambiguous', modify: result => result },
    {
      name: 'stale registration',
      modify: result => { result.receipt.registration.revision++; },
    },
    {
      name: 'foreign registration',
      modify: result => { result.receipt.registration.binding.actorId = 'foreign-actor'; },
    },
    {
      name: 'stale source grant',
      modify: result => { result.evidence.sourceGrantRevision++; },
    },
    {
      name: 'incomplete pair',
      modify: result => { result.evidence = null; },
    },
  ];
  for (const { name, modify } of cases) {
    await t.test(name, async sub => {
      const current = observations();
      const { providers } = setup(sub, (body, kind) => {
        if (kind !== 'selection') return;
        if (name === 'missing' || name === 'ambiguous') return;
        body.receipt = structuredClone(current.fixedPair.receipt);
        body.evidence = structuredClone(current.fixedPair.evidence);
        modify(body);
      });
      if (name === 'missing' || name === 'ambiguous') {
        const result = await providers.nativeAuthorityProvider.readCurrent();
        assert.equal(result.status, 'blocked');
        assert.equal(result.acceptanceEvidence, false);
        assert.equal(result.nativeNoSendProven, false);
        assert.equal(result.partialObservation.fixedCreditNoSendPair, null);
      } else {
        await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
          /^Error: native_fixed_credit_/);
      }
    });
  }
});

test('caller JSON cannot supply authoritative receipts or endpoints', async t => {
  const { trusted, calls } = setup(t);
  const caller = structuredClone(input);
  caller.pins.runtimeReceipt = observations();
  caller.environment = { baseUrl: 'https://attacker.example.test' };
  const result = await createP1NativeProviders({ input: caller }, trusted).nativeAuthorityProvider.readCurrent();
  assert.equal(result.status, 'blocked');
  assert.equal(result.code, 'native_no_send_effect_proof_missing');
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
    (body, kind) => { if (kind === 'creation') body.podObservation.resource.resourceId = body.podObservation.podUid; },
    (body, kind) => { if (kind === 'creation') body.podObservation.resource.seam = 'storage'; },
    (body, kind) => { if (kind === 'creation') body.podObservation.environmentFence.owner.runId = 'other-run'; },
    (body, kind) => { if (kind === 'creation') body.podObservation.fencingGeneration++; },
    (body, kind) => { if (kind === 'p1Evidence') body.host.workspaceMount.podUid = 'replacement-pod'; },
    (body, kind) => { if (kind === 'p1Evidence') body.host.readiness.registrationRevision++; },
    (body, kind) => { if (kind === 'p1Evidence') body.creation.environment.operationId = '66666666-6666-4666-8666-666666666666'; },
    (body, kind, count) => { if (kind === 'creation' && count === 2) body.podObservation.podResourceVersion = '78'; },
    (body, kind, count) => { if (kind === 'provenance' && count === 2) body.rootStateVersion++; },
    (body, kind, count) => { if (kind === 'registration' && count === 2) body.revision++; },
    (body, kind, count) => { if (kind === 'creation' && count === 2) body.sandboxProvision.providerResource.resourceId = 'replacement-claim'; },
    (body, kind) => { if (kind === 'creation') body.sandboxProvision.resourceId = 'claim-uid'; },
    (body, kind) => { if (kind === 'creation') body.workspaceProvision.transitionRevision = 7; },
    (body, kind) => { if (kind === 'creation') body.sandboxProvision.providerFencingGeneration++; },
    (body, kind) => { if (kind === 'creation') body.environment.providerFencingGeneration = 1; },
    (body, kind) => { if (kind === 'creation') body.sandboxProvision.leaseRevision = 7; },
    (body, kind) => { if (kind === 'creation') body.workspaceProvision.createdAt = '2026-01-01T00:00:00Z'; },
    (body, kind) => { if (kind === 'p1Evidence') body.creation.podObservation.observedAt = 'invalid-time'; },
  ];
  for (const [index, change] of changes.entries()) {
    await t.test(`mismatch ${index + 1}`, async sub => {
      const { providers } = setup(sub, change);
      await assert.rejects(providers.nativeAuthorityProvider.readCurrent, /^Error: native_/);
    });
  }
});

test('allows only Pod observedAt to change across the Environment and Host bracket', async t => {
  const { providers } = setup(t, (body, kind, count) => {
    if (kind === 'creation') body.podObservation.observedAt = count === 1
      ? '2026-01-01T00:00:00Z' : '2026-01-02T00:00:00Z';
    if (kind === 'p1Evidence') {
      body.creation.podObservation.observedAt = '2026-01-03T00:00:00Z';
      body.host.environmentPodObservation.observedAt = '2026-01-04T00:00:00Z';
    }
  });
  const result = await providers.nativeAuthorityProvider.readCurrent();
  assert.equal(result.code, 'native_no_send_effect_proof_missing');
});

test('rejects unknown or missing Environment and Host response fields', async t => {
  for (const mutate of [
    (body, kind) => { if (kind === 'creation') body.sandboxProvision.unexpected = true; },
    (body, kind) => { if (kind === 'creation') delete body.workspaceMetadata.leaseRevision; },
    (body, kind) => { if (kind === 'p1Evidence') body.creation.unexpected = true; },
    (body, kind) => { if (kind === 'p1Evidence') body.unexpected = true; },
    (body, kind) => { if (kind === 'creation') body.environmentFence.owner.unexpected = true; },
    (body, kind) => { if (kind === 'creation') body.workspaceProvision.providerResource.unexpected = true; },
    (body, kind) => { if (kind === 'p1Evidence') delete body.host.workspaceMount.mountInfoSha256; },
  ]) {
    await t.test('rejects malformed response', async sub => {
      const { providers } = setup(sub, mutate);
      await assert.rejects(providers.nativeAuthorityProvider.readCurrent, /^Error: native_/);
    });
  }
});

test('requires Cache-Control no-store on successful owner responses', async t => {
  const { providers } = setup(t, () => {}, { 'Content-Type': 'application/json' });
  await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
    { message: 'native_owner_read_unavailable' });
});

test('a legacy missing Environment creation route keeps the generic authority-read failure', async t => {
  const { providers } = setup(t, () => {}, undefined, kind => kind === 'creation' ? 404 : 200);
  await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
    { message: 'native_owner_read_unavailable' });
});

test('owner failures, redirects, malformed and oversized bodies are bounded and redacted', async t => {
  const { providers, calls } = setup(t);
  for (const response of [
    () => new Response('private-owner-error', { status: 403 }),
    () => new Response('', { status: 302, headers: { Location: 'https://never-follow.example.test' } }),
    () => new Response('invalid-json-secret', {
      headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' },
    }),
    () => new Response('x'.repeat(65537), {
      headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' },
    }),
    () => { throw new Error('token-private-error'); },
  ]) {
    t.mock.method(globalThis, 'fetch', async () => response());
    await assert.rejects(providers.nativeAuthorityProvider.readCurrent,
      { message: 'native_owner_read_unavailable' });
  }
  assert.equal(calls.length, 0);
});
