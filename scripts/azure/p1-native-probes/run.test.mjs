import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { createP1NativeProcessProvider } from './process-provider.mjs';
import { TEST_CLUSTER_CA_PEM } from './test-fixtures.mjs';
import { runP1NativeCreditDenialReaders, runP1NativeProbes } from './run.mjs';
import { runP1DispatchRecoveryProbes } from '../p1-dispatch-recovery-probes/run.mjs';

const invocationId = 'a6a04b52-34bd-4c58-8d95-537912dc8e55';
const policyOperationId = 'b7b15cb2-45a3-4d69-9e06-648023ed9f66';
const future = '2099-01-01T00:00:00.000Z';
const hash = character => character.repeat(64);
const sha256 = value => createHash('sha256').update(value, 'utf8').digest('hex');
const testClusterIdentity = {
  sha256: '6b333f2199e82077ee02ceffd63b1a2e6ff8becf905f3aa56167a853c36aea75',
};
const phaseTimes = {
  scheduled: '2026-01-01T00:00:30.000Z',
  imageReady: '2026-01-01T00:02:30.000Z',
  started: '2026-01-01T00:04:00.000Z',
  configured: '2026-01-01T00:05:00.000Z',
  ready: '2026-01-01T00:05:30.000Z',
};
const leaseCreatedAt = '2026-01-01T00:00:00.000Z';
const sandboxLabelKeys = {
  managed: 'agentweaver.dev/managed-sandbox',
  owner: 'agentweaver.dev/sandbox-owner',
  lifecycle: 'agentweaver.dev/sandbox-lifecycle',
  resourceGeneration: 'agentweaver.dev/sandbox-generation',
  fencingGeneration: 'agentweaver.dev/sandbox-fence',
  operation: 'agentweaver.dev/sandbox-operation',
  claimUid: 'agents.x-k8s.io/claim-uid',
};

function createInput() {
  const context = { kubeContext: 'p1-fixture', clusterIdentity: { ...testClusterIdentity } };
  const owners = [
    {
      ownerId: 'owner-a',
      actorId: 'shared-actor',
      tenantId: 'tenant-a',
      projectId: 'project-a',
      runId: 'run-a',
      environmentId: 'environment-a',
      rootSessionId: 'root-session-a',
      sessionId: 'session-a',
      executionFence: 17,
      volume: {
        volumeId: 'volume-a',
        resourceGeneration: 3,
        dataGeneration: 7,
        transitionRevision: 9,
        phase: 'bound',
        providerBinding: { providerId: 'azure-files', resourceId: 'share-a', bindingId: 'binding-a' },
        owner: { kind: 'Run', id: 'run-a' },
        pvc: { name: 'claim-a', uid: 'pvc-uid-a' },
        pv: { name: 'pv-a', uid: 'pv-uid-a' },
        claimUid: 'claim-uid-a',
        mountPath: '/workspace',
        mountAllowlist: [{
          volumeName: 'agentweaver-workspace',
          containerName: 'agenthost',
          pvcName: 'claim-a',
          mountPath: '/workspace',
          readOnly: false,
        }],
      },
    },
    {
      ownerId: 'owner-b',
      actorId: 'shared-actor',
      tenantId: 'tenant-b',
      projectId: 'project-b',
      runId: 'run-b',
      environmentId: 'environment-b',
      rootSessionId: 'root-session-b',
      sessionId: 'session-b',
      executionFence: 18,
      volume: {
        volumeId: 'volume-b',
        resourceGeneration: 4,
        dataGeneration: 8,
        transitionRevision: 10,
        phase: 'bound',
        providerBinding: { providerId: 'azure-files', resourceId: 'share-b', bindingId: 'binding-b' },
        owner: { kind: 'Team', id: 'team-b' },
        pvc: { name: 'claim-b', uid: 'pvc-uid-b' },
        pv: { name: 'pv-b', uid: 'pv-uid-b' },
        claimUid: 'claim-uid-b',
        mountPath: '/workspace',
        mountAllowlist: [{
          volumeName: 'agentweaver-workspace',
          containerName: 'agenthost',
          pvcName: 'claim-b',
          mountPath: '/workspace',
          readOnly: false,
        }],
      },
    },
  ];
  const fenceFor = owner => ({
    ownerId: owner.ownerId,
    actorId: owner.actorId,
    tenantId: owner.tenantId,
    projectId: owner.projectId,
    runId: owner.runId,
    environmentId: owner.environmentId,
    rootSessionId: owner.rootSessionId,
    sessionId: owner.sessionId,
    executionFence: owner.executionFence,
    lifecycleGeneration: 2,
    currentFencingGeneration: 5,
    leaseRevision: 7,
  });
  const receipt = (owner, resourceId, uid, operationId = null) => ({
    resourceType: 'fixture-resource',
    resourceId,
    invocationId,
    creationReceipt: { resourceId, uid, operationId: operationId ?? `created-${resourceId}` },
    observedReceipt: { resourceId, uid, operationId: operationId ?? `observed-${resourceId}` },
    ownerFence: fenceFor(owner),
  });
  const runtimeOwners = owners.map((owner, index) => ({
    ...Object.fromEntries([
      'ownerId', 'actorId', 'tenantId', 'projectId', 'runId', 'environmentId',
      'rootSessionId', 'sessionId', 'executionFence',
    ].map(key => [key, owner[key]])),
    sandboxResourceId: `sandbox-claim-uid-${index ? 'b' : 'a'}`,
    sandboxResourceGeneration: owner.volume.resourceGeneration,
    sandboxUid: `pod-uid-${index ? 'b' : 'a'}`,
    networkPolicyResourceId: `test/policy-${index ? 'b' : 'a'}`,
    networkPolicyUid: `policy-uid-${index ? 'b' : 'a'}`,
    lateSandboxResourceId: `late-claim-uid-${index ? 'b' : 'a'}`,
    lateSandboxUid: `late-claim-uid-${index ? 'b' : 'a'}`,
    sandboxLabels: {
      [sandboxLabelKeys.managed]: 'true',
      [sandboxLabelKeys.owner]: `owner-fingerprint-${index ? 'b' : 'a'}`,
      [sandboxLabelKeys.lifecycle]: '2',
      [sandboxLabelKeys.resourceGeneration]: String(owner.volume.resourceGeneration),
      [sandboxLabelKeys.fencingGeneration]: '5',
      [sandboxLabelKeys.operation]: `createdsandbox${index ? 'b' : 'a'}`,
      [sandboxLabelKeys.claimUid]: `sandbox-claim-uid-${index ? 'b' : 'a'}`,
    },
  }));
  const ownershipReceipts = owners.flatMap((owner, index) => [
    receipt(owner, owner.environmentId, `environment-uid-${index}`),
    receipt(owner, owner.volume.volumeId, `volume-uid-${index}`),
    receipt(owner, runtimeOwners[index].sandboxResourceId, runtimeOwners[index].sandboxResourceId),
    receipt(owner, runtimeOwners[index].networkPolicyResourceId, runtimeOwners[index].networkPolicyUid,
      policyOperationId),
    receipt(owner, runtimeOwners[index].lateSandboxResourceId, runtimeOwners[index].lateSandboxResourceId),
  ]);
  const environmentFence = owner => ({
    owner: {
      tenantId: owner.tenantId,
      projectId: owner.projectId,
      runId: owner.runId,
      environmentId: owner.environmentId,
    },
    lifecycleGeneration: 2,
  });
  const acceptedOptions = { mode: 'native', registrationRevision: 4 };
  const startupBudget = {
    scheduledSeconds: 30,
    imageReadySeconds: 120,
    startedSeconds: 90,
    configuredSeconds: 60,
    readySeconds: 30,
    totalSeconds: 330,
  };
  const imageDigest = `sha256:${'a'.repeat(64)}`;
  return {
    environment: {
      baseUrl: 'https://environment.example.test',
      issuer: 'https://issuer.example.test',
      audience: 'agentweaver-environment',
      actorId: owners[0].actorId,
      tenantId: owners[0].tenantId,
    },
    pins: {
      context: { ...context, namespace: 'p1-fixture' },
      namespace: 'p1-fixture',
      providerReceipt: {
        clusterIdentity: context.clusterIdentity,
        owners: owners.map(owner => ({
          ownerId: owner.ownerId,
          clusterIdentity: context.clusterIdentity,
          pvc: owner.volume.pvc,
          pv: owner.volume.pv,
          claimUid: owner.volume.claimUid,
          providerBinding: owner.volume.providerBinding,
        })),
      },
      runtimeReceipt: {
        clusterIdentity: context.clusterIdentity,
        owners: runtimeOwners,
        network: {
          ownerId: 'owner-a',
          generation: 6,
          allowedServiceIdentities: ['service-ns/allowed'],
          deniedServiceIdentities: ['service-ns/denied'],
          policy: {
            resourceId: runtimeOwners[0].networkPolicyResourceId,
            uid: runtimeOwners[0].networkPolicyUid,
            namespace: 'p1-fixture',
            generation: 6,
            annotations: { generation: '6', hash: 'sha256:policy', revoked: 'false' },
            selectorLabels: { 'agentweaver.owner-id': 'owner-a' },
          },
          revoke: {
            policyGeneration: 7,
            expectedPreviousPolicyGeneration: 6,
            idempotencyKey: 'fixture-revoke-key',
            fence: environmentFence(owners[0]),
          },
          reconcileOperationId: policyOperationId,
        },
        activation: {
          ownerId: 'owner-a',
          contractVersion: 1,
          runtimeInstanceId: 'runtime-a',
          registrationRevision: 4,
          executionFence: owners[0].executionFence,
          image: { digest: imageDigest, platform: 'linux/amd64', compressedPullBytes: 1024 },
          startupPhases: [
            { phase: 'scheduled', contractVersion: 1, observedAt: phaseTimes.scheduled },
            {
              phase: 'imageReady',
              contractVersion: 1,
              observedAt: phaseTimes.imageReady,
              imageDigest,
              compressedPullBytes: 1024,
            },
            { phase: 'started', contractVersion: 1, observedAt: phaseTimes.started },
            { phase: 'configured', contractVersion: 1, observedAt: phaseTimes.configured },
            { phase: 'ready', contractVersion: 1, observedAt: phaseTimes.ready },
          ],
          sourceGrant: {
            grantId: 'grant-a',
            runtimeInstanceId: 'runtime-a',
            registrationRevision: 4,
            revision: 2,
          },
        },
        registration: {
          runtimeInstanceId: 'runtime-a',
          revision: 4,
          state: 'active',
          expiresAt: future,
          binding: {
            actorId: owners[0].actorId,
            tenantId: owners[0].tenantId,
            projectId: owners[0].projectId,
            runId: owners[0].runId,
            sessionId: owners[0].sessionId,
            environmentId: owners[0].environmentId,
            placementUid: runtimeOwners[0].sandboxResourceId,
            placementGeneration: runtimeOwners[0].sandboxResourceGeneration,
            profileId: 'agenthost-default',
            executionFence: owners[0].executionFence,
            environmentCurrentFencingGeneration: 5,
            environmentProviderFencingGeneration: 5,
          },
        },
      },
      storageReceipt: { owners },
      sourceReceipt: { acceptedOptions, startupBudget },
      imageReceipt: {
        reference: `registry.example.test/agenthost@${imageDigest}`,
        digest: imageDigest,
      },
      ownerFences: {
        owners: owners.map(owner => ({
          ...Object.fromEntries([
            'ownerId', 'actorId', 'tenantId', 'projectId', 'runId', 'environmentId',
            'rootSessionId', 'sessionId', 'executionFence',
          ].map(key => [key, owner[key]])),
          lifecycleFence: {
            lifecycleGeneration: 2,
            currentFencingGeneration: 5,
            leaseRevision: 7,
          },
          environmentFence: environmentFence(owner),
        })),
      },
      fixtureScope: {
        kind: 'p1-runner-owned',
        status: 'ready',
        invocationId,
        fixtureId: 'fixture-p1-1',
        expiresAt: future,
        allowedJourneys: ['opaque journey supplied by fixture owner'],
        ownershipReceipts,
      },
    },
  };
}

