import { isDeepStrictEqual } from 'node:util';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const HASH = /^[a-f0-9]{64}$/;
const creationGap = 'native_no_send_effect_proof_missing';
const bindingFields = [
  'contractVersion', 'coreInvocationId', 'setupInvocationId', 'setupReceiptHash',
  'tenantId', 'projectId', 'runId', 'rootSessionId', 'actorIssuer', 'actorSubject',
  'membershipId', 'membershipRevision', 'acceptedSelectionHash', 'executionFence',
  'rootStateVersion', 'acceptedAt',
];
const creationFields = [
  'contractVersion', 'binding', 'environmentFence', 'environment', 'workspaceMetadata',
  'workspaceProvision', 'sandboxProvision', 'podObservation',
];
const receiptFields = [
  'operationId', 'resourceKind', 'resourceId', 'resourceGeneration',
  'providerFencingGeneration', 'transitionRevision', 'leaseRevision',
  'providerResource', 'createdAt', 'completedAt',
];
const podFields = [
  'contractVersion', 'observedAt', 'environmentFence', 'resource', 'fencingGeneration',
  'provisionOperationId', 'clusterIdentity', 'namespace', 'claimUid', 'sandboxUid',
  'podName', 'podUid', 'podResourceVersion', 'podGeneration', 'podPhase',
  'workspaceAttachment', 'isolationConfigurationVerified',
];
const hostFields = [
  'contractVersion', 'observedAt', 'readiness', 'environmentPodObservation',
  'environmentPodObservationHash', 'workspaceMount', 'registrationHash', 'sourceHash',
  'nativeSessionHash',
];
const hostEnvelopeFields = ['contractVersion', 'observedAt', 'creation', 'host'];
const workspaceMountFields = [
  'contractVersion', 'observedAt', 'podUid', 'mountPath', 'mountId', 'deviceMajor',
  'deviceMinor', 'directoryInode', 'fileSystemType', 'readOnly', 'mountInfoSha256',
];
const positive = value => Number.isSafeInteger(value) && value > 0;
const text = value => typeof value === 'string' && value.length > 0 &&
  value.length <= 256 && !/[\u0000-\u0020]/.test(value);
const guid = value => typeof value === 'string' && GUID.test(value) &&
  value !== '00000000-0000-0000-0000-000000000000';
const hash = value => typeof value === 'string' && HASH.test(value);
const currentTime = value => typeof value === 'string' &&
  Number.isFinite(Date.parse(value)) && Date.parse(value) <= Date.now();
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const requireValue = (condition, code) => {
  if (!condition) throw new Error(code);
};
const exactRecord = (value, fields) => record(value) &&
  Object.keys(value).length === fields.length && fields.every(key => Object.hasOwn(value, key));

function timestampTicks(value) {
  if (typeof value !== 'string') return null;
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) return null;
  const milliseconds = Date.parse(`${match[1]}${match[3]}`);
  if (!Number.isFinite(milliseconds)) return null;
  return BigInt(milliseconds) * 10_000n + BigInt((match[2] ?? '').padEnd(7, '0'));
}

function requireEnvironmentFence(fence, binding) {
  requireValue(exactRecord(fence, ['owner', 'lifecycleGeneration']) &&
    exactRecord(fence.owner, ['tenantId', 'projectId', 'runId', 'environmentId']) &&
    ['tenantId', 'projectId', 'runId', 'environmentId'].every(key => fence.owner[key] === binding[key]) &&
    fence.lifecycleGeneration === binding.environmentLifecycleGeneration,
  'native_environment_fence_mismatch');
}

