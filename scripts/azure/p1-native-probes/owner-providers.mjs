import { isDeepStrictEqual } from 'node:util';
import { createHash } from 'node:crypto';

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
const sourceReceiptFields = [
  'runtimeInstanceId', 'registrationRevision', 'sourceGrantId', 'source',
  'canonicalPayloadHash', 'recordedAt',
];
const sourceFields = [
  'runtimeInstanceId', 'sdkSessionId', 'sdkVersion', 'runtimeVersion',
  'modelSelectionReference', 'modelId', 'catalogHash', 'modelMultiplier',
  'sourceMode', 'meterSource', 'acceptedSelectionHash', 'registrationRevision',
  'reasoningEffort',
];
const sourceOptionalFields = ['maxPromptTokens', 'modelBindingPin', 'resolvedModelBindingPin', 'byokProvider'];
const grantFields = [
  'grantId', 'runtimeInstanceId', 'registrationRevision', 'revision', 'issuer',
  'purpose', 'audience', 'state', 'configurationHash', 'expiresAt', 'recordedAt',
];
const fixedReceiptFields = [
  'contractVersion', 'stage', 'code', 'registration', 'registrationHash',
  'requestId', 'requestHash', 'sourceHash', 'sourceGrantId', 'sourceGrantRevision',
  'recordedAt', 'canonicalPayloadHash',
];
const fixedEvidenceFields = [
  'contractVersion', 'runtimeInstanceId', 'registrationRevision', 'executionFence',
  'requestId', 'stage', 'guardReceiptHash', 'registrationHash', 'sourceHash',
  'sourceGrantId', 'sourceGrantRevision', 'nativeSessionHash', 'beforeCursorHash',
  'afterCursorHash', 'observedEventCount', 'rangeComplete', 'verifiedAt',
  'canonicalPayloadHash',
];
const workspaceMountFields = [
  'contractVersion', 'observedAt', 'podUid', 'mountPath', 'mountId', 'deviceMajor',
  'deviceMinor', 'directoryInode', 'fileSystemType', 'readOnly', 'mountInfoSha256',
];
const positive = value => Number.isSafeInteger(value) && value > 0;
const text = value => typeof value === 'string' && value.length > 0 &&
  value.length <= 256 && !/[\u0000-\u0020]/.test(value);
const identifier = value => typeof value === 'string' && value.length > 0 &&
  value.length <= 256 && /^[A-Za-z0-9._:-]+$/.test(value);
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