function blockedDependencies() {
  return {
    now: () => Date.parse('2026-01-01T00:00:00.000Z'),
    fetch: async () => {
      throw new Error('HTTP must not run in this test');
    },
    authProvider: {
      async getValidatedToken() {
        throw new Error('AUTH must not run in this test');
      },
    },
    journeyAuthorizer: {
      async authorize() {
        throw new Error('JOURNEY must not run in this test');
      },
    },
    processProvider: {
      async run() {
        throw new Error('PROCESS must not run in this test');
      },
    },
  };
}

function environmentVolumeSnapshot(volume) {
  const { owner, ...snapshot } = volume;
  const ownerKindValues = { Run: 0, Agent: 1, Team: 2 };
  return {
    ...snapshot,
    specification: {
      owner: { kind: ownerKindValues[owner.kind], id: owner.id },
    },
  };
}

function createCreditReaderInput() {
  const fixedZero = {
    ownerId: 'owner-a',
    actorId: 'actor-a',
    tenantId: 'tenant-a',
    projectId: 'project-a',
    runId: 'run-a',
    sessionId: 'session-a',
    environmentId: 'environment-a',
    runtimeInstanceId: '00000000-0000-4000-8000-000000000001',
    registrationRevision: 12,
    executionFence: 19,
    registrationHash: hash('1'),
    sourceHash: hash('2'),
    sourceGrantId: '00000000-0000-4000-8000-000000000011',
    sourceGrantRevision: 3,
    expectedHardCreditLimit: 0,
    stage: 'hostPreQueue',
    requestId: '00000000-0000-4000-8000-000000000021',
    requestHash: hash('3'),
  };
  const positive = {
    ...fixedZero,
    runtimeInstanceId: '00000000-0000-4000-8000-000000000002',
    registrationRevision: 13,
    executionFence: 20,
    registrationHash: hash('4'),
    sourceHash: hash('5'),
    sourceGrantId: '00000000-0000-4000-8000-000000000012',
    sourceGrantRevision: 4,
    expectedHardCreditLimit: 5,
    stage: 'nativeTurnBegin',
    dispatchId: '00000000-0000-4000-8000-000000000022',
  };
  const input = { fixedZero, positive };
  for (const [key, kind] of [['fixedZero', 'fixedZero'], ['positive', 'positive']]) {
    const { registration, sourceGrant, sourceReceipt } =
      createCurrentCreditReadback(input[key], kind);
    input[key].current = { registration, sourceGrant, sourceReceipt };
    input[key].proof = {
      contractVersion: 1,
      registration,
      sourceGrantId: sourceGrant.grantId,
      sourceGrantRevision: sourceGrant.revision,
      purpose: 'Observe',
    };
    if (key === 'positive') {
      registration.binding.maxPromptTokens = null;
      sourceReceipt.source.maxPromptTokens = null;
    }
  }
  return input;
}

function createCurrentCreditReadback(pin, kind) {
  const observationEndpoint = 'https://runtime.example.test/internal/runtime/observations';
  const registration = pin.current?.registration ?? {
    runtimeInstanceId: pin.runtimeInstanceId,
    revision: pin.registrationRevision,
    state: 'active',
    expiresAt: future,
    binding: {
      actorId: pin.actorId,
      tenantId: pin.tenantId,
      projectId: pin.projectId,
      runId: pin.runId,
      sessionId: pin.sessionId,
      environmentId: pin.environmentId,
      executionFence: pin.executionFence,
      copilotHardCreditLimit: pin.expectedHardCreditLimit,
      configureEndpoint: 'https://runtime.example.test/configure',
      observationEndpoint,
      actorIssuer: 'https://issuer.example.test',
      agentId: 'agent-a',
      turnId: 'turn-a',
      placementUid: 'pod-a',
      placementGeneration: 7,
      profileId: 'agenthost-default',
      acceptedSelectionHash: hash('a'),
      modelSelectionReference: 'selection-a',
      maxPromptTokens: 8192,
    },
  };
  const sourceGrant = pin.current?.sourceGrant ?? {
    grantId: pin.sourceGrantId,
    runtimeInstanceId: pin.runtimeInstanceId,
    registrationRevision: pin.registrationRevision,
    revision: pin.sourceGrantRevision,
    purpose: 'Observe',
    state: 'active',
    audience: observationEndpoint,
    issuer: 'https://issuer.example.test',
    configurationHash: hash('b'),
    expiresAt: future,
    recordedAt: '2026-01-01T00:04:00.000Z',
  };
  const sdkSessionId = 'sdk-session-a';
  const sourceReceipt = pin.current?.sourceReceipt ?? {
    runtimeInstanceId: pin.runtimeInstanceId,
    registrationRevision: pin.registrationRevision,
    sourceGrantId: pin.sourceGrantId,
    canonicalPayloadHash: pin.sourceHash,
    recordedAt: '2026-01-01T00:04:30.000Z',
    source: {
      runtimeInstanceId: pin.runtimeInstanceId,
      registrationRevision: pin.registrationRevision,
      sdkSessionId,
      sdkVersion: '1.0',
      runtimeVersion: '1.0',
      sourceMode: 'hosted-copilot',
      meterSource: 'copilot.nano_aiu',
      byokProvider: null,
      modelSelectionReference: registration.binding.modelSelectionReference,
      acceptedSelectionHash: registration.binding.acceptedSelectionHash,
      modelId: 'model-a',
      catalogHash: hash('c'),
      maxPromptTokens: 4096,
    },
  };
  let result;
  if (kind === 'fixedZero') {
    const receipt = {
      contractVersion: 1,
      stage: 'hostPreQueue',
      code: 'hardLimitZero',
      registration,
      registrationHash: pin.registrationHash,
      requestId: pin.requestId,
      requestHash: pin.requestHash,
      sourceHash: pin.sourceHash,
      sourceGrantId: pin.sourceGrantId,
      sourceGrantRevision: pin.sourceGrantRevision,
      recordedAt: '2026-01-01T00:05:00.000Z',
      canonicalPayloadHash: hash('d'),
    };
    result = {
      receipt,
      evidence: {
        contractVersion: 1,
        runtimeInstanceId: pin.runtimeInstanceId,
        registrationRevision: pin.registrationRevision,
        executionFence: pin.executionFence,
        requestId: pin.requestId,
        stage: 'hostPreQueue',
        guardReceiptHash: receipt.canonicalPayloadHash,
        registrationHash: pin.registrationHash,
        sourceHash: pin.sourceHash,
        sourceGrantId: pin.sourceGrantId,
        sourceGrantRevision: pin.sourceGrantRevision,
        nativeSessionHash: sha256(sourceReceipt.source.sdkSessionId),
        beforeCursorHash: hash('f'),
        afterCursorHash: hash('f'),
        observedEventCount: 0,
        rangeComplete: true,
        verifiedAt: '2026-01-01T00:05:30.000Z',
        canonicalPayloadHash: hash('6'),
      },
    };
  } else {
    const receipt = {
      contractVersion: 1,
      runtimeInstanceId: pin.runtimeInstanceId,
      registrationRevision: pin.registrationRevision,
      executionFence: pin.executionFence,
      sourceGrantId: pin.sourceGrantId,
      dispatchId: pin.dispatchId,
      stage: 'nativeTurnBegin',
      code: 'hardLimitReached',
      registrationHash: pin.registrationHash,
      sourceHash: pin.sourceHash,
      dispatchHash: hash('7'),
      checkpointHash: hash('8'),
      hardCreditLimit: pin.expectedHardCreditLimit,
      observedCredits: pin.expectedHardCreditLimit,
      ledgerEventCount: 1,
      ledgerDigest: hash('9'),
      costIdentity: hash('a'),
      guardRevisionBefore: 1,
      guardRevisionAfter: 2,
      dispatchStateBefore: 'prepared',
      dispatchRevisionBefore: 1,
      dispatchStateAfter: 'prepared',
      dispatchRevisionAfter: 1,
      recordedAt: '2026-01-01T00:05:00.000Z',
      canonicalPayloadHash: hash('b'),
    };
    result = {
      receipt,
      noSend: {
        contractVersion: 1,
        runtimeInstanceId: pin.runtimeInstanceId,
        registrationRevision: pin.registrationRevision,
        dispatchId: pin.dispatchId,
        denialReceiptHash: receipt.canonicalPayloadHash,
        registrationHash: pin.registrationHash,
        sourceHash: pin.sourceHash,
        nativeSessionHash: sha256(sourceReceipt.source.sdkSessionId),
        beforeCursorHash: hash('d'),
        afterCursorHash: hash('d'),
        observedEventCount: 0,
        rangeComplete: true,
        verifiedAt: '2026-01-01T00:05:30.000Z',
        canonicalPayloadHash: hash('e'),
      },
    };
  }
  return { registration, sourceGrant, sourceReceipt, result };
}