function createOwnerReader(owner) {
  requireValue(record(owner) && typeof owner.getAuthorization === 'function' &&
    text(owner.tenantId), 'native_owner_client_required');
  const origin = new URL(owner.origin);
  requireValue(origin.protocol === 'https:' && origin.pathname === '/' &&
    !origin.username && !origin.password && !origin.search && !origin.hash,
  'native_owner_origin_invalid');
  return async path => {
    const signal = AbortSignal.timeout(10_000);
    let response;
    try {
      const authorization = await owner.getAuthorization(origin.origin);
      requireValue(typeof authorization === 'string' &&
        /^Bearer [^\s]+$/.test(authorization), 'native_owner_authorization_invalid');
      signal.throwIfAborted();
      response = await globalThis.fetch(new URL(path, origin), {
        method: 'GET', redirect: 'error', cache: 'no-store', signal,
        headers: {
          Authorization: authorization,
          'X-Agentweaver-Tenant': owner.tenantId,
          Accept: 'application/json',
          'Cache-Control': 'no-store',
        },
      });
      try {
        requireValue(response.status === 200 && !response.redirected &&
          /^application\/json(?:\s*;|$)/i.test(response.headers.get('content-type') ?? '') &&
          (response.headers.get('cache-control') ?? '').split(',')
            .some(directive => /^no-store(?:\s*;|$)/i.test(directive.trim())),
        'native_owner_response_invalid');
        requireValue(response.body !== null, 'native_owner_body_missing');
        const reader = response.body.getReader();
        const chunks = [];
        let size = 0;
        try {
          while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            size += value.byteLength;
            requireValue(size <= 64 * 1024, 'native_owner_body_too_large');
            chunks.push(value);
          }
        } finally {
          await reader.cancel();
          reader.releaseLock();
        }
        signal.throwIfAborted();
        return JSON.parse(new TextDecoder('utf-8', { fatal: true })
          .decode(Buffer.concat(chunks)));
      } finally {
        if (response.body && !response.body.locked) await response.body.cancel();
      }
    } catch {
      // Transport failures and owner bodies can contain bearer values or private endpoints.
      throw new Error('native_owner_read_unavailable');
    }
  };
}

function requireRegistration(registration, runtimeInstanceId, actor) {
  const binding = registration?.binding;
  requireValue(record(registration) && registration.runtimeInstanceId === runtimeInstanceId &&
    positive(registration.revision) && ['active', 'Active', 0].includes(registration.state) &&
    Date.parse(registration.expiresAt) > Date.now() && record(binding),
  'native_registration_invalid');
  requireValue(binding.actorIssuer === actor.issuer && binding.actorId === actor.subject &&
    binding.tenantId === actor.tenantId &&
    ['projectId', 'runId', 'sessionId', 'environmentId', 'profileId', 'placementUid',
      'placementProviderId'].every(key => text(binding[key])) &&
    ['executionFence', 'placementGeneration', 'environmentLifecycleGeneration',
      'environmentCurrentFencingGeneration', 'environmentProviderFencingGeneration',
      'environmentLeaseRevision'].every(key => positive(binding[key])) &&
    hash(binding.acceptedSelectionHash), 'native_registration_binding_invalid');
  return binding;
}

function requireCore(provenance, binding) {
  requireValue(record(provenance) && Object.keys(provenance).length === bindingFields.length &&
    bindingFields.every(key => Object.hasOwn(provenance, key)) &&
    provenance.contractVersion === 1 &&
    ['coreInvocationId', 'setupInvocationId', 'membershipId'].every(key => guid(provenance[key])) &&
    ['membershipRevision', 'rootStateVersion', 'executionFence'].every(key => positive(provenance[key])) &&
    hash(provenance.setupReceiptHash) && hash(provenance.acceptedSelectionHash) &&
    timestampTicks(provenance.acceptedAt) !== null &&
    currentTime(provenance.acceptedAt), 'native_core_binding_invalid');
  requireValue(['tenantId', 'projectId', 'runId', 'executionFence', 'acceptedSelectionHash']
    .every(key => provenance[key] === binding[key]) &&
    provenance.actorIssuer === binding.actorIssuer && provenance.actorSubject === binding.actorId &&
    provenance.rootSessionId === binding.sessionId, 'native_core_root_binding_mismatch');
}