function createOwnerReader(owner, method = 'GET') {
  requireValue(record(owner) && typeof owner.getAuthorization === 'function' &&
    text(owner.tenantId), 'native_owner_client_required');
  requireValue(method === 'GET' || method === 'POST', 'native_owner_method_invalid');
  const origin = new URL(owner.origin);
  requireValue(origin.protocol === 'https:' && origin.pathname === '/' &&
    !origin.username && !origin.password && !origin.search && !origin.hash,
  'native_owner_origin_invalid');
  return async (path, body) => {
    const signal = AbortSignal.timeout(10_000);
    let response;
    try {
      const authorization = await owner.getAuthorization(origin.origin);
      requireValue(typeof authorization === 'string' &&
        /^Bearer [^\s]+$/.test(authorization), 'native_owner_authorization_invalid');
      signal.throwIfAborted();
      response = await globalThis.fetch(new URL(path, origin), {
        method, redirect: 'error', cache: 'no-store', signal,
        headers: {
          Authorization: authorization,
          'X-Agentweaver-Tenant': owner.tenantId,
          Accept: 'application/json',
          'Cache-Control': 'no-store',
          ...(method === 'POST' ? { 'Content-Type': 'application/json' } : {}),
        },
        ...(method === 'POST' ? { body: JSON.stringify(body) } : {}),
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
    binding.modelSourceMode === 'hostedCopilot' && binding.copilotHardCreditLimit === 0 &&
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
    exactRecord(readiness.sourceGrant, grantFields) &&
    readiness.sourceGrant.runtimeInstanceId === registration.runtimeInstanceId &&
    readiness.sourceGrant.registrationRevision === registration.revision &&
    positive(readiness.sourceGrant.revision) && guid(readiness.sourceGrant.grantId) &&
    readiness.sourceGrant.purpose === 'observe' && readiness.sourceGrant.state === 'active' &&
    Date.parse(readiness.sourceGrant.expiresAt) > Date.now() &&
    hash(readiness.sourceGrant.configurationHash) &&
    text(readiness.sourceGrant.issuer) &&
    (() => {
      try {
        const audience = new URL(readiness.sourceGrant.audience);
        return audience.protocol === 'https:' &&
          audience.pathname === '/internal/runtime/observations' &&
          !audience.username && !audience.password && !audience.search && !audience.hash;
      } catch {
        return false;
      }
    })() &&
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

function requireCurrentSource(sourceReceipt, registration, host) {
  const source = sourceReceipt?.source;
  const grant = host.readiness.sourceGrant;
  const binding = registration.binding;
  const optional = sourceOptionalFields.filter(key => Object.hasOwn(source ?? {}, key));
  const modelPinFields = [
    'contractVersion', 'modelSelectionReference', 'modelId', 'sourceMode',
    'configurationRevision', 'configurationHash', 'reasoningEffort',
  ];
  const validModelPin = pin => pin === null || exactRecord(pin, modelPinFields.concat(
    Object.hasOwn(pin ?? {}, 'providerType') ? ['providerType'] : [])) &&
    pin.contractVersion === 1 && identifier(pin.modelSelectionReference) && identifier(pin.modelId) &&
    ['hostedCopilot', 'byok'].includes(pin.sourceMode) && identifier(pin.configurationRevision) &&
    hash(pin.configurationHash) && ['low', 'medium', 'high', 'xhigh'].includes(pin.reasoningEffort) &&
    (pin.sourceMode === 'hostedCopilot'
      ? pin.providerType == null
      : ['azure', 'openai', 'anthropic'].includes(pin.providerType));
  const sameModelPin = (left, right) => left == null && right == null ||
    left != null && right != null && modelPinFields.every(key => left[key] === right[key]) &&
    (left.providerType ?? null) === (right.providerType ?? null);
  const acceptedPin = binding.modelBindingPin ?? null;
  const sourcePin = source?.modelBindingPin ?? null;
  const resolvedPin = source?.resolvedModelBindingPin ?? null;
  const promptLimit = binding.maxPromptTokens;
  requireValue(exactRecord(sourceReceipt, sourceReceiptFields) &&
    sourceReceipt.runtimeInstanceId === registration.runtimeInstanceId &&
    sourceReceipt.registrationRevision === registration.revision &&
    sourceReceipt.sourceGrantId === grant.grantId &&
    hash(sourceReceipt.canonicalPayloadHash) &&
    sourceReceipt.canonicalPayloadHash === host.sourceHash &&
    timestampTicks(sourceReceipt.recordedAt) !== null &&
    exactRecord(source, sourceFields.concat(optional)) &&
    source.runtimeInstanceId === registration.runtimeInstanceId &&
    source.registrationRevision === registration.revision &&
    text(source.sdkSessionId) && text(source.sdkVersion) && text(source.runtimeVersion) &&
    text(source.modelSelectionReference) &&
    source.modelSelectionReference === binding.modelSelectionReference &&
    text(source.modelId) && hash(source.catalogHash) &&
    source.sourceMode === 'hosted-copilot' && source.meterSource === 'copilot.nano_aiu' &&
    source.acceptedSelectionHash === registration.binding.acceptedSelectionHash &&
    source.reasoningEffort === binding.reasoningEffort &&
    validModelPin(acceptedPin) && validModelPin(sourcePin) &&
    sameModelPin(sourcePin, acceptedPin) &&
    (acceptedPin === null ||
      acceptedPin.modelSelectionReference === binding.modelSelectionReference &&
      acceptedPin.modelId === source.modelId &&
      acceptedPin.sourceMode === binding.modelSourceMode &&
      acceptedPin.reasoningEffort === binding.reasoningEffort) &&
    validModelPin(resolvedPin) &&
    (resolvedPin === null
      ? acceptedPin === null && binding.reasoningEffort === 'high'
      : resolvedPin.modelSelectionReference === binding.modelSelectionReference &&
        resolvedPin.modelId === source.modelId &&
        resolvedPin.sourceMode === binding.modelSourceMode &&
        resolvedPin.reasoningEffort === binding.reasoningEffort &&
        (acceptedPin === null || sameModelPin(resolvedPin, acceptedPin))) &&
    (source.modelMultiplier === null ||
      typeof source.modelMultiplier === 'number' && Number.isFinite(source.modelMultiplier) &&
      source.modelMultiplier >= 0) &&
    (promptLimit == null
      ? source.maxPromptTokens == null
      : Number.isSafeInteger(source.maxPromptTokens) &&
        source.maxPromptTokens > 0 && source.maxPromptTokens <= promptLimit) &&
    source.byokProvider == null &&
    createHash('sha256').update(source.sdkSessionId, 'utf8').digest('hex') === host.nativeSessionHash &&
    grant.expiresAt && Date.parse(grant.expiresAt) > Date.now(),
  'native_fixed_credit_source_mismatch');
}

function requireFixedCreditSelection(selection, registration, host, sourceReceipt) {
  requireValue(exactRecord(selection, ['receipt', 'evidence']),
    'native_fixed_credit_selection_invalid');
  if (selection.receipt === null && selection.evidence === null)
    return null;
  const receipt = selection.receipt;
  const evidence = selection.evidence;
  const grant = host.readiness.sourceGrant;
  requireValue(exactRecord(receipt, fixedReceiptFields) &&
    exactRecord(evidence, fixedEvidenceFields) &&
    receipt.contractVersion === 1 && receipt.stage === 'hostPreQueue' &&
    receipt.code === 'hardLimitZero' && isDeepStrictEqual(receipt.registration, registration) &&
    receipt.registrationHash === host.registrationHash &&
    guid(receipt.requestId) && hash(receipt.requestHash) &&
    receipt.sourceHash === sourceReceipt.canonicalPayloadHash &&
    receipt.sourceGrantId === grant.grantId &&
    receipt.sourceGrantRevision === grant.revision &&
    currentTime(receipt.recordedAt) && hash(receipt.canonicalPayloadHash) &&
    evidence.contractVersion === 1 &&
    evidence.runtimeInstanceId === registration.runtimeInstanceId &&
    evidence.registrationRevision === registration.revision &&
    evidence.executionFence === registration.binding.executionFence &&
    evidence.requestId === receipt.requestId && evidence.stage === receipt.stage &&
    evidence.guardReceiptHash === receipt.canonicalPayloadHash &&
    evidence.registrationHash === receipt.registrationHash &&
    evidence.sourceHash === sourceReceipt.canonicalPayloadHash &&
    evidence.sourceGrantId === grant.grantId &&
    evidence.sourceGrantRevision === grant.revision &&
    evidence.nativeSessionHash === host.nativeSessionHash &&
    hash(evidence.beforeCursorHash) && evidence.beforeCursorHash === evidence.afterCursorHash &&
    evidence.observedEventCount === 0 && evidence.rangeComplete === true &&
    currentTime(evidence.verifiedAt) && hash(evidence.canonicalPayloadHash),
  'native_fixed_credit_selection_mismatch');
  requireValue(timestampTicks(evidence.verifiedAt) >= timestampTicks(receipt.recordedAt),
    'native_fixed_credit_selection_time_mismatch');
  return {
    requestId: receipt.requestId,
    requestHash: receipt.requestHash,
    registrationRevision: evidence.registrationRevision,
    executionFence: evidence.executionFence,
    sourceGrantId: evidence.sourceGrantId,
    sourceGrantRevision: evidence.sourceGrantRevision,
    beforeCursorHash: evidence.beforeCursorHash,
    afterCursorHash: evidence.afterCursorHash,
    observedEventCount: evidence.observedEventCount,
    rangeComplete: evidence.rangeComplete,
    verifiedAt: evidence.verifiedAt,
  };
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
  const hostPost = createOwnerReader(trusted.host, 'POST');

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
    const selection = await hostPost(
      '/runtime/v1/turns/fixed-credit-denials/select-current', { runtimeInstanceId });
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
    const sourcePath = `/internal/runtime/sources/${runtimeInstanceId}`;
    const sourceReceipt = await core(sourcePath);
    requireCurrentSource(sourceReceipt, before, evidence.host);
    const fixedCreditPair = requireFixedCreditSelection(
      selection, before, evidence.host, sourceReceipt);
    const afterCreation = await environment(creationPath);
    requireEnvironmentCreation(afterCreation, provenance, binding, clusterIdentity);
    requireValue(isDeepStrictEqual(withoutPodObservedAt(creation), withoutPodObservedAt(afterCreation)),
      'native_environment_creation_changed');
    const afterProvenance = await core(bindingPath);
    requireCore(afterProvenance, binding);
    requireValue(isDeepStrictEqual(provenance, afterProvenance), 'native_core_binding_changed');
    const afterSource = await core(sourcePath);
    requireValue(isDeepStrictEqual(sourceReceipt, afterSource), 'native_fixed_credit_source_changed');
    const after = await core(registrationPath);
    requireRegistration(after, runtimeInstanceId, actor);
    requireValue(isDeepStrictEqual(before, after), 'native_registration_changed');
    const closingSelection = await hostPost(
      '/runtime/v1/turns/fixed-credit-denials/select-current', { runtimeInstanceId });
    const closingEvidence = await host('/runtime/v1/readiness/p1-evidence');
    requireValue(exactRecord(closingEvidence, hostEnvelopeFields) &&
      closingEvidence.contractVersion === 1 &&
      currentTime(closingEvidence.observedAt) &&
      timestampTicks(closingEvidence.observedAt) !== null,
    'native_host_p1_envelope_invalid');
    requireEnvironmentCreation(closingEvidence.creation, provenance, binding, clusterIdentity);
    requireValue(isDeepStrictEqual(
      withoutPodObservedAt(creation), withoutPodObservedAt(closingEvidence.creation)),
    'native_environment_creation_changed');
    requireHost(closingEvidence.host, creation.podObservation, after);
    const initialGrant = evidence.host.readiness.sourceGrant;
    const closingGrant = closingEvidence.host.readiness.sourceGrant;
    requireValue(closingGrant.grantId === initialGrant.grantId &&
      closingGrant.revision === initialGrant.revision,
    'native_fixed_credit_authority_changed');
    requireCurrentSource(afterSource, after, closingEvidence.host);
    const closingPair = requireFixedCreditSelection(
      closingSelection, after, closingEvidence.host, afterSource);
    requireValue(isDeepStrictEqual(selection, closingSelection) &&
      isDeepStrictEqual(fixedCreditPair, closingPair),
    'native_fixed_credit_selection_changed');
    requireValue(Date.parse(closingGrant.expiresAt) > Date.now(),
      'native_fixed_credit_source_mismatch');

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
        fixedCreditNoSendPair: fixedCreditPair,
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