function createCreditHostCaller(input, corrupt = () => {}) {
  return {
    async postJson(path, body) {
      const kind = path === '/runtime/v1/turns/fixed-credit-denials/read'
        ? 'fixedZero'
        : 'positive';
      const pin = input[kind];
      const expectedBody = kind === 'fixedZero'
        ? { proof: pin.proof, requestId: pin.requestId, requestHash: pin.requestHash }
        : { proof: pin.proof, dispatchId: pin.dispatchId, stage: pin.stage };
      assert.deepEqual(body, expectedBody);
      const result = createCurrentCreditReadback(pin, kind).result;
      corrupt(kind, result, input);
      return result;
    },
  };
}

function successDependencies(input, { token = ownerId => `private-${ownerId}` } = {}) {
  const owners = input.pins.storageReceipt.owners;
  const runtimes = input.pins.runtimeReceipt.owners;
  let operationSequence = 0;
  const nextOperationId = () => `operation-${++operationSequence}`;
  const stateByOwner = new Map(owners.map(owner => {
    const runtime = runtimes.find(item => item.ownerId === owner.ownerId);
    const sandboxReceipt = input.pins.fixtureScope.ownershipReceipts.find(item =>
      item.resourceId === runtime.sandboxResourceId);
    return [owner.ownerId, {
      owner,
      runtime,
      volume: { ...owner.volume },
      current: {
        resourceId: runtime.sandboxResourceId,
        generation: runtime.sandboxResourceGeneration,
        uid: runtime.sandboxUid,
        operationId: sandboxReceipt.creationReceipt.operationId ??
          runtime.sandboxLabels[sandboxLabelKeys.operation],
      },
      provisionCount: 0,
      lateExists: true,
      dependentPods: [],
    }];
  }));
  const podOwnerByUid = new Map();
  const podByResourceId = new Map();
  for (const state of stateByOwner.values()) {
    podOwnerByUid.set(state.current.uid, state.owner.ownerId);
    podByResourceId.set(state.current.resourceId, state);
  }
  const mountedFiles = new Map();
  const mountKey = args => `${podOwnerByUid.get(args.podUid)}:${args.path}`;
  const calls = [];
  const recipes = [];
  const recipeRequests = [];
  const authorizations = [];
  const response = (status, body) => ({ status, body });
  const placementFor = state => {
    if (!state.current) return null;
    const owner = state.owner;
    return {
      contractVersion: 1,
      tenantId: owner.tenantId,
      projectId: owner.projectId,
      runId: owner.runId,
      environmentId: owner.environmentId,
      lifecycleGeneration: 2,
      currentFencingGeneration: 5,
      providerFencingGeneration: 5,
      leaseRevision: 7,
      leaseExpiresAt: future,
      isCurrent: true,
      state: 'active',
      resource: {
        providerId: 'agent-sandbox',
        resourceId: state.current.resourceId,
        generation: state.current.generation,
      },
      endpoint: { value: `endpoint-${owner.ownerId}` },
      placement: { value: `placement-${owner.ownerId}` },
      ...(owner.ownerId === 'owner-a'
        ? { image: input.pins.runtimeReceipt.activation.image }
        : {}),
    };
  };
  const authOwnerForRequest = (url, init) => {
    const owner = owners.find(item => url.pathname.includes(`/environments/${item.environmentId}/`));
    if (!owner) return null;
    const tenant = init.headers['X-Agentweaver-Tenant'];
    return { owner, forbidden: tenant !== owner.tenantId };
  };
  const fetch = async (urlValue, init) => {
    const url = new URL(String(urlValue));
    calls.push({ url: url.href, method: init.method, tenant: init.headers['X-Agentweaver-Tenant'] });
    const body = init.body ? JSON.parse(init.body) : undefined;
    if (url.pathname === '/api/network-egress/revoke') {
      stateByOwner.get('owner-a').policyRevoked = true;
      stateByOwner.get('owner-a').policyGeneration = 7;
      return response(200, {
        readyForDispatch: false,
        revoked: true,
        appliedState: { appliedIntentGeneration: 7, revoked: true },
        binding: null,
        failureCode: null,
        failureMessage: null,
      });
    }
    if (url.pathname === '/api/network-egress/reconcile') {
      return response(200, {
        readyForDispatch: false,
        revoked: true,
        appliedState: { appliedIntentGeneration: 7, revoked: true },
        binding: null,
        failureCode: null,
        failureMessage: null,
      });
    }
    const target = authOwnerForRequest(url, init);
    if (!target) return response(404, null);
    if (target.forbidden) return response(403, null);
    const state = stateByOwner.get(target.owner.ownerId);
    if (url.pathname.endsWith('/lifecycle')) {
      return response(200, {
        fence: input.pins.ownerFences.owners.find(item => item.ownerId === target.owner.ownerId).environmentFence,
        state: 'active',
      });
    }
    if (url.pathname.endsWith(`/workspace-volumes/${target.owner.volume.volumeId}`)) {
      return response(200, environmentVolumeSnapshot(state.volume));
    }
    if (url.pathname.endsWith('/sandbox/v1/internal/placement')) {
      const placement = placementFor(state);
      if (!placement) return response(404, null);
      return response(200, {
        ...placement,
        runtimeReadiness: {
          leaseCreatedAt,
          observation: {
            resource: placement.resource,
            state: 'ready',
            fencingGeneration: placement.providerFencingGeneration,
            vmIsolationVerified: true,
            workspaceAttachmentVerified: true,
            verifiedNetworkGeneration: input.pins.runtimeReceipt.network.generation,
            startupPhases: [
              {
                phase: 'scheduled',
                contractVersion: 1,
                observedAt: phaseTimes.scheduled,
              },
              {
                phase: 'imageReady',
                contractVersion: 1,
                observedAt: phaseTimes.imageReady,
                imageDigest: input.pins.runtimeReceipt.activation.image.digest,
                compressedPullBytes: input.pins.runtimeReceipt.activation.image.compressedPullBytes,
              },
              {
                phase: 'started',
                contractVersion: 1,
                observedAt: phaseTimes.started,
              },
            ],
            startupFailure: null,
          },
          startupBudgets: input.pins.sourceReceipt.startupBudget,
          workspaceMountPath: state.owner.volume.mountPath,
        },
      });
    }
    if (url.pathname.endsWith('/sandbox/v1/placement')) {
      const placement = placementFor(state);
      return placement ? response(200, placement) : response(404, null);
    }
    if (url.pathname.endsWith('/sandbox/provision') && init.method === 'POST') {
      state.provisionCount += 1;
      const resourceId = `${target.owner.ownerId}-sandbox-${state.provisionCount}`;
      const generation = 20 + state.provisionCount;
      const provisioned = {
        resourceId,
        generation,
        uid: `pod-${resourceId}`,
        operationId: nextOperationId(),
      };
      state.current = provisioned;
      podOwnerByUid.set(provisioned.uid, target.owner.ownerId);
      podByResourceId.set(resourceId, state);
      return response(200, {
        operationId: provisioned.operationId,
        state: 'active',
        resource: {
          providerId: 'agent-sandbox',
          resourceId,
          generation,
        },
        readyForDispatch: false,
      });
    }
    if (url.pathname.endsWith('/sandbox/abandon') && init.method === 'POST') {
      const abandoned = state.current;
      if (!abandoned || body.resourceGeneration !== abandoned.generation ||
          body.providerFencingGeneration !== 5) return response(409, {});
      state.current = null;
      return response(200, {
        operationId: nextOperationId(),
        state: 'released',
        resourceGeneration: abandoned.generation,
        providerFencingGeneration: 5,
        currentFencingGeneration: 5,
        resource: {
          providerId: 'agent-sandbox',
          resourceId: abandoned.resourceId,
          generation: abandoned.generation,
        },
      });
    }
    if (url.pathname.endsWith('/sandbox/reconcile') && init.method === 'POST') {
      state.lateExists = false;
      return response(200, {
        operationId: nextOperationId(),
        state: 'released',
        resource: null,
        readyForDispatch: false,
      });
    }
    if (/\/workspace-volumes\/[^/]+\/(?:release|bind)$/.test(url.pathname) && init.method === 'POST') {
      const operation = url.pathname.endsWith('/release') ? 'release' : 'bind';
      if (body.expectedTransitionRevision !== state.volume.transitionRevision) return response(409, {});
      state.volume = {
        ...state.volume,
        transitionRevision: state.volume.transitionRevision + 1,
        phase: operation === 'release' ? 'released' : 'bound',
      };
      return response(200, { state: 'Completed', transitionRevision: state.volume.transitionRevision });
    }
    if (url.pathname.endsWith('/sandbox') && url.searchParams.has('networkPolicyGeneration')) {
      const current = state.current;
      const resource = {
        providerId: 'agent-sandbox',
        resourceId: current.resourceId,
        generation: current.generation,
      };
      return response(202, {
        operationId: current.operationId,
        state: 'active',
        resourceGeneration: current.generation,
        providerFencingGeneration: 5,
        currentFencingGeneration: 5,
        resource,
        readyForDispatch: false,
        observation: {
          resource,
          state: 'pending',
          fencingGeneration: 5,
          verifiedNetworkGeneration: 6,
        },
      });
    }
    return response(404, null);
  };
  const processProvider = {
    async run({ recipeId, args }) {
      recipes.push(recipeId);
      recipeRequests.push({ recipeId, args });
      const base = { clusterIdentity: input.pins.context.clusterIdentity };
      if (recipeId === 'observe-cluster') return base;
      if (recipeId === 'verify-hubble') {
        return { ...base, relayEndpoint: '127.0.0.1:4245', healthy: true };
      }
      if (recipeId === 'observe-storage') {
        const owner = owners.find(item => item.ownerId === args.owner.ownerId);
        return {
          ...base,
          pvc: owner.volume.pvc,
          pv: owner.volume.pv,
          claimUid: owner.volume.claimUid,
          providerBinding: owner.volume.providerBinding,
        };
      }
      if (recipeId === 'observe-owned-resource') {
        const state = stateByOwner.get(args.owner.ownerId);
        return {
          ...base,
          exists: state.lateExists,
          uid: args.expectedUid,
          ownerId: args.owner.ownerId,
          dependentPods: state.dependentPods,
        };
      }
      if (recipeId === 'observe-sandbox') {
        const resourceId = args.placement.resource.resourceId;
        const state = podByResourceId.get(resourceId);
        const owner = state.owner;
        const runtime = state.runtime;
        const current = state.current?.resourceId === resourceId
          ? state.current
          : {
            resourceId,
            generation: args.placement.resource.generation,
            uid: runtime.sandboxUid,
            operationId: input.pins.fixtureScope.ownershipReceipts.find(item =>
              item.resourceId === resourceId)?.creationReceipt.operationId,
          };
        const labels = {
          ...runtime.sandboxLabels,
          [sandboxLabelKeys.claimUid]: resourceId,
          [sandboxLabelKeys.lifecycle]: String(args.placement.lifecycleGeneration),
          [sandboxLabelKeys.resourceGeneration]: String(args.placement.resource.generation),
          [sandboxLabelKeys.fencingGeneration]: String(args.placement.providerFencingGeneration),
          [sandboxLabelKeys.operation]: current.operationId.replaceAll('-', '').toLowerCase(),
          'agentweaver.owner-id': owner.ownerId,
        };
        const result = {
          ...base,
          pod: {
            name: `pod-${resourceId}`,
            uid: current.uid,
            labels,
            containers: [{ name: 'agenthost', image: input.pins.imageReceipt.reference }],
            mounts: [{
              name: 'agentweaver-workspace',
              containerName: 'agenthost',
              pvcName: owner.volume.pvc.name,
              mountPath: owner.volume.mountPath,
              pvcUid: owner.volume.pvc.uid,
            }],
          },
        };
        if (owner.ownerId === 'owner-a') {
          result.runtimeReadinessReceipt = {
            contractVersion: input.pins.runtimeReceipt.activation.contractVersion,
            runtimeInstanceId: input.pins.runtimeReceipt.activation.runtimeInstanceId,
            registrationRevision: input.pins.runtimeReceipt.activation.registrationRevision,
            executionFence: input.pins.runtimeReceipt.activation.executionFence,
            image: input.pins.runtimeReceipt.activation.image,
            startupPhases: input.pins.runtimeReceipt.activation.startupPhases,
            sourceGrant: input.pins.runtimeReceipt.activation.sourceGrant,
          };
          result.acceptedOptions = input.pins.sourceReceipt.acceptedOptions;
          result.startupBudget = input.pins.sourceReceipt.startupBudget;
        }
        return result;
      }
      if (recipeId === 'mount-file') {
        const key = mountKey(args);
        const file = mountedFiles.get(key);
        if (args.operation === 'stat') {
          return file
            ? {
              ...base,
              exists: true,
              ownerId: podOwnerByUid.get(args.podUid),
              podUid: args.podUid,
              content: file.content,
            }
            : {
              ...base,
              exists: false,
              ownerId: podOwnerByUid.get(args.podUid),
              podUid: args.podUid,
            };
        }
        if (args.operation === 'write') {
          const ownerId = podOwnerByUid.get(args.podUid);
          mountedFiles.set(key, {
            content: args.content,
            ownerId,
          });
          return {
            ...base,
            written: true,
            sha256: createHash('sha256').update(args.content).digest('hex'),
            ownerId,
            podUid: args.podUid,
          };
        }
        if (args.operation === 'read') {
          return file
            ? {
              ...base,
              exists: true,
              content: file.content,
              ownerId: podOwnerByUid.get(args.podUid),
              podUid: args.podUid,
            }
            : {
              ...base,
              exists: false,
              ownerId: podOwnerByUid.get(args.podUid),
              podUid: args.podUid,
            };
        }
        if (args.operation === 'unlink') {
          if (!file || file.content !== args.expectedContent ||
              args.podUid !== args.expectedPodUid ||
              file.ownerId !== podOwnerByUid.get(args.podUid)) {
            return {
              ...base,
              removed: false,
              ownerId: podOwnerByUid.get(args.podUid),
              podUid: args.podUid,
            };
          }
          mountedFiles.delete(key);
          return {
            ...base,
            removed: true,
            ownerId: podOwnerByUid.get(args.podUid),
            podUid: args.podUid,
          };
        }
      }
      if (recipeId === 'observe-network-policy') {
        const ownerState = stateByOwner.get(input.pins.runtimeReceipt.network.ownerId);
        return {
          ...base,
          uid: input.pins.runtimeReceipt.network.policy.uid,
          appliedIntentGeneration: ownerState.policyGeneration ?? input.pins.runtimeReceipt.network.generation,
          revoked: ownerState.policyRevoked === true,
          intentHash: input.pins.runtimeReceipt.network.policy.annotations.hash,
          selectorLabels: input.pins.runtimeReceipt.network.policy.selectorLabels,
        };
      }
      if (recipeId === 'probe-service') {
        const allowed = input.pins.runtimeReceipt.network.allowedServiceIdentities.includes(args.serviceIdentity);
        return {
          ...base,
          sourcePodUid: args.sourcePodUid,
          serviceIdentity: args.serviceIdentity,
          policyUid: args.networkPolicyUid,
          policyGeneration: args.networkPolicyGeneration,
          targetServiceUid: 'service-endpoint-uid',
          readyEndpointCount: 1,
          probedPorts: [80],
          portVerdicts: [{
            servicePort: 80,
            targetPort: 8080,
            verdict: allowed ? 'FORWARDED' : 'DROPPED',
            connected: allowed,
          }],
          datapathVerdict: allowed ? 'allowed' : 'denied',
          connected: allowed,
        };
      }
      throw new Error(`Unexpected fixed recipe: ${recipeId}`);
    },
  };
  return {
    calls,
    recipes,
    recipeRequests,
    authorizations,
    states: stateByOwner,
    dependencies: {
      now: () => Date.parse('2026-01-01T00:06:00.000Z'),
      fetch,
      authProvider: {
        async getValidatedToken(selector) {
          return {
            validated: true,
            token: token(selector.ownerId),
            issuer: input.environment.issuer,
            audience: input.environment.audience,
            actorId: selector.actorId,
            tenantId: selector.tenantId,
          };
        },
      },
      journeyAuthorizer: {
        async authorize(request) {
          authorizations.push(request);
          return { allowed: true, journey: input.pins.fixtureScope.allowedJourneys[0] };
        },
      },
      runtimeReadinessProvider: {
        async readCurrent() {
          return {
            registration: input.pins.runtimeReceipt.registration,
            readinessReceipt: input.pins.runtimeReceipt.activation,
            acceptedOptions: input.pins.sourceReceipt.acceptedOptions,
          };
        },
      },
      processProvider,
    },
  };
}