function requirePod(pod, binding, clusterIdentity) {
  const owner = pod?.environmentFence?.owner;
  const attachment = pod?.workspaceAttachment;
  requireValue(exactRecord(pod, podFields) && pod.contractVersion === 1 &&
    currentTime(pod.observedAt) && timestampTicks(pod.observedAt) !== null &&
    record(owner) && ['tenantId', 'projectId', 'runId', 'environmentId']
      .every(key => owner[key] === binding[key]) &&
    exactRecord(pod.environmentFence, ['owner', 'lifecycleGeneration']) &&
    exactRecord(owner, ['tenantId', 'projectId', 'runId', 'environmentId']) &&
    exactRecord(pod.resource, ['seam', 'providerId', 'resourceId', 'generation']) &&
    pod.environmentFence.lifecycleGeneration === binding.environmentLifecycleGeneration &&
    pod.resource?.providerId === binding.placementProviderId &&
    pod.resource.resourceId === binding.placementUid &&
    pod.resource.generation === binding.placementGeneration &&
    ['sandbox', 'Sandbox', 2].includes(pod.resource.seam) &&
    pod.fencingGeneration === binding.environmentProviderFencingGeneration &&
    guid(pod.provisionOperationId) && pod.clusterIdentity === clusterIdentity &&
    pod.claimUid === binding.placementUid &&
    ['namespace', 'sandboxUid', 'podName', 'podUid', 'podResourceVersion']
      .every(key => text(pod[key])) &&
    positive(pod.podGeneration) && pod.podPhase === 'Running' &&
    pod.isolationConfigurationVerified === true &&
    exactRecord(attachment, ['namespace', 'claimName', 'claimUid', 'mountPath', 'readOnly']) &&
    attachment.namespace === pod.namespace && text(attachment.claimName) &&
    text(attachment.claimUid) && typeof attachment.mountPath === 'string' &&
    attachment.mountPath.startsWith('/') && attachment.mountPath.length <= 512 &&
    typeof attachment.readOnly === 'boolean', 'native_environment_pod_mismatch');
}

function withoutPodObservedAt(creation) {
  const copy = structuredClone(creation);
  delete copy.podObservation.observedAt;
  return copy;
}

function withoutObservedAt(value) {
  const { observedAt, ...rest } = value;
  return rest;
}

function requireEnvironmentCreation(creation, provenance, binding, clusterIdentity) {
  requireValue(exactRecord(creation, creationFields) && creation.contractVersion === 1 &&
    exactRecord(creation.binding, bindingFields) &&
    isDeepStrictEqual(creation.binding, provenance),
  'native_environment_creation_binding_mismatch');
  requireEnvironmentFence(creation.environmentFence, binding);
  const pod = creation.podObservation;
  requirePod(pod, binding, clusterIdentity);

  const receipts = [
    creation.environment, creation.workspaceMetadata, creation.workspaceProvision,
    creation.sandboxProvision,
  ];
  requireValue(receipts.every(receipt => exactRecord(receipt, receiptFields) &&
    guid(receipt.operationId) && text(receipt.resourceKind) && text(receipt.resourceId) &&
    (receipt.resourceGeneration === null || Number.isSafeInteger(receipt.resourceGeneration)) &&
    (receipt.providerFencingGeneration === null || positive(receipt.providerFencingGeneration)) &&
    (receipt.transitionRevision === null || positive(receipt.transitionRevision)) &&
    (receipt.leaseRevision === null || positive(receipt.leaseRevision)) &&
    (receipt.providerResource === null ||
      exactRecord(receipt.providerResource, ['seam', 'providerId', 'resourceId', 'generation']) &&
      ['sandbox', 'storage'].includes(receipt.providerResource.seam) &&
      text(receipt.providerResource.providerId) && text(receipt.providerResource.resourceId) &&
      positive(receipt.providerResource.generation)) &&
    timestampTicks(receipt.createdAt) !== null && timestampTicks(receipt.completedAt) !== null &&
    timestampTicks(receipt.createdAt) >= timestampTicks(provenance.acceptedAt) / 10n * 10n &&
    timestampTicks(receipt.completedAt) >= timestampTicks(receipt.createdAt) &&
    timestampTicks(receipt.completedAt) <= BigInt(Date.now()) * 10_000n),
  'native_environment_creation_receipt_invalid');

  const [environment, metadata, workspace, sandbox] = receipts;
  requireValue(environment.resourceKind === 'Environment' &&
    environment.resourceId === binding.environmentId &&
    environment.resourceGeneration === null && environment.providerFencingGeneration === null &&
    environment.transitionRevision === null && environment.leaseRevision === null &&
    environment.providerResource === null &&
    metadata.resourceKind === 'WorkspaceVolumeMetadata' &&
    metadata.resourceGeneration === 0 && metadata.transitionRevision > 0 &&
    metadata.providerResource === null && metadata.providerFencingGeneration === null &&
    metadata.leaseRevision === null &&
    workspace.resourceKind === 'WorkspaceVolume' && workspace.resourceId === metadata.resourceId &&
    positive(workspace.resourceGeneration) &&
    workspace.providerResource?.seam === 'storage' &&
    workspace.providerResource.generation === workspace.resourceGeneration &&
    positive(workspace.transitionRevision) &&
    workspace.transitionRevision > metadata.transitionRevision &&
    workspace.providerFencingGeneration === null && workspace.leaseRevision === null &&
    sandbox.resourceKind === 'Sandbox' &&
    sandbox.operationId === pod.provisionOperationId &&
    sandbox.resourceId === pod.provisionOperationId &&
    sandbox.resourceId !== binding.placementUid &&
    sandbox.providerResource?.seam === 'sandbox' &&
    sandbox.providerResource.providerId === binding.placementProviderId &&
    sandbox.providerResource.resourceId === binding.placementUid &&
    sandbox.providerResource.generation === binding.placementGeneration &&
    sandbox.resourceGeneration === binding.placementGeneration &&
    sandbox.providerFencingGeneration === pod.fencingGeneration &&
    positive(sandbox.leaseRevision) &&
    sandbox.leaseRevision <= binding.environmentLeaseRevision &&
    sandbox.transitionRevision === null,
  'native_environment_creation_receipt_mismatch');
  const instants = receipts.map(receipt => ({
    created: timestampTicks(receipt.createdAt),
    completed: timestampTicks(receipt.completedAt),
  }));
  requireValue(instants[0].completed <= instants[1].created &&
    instants[1].completed <= instants[2].created &&
    instants[2].completed <= instants[3].created,
  'native_environment_creation_sequence_mismatch');
}

