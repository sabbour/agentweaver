import { isDeepStrictEqual } from 'node:util';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const HASH = /^[a-f0-9]{64}$/;
const missingCreation = 'environment_creation_binding_read_contract_missing';
const bindingFields = [
  'contractVersion', 'coreInvocationId', 'setupInvocationId', 'setupReceiptHash',
  'tenantId', 'projectId', 'runId', 'rootSessionId', 'actorIssuer', 'actorSubject',
  'membershipId', 'membershipRevision', 'acceptedSelectionHash', 'executionFence',
  'rootStateVersion', 'acceptedAt',
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
          /^application\/json(?:\s*;|$)/i.test(response.headers.get('content-type') ?? ''),
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
    currentTime(provenance.acceptedAt), 'native_core_binding_invalid');
  requireValue(['tenantId', 'projectId', 'runId', 'executionFence', 'acceptedSelectionHash']
    .every(key => provenance[key] === binding[key]) &&
    provenance.actorIssuer === binding.actorIssuer && provenance.actorSubject === binding.actorId &&
    provenance.rootSessionId === binding.sessionId, 'native_core_root_binding_mismatch');
}

function requirePod(pod, binding, clusterIdentity) {
  const owner = pod?.environmentFence?.owner;
  const attachment = pod?.workspaceAttachment;
  requireValue(pod?.contractVersion === 1 && currentTime(pod.observedAt) &&
    record(owner) && ['tenantId', 'projectId', 'runId', 'environmentId']
      .every(key => owner[key] === binding[key]) &&
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
    attachment?.namespace === pod.namespace && text(attachment.claimName) &&
    text(attachment.claimUid) && typeof attachment.mountPath === 'string' &&
    attachment.mountPath.startsWith('/') && attachment.mountPath.length <= 512 &&
    typeof attachment.readOnly === 'boolean', 'native_environment_pod_mismatch');
}

function withoutObservedAt(value) {
  const { observedAt, ...rest } = value;
  return rest;
}

function requireHost(host, pod, registration) {
  const readiness = host?.readiness;
  const mount = host?.workspaceMount;
  requireValue(host?.contractVersion === 1 && currentTime(host.observedAt) &&
    readiness?.contractVersion === 1 &&
    readiness.runtimeInstanceId === registration.runtimeInstanceId &&
    readiness.registrationRevision === registration.revision &&
    readiness.executionFence === registration.binding.executionFence &&
    record(host.environmentPodObservation) &&
    isDeepStrictEqual(withoutObservedAt(host.environmentPodObservation), withoutObservedAt(pod)) &&
    ['environmentPodObservationHash', 'registrationHash', 'sourceHash', 'nativeSessionHash']
      .every(key => hash(host[key])) &&
    mount?.contractVersion === 1 && currentTime(mount.observedAt) &&
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
    const podPath = `${prefix}/environments/${encodeURIComponent(binding.environmentId)}/coordination/sessions/${encodeURIComponent(binding.sessionId)}/runtime-bootstrap/profiles/${encodeURIComponent(binding.profileId)}/pod-observation`;
    const pod = await environment(podPath);
    requirePod(pod, binding, clusterIdentity);
    const evidence = await host('/runtime/v1/readiness/evidence');
    requireHost(evidence, pod, before);
    const afterPod = await environment(podPath);
    requirePod(afterPod, binding, clusterIdentity);
    requireValue(isDeepStrictEqual(withoutObservedAt(pod), withoutObservedAt(afterPod)),
      'native_environment_observation_changed');
    const afterProvenance = await core(bindingPath);
    requireCore(afterProvenance, binding);
    requireValue(isDeepStrictEqual(provenance, afterProvenance), 'native_core_binding_changed');
    const after = await core(registrationPath);
    requireRegistration(after, runtimeInstanceId, actor);
    requireValue(isDeepStrictEqual(before, after), 'native_registration_changed');

    // #2425 retains creation links but has no stable authenticated read contract.
    // Never invent a route or enroll existing resources from caller receipts.
    return {
      status: 'blocked', code: missingCreation,
      partialObservation: {
        coreInvocationId: provenance.coreInvocationId,
        runtimeInstanceId, registrationRevision: before.revision,
        claimUid: pod.claimUid, podUid: pod.podUid,
      },
    };
  }

  return {
    authProvider: trusted.authProvider,
    journeyAuthorizer: {
      async authorize() { return { allowed: false, code: missingCreation }; },
    },
    runtimeReadinessProvider: {
      async readCurrent() { throw new Error('native_readiness_authority_join_incomplete'); },
    },
    nativeAuthorityProvider: { readCurrent },
  };
}