test('missing input blocks all four probes without invoking dependencies', async () => {
  const result = await runP1NativeProbes({}, blockedDependencies());
  assert.equal(result.kind, 'p1-native-acceptance');
  assert.equal(result.schemaVersion, 1);
  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.probes.length, 4);
  assert.deepEqual(result.probes.map(probe => probe.status), ['blocked', 'blocked', 'blocked', 'blocked']);
  assert.ok(result.probes.every(probe => probe.cleanup.status === 'not-started'));
  assert.ok(Date.parse(result.completedAt) >= Date.parse(result.startedAt));
});

test('a missing runtime readiness reader blocks before dependency execution', async () => {
  const input = createInput();
  const harness = successDependencies(input);
  delete harness.dependencies.runtimeReadinessProvider;

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'runtime_current_readback_unavailable'));
  assert.deepEqual(harness.calls, []);
  assert.deepEqual(harness.recipes, []);
  assert.deepEqual(harness.authorizations, []);
});

test('a mismatching current runtime read blocks every probe before mutation', async () => {
  const input = createInput();
  const harness = successDependencies(input);
  harness.dependencies.runtimeReadinessProvider.readCurrent = async () => ({
    registration: {
      ...input.pins.runtimeReceipt.registration,
      revision: input.pins.runtimeReceipt.registration.revision + 1,
    },
    readinessReceipt: input.pins.runtimeReceipt.activation,
    acceptedOptions: input.pins.sourceReceipt.acceptedOptions,
  });

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'agenthost_current_readback_mismatch'));
  assert.deepEqual(harness.authorizations, []);
  assert.ok(harness.calls.every(call => call.method === 'GET'));
});