function requireHost(host, pod, registration) {
  const readiness = host?.readiness;
  const mount = host?.workspaceMount;
  const readinessFields = [
    'contractVersion', 'runtimeInstanceId', 'registrationRevision', 'executionFence',
    'image', 'startupPhases', 'sourceGrant',
  ];
  requireValue(exactRecord(host, hostFields) && host.contractVersion === 1 &&
    currentTime(host.observedAt) && timestampTicks(host.observedAt) !== null &&
    exactRecord(readiness, readinessFields.concat(
      ['resumeCache', 'recovery'].filter(key => Object.hasOwn(readiness ?? {}, key)))) &&
    readiness?.contractVersion === 1 &&
    readiness.runtimeInstanceId === registration.runtimeInstanceId &&
    readiness.registrationRevision === registration.revision &&
    readiness.executionFence === registration.binding.executionFence &&
    record(host.environmentPodObservation) &&
    currentTime(host.environmentPodObservation.observedAt) &&
    timestampTicks(host.environmentPodObservation.observedAt) !== null &&
    isDeepStrictEqual(withoutObservedAt(host.environmentPodObservation), withoutObservedAt(pod)) &&
    exactRecord(host.environmentPodObservation, podFields) &&
    ['environmentPodObservationHash', 'registrationHash', 'sourceHash', 'nativeSessionHash']
      .every(key => hash(host[key])) &&
    exactRecord(mount, workspaceMountFields) && mount.contractVersion === 1 &&
    currentTime(mount.observedAt) && timestampTicks(mount.observedAt) !== null &&
    mount.podUid === pod.podUid && mount.mountPath === pod.workspaceAttachment.mountPath &&
    mount.readOnly === pod.workspaceAttachment.readOnly &&
    positive(mount.mountId) && positive(mount.directoryInode) &&
    Number.isSafeInteger(mount.deviceMajor) && mount.deviceMajor >= 0 &&
    Number.isSafeInteger(mount.deviceMinor) && mount.deviceMinor >= 0 &&
    text(mount.fileSystemType) && hash(mount.mountInfoSha256),
  'native_host_observation_mismatch');
}

/**
 * Trusted provider modules supply fixed owner origins and already-authorized in-memory
 * clients as the second argument. Input JSON supplies lookup pins, never owner clients.
 */