test('registration claim UID and native Pod UID remain independent through readiness', async () => {
  const input = createInput();
  const runtime = input.pins.runtimeReceipt.owners[0];
  const harness = successDependencies(input);
  const reads = [];
  const readCurrent = harness.dependencies.runtimeReadinessProvider.readCurrent;
  harness.dependencies.runtimeReadinessProvider.readCurrent = async request => {
    reads.push(request);
    return readCurrent(request);
  };

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'passed', JSON.stringify(result.probes));
  assert.notEqual(runtime.sandboxResourceId, runtime.sandboxUid);
  assert.ok(reads.length >= 2);
  for (const request of reads) {
    assert.equal(request.placement.resource.resourceId, runtime.sandboxResourceId);
    assert.equal(request.expectedPodUid, runtime.sandboxUid);
  }
  const evidence = result.probes.find(probe => probe.name === 'agenthost-image-readiness').evidence;
  assert.equal(evidence.placementUid, runtime.sandboxResourceId);
  assert.equal(evidence.podUid, runtime.sandboxUid);
});

test('a pinned Pod UID cannot masquerade as the registration claim UID', async () => {
  for (const placementUid of ['foreign-claim-uid', 'pod-uid-a']) {
    const input = createInput();
    input.pins.runtimeReceipt.registration.binding.placementUid = placementUid;
    const harness = successDependencies(input);

    const result = await runP1NativeProbes(input, harness.dependencies);

    assert.equal(result.overallStatus, 'blocked');
    assert.ok(result.probes.every(probe => probe.code === 'runtime_owner_fence_mismatch'));
    assert.deepEqual(harness.calls, []);
    assert.deepEqual(harness.recipes, []);
    assert.deepEqual(harness.authorizations, []);
  }
});

test('a current registration with a replaced claim UID is rejected before mutation', async () => {
  const input = createInput();
  const harness = successDependencies(input);
  const readCurrent = harness.dependencies.runtimeReadinessProvider.readCurrent;
  harness.dependencies.runtimeReadinessProvider.readCurrent = async request => {
    const current = structuredClone(await readCurrent(request));
    current.registration.binding.placementUid = 'replaced-claim-uid';
    return current;
  };

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'agenthost_current_readback_mismatch'));
  assert.deepEqual(harness.authorizations, []);
  assert.ok(harness.calls.every(call => call.method === 'GET'));
});

test('a replaced native Pod UID is rejected even when the claim UID is unchanged', async () => {
  const input = createInput();
  const harness = successDependencies(input);
  const run = harness.dependencies.processProvider.run;
  harness.dependencies.processProvider.run = async request => {
    const observed = await run(request);
    if (request.recipeId === 'observe-sandbox') observed.pod.uid = 'replacement-pod-uid';
    return observed;
  };

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'sandbox_uid_mismatch'));
  assert.deepEqual(harness.authorizations, []);
  assert.ok(harness.calls.every(call => call.method === 'GET'));
});

test('standalone reader accepts production-shaped enum values and returns safe summaries', async () => {
  const input = createCreditReaderInput();
  const calls = [];
  const replies = {};
  const result = await runP1NativeCreditDenialReaders(input, {
    hostCaller: {
      async postJson(path, body) {
        calls.push({ path, body });
        const kind = path.includes('fixed-credit-denials') ? 'fixedZero' : 'positive';
        replies[kind] = createCurrentCreditReadback(input[kind], kind).result;
        return replies[kind];
      },
    },
  });

  assert.equal(result.kind, 'p1-native-credit-denial-readers');
  assert.equal(result.overallStatus, 'passed');
  assert.deepEqual(calls.map(call => call.path), [
    '/runtime/v1/turns/fixed-credit-denials/read',
    '/runtime/v1/turns/denials/read',
  ]);
  assert.deepEqual(calls[0].body, {
    proof: input.fixedZero.proof,
    requestId: input.fixedZero.requestId,
    requestHash: input.fixedZero.requestHash,
  });
  assert.deepEqual(calls[1].body, {
    proof: input.positive.proof,
    dispatchId: input.positive.dispatchId,
    stage: input.positive.stage,
  });
  assert.equal(replies.fixedZero.receipt.stage, 'hostPreQueue');
  assert.equal(replies.fixedZero.receipt.code, 'hardLimitZero');
  assert.equal(replies.positive.receipt.stage, 'nativeTurnBegin');
  assert.equal(replies.positive.receipt.code, 'hardLimitReached');
  assert.deepEqual(Object.keys(result.cases), ['fixedZero', 'positive']);
  assert.equal(result.cases.fixedZero.evidence.stage, 'hostPreQueue');
  assert.equal(input.fixedZero.stage, 'hostPreQueue');
  assert.equal(result.cases.positive.evidence.stage, 'nativeTurnBegin');
  assert.equal(input.fixedZero.current.registration.binding.maxPromptTokens, 8192);
  assert.equal(input.fixedZero.current.sourceReceipt.source.maxPromptTokens, 4096);
  assert.equal(input.positive.current.registration.binding.maxPromptTokens, null);
  assert.equal(input.positive.current.sourceReceipt.source.maxPromptTokens, null);
  const serialized = JSON.stringify(result);
  assert.equal(serialized.includes('observationEndpoint'), false);
  assert.equal(serialized.includes('configureEndpoint'), false);
  assert.equal(serialized.includes('modelSelectionReference'), false);
});

test('dispatch recovery keeps a valid denial receipt when its sibling case blocks', async () => {
  const input = createCreditReaderInput();
  const result = await runP1DispatchRecoveryProbes({
    creditDenialInput: input,
    creditDenialDependencies: {
      hostCaller: createCreditHostCaller(input, (kind, response) => {
        if (kind === 'positive') response.receipt.stage = 'Preparation';
      }),
    },
  });

  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.liveObservations.creditDenials.status, 'blocked');
  assert.equal(result.liveObservations.creditDenials.cases.fixedZero.status, 'passed');
  assert.equal(result.liveObservations.creditDenials.cases.positive.status, 'blocked');
  const observation = result.probes[0].evidence.observations.find(item =>
    item.kind === 'partial-host-credit-denial-read-results');
  assert.ok(observation);
  assert.equal(observation.cases.fixedZero.status, 'passed');
  assert.equal(observation.cases.positive.status, 'blocked');
  assert.equal(result.probes[0].status, 'blocked');
});