export function createP1NativeProviders({ input }, trusted) {
  const locator = input?.nativeOwner;
  requireValue(record(locator) && Object.keys(locator).length === 1 &&
    guid(locator.runtimeInstanceId), 'native_owner_locator_required');
  const runtimeInstanceId = locator.runtimeInstanceId;
  const clusterIdentity = input?.pins?.context?.clusterIdentity?.sha256;
  requireValue(hash(clusterIdentity), 'native_cluster_identity_required');
  const actor = structuredClone(trusted?.actor);
  requireValue(record(actor) && ['issuer', 'subject', 'tenantId'].every(key => text(actor[key])) &&
    typeof trusted?.authProvider?.getValidatedToken === 'function',
  'native_trusted_actor_required');
  for (const owner of ['core', 'environment', 'host']) {
    requireValue(trusted?.[owner]?.tenantId === actor.tenantId, 'native_owner_tenant_mismatch');
  }
  const core = createOwnerReader(trusted.core);
  const environment = createOwnerReader(trusted.environment);
  const host = createOwnerReader(trusted.host);

  async function readCurrent() {
    const registrationPath = `/internal/runtime/registrations/${runtimeInstanceId}`;
    const before = await core(registrationPath);
    const binding = requireRegistration(before, runtimeInstanceId, actor);
    const prefix = `/internal/projects/${encodeURIComponent(binding.projectId)}/runs/${encodeURIComponent(binding.runId)}`;
    const bindingPath = `${prefix}/p1-invocation-binding`;
    const provenance = await core(bindingPath);
    requireCore(provenance, binding);
    const creationPath = `${prefix}/environments/${encodeURIComponent(binding.environmentId)}/coordination/sessions/${encodeURIComponent(binding.sessionId)}/runtime-bootstrap/profiles/${encodeURIComponent(binding.profileId)}/p1-creation-binding`;
    const creation = await environment(creationPath);
    requireEnvironmentCreation(creation, provenance, binding, clusterIdentity);
    const evidence = await host('/runtime/v1/readiness/p1-evidence');
    requireValue(exactRecord(evidence, hostEnvelopeFields) && evidence.contractVersion === 1 &&
      currentTime(evidence.observedAt) && timestampTicks(evidence.observedAt) !== null,
    'native_host_p1_envelope_invalid');
    requireEnvironmentCreation(evidence.creation, provenance, binding, clusterIdentity);
    requireValue(isDeepStrictEqual(withoutPodObservedAt(creation), withoutPodObservedAt(evidence.creation)),
      'native_environment_creation_changed');
    requireHost(evidence.host, creation.podObservation, before);
    requireValue(isDeepStrictEqual(
      withoutObservedAt(evidence.creation.podObservation),
      withoutObservedAt(evidence.host.environmentPodObservation)),
    'native_host_pod_observation_mismatch');
    const afterCreation = await environment(creationPath);
    requireEnvironmentCreation(afterCreation, provenance, binding, clusterIdentity);
    requireValue(isDeepStrictEqual(withoutPodObservedAt(creation), withoutPodObservedAt(afterCreation)),
      'native_environment_creation_changed');
    const afterProvenance = await core(bindingPath);
    requireCore(afterProvenance, binding);
    requireValue(isDeepStrictEqual(provenance, afterProvenance), 'native_core_binding_changed');
    const after = await core(registrationPath);
    requireRegistration(after, runtimeInstanceId, actor);
    requireValue(isDeepStrictEqual(before, after), 'native_registration_changed');

    return {
      status: 'blocked', code: creationGap,
      acceptanceEvidence: false,
      nativeNoSendProven: false,
      partialObservation: {
        coreInvocationId: provenance.coreInvocationId,
        runtimeInstanceId, registrationRevision: before.revision,
        environmentOperationId: creation.environment.operationId,
        workspaceMetadataOperationId: creation.workspaceMetadata.operationId,
        workspaceProvisionOperationId: creation.workspaceProvision.operationId,
        sandboxProvisionOperationId: creation.sandboxProvision.operationId,
        sandboxClaimUid: creation.sandboxProvision.providerResource.resourceId,
        podUid: creation.podObservation.podUid,
      },
    };
  }

  return {
    authProvider: trusted.authProvider,
    journeyAuthorizer: {
      async authorize() { return { allowed: false, code: creationGap }; },
    },
    runtimeReadinessProvider: {
      async readCurrent() { throw new Error('native_readiness_authority_join_incomplete'); },
    },
    nativeAuthorityProvider: { readCurrent },
  };
}