test('standalone credit readers reject stale current context and incomplete denial evidence', async () => {
  const corruptions = [
    ['stale fixed registration revision', 'fixedZero', (_result, input) => {
      input.fixedZero.current.registration.revision += 1;
    }, 'credit_reader_current_context_mismatch'],
    ['stale positive registration revision', 'positive', result => {
      result.receipt.registrationRevision += 1;
    }, 'credit_reader_receipt_invalid'],
    ['wrong source grant revision', 'fixedZero', (_result, input) => {
      input.fixedZero.current.sourceGrant.revision += 1;
    }, 'credit_reader_current_context_mismatch'],
    ['wrong fixed request ID', 'fixedZero', result => {
      result.receipt.requestId = 'stale-request-id';
    }, 'credit_reader_receipt_invalid'],
    ['wrong native dispatch stage', 'positive', result => {
      result.receipt.stage = 'Preparation';
    }, 'credit_reader_receipt_invalid'],
    ['incomplete fixed no-send range', 'fixedZero', result => {
      result.evidence.observedEventCount = 1;
    }, 'credit_reader_receipt_invalid'],
    ['wrong fixed native session hash', 'fixedZero', result => {
      result.evidence.nativeSessionHash = hash('a');
    }, 'credit_reader_receipt_invalid'],
    ['incomplete native no-send range', 'positive', result => {
      result.noSend.afterCursorHash = hash('f');
    }, 'credit_reader_receipt_invalid'],
    ['wrong native no-send session hash', 'positive', result => {
      result.noSend.nativeSessionHash = hash('a');
    }, 'credit_reader_receipt_invalid'],
  ];

  for (const [label, kind, corrupt, expectedCode] of corruptions) {
    const input = createCreditReaderInput();
    const result = await runP1NativeCreditDenialReaders(input, {
      hostCaller: createCreditHostCaller(input, (caseKind, response, cases) => {
        if (caseKind === kind) corrupt(response, cases);
      }),
    });
    assert.equal(result.overallStatus, 'blocked', label);
    assert.equal(result.cases[kind].code, expectedCode, label);
    assert.equal(result.cases[kind === 'fixedZero' ? 'positive' : 'fixedZero'].status, 'passed', label);
  }
});

test('standalone credit readers fail closed without an authorized Host caller or with stale pins', async () => {
  const input = createCreditReaderInput();
  const missing = await runP1NativeCreditDenialReaders(input, {});
  assert.equal(missing.overallStatus, 'blocked');
  assert.equal(missing.cases.fixedZero.code, 'credit_source_reader_unavailable');
  assert.equal(missing.cases.positive.code, 'credit_source_reader_unavailable');

  const sameRuntime = createCreditReaderInput();
  sameRuntime.positive.runtimeInstanceId = sameRuntime.fixedZero.runtimeInstanceId;
  const stale = await runP1NativeCreditDenialReaders(sameRuntime, {
    hostCaller: {
      async postJson() {
        assert.fail('Host caller must not run for stale pins');
      },
    },
  });
  assert.equal(stale.overallStatus, 'blocked');
  assert.equal(stale.cases.fixedZero.code, 'credit_reader_instances_not_distinct');
});

test('standalone credit readers require nonempty GUID runtime, grant, request, and dispatch IDs', async () => {
  const invalidPins = [
    ['empty runtime GUID', input => { input.fixedZero.runtimeInstanceId = '00000000-0000-0000-0000-000000000000'; }],
    ['empty source grant GUID', input => { input.positive.sourceGrantId = '00000000-0000-0000-0000-000000000000'; }],
    ['empty fixed request GUID', input => { input.fixedZero.requestId = '00000000-0000-0000-0000-000000000000'; }],
    ['empty positive dispatch GUID', input => { input.positive.dispatchId = '00000000-0000-0000-0000-000000000000'; }],
    ['invalid fixed request GUID', input => { input.fixedZero.requestId = 'fixed-request'; }],
    ['invalid positive dispatch GUID', input => { input.positive.dispatchId = 'positive-dispatch'; }],
  ];
  for (const [label, corrupt] of invalidPins) {
    const input = createCreditReaderInput();
    corrupt(input);
    const result = await runP1NativeCreditDenialReaders(input, {
      hostCaller: {
        async postJson() {
          assert.fail(`Host caller must not run for ${label}`);
        },
      },
    });
    assert.equal(result.overallStatus, 'blocked', label);
    assert.equal(result.cases.fixedZero.code, 'credit_reader_input_incomplete', label);
  }
});

test('standalone credit reader permits typed prompt limits but rejects credential fields', async () => {
  const input = createCreditReaderInput();
  input.fixedZero.current.registration.binding.accessToken = 'must-not-be-forwarded';
  const result = await runP1NativeCreditDenialReaders(input, {
    hostCaller: {
      async postJson() {
        assert.fail('Host caller must not run with credential material in context');
      },
    },
  });

  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.cases.fixedZero.code, 'credential_in_input');
  assert.equal(JSON.stringify(result).includes('must-not-be-forwarded'), false);

  const invalidPromptLimit = createCreditReaderInput();
  invalidPromptLimit.fixedZero.current.sourceReceipt.source.maxPromptTokens = '4096';
  const invalid = await runP1NativeCreditDenialReaders(invalidPromptLimit, {
    hostCaller: {
      async postJson() {
        assert.fail('Host caller must not receive an invalid prompt limit');
      },
    },
  });
  assert.equal(invalid.overallStatus, 'blocked');
  assert.equal(invalid.cases.fixedZero.code, 'credential_in_input');
});

test('standalone source-reader errors do not expose source proof details', async () => {
  const input = createCreditReaderInput();
  const proofSecret = 'current-source-proof-secret';
  const result = await runP1NativeCreditDenialReaders(input, {
    hostCaller: {
      async postJson(path) {
        if (path.includes('fixed-credit-denials')) {
          throw new Error(`proof rejected: ${proofSecret}`);
        }
        return createCurrentCreditReadback(input.positive, 'positive').result;
      },
    },
  });

  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.cases.fixedZero.code, 'credit_source_read_unavailable');
  assert.equal(JSON.stringify(result).includes(proofSecret), false);
});

test('standalone credit readers recheck registration and grant expiry after each read', async () => {
  const input = createCreditReaderInput();
  const expiresAt = '2026-01-01T00:05:20.000Z';
  for (const pin of [input.fixedZero, input.positive]) {
    pin.current.registration.expiresAt = expiresAt;
    pin.current.sourceGrant.expiresAt = expiresAt;
  }
  let clock = Date.parse('2026-01-01T00:05:10.000Z');
  const result = await runP1NativeCreditDenialReaders(input, {
    now: () => clock,
    hostCaller: {
      async postJson(path) {
        clock = Date.parse('2026-01-01T00:05:21.000Z');
        const kind = path.includes('fixed-credit-denials') ? 'fixedZero' : 'positive';
        return createCurrentCreditReadback(input[kind], kind).result;
      },
    },
  });

  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.cases.fixedZero.code, 'credit_reader_current_context_mismatch');
  assert.equal(result.cases.positive.code, 'credit_reader_current_context_mismatch');
});

test('standalone credit reads block missing stored pairs without issuing record requests', async () => {
  for (const missingKind of ['fixedZero', 'positive']) {
    const input = createCreditReaderInput();
    const paths = [];
    const result = await runP1NativeCreditDenialReaders(input, {
      hostCaller: {
        async postJson(path) {
          paths.push(path);
          if ((missingKind === 'fixedZero' &&
              path === '/runtime/v1/turns/fixed-credit-denials/read') ||
              (missingKind === 'positive' &&
              path === '/runtime/v1/turns/denials/read')) {
            return missingKind === 'fixedZero'
              ? { receipt: null, evidence: null }
              : { receipt: null, noSend: null };
          }
          const kind = path.includes('fixed-credit-denials') ? 'fixedZero' : 'positive';
          return createCurrentCreditReadback(input[kind], kind).result;
        },
      },
    });

    assert.equal(result.overallStatus, 'blocked', missingKind);
    assert.equal(result.cases[missingKind].code, 'credit_reader_receipt_invalid', missingKind);
    assert.equal(result.cases[missingKind === 'fixedZero' ? 'positive' : 'fixedZero'].status,
      'passed', missingKind);
    assert.deepEqual(paths, [
      '/runtime/v1/turns/fixed-credit-denials/read',
      '/runtime/v1/turns/denials/read',
    ]);
  }
});

test('standalone credit reads reject a proof that does not bind the pinned current grant', async () => {
  const input = createCreditReaderInput();
  input.fixedZero.proof.sourceGrantRevision += 1;
  const result = await runP1NativeCreditDenialReaders(input, {
    hostCaller: {
      async postJson() {
        assert.fail('Host caller must not receive a mismatched proof');
      },
    },
  });

  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.cases.fixedZero.code, 'credit_reader_proof_mismatch');
});

test('fixture ownership mismatch blocks before auth or native execution', async () => {
  const input = createInput();
  input.pins.fixtureScope.ownershipReceipts[0].invocationId = 'another-invocation';
  const result = await runP1NativeProbes(input, blockedDependencies());
  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'invocation_ownership_receipt_missing'),
    JSON.stringify(result.probes.map(({ code, reason }) => ({ code, reason }))));
});

test('live Environment volume owner must match the pinned volume owner', async () => {
  const input = createInput();
  const harness = successDependencies(input);
  const fetch = harness.dependencies.fetch;
  harness.dependencies.fetch = async (url, init) => {
    const response = await fetch(url, init);
    if (new URL(String(url)).pathname.endsWith('/workspace-volumes/volume-a')) {
      return {
        ...response,
        body: {
          ...response.body,
          specification: { owner: { kind: 2, id: 'foreign-team' } },
        },
      };
    }
    return response;
  };

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'live_volume_receipt_mismatch'));
  assert.ok(harness.recipes.every(recipeId => recipeId === 'observe-sandbox'));
});

test('Hubble prerequisite blocks before any probe mutation', async () => {
  const input = createInput();
  const harness = successDependencies(input);
  const nativeRun = harness.dependencies.processProvider.run;
  harness.dependencies.processProvider.run = async request => {
    if (request.recipeId === 'verify-hubble') {
      throw new Error('Hubble is unavailable');
    }
    return nativeRun(request);
  };

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'hubble_prerequisite_unavailable'));
  assert.deepEqual(harness.authorizations, []);
  assert.ok(harness.calls.every(call => call.method === 'GET'));
});

test('run and session identity, execution fences, and explicit receipt resource IDs are mandatory', async () => {
  const corruptions = [
    input => {
      input.pins.ownerFences.owners[0].rootSessionId = 'foreign-root-session';
    },
    input => {
      input.pins.runtimeReceipt.owners[0].sessionId = 'foreign-session';
    },
    input => {
      input.pins.fixtureScope.ownershipReceipts[0].ownerFence.executionFence += 1;
    },
    input => {
      delete input.pins.fixtureScope.ownershipReceipts[0].creationReceipt.resourceId;
    },
    input => {
      delete input.pins.fixtureScope.ownershipReceipts[0].observedReceipt.resourceId;
    },
  ];
  for (const corrupt of corruptions) {
    const input = createInput();
    corrupt(input);
    const result = await runP1NativeProbes(input, blockedDependencies());
    assert.equal(result.overallStatus, 'blocked');
    assert.ok(result.probes.every(probe => probe.status === 'blocked'));
  }
});

test('same actor is accepted for distinct tenant scopes, but auth tokens stay out of the result', async () => {
  const input = createInput();
  const token = 'secret-test-token-should-never-appear';
  const dependencies = {
    ...blockedDependencies(),
    runtimeReadinessProvider: {
      async readCurrent() {
        return {
          registration: input.pins.runtimeReceipt.registration,
          readinessReceipt: input.pins.runtimeReceipt.activation,
          acceptedOptions: input.pins.sourceReceipt.acceptedOptions,
        };
      },
    },
    authProvider: {
      async getValidatedToken() {
        throw new Error(`Bearer ${token}`);
      },
    },
  };
  const result = await runP1NativeProbes(input, dependencies);
  const serialized = JSON.stringify(result);
  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'probe_dependency_unavailable'));
  assert.equal(serialized.includes(token), false);
  assert.equal(serialized.includes('Bearer ' + token), false);
});

test('validated owner tokens use their own tenant selectors and never enter evidence', async () => {
  const input = createInput();
  const calls = [];
  const dependencies = {
    now: () => Date.parse('2026-01-01T00:00:00.000Z'),
    fetch: async (url, init) => {
      calls.push({ url: String(url), headers: init.headers });
      const urlText = String(url);
      const ownerIndex = urlText.includes('/environment-b/') ? 1 : 0;
      const owner = input.pins.storageReceipt.owners[ownerIndex];
      const runtimeOwner = input.pins.runtimeReceipt.owners[ownerIndex];
      if (urlText.endsWith('/lifecycle')) {
        return {
          status: 200,
          body: { fence: input.pins.ownerFences.owners[ownerIndex].environmentFence, state: 'active' },
        };
      }
      if (urlText.endsWith(`/workspace-volumes/${owner.volume.volumeId}`)) {
        return { status: 200, body: { ...owner.volume } };
      }
      if (urlText.endsWith('/sandbox/v1/placement')) {
        return {
          status: 200,
          body: {
            contractVersion: 1,
            tenantId: owner.tenantId,
            projectId: owner.projectId,
            runId: owner.runId,
            environmentId: owner.environmentId,
            lifecycleGeneration: 2,
            currentFencingGeneration: 5,
            providerFencingGeneration: 5,
            leaseRevision: 7,
            isCurrent: true,
            state: 'active',
            resource: {
              providerId: 'agent-sandbox',
              resourceId: runtimeOwner.sandboxResourceId,
              generation: runtimeOwner.sandboxResourceGeneration,
            },
          },
        };
      }
      return { status: 200, body: {} };
    },
    authProvider: {
      async getValidatedToken(selector) {
        return {
          validated: true,
          token: `private-${selector.ownerId}`,
          issuer: selector.issuer ?? input.environment.issuer,
          audience: selector.audience ?? input.environment.audience,
          actorId: selector.actorId,
          tenantId: selector.tenantId,
        };
      },
    },
    journeyAuthorizer: { async authorize() { throw new Error('must not mutate'); } },
    runtimeReadinessProvider: {
      async readCurrent() {
        return {
          registration: input.pins.runtimeReceipt.registration,
          readinessReceipt: input.pins.runtimeReceipt.activation,
          acceptedOptions: input.pins.sourceReceipt.acceptedOptions,
        };
      },
    },
    processProvider: {
      async run({ recipeId, args }) {
        if (recipeId === 'observe-cluster') throw new Error('native unavailable private-owner-a');
        assert.equal(recipeId, 'observe-sandbox');
        const ownerIndex = args.owner.ownerId === 'owner-a' ? 0 : 1;
        return {
          clusterIdentity: input.pins.context.clusterIdentity,
          pod: {
            name: `sandbox-pod-${ownerIndex ? 'b' : 'a'}`,
            uid: input.pins.runtimeReceipt.owners[ownerIndex].sandboxUid,
            labels: input.pins.runtimeReceipt.owners[ownerIndex].sandboxLabels,
          },
        };
      },
    },
  };
  const result = await runP1NativeProbes(input, dependencies);
  assert.equal(result.overallStatus, 'blocked');
  assert.equal(calls.length, 6);
  assert.deepEqual(new Set(calls.map(call => call.headers['X-Agentweaver-Tenant'])), new Set(['tenant-a', 'tenant-b']));
  assert.ok(calls.every(call => call.headers.Authorization.startsWith('Bearer private-owner-')));
  assert.deepEqual(new Set(calls.map(call => call.headers.Authorization)), new Set([
    'Bearer private-owner-a',
    'Bearer private-owner-b',
  ]));
  const serialized = JSON.stringify(result);
  assert.equal(serialized.includes('private-owner-a'), false);
  assert.equal(serialized.includes('private-owner-b'), false);
});

test('input cannot supply arbitrary commands or credentials', async () => {
  const dependencies = blockedDependencies();
  const commandInput = createInput();
  commandInput.pins.sourceReceipt.command = 'kubectl delete pods --all';
  const commandResult = await runP1NativeProbes(commandInput, dependencies);
  assert.equal(commandResult.overallStatus, 'blocked');
  assert.ok(commandResult.probes.every(probe => probe.code === 'arbitrary_native_command_in_input'));

  const credentialInput = createInput();
  credentialInput.environment.accessToken = 'not-allowed';
  const credentialResult = await runP1NativeProbes(credentialInput, dependencies);
  assert.equal(credentialResult.overallStatus, 'blocked');
  assert.ok(credentialResult.probes.every(probe => probe.code === 'credential_in_input'));
});

test('protected active resources without same-invocation receipts block the run', async () => {
  const input = createInput();
  input.pins.fixtureScope.ownershipReceipts = input.pins.fixtureScope.ownershipReceipts
    .filter(receipt => receipt.resourceId !== 'sandbox-claim-uid-b');
  const result = await runP1NativeProbes(input, blockedDependencies());
  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.code === 'invocation_ownership_receipt_missing'),
    JSON.stringify(result.probes.map(({ code, reason }) => ({ code, reason }))));
});

test('active Sandbox operation labels require the actual invocation creation receipt', async () => {
  const input = createInput();
  const sandboxReceipt = input.pins.fixtureScope.ownershipReceipts
    .find(receipt => receipt.resourceId === 'sandbox-claim-uid-a');
  delete sandboxReceipt.creationReceipt.operationId;
  delete sandboxReceipt.observedReceipt.operationId;
  const harness = successDependencies(input);

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.some(probe => probe.code === 'sandbox_operation_receipt_missing'),
    JSON.stringify(result.probes.map(({ name, code, reason }) => ({ name, code, reason }))));
  assert.equal(harness.authorizations.some(request => request.action === 'sandbox-abandon'), false);
});

test('four native probes pass with injected production-shaped reads, mutations, cleanup receipts, and redaction', async () => {
  const input = createInput();
  const imageDigest = input.pins.runtimeReceipt.activation.image.digest;
  const phases = input.pins.runtimeReceipt.activation.startupPhases;
  phases[0].imageDigest = imageDigest;
  delete phases[1].imageDigest;
  delete phases[1].compressedPullBytes;
  phases[2].compressedPullBytes = input.pins.runtimeReceipt.activation.image.compressedPullBytes;
  const opaqueToken = 'opaque-owner-a-auth-token';
  input.pins.runtimeReceipt.activation.sourceGrant.grantId = opaqueToken;
  const harness = successDependencies(input, {
    token: ownerId => ownerId === 'owner-a' ? opaqueToken : `private-${ownerId}`,
  });

  const result = await runP1NativeProbes(input, harness.dependencies);

  assert.equal(result.kind, 'p1-native-acceptance');
  assert.equal(result.schemaVersion, 1);
  assert.equal(result.overallStatus, 'passed', JSON.stringify(result.probes));
  assert.deepEqual(result.probes.map(probe => probe.status), ['passed', 'passed', 'passed', 'passed']);
  assert.ok(harness.recipes.includes('observe-storage'));
  assert.ok(harness.recipes.includes('probe-service'));
  assert.ok(harness.recipes.includes('mount-file'));
  const absenceRequests = harness.recipeRequests.filter(request =>
    request.recipeId === 'observe-owned-resource');
  for (const runtime of input.pins.runtimeReceipt.owners) {
    assert.ok(absenceRequests.some(request =>
      request.args.resourceId === runtime.sandboxResourceId &&
      request.args.expectedPodUids.length === 1 &&
      request.args.expectedPodUids[0] === runtime.sandboxUid));
  }
  assert.ok(harness.authorizations.some(request => request.action === 'sandbox-abandon'));
  assert.ok(harness.authorizations.some(request => request.action === 'workspace-volume-release'));
  assert.ok(harness.authorizations.some(request => request.action === 'network-policy-revoke'));
  const storage = result.probes.find(probe => probe.name === 'storage-owner-isolation');
  const network = result.probes.find(probe => probe.name === 'network-policy-datapath');
  const lifecycle = result.probes.find(probe => probe.name === 'sandbox-owner-lifecycle');
  assert.equal(storage.cleanup.status, 'complete');
  assert.ok(storage.cleanup.actions.some(action => action.action === 'remove-owned-marker' && action.status === 'complete'));
  assert.equal(network.cleanup.status, 'complete');
  assert.ok(network.cleanup.actions.some(action => action.action === 'leave-owned-deny-tombstone' && action.status === 'complete'));
  assert.equal(lifecycle.cleanup.status, 'complete');
  assert.ok(lifecycle.cleanup.actions.filter(action => action.action === 'final-sandbox-release')
    .every(action => action.status === 'complete'));
  const readiness = result.probes.find(probe => probe.name === 'agenthost-image-readiness')
    .evidence.readinessReceipt;
  assert.deepEqual(readiness.startupPhases.map(phase => phase.phase),
    ['scheduled', 'imageReady', 'started', 'configured', 'ready']);
  assert.equal(readiness.startupPhases[0].imageDigest, imageDigest);
  assert.equal('imageDigest' in readiness.startupPhases[1], false);
  assert.equal('compressedPullBytes' in readiness.startupPhases[1], false);
  assert.equal(readiness.startupPhases[2].compressedPullBytes,
    input.pins.runtimeReceipt.activation.image.compressedPullBytes);
  const serialized = JSON.stringify(result);
  assert.equal(serialized.includes(opaqueToken), false);
  assert.equal(serialized.includes('private-owner-b'), false);
});

test('late Sandbox lifecycle rejects a changed-label Pod using the real adapter contract', async () => {
  const input = createInput();
  const clusterServer = 'https://p1-cluster.example.test/';
  const harness = successDependencies(input);
  const fakeProvider = harness.dependencies.processProvider;
  const lateResourceId = input.pins.runtimeReceipt.owners[1].lateSandboxResourceId;
  const latePodUid = 'actual-late-agenthost-pod-uid';
  const disposableOwner = input.pins.storageReceipt.owners[1];
  const lateOwnerLabel = createHash('sha256')
    .update([
      disposableOwner.tenantId,
      disposableOwner.projectId,
      disposableOwner.runId,
      disposableOwner.environmentId,
    ].join('\0'))
    .digest('hex')
    .slice(0, 40);
  let reconciled = false;
  let lateReadError;
  const adapter = createP1NativeProcessProvider({
    kubeContext: input.pins.context.kubeContext,
    clusterIdentity: input.pins.context.clusterIdentity,
    execute(command, args) {
      assert.equal(command, 'kubectl');
      if (args.includes('config')) {
        assert.equal(args.at(-1), '-o=jsonpath-as-json={.clusters[0].cluster}');
        return {
          status: 0,
          stdout: JSON.stringify([{
            server: clusterServer,
            'certificate-authority-data': Buffer.from(TEST_CLUSTER_CA_PEM, 'utf8').toString('base64'),
          }]),
          stderr: '',
        };
      }
      if (args.includes('sandboxclaims.extensions.agents.x-k8s.io')) {
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'v1',
            kind: 'List',
            items: reconciled
              ? []
              : [{
                apiVersion: 'extensions.agents.x-k8s.io/v1beta1',
                kind: 'SandboxClaim',
                metadata: {
                  name: 'late-agenthost',
                  namespace: input.pins.namespace,
                  uid: lateResourceId,
                  labels: {
                    'agentweaver.dev/managed-sandbox': 'true',
                    'agentweaver.dev/sandbox-owner': lateOwnerLabel,
                  },
                },
              }],
          }),
          stderr: '',
        };
      }
      assert.ok(args.includes('pods'));
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: 'List',
          items: [{
            apiVersion: 'v1',
            kind: 'Pod',
            metadata: {
              name: 'late-agenthost-pod',
              namespace: input.pins.namespace,
              uid: latePodUid,
              labels: {
                'agentweaver.dev/managed-sandbox': 'true',
                'agentweaver.dev/sandbox-owner': lateOwnerLabel,
                'agents.x-k8s.io/claim-uid': reconciled
                  ? 'changed-claim-label'
                  : lateResourceId,
              },
            },
          }],
        }),
        stderr: '',
      };
    },
  });
  const fakeFetch = harness.dependencies.fetch;
  harness.dependencies.fetch = async (urlValue, init) => {
    const response = await fakeFetch(urlValue, init);
    const url = new URL(String(urlValue));
    if (url.pathname.endsWith('/sandbox/reconcile') && init.method === 'POST') {
      reconciled = true;
    }
    return response;
  };
  harness.dependencies.processProvider = {
    async run(request) {
      if (request.recipeId === 'observe-owned-resource' &&
          request.args.resourceId === lateResourceId) {
        harness.recipeRequests.push(request);
        try {
          return await adapter.run(request);
        } catch (error) {
          lateReadError = error;
          throw error;
        }
      }
      return fakeProvider.run(request);
    },
  };

  const result = await runP1NativeProbes(input, harness.dependencies);
  const lifecycle = result.probes.find(probe => probe.name === 'sandbox-owner-lifecycle');
  assert.equal(lifecycle.status, 'blocked');
  assert.equal(reconciled, true);
  assert.match(lateReadError?.message ?? '', /mismatched SandboxClaim UID label/);
  const lateReads = harness.recipeRequests.filter(request =>
    request.recipeId === 'observe-owned-resource' &&
    request.args.resourceId === lateResourceId);
  assert.equal(lateReads.length, 2);
  assert.deepEqual(lateReads[0].args.expectedPodUids, []);
  assert.equal(lateReads[0].args.allowEmptyPodUidInventory, true);
  assert.deepEqual(lateReads[1].args.expectedPodUids, [latePodUid]);
  assert.equal(lateReads[1].args.expectedPodUids.includes(lateResourceId), false);
  assert.equal(lateReads[1].args.allowEmptyPodUidInventory, false);
});

test('a remaining exact Pod makes Sandbox cleanup uncertain after its claim is gone', async () => {
  const input = createInput();
  const imageDigest = input.pins.runtimeReceipt.activation.image.digest;
  const phases = input.pins.runtimeReceipt.activation.startupPhases;
  phases[0].imageDigest = imageDigest;
  delete phases[1].imageDigest;
  delete phases[1].compressedPullBytes;
  phases[2].compressedPullBytes = input.pins.runtimeReceipt.activation.image.compressedPullBytes;
  const harness = successDependencies(input);
  harness.states.get('owner-a').dependentPods = [{
    name: 'orphan-agenthost',
    uid: 'orphan-pod-uid',
  }];

  const result = await runP1NativeProbes(input, harness.dependencies);

  const lifecycle = result.probes.find(probe => probe.name === 'sandbox-owner-lifecycle');
  const release = lifecycle.cleanup.actions.find(action =>
    action.action === 'final-sandbox-release' && action.evidence.ownerId === 'owner-a');
  assert.equal(release.status, 'uncertain');
  assert.equal(lifecycle.cleanup.status, 'uncertain');
  assert.equal(lifecycle.status, 'blocked');
});

test('a supplied startup image field that disagrees with the receipt image blocks', async () => {
  const input = createInput();
  input.pins.runtimeReceipt.activation.startupPhases[0].imageDigest =
    `sha256:${'b'.repeat(64)}`;
  const result = await runP1NativeProbes(input, blockedDependencies());

  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.probes[0].code, 'runtime_receipt_incomplete');
});

test('marker cleanup blocks when the native readback is not bound to the exact current Pod UID', async () => {
  const input = createInput();
  const harness = successDependencies(input);
  const processRun = harness.dependencies.processProvider.run;
  harness.dependencies.processProvider.run = async request => {
    const result = await processRun(request);
    if (request.recipeId === 'mount-file' &&
        request.args.operation === 'stat' &&
        result.exists === false) {
      return { ...result, podUid: 'another-pod-uid' };
    }
    return result;
  };

  const result = await runP1NativeProbes(input, harness.dependencies);

  const storage = result.probes.find(probe => probe.name === 'storage-owner-isolation');
  assert.equal(storage.status, 'blocked');
  assert.equal(storage.code, 'marker_path_owner_readback_mismatch');
});
