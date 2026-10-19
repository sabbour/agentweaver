/**
 * Runs P1 native recovery and credit-denial probes against injected application dependencies.
 * It validates tenant, request, and evidence bindings and returns results without provisioning Azure resources.
 */
import { createHash, randomUUID } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { sealP1NativeProbeEnvelope } from '../../harness-shared/p1-acceptance-result.mjs';

const TENANT_SELECTOR_HEADER = 'X-Agentweaver-Tenant';
const DNS_LABEL = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;
const SHA256_HASH = /^[a-f0-9]{64}$/i;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
const FIXED_CREDIT_READ_PATH = '/runtime/v1/turns/fixed-credit-denials/read';
const NATIVE_CREDIT_READ_PATH = '/runtime/v1/turns/denials/read';
const HUBBLE_RELAY_ENDPOINT = '127.0.0.1:4245';
const PROBE_NAMES = Object.freeze([
  'storage-owner-isolation',
  'network-policy-datapath',
  'sandbox-owner-lifecycle',
  'agenthost-image-readiness',
]);
const NATIVE_PROVIDER_PHASES = Object.freeze(['scheduled', 'imageReady', 'started']);
const WORKSPACE_VOLUME_OWNER_KINDS = Object.freeze(['Run', 'Agent', 'Team']);
const RUNTIME_HOST_PHASES = Object.freeze([
  'scheduled',
  'imageReady',
  'started',
  'configured',
  'ready',
]);
const STARTUP_BUDGET_FIELDS = Object.freeze([
  'scheduledSeconds',
  'imageReadySeconds',
  'startedSeconds',
  'configuredSeconds',
  'readySeconds',
  'totalSeconds',
]);
const SAFE_RECIPES = new Set([
  'observe-cluster',
  'verify-hubble',
  'observe-storage',
  'observe-sandbox',
  'observe-network-policy',
  'probe-service',
  'observe-owned-resource',
  'mount-file',
]);
const SANDBOX_LABELS = Object.freeze({
  managed: 'agentweaver.dev/managed-sandbox',
  owner: 'agentweaver.dev/sandbox-owner',
  lifecycle: 'agentweaver.dev/sandbox-lifecycle',
  resourceGeneration: 'agentweaver.dev/sandbox-generation',
  fencingGeneration: 'agentweaver.dev/sandbox-fence',
  operation: 'agentweaver.dev/sandbox-operation',
  claimUid: 'agents.x-k8s.io/claim-uid',
});
const REDACTED = '[redacted]';

class ProbeIssue extends Error {
  constructor(status, code, message, evidence = {}) {
    super(message);
    this.status = status;
    this.code = code;
    this.evidence = evidence;
  }
}

const block = (code, message, evidence) => {
  throw new ProbeIssue('blocked', code, message, evidence);
};

const fail = (code, message, evidence) => {
  throw new ProbeIssue('failed', code, message, evidence);
};

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function nonEmpty(value) {
  return typeof value === 'string' && value.trim().length > 0;
}

function positive(value) {
  return Number.isSafeInteger(value) && value > 0;
}

function hasPositiveStartupBudgets(value) {
  return isRecord(value) &&
    Object.keys(value).length === STARTUP_BUDGET_FIELDS.length &&
    STARTUP_BUDGET_FIELDS.every(field => Number.isSafeInteger(value[field]) && value[field] > 0);
}

function sameJson(left, right) {
  return isDeepStrictEqual(left, right);
}

function sanitize(value, secrets = []) {
  if (Array.isArray(value)) return value.map(item => sanitize(item, secrets));
  if (isRecord(value)) {
    return Object.fromEntries(Object.entries(value)
      .filter(([key]) => !/^(?:modelCredentialReference|configureEndpoint|observationEndpoint|configureEndpointUrl|observationEndpointUrl)$/i.test(key))
      .map(([key, item]) => [
      key,
      /token|authorization|secret|credential|password|cookie/i.test(key)
        ? REDACTED
        : sanitize(item, secrets),
      ]));
  }
  if (typeof value !== 'string') return value;
  let text = value;
  for (const secret of secrets) {
    if (typeof secret === 'string' && secret.length > 0) text = text.replaceAll(secret, REDACTED);
  }
  return text
    .replace(/Bearer\s+[^\s,;"']+/gi, `Bearer ${REDACTED}`)
    .replace(/[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/g, REDACTED)
    .replace(/((?:token|secret|password|credential)\s*[:=]\s*)[^\s,;"']+/gi, `$1${REDACTED}`);
}

function inspectInputForSecrets(value, path = 'input') {
  if (Array.isArray(value)) {
    value.forEach((item, index) => inspectInputForSecrets(item, `${path}[${index}]`));
    return;
  }
  if (!isRecord(value)) return;
  for (const [key, item] of Object.entries(value)) {
    if (/^(?:command|commands|commandline|script|shell|argv|executable|executablepath)$/i.test(key)) {
      block('arbitrary_native_command_in_input', 'Native probe commands are source-owned and may not be supplied through input pins.', {
        field: `${path}.${key}`,
      });
    }
    if (/(?:token|authorization|credential|password|secret|cookie|configureendpoint|observationendpoint)/i.test(key)) {
      block('credential_in_input', 'Credentials must come from the in-memory auth provider, not input pins.', {
        field: `${path}.${key}`,
      });
    }

    inspectInputForSecrets(item, `${path}.${key}`);
  }
}

function placementResourceId(placement) {
  return placement?.resource?.resourceId ?? null;
}

function placementResourceGeneration(placement) {
  return placement?.resource?.generation ?? null;
}

function environmentFenceFromLifecycle(snapshot) {
  return snapshot?.fence;
}

function environmentOwnerFromFence(fence) {
  return fence?.owner;
}

function placementFence(placement) {
  return {
    lifecycleGeneration: placement?.lifecycleGeneration,
    currentFencingGeneration: placement?.currentFencingGeneration,
    providerFencingGeneration: placement?.providerFencingGeneration,
    leaseRevision: placement?.leaseRevision,
  };
}

function projectVolumeOwner(snapshot) {
  const owner = snapshot?.specification?.owner;
  const kind = Number.isSafeInteger(owner?.kind)
    ? WORKSPACE_VOLUME_OWNER_KINDS[owner.kind]
    : owner?.kind;
  if (!WORKSPACE_VOLUME_OWNER_KINDS.includes(kind) || !nonEmpty(owner?.id)) {
    block('volume_owner_readback_missing', 'The current Environment volume specification must include its exact owner kind and ID.');
  }
  return { kind, id: owner.id };
}

function isObservedPlacementFence(placement) {
  const fence = placementFence(placement);
  return positive(fence.lifecycleGeneration) &&
    positive(fence.currentFencingGeneration) &&
    positive(fence.providerFencingGeneration) &&
    positive(fence.leaseRevision);
}

function projectSafeRegistration(registration) {
  const binding = registration?.binding;
  if (!isRecord(registration) || !isRecord(binding)) return null;
  return {
    runtimeInstanceId: registration.runtimeInstanceId,
    revision: registration.revision,
    state: registration.state,
    expiresAt: registration.expiresAt,
    binding: {
      actorId: binding.actorId,
      tenantId: binding.tenantId,
      projectId: binding.projectId,
      runId: binding.runId,
      sessionId: binding.sessionId,
      environmentId: binding.environmentId,
      placementUid: binding.placementUid,
      placementGeneration: binding.placementGeneration,
      profileId: binding.profileId,
      executionFence: binding.executionFence,
      environmentCurrentFencingGeneration: binding.environmentCurrentFencingGeneration,
      environmentProviderFencingGeneration: binding.environmentProviderFencingGeneration,
    },
  };
}

function isSha256Hash(value) {
  return typeof value === 'string' && SHA256_HASH.test(value);
}

function sha256Utf8(value) {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

function projectSafeReadinessReceipt(receipt) {
  if (!isRecord(receipt) || !isRecord(receipt.image) || !Array.isArray(receipt.startupPhases)) return null;
  return {
    contractVersion: receipt.contractVersion,
    runtimeInstanceId: receipt.runtimeInstanceId,
    registrationRevision: receipt.registrationRevision,
    executionFence: receipt.executionFence,
    image: {
      digest: receipt.image.digest,
      platform: receipt.image.platform,
      compressedPullBytes: receipt.image.compressedPullBytes,
    },
    startupPhases: receipt.startupPhases.map(phase => ({
      phase: phase?.phase,
      contractVersion: phase?.contractVersion,
      observedAt: phase?.observedAt,
      ...(phase?.imageDigest === undefined || phase?.imageDigest === null ? {} : { imageDigest: phase.imageDigest }),
      ...(phase?.compressedPullBytes === undefined || phase?.compressedPullBytes === null
        ? {}
        : { compressedPullBytes: phase.compressedPullBytes }),
    })),
    sourceGrant: isRecord(receipt.sourceGrant)
      ? {
        grantId: receipt.sourceGrant.grantId,
        runtimeInstanceId: receipt.sourceGrant.runtimeInstanceId,
        registrationRevision: receipt.sourceGrant.registrationRevision,
        revision: receipt.sourceGrant.revision,
      }
      : null,
  };
}

function ownerSelector(owner) {
  return {
    ownerId: owner.ownerId,
    actorId: owner.actorId,
    tenantId: owner.tenantId,
    projectId: owner.projectId,
    runId: owner.runId,
    environmentId: owner.environmentId,
    rootSessionId: owner.rootSessionId,
    sessionId: owner.sessionId,
    executionFence: owner.executionFence,
  };
}

function sameOwnerSelector(left, right) {
  return sameJson(ownerSelector(left), ownerSelector(right));
}

function selectorDiffers(left, right) {
  return ['actorId', 'tenantId', 'projectId', 'runId', 'environmentId', 'rootSessionId', 'sessionId']
    .some(key => left[key] !== right[key]);
}

function validateOwnerPins(input) {
  const { pins } = input;
  const storageOwners = pins.storageReceipt?.owners;
  const fenceOwners = pins.ownerFences?.owners;
  const runtimeOwners = pins.runtimeReceipt?.owners;
  const ownershipReceipts = pins.fixtureScope?.ownershipReceipts;

  if (![storageOwners, fenceOwners, runtimeOwners, ownershipReceipts].every(Array.isArray) ||
      storageOwners.length !== 2 || fenceOwners.length !== 2 ||
      runtimeOwners.length !== 2 || ownershipReceipts.length < 8) {
    block('owner_receipts_missing', 'Exactly two matching owner receipts are required in every pin set.');
  }

  const owners = [];
  for (const storage of storageOwners) {
    if (!isRecord(storage) || ![
      'ownerId', 'actorId', 'tenantId', 'projectId', 'runId', 'environmentId',
      'rootSessionId', 'sessionId',
    ]
      .every(key => nonEmpty(storage[key]))) {
      block('storage_owner_selector_missing', 'A storage receipt is missing its owner and actor/tenant selectors.');
    }
    if (!positive(storage.executionFence)) {
      block('owner_execution_fence_missing', 'Each owner fixture must pin its exact execution fence.', {
        ownerId: storage.ownerId,
      });
    }
    const fence = fenceOwners.find(item => item?.ownerId === storage.ownerId);
    const runtime = runtimeOwners.find(item => item?.ownerId === storage.ownerId);
    if (!fence || !runtime || !sameOwnerSelector(storage, runtime) ||
        !sameOwnerSelector(storage, fence)) {
      block('owner_selector_mismatch', 'Storage, runtime, fence, and invocation-creation selectors must match.', {
        ownerId: storage.ownerId,
      });
    }
    if (!isRecord(fence.lifecycleFence) ||
        !positive(fence.lifecycleFence.lifecycleGeneration) ||
        !positive(fence.lifecycleFence.currentFencingGeneration) ||
        !positive(fence.lifecycleFence.leaseRevision) ||
        !isRecord(fence.environmentFence)) {
      block('owner_lifecycle_fence_missing', 'Each owner needs its current lifecycle generation, fencing generation, and lease revision.', {
        ownerId: storage.ownerId,
      });
    }
    if (!isRecord(storage.volume) || !nonEmpty(storage.volume.volumeId) ||
        !positive(storage.volume.resourceGeneration) ||
        !Number.isSafeInteger(storage.volume.dataGeneration) ||
        !positive(storage.volume.transitionRevision) ||
        !nonEmpty(storage.volume.phase) ||
        !isRecord(storage.volume.providerBinding) ||
        !isRecord(storage.volume.owner) ||
        !WORKSPACE_VOLUME_OWNER_KINDS.includes(storage.volume.owner.kind) ||
        !nonEmpty(storage.volume.owner.id) ||
        !isRecord(storage.volume.pvc) || !nonEmpty(storage.volume.pvc.name) ||
        !nonEmpty(storage.volume.pvc.uid) ||
        !isRecord(storage.volume.pv) || !nonEmpty(storage.volume.pv.name) ||
        !nonEmpty(storage.volume.pv.uid) ||
        !nonEmpty(storage.volume.mountPath) || !nonEmpty(storage.volume.claimUid) ||
        !Array.isArray(storage.volume.mountAllowlist) || storage.volume.mountAllowlist.length === 0) {
      block('storage_receipt_incomplete', 'Each owner needs a complete volume, provider binding, PVC/PV UID, and mount allowlist receipt.', {
        ownerId: storage.ownerId,
      });
    }
    const mountParts = storage.volume.mountPath.split('/').filter(Boolean);
    if (!storage.volume.mountPath.startsWith('/') || mountParts.length === 0 ||
        storage.volume.mountPath.includes('\\') ||
        mountParts.some(segment => segment === '.' || segment === '..' || /[\u0000-\u001f]/.test(segment)) ||
        !storage.volume.mountAllowlist.some(item =>
          item?.volumeName === 'agentweaver-workspace' &&
          item?.containerName === 'agenthost' &&
          item?.pvcName === storage.volume.pvc.name &&
          item?.mountPath === storage.volume.mountPath &&
          item?.readOnly === false)) {
      block('mount_allowlist_mismatch', 'The exact AgentHost workspace PVC mount must be allowlisted read/write at a safe absolute path.', {
        ownerId: storage.ownerId,
      });
    }
    const matchingProvider = pins.providerReceipt?.owners?.find(item => item?.ownerId === storage.ownerId);
    if (!matchingProvider ||
        matchingProvider.pvc?.uid !== storage.volume.pvc.uid ||
        matchingProvider.pvc?.name !== storage.volume.pvc.name ||
        matchingProvider.pv?.uid !== storage.volume.pv.uid ||
        matchingProvider.pv?.name !== storage.volume.pv.name ||
        matchingProvider.claimUid !== storage.volume.claimUid ||
        !sameJson(matchingProvider.providerBinding, storage.volume.providerBinding) ||
        matchingProvider.clusterIdentity?.uid !== pins.context.clusterIdentity.uid) {
      block('provider_storage_receipt_mismatch', 'Provider PVC/PV and binding receipts must match the owner-scoped storage pin.', {
        ownerId: storage.ownerId,
      });
    }
    const scopedReceipts = ownershipReceipts.filter(receipt => {
      const ownerFence = receipt?.ownerFence;
      const owner = ownerFence?.owner ?? ownerFence;
      return isRecord(receipt) && receipt.invocationId === pins.fixtureScope.invocationId &&
        isRecord(owner) &&
        owner.actorId === storage.actorId &&
        owner.tenantId === storage.tenantId &&
        owner.projectId === storage.projectId &&
        owner.runId === storage.runId &&
        owner.environmentId === storage.environmentId &&
        owner.rootSessionId === storage.rootSessionId &&
        owner.sessionId === storage.sessionId &&
        (owner.executionFence ?? ownerFence.executionFence) === storage.executionFence;
    });
    if (scopedReceipts.length < 5 ||
        scopedReceipts.some(receipt =>
          !nonEmpty(receipt.resourceType) || !nonEmpty(receipt.resourceId) ||
          !isRecord(receipt.creationReceipt) || !isRecord(receipt.observedReceipt) ||
          (receipt.ownerFence.lifecycleFence ?? receipt.ownerFence).lifecycleGeneration !== fence.lifecycleFence.lifecycleGeneration ||
          (receipt.ownerFence.lifecycleFence ?? receipt.ownerFence).currentFencingGeneration !== fence.lifecycleFence.currentFencingGeneration ||
          (receipt.ownerFence.lifecycleFence ?? receipt.ownerFence).leaseRevision !== fence.lifecycleFence.leaseRevision ||
          receipt.creationReceipt.resourceId !== receipt.resourceId ||
          receipt.observedReceipt.resourceId !== receipt.resourceId)) {
      block('invocation_ownership_receipt_missing', 'Each owner resource needs matching actual creation/readback receipts bound to this invocation and current fence.', {
        ownerId: storage.ownerId,
      });
    }
    const environmentReceipt = scopedReceipts.find(receipt => receipt.resourceId === storage.environmentId);
    const volumeReceipt = scopedReceipts.find(receipt => receipt.resourceId === storage.volume.volumeId);
    if (!environmentReceipt || !volumeReceipt) {
      block('invocation_owner_resources_missing', 'Invocation ownership receipts must cover the exact Environment and Workspace volume IDs.', {
        ownerId: storage.ownerId,
      });
    }
    if (![runtime.sandboxResourceId, runtime.networkPolicyResourceId, runtime.lateSandboxResourceId]
      .every(nonEmpty) ||
        ![runtime.sandboxUid, runtime.networkPolicyUid, runtime.lateSandboxUid].every(nonEmpty) ||
        runtime.sandboxLabels?.[SANDBOX_LABELS.claimUid] !== runtime.sandboxResourceId ||
        runtime.lateSandboxUid !== runtime.lateSandboxResourceId ||
        !positive(runtime.sandboxResourceGeneration) ||
        !isRecord(runtime.sandboxLabels) ||
        !Object.values(SANDBOX_LABELS).every(key => nonEmpty(runtime.sandboxLabels[key])) ||
        !nonEmpty(runtime.networkPolicyResourceId) ||
        !scopedReceipts.some(receipt => receipt.resourceId === runtime.sandboxResourceId) ||
        !scopedReceipts.some(receipt => receipt.resourceId === runtime.networkPolicyResourceId) ||
        !scopedReceipts.some(receipt => receipt.resourceId === runtime.lateSandboxResourceId)) {
      block('invocation_runtime_resources_missing', 'Invocation receipts must cover the exact Sandbox, policy, and late-resource IDs and UIDs.', {
        ownerId: storage.ownerId,
      });
    }
    owners.push({ storage, fence, runtime, scopedReceipts });
  }

  if (owners[0].storage.ownerId === owners[1].storage.ownerId ||
      !selectorDiffers(owners[0].storage, owners[1].storage)) {
    block('owner_scopes_not_distinct', 'The two owner scopes must differ; a shared actor is allowed when tenant or resource scope differs.');
  }

  return owners;
}

function receiptUid(receipt) {
  return receipt?.uid ??
    receipt?.resourceUid ??
    receipt?.resource?.uid ??
    receipt?.observedResource?.uid ??
    receipt?.metadata?.uid ??
    receipt?.pvc?.uid ??
    receipt?.pv?.uid ??
    null;
}

function findOwnershipReceipt(owner, resourceId, expectedUid) {
  const receipt = owner.scopedReceipts.find(item => item.resourceId === resourceId);
  if (!receipt) {
    block('invocation_resource_receipt_missing', 'The exact live resource has no invocation-bound ownership receipt.', {
      ownerId: owner.storage.ownerId,
      resourceId,
    });
  }
  if (expectedUid && receiptUid(receipt.observedReceipt) !== expectedUid) {
    block('invocation_resource_uid_mismatch', 'The observed ownership receipt UID does not match the live resource UID.', {
      ownerId: owner.storage.ownerId,
      resourceId,
      expectedUid,
      observedUid: receiptUid(receipt.observedReceipt),
    });
  }
  return {
    resourceId: receipt.resourceId,
    uid: receiptUid(receipt.observedReceipt),
    operationId: receipt.creationReceipt.operationId ?? receipt.observedReceipt.operationId ?? null,
    creationReceipt: receipt.creationReceipt,
    observedReceipt: receipt.observedReceipt,
  };
}

function validateInput(input, dependencies) {
  if (!isRecord(input) || !isRecord(input.environment) || !isRecord(input.pins)) {
    block('input_missing', 'The P1 Environment target and pins are required.');
  }
  inspectInputForSecrets(input);
  const environment = input.environment;
  if (!['baseUrl', 'issuer', 'audience', 'actorId', 'tenantId'].every(key => nonEmpty(environment[key]))) {
    block('environment_target_missing', 'The Environment URL, issuer, audience, actor, and tenant are required.');
  }
  let baseUrl;
  try {
    baseUrl = new URL(environment.baseUrl);
  } catch {
    block('environment_url_invalid', 'The Environment base URL is invalid.');
  }
  if (baseUrl.protocol !== 'https:' || baseUrl.username || baseUrl.password ||
      baseUrl.search || baseUrl.hash || baseUrl.pathname !== '/') {
    block('environment_url_invalid', 'The Environment URL must be an HTTPS origin without embedded credentials or paths.');
  }

  const { pins } = input;
  if (!isRecord(pins.context) || !nonEmpty(pins.context.namespace) ||
      !DNS_LABEL.test(pins.context.namespace) ||
      !isRecord(pins.context.clusterIdentity) || !nonEmpty(pins.context.clusterIdentity.uid) ||
      pins.namespace !== pins.context.namespace) {
    block('cluster_context_missing', 'The target namespace and exact same-cluster identity are required.');
  }
  if (!isRecord(pins.providerReceipt) ||
      pins.providerReceipt.clusterIdentity?.uid !== pins.context.clusterIdentity.uid ||
      !Array.isArray(pins.providerReceipt.owners)) {
    block('provider_receipt_mismatch', 'The provider receipt must pin the same cluster and both owner PVC/PV resources.');
  }
  if (!isRecord(pins.runtimeReceipt) ||
      pins.runtimeReceipt.clusterIdentity?.uid !== pins.context.clusterIdentity.uid ||
      !isRecord(pins.runtimeReceipt.network) ||
      !positive(pins.runtimeReceipt.network.generation) ||
      !Array.isArray(pins.runtimeReceipt.network.allowedServiceIdentities) ||
      pins.runtimeReceipt.network.allowedServiceIdentities.length === 0 ||
      !Array.isArray(pins.runtimeReceipt.network.deniedServiceIdentities) ||
      pins.runtimeReceipt.network.deniedServiceIdentities.length === 0 ||
      !isRecord(pins.runtimeReceipt.network.policy) ||
      !isRecord(pins.runtimeReceipt.network.revoke) ||
      !nonEmpty(pins.runtimeReceipt.network.ownerId) ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
        .test(pins.runtimeReceipt.network.reconcileOperationId ?? '') ||
      !isRecord(pins.runtimeReceipt.network.revoke.fence) ||
      !positive(pins.runtimeReceipt.network.revoke.policyGeneration) ||
      !positive(pins.runtimeReceipt.network.revoke.expectedPreviousPolicyGeneration) ||
      !nonEmpty(pins.runtimeReceipt.network.revoke.idempotencyKey) ||
      !isRecord(pins.runtimeReceipt.activation) ||
      !isRecord(pins.runtimeReceipt.registration)) {
    block('runtime_receipt_missing', 'Current network, service-identity, activation, and registration-fence receipts are required.');
  }
  const activation = pins.runtimeReceipt?.activation;
  const registration = projectSafeRegistration(pins.runtimeReceipt?.registration);
  const safeActivation = projectSafeReadinessReceipt(activation);
  const registrationBinding = registration?.binding;
  const identityPattern = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\/[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;
  if (!Array.isArray(pins.runtimeReceipt?.network?.allowedServiceIdentities) ||
      !Array.isArray(pins.runtimeReceipt?.network?.deniedServiceIdentities) ||
      [...pins.runtimeReceipt.network.allowedServiceIdentities,
        ...pins.runtimeReceipt.network.deniedServiceIdentities]
        .some(identity => !nonEmpty(identity) || !identityPattern.test(identity)) ||
      pins.runtimeReceipt.network.allowedServiceIdentities.some(identity =>
        pins.runtimeReceipt.network.deniedServiceIdentities.includes(identity)) ||
      !isRecord(pins.runtimeReceipt.network.policy?.selectorLabels) ||
      !nonEmpty(pins.runtimeReceipt.network.policy?.annotations?.hash) ||
      pins.runtimeReceipt.network.policy?.annotations?.generation !== String(pins.runtimeReceipt.network.generation) ||
      pins.runtimeReceipt.network.policy?.annotations?.revoked !== 'false' ||
      !nonEmpty(activation.ownerId) ||
      !safeActivation || safeActivation.contractVersion !== 1 ||
      !nonEmpty(safeActivation.runtimeInstanceId) ||
      !positive(safeActivation.registrationRevision) ||
      !positive(safeActivation.executionFence) ||
      !isRecord(safeActivation.image) ||
      !/^sha256:[a-f0-9]{64}$/.test(safeActivation.image.digest ?? '') ||
      safeActivation.image.platform !== 'linux/amd64' ||
      !positive(safeActivation.image.compressedPullBytes) ||
      !Array.isArray(safeActivation.startupPhases) ||
      safeActivation.startupPhases.length !== RUNTIME_HOST_PHASES.length ||
      safeActivation.startupPhases.some(phase =>
        !['scheduled', 'imageReady', 'started', 'configured', 'ready'].includes(phase.phase) ||
        phase.contractVersion !== 1 ||
        !Number.isFinite(Date.parse(phase.observedAt)) ||
        (phase.imageDigest !== undefined && phase.imageDigest !== safeActivation.image.digest) ||
        (phase.compressedPullBytes !== undefined &&
          phase.compressedPullBytes !== safeActivation.image.compressedPullBytes)) ||
      safeActivation.startupPhases.some((phase, index) => phase.phase !== RUNTIME_HOST_PHASES[index]) ||
      new Set(safeActivation.startupPhases.map(phase => phase.phase)).size !== safeActivation.startupPhases.length ||
      !nonEmpty(safeActivation.sourceGrant?.grantId) ||
      safeActivation.sourceGrant?.runtimeInstanceId !== safeActivation.runtimeInstanceId ||
      safeActivation.sourceGrant?.registrationRevision !== safeActivation.registrationRevision ||
      !positive(safeActivation.sourceGrant?.revision) ||
      !registration || !nonEmpty(registration.runtimeInstanceId) ||
      !positive(registration.revision) || registration.state !== 'active' ||
      !Number.isFinite(Date.parse(registration.expiresAt)) ||
      Date.parse(registration.expiresAt) <= (typeof dependencies?.now === 'function'
        ? dependencies.now()
        : Date.now()) ||
      !isRecord(registrationBinding) ||
      !['actorId', 'tenantId', 'projectId', 'runId', 'sessionId', 'environmentId', 'placementUid', 'profileId']
        .every(key => nonEmpty(registrationBinding[key])) ||
      !positive(registrationBinding.placementGeneration) ||
      !positive(registrationBinding.executionFence) ||
      !positive(registrationBinding.environmentCurrentFencingGeneration) ||
      !positive(registrationBinding.environmentProviderFencingGeneration)) {
    block('runtime_receipt_incomplete', 'Network service identities and exact AgentHost activation/readiness and registration fences are required.');
  }
  if (!isRecord(pins.sourceReceipt) ||
      !isRecord(pins.sourceReceipt.acceptedOptions) ||
      !hasPositiveStartupBudgets(pins.sourceReceipt.startupBudget) ||
      !isRecord(pins.imageReceipt) ||
      !nonEmpty(pins.imageReceipt.reference) || !nonEmpty(pins.imageReceipt.digest) ||
      !pins.imageReceipt.reference.endsWith(`@${pins.imageReceipt.digest}`) ||
      !isRecord(pins.fixtureScope) ||
      pins.fixtureScope.kind !== 'p1-runner-owned' ||
      pins.fixtureScope.status !== 'ready' ||
      !nonEmpty(pins.fixtureScope.invocationId) ||
      !nonEmpty(      pins.fixtureScope.fixtureId) ||
      !Array.isArray(pins.fixtureScope.allowedJourneys) ||
      pins.fixtureScope.allowedJourneys.length === 0 ||
      !Array.isArray(pins.fixtureScope.ownershipReceipts) ||
      !Number.isFinite(Date.parse(pins.fixtureScope.expiresAt)) ||
      Date.parse(pins.fixtureScope.expiresAt) <= (typeof dependencies?.now === 'function'
        ? dependencies.now()
        : Date.now())) {
    block('source_or_image_receipt_missing', 'Accepted source, exact image, and invocation scope receipts are required.');
  }

  const owners = validateOwnerPins(input);
  const policyOwner = owners.find(item =>
    item.storage.ownerId === pins.runtimeReceipt.network.ownerId);
  const activationOwner = owners.find(item =>
    item.storage.ownerId === pins.runtimeReceipt.activation.ownerId);
  if (!policyOwner || !activationOwner ||
      pins.runtimeReceipt.network.policy.resourceId !== policyOwner.runtime.networkPolicyResourceId ||
      pins.runtimeReceipt.network.policy.uid !== policyOwner.runtime.networkPolicyUid ||
      pins.runtimeReceipt.network.revoke.expectedPreviousPolicyGeneration !== pins.runtimeReceipt.network.generation ||
      pins.runtimeReceipt.network.revoke.policyGeneration <= pins.runtimeReceipt.network.generation ||
      !isRecord(policyOwner.fence.environmentFence) ||
      !sameJson(pins.runtimeReceipt.network.revoke.fence, policyOwner.fence.environmentFence) ||
      !policyOwner.scopedReceipts.some(receipt =>
        receipt.resourceId === pins.runtimeReceipt.network.policy.resourceId &&
        (receipt.creationReceipt.operationId === pins.runtimeReceipt.network.reconcileOperationId ||
          receipt.observedReceipt.operationId === pins.runtimeReceipt.network.reconcileOperationId)) ||
      safeActivation.runtimeInstanceId !== registration.runtimeInstanceId ||
      safeActivation.registrationRevision !== registration.revision ||
      safeActivation.executionFence !== registrationBinding.executionFence ||
      registrationBinding.actorId !== activationOwner.storage.actorId ||
      registrationBinding.tenantId !== activationOwner.storage.tenantId ||
      registrationBinding.projectId !== activationOwner.storage.projectId ||
      registrationBinding.runId !== activationOwner.storage.runId ||
      registrationBinding.sessionId !== activationOwner.storage.sessionId ||
      registrationBinding.environmentId !== activationOwner.storage.environmentId ||
      registrationBinding.executionFence !== activationOwner.storage.executionFence ||
      registrationBinding.placementUid !== activationOwner.runtime.sandboxUid ||
      registrationBinding.placementGeneration !== activationOwner.runtime.sandboxResourceGeneration ||
      registrationBinding.environmentCurrentFencingGeneration !== activationOwner.fence.lifecycleFence.currentFencingGeneration ||
      registrationBinding.environmentProviderFencingGeneration !== Number(
        activationOwner.runtime.sandboxLabels[SANDBOX_LABELS.fencingGeneration]) ||
      safeActivation.sourceGrant.grantId !== activation.sourceGrant?.grantId ||
      safeActivation.sourceGrant.revision !== activation.sourceGrant?.revision) {
    block('runtime_owner_fence_mismatch', 'Network policy, activation, revoke fence, and owner receipts must identify the same current disposable scopes.');
  }
  if (!dependencies || typeof dependencies.fetch !== 'function' ||
      typeof dependencies.authProvider?.getValidatedToken !== 'function' ||
      typeof dependencies.journeyAuthorizer?.authorize !== 'function' ||
      typeof dependencies.processProvider?.run !== 'function') {
    block('injected_dependencies_missing', 'Injected HTTP, validated-auth, allowed-journey authorization, and native process providers are required.');
  }
  if (typeof dependencies.runtimeReadinessProvider?.readCurrent !== 'function') {
    block('runtime_current_readback_unavailable', 'An independent current RuntimeHost registration and readiness reader is required before any probe can run.');
  }
  return { baseUrl, owners };
}

function environmentPath(owner, suffix) {
  const prefix = `/api/projects/${encodeURIComponent(owner.projectId)}` +
    `/runs/${encodeURIComponent(owner.runId)}/environments/${encodeURIComponent(owner.environmentId)}`;
  return `${prefix}${suffix}`;
}

function readBody(response) {
  if (typeof response?.json !== 'function') return Promise.resolve(response?.body ?? null);
  return Promise.resolve().then(() => response.json()).catch(() => null);
}

function requireUid(observed, expected, code, label) {
  if (!nonEmpty(expected) || observed !== expected) {
    block(code, `${label} UID does not match the pinned owner receipt.`, { expectedUid: expected ?? null, observedUid: observed ?? null });
  }
}

function requireOwnerFence(lifecycle, placement, owner, label) {
  const lifecycleFence = environmentFenceFromLifecycle(lifecycle);
  const lifecycleOwner = environmentOwnerFromFence(lifecycleFence);
  const expectedLifecycleFence = owner.fence.lifecycleFence;
  const expectedEnvironmentFence = owner.fence.environmentFence;
  const expectedEnvironmentOwner = expectedEnvironmentFence?.owner;
  const current = placementFence(placement);
  if (!isRecord(lifecycleFence) || !isRecord(lifecycleOwner) ||
      !isRecord(expectedEnvironmentFence) || !isRecord(expectedEnvironmentOwner) ||
      lifecycle.state !== 'active' ||
      lifecycleOwner.tenantId !== owner.storage.tenantId ||
      lifecycleOwner.projectId !== owner.storage.projectId ||
      lifecycleOwner.runId !== owner.storage.runId ||
      lifecycleOwner.environmentId !== owner.storage.environmentId ||
      !sameJson(lifecycleFence, expectedEnvironmentFence) ||
      expectedEnvironmentOwner.tenantId !== owner.storage.tenantId ||
      expectedEnvironmentOwner.projectId !== owner.storage.projectId ||
      expectedEnvironmentOwner.runId !== owner.storage.runId ||
      expectedEnvironmentOwner.environmentId !== owner.storage.environmentId ||
      lifecycleFence.lifecycleGeneration !== expectedLifecycleFence.lifecycleGeneration ||
      placement?.tenantId !== owner.storage.tenantId ||
      placement?.projectId !== owner.storage.projectId ||
      placement?.runId !== owner.storage.runId ||
      placement?.environmentId !== owner.storage.environmentId ||
      placement?.isCurrent !== true ||
      placement?.lifecycleGeneration !== lifecycleFence.lifecycleGeneration ||
      !isObservedPlacementFence(placement) ||
      current.currentFencingGeneration !== expectedLifecycleFence.currentFencingGeneration ||
      current.leaseRevision !== expectedLifecycleFence.leaseRevision ||
      current.providerFencingGeneration !== Number(owner.runtime.sandboxLabels[SANDBOX_LABELS.fencingGeneration])) {
    block('owner_fence_mismatch', `The live ${label} lifecycle and placement projections do not match the pinned owner fence.`, {
      ownerId: owner.storage.ownerId,
    });
  }
}

function verifyCluster(observed, expected) {
  if (observed?.clusterIdentity?.uid !== expected.uid) {
    block('cluster_identity_mismatch', 'Native evidence does not identify the pinned cluster.', {
      expectedClusterUid: expected.uid,
      observedClusterUid: observed?.clusterIdentity?.uid ?? null,
    });
  }

}

function verifySandboxOwnership(owner, observed, placement, operationId) {
  const labels = observed?.pod?.labels;
  const expected = sandboxLabelsForResource(owner.runtime, placement, operationId);
  if (!isRecord(labels) || Object.entries(expected).some(([key, value]) =>
    !nonEmpty(value) || labels[key] !== value)) {
    block('sandbox_owner_labels_mismatch', 'Native Sandbox managed, owner, lifecycle, resource, fence, operation, or claim-UID labels differ from the exact owner receipt.', {
      ownerId: owner.storage.ownerId,
      resourceId: placementResourceId(placement),
      podUid: observed?.pod?.uid ?? null,
    });
  }
}

function sandboxLabelsForResource(runtime, placement, operationId) {
  const resourceId = placementResourceId(placement);
  const resourceGeneration = placementResourceGeneration(placement);
  if (!nonEmpty(operationId)) {
    block('sandbox_operation_receipt_missing', 'The actual invocation-owned Sandbox creation operation ID is required to validate its native ownership label.', {
      resourceId,
    });
  }
  if (!isRecord(runtime?.sandboxLabels) ||
      runtime.sandboxLabels[SANDBOX_LABELS.managed] !== 'true' ||
      !nonEmpty(runtime.sandboxLabels[SANDBOX_LABELS.owner]) ||
      !nonEmpty(resourceId) ||
      !positive(placement?.lifecycleGeneration) ||
      !positive(resourceGeneration) ||
      !positive(placement?.providerFencingGeneration)) {
    block('sandbox_labels_missing', 'The current placement and explicit AgentSandbox owner label receipt are required to derive the native selector.', {
      resourceId,
    });
  }
  const operationLabel = operationId.replaceAll('-', '').toLowerCase();
  if (!nonEmpty(operationLabel)) {
    block('sandbox_operation_receipt_missing', 'The actual Sandbox creation operation ID does not produce a valid ownership label.', {
      resourceId,
    });
  }
  return {
    [SANDBOX_LABELS.managed]: 'true',
    [SANDBOX_LABELS.owner]: runtime.sandboxLabels[SANDBOX_LABELS.owner],
    [SANDBOX_LABELS.lifecycle]: String(placement.lifecycleGeneration),
    [SANDBOX_LABELS.resourceGeneration]: String(resourceGeneration),
    [SANDBOX_LABELS.fencingGeneration]: String(placement.providerFencingGeneration),
    [SANDBOX_LABELS.operation]: operationLabel,
    [SANDBOX_LABELS.claimUid]: resourceId,
  };
}

function verifySandboxMount(owner, observed) {
  const volume = owner.storage.volume;
  const allowedMount = volume.mountAllowlist.find(item =>
    item?.volumeName === 'agentweaver-workspace' &&
    item?.containerName === 'agenthost' &&
    item?.pvcName === volume.pvc.name &&
    item?.mountPath === volume.mountPath &&
    item?.readOnly === false);
  const actualMount = observed?.pod?.mounts?.find(item =>
    item?.name === allowedMount?.volumeName &&
    item?.containerName === allowedMount?.containerName &&
    item?.pvcName === allowedMount?.pvcName &&
    item?.pvcUid === volume.pvc.uid &&
    item?.mountPath === allowedMount?.mountPath);
  if (!allowedMount || !actualMount) {
    block('sandbox_mount_receipt_mismatch', 'The actual AgentHost Pod mount does not match the exact owner PVC and allowlist descriptor.', {
      ownerId: owner.storage.ownerId,
      expectedPvcUid: volume.pvc.uid,
    });
  }
}

function resultFor(name, status, details = {}, secrets = []) {
  return {
    name,
    status,
    ...(details.code ? { code: details.code } : {}),
    ...(details.reason ? { reason: sanitize(details.reason, secrets) } : {}),
    evidence: sanitize(details.evidence ?? {}, secrets),
    cleanup: sanitize(details.cleanup ?? { status: 'not-started', actions: [] }, secrets),
  };
}

function evidenceError(error, secrets) {
  if (error instanceof ProbeIssue) {
    return {
      status: error.status,
      code: error.code,
      reason: sanitize(error.message, secrets),
      evidence: sanitize(error.evidence, secrets),
    };
  }
  return {
    status: 'blocked',
    code: 'probe_dependency_unavailable',
    reason: 'A required probe dependency failed.',
    evidence: {},
  };
}

/**
 * Executes the P1 native probe set and returns structured observations for each readiness gate.
 * Invalid evidence bindings fail closed; individual probe failures are captured in the result.
 */
async function runP1NativeProbesCore(input, dependencies) {
  const startedAt = new Date().toISOString();
  const probes = [];
  const secrets = [];
  let validated;
  try {
    validated = validateInput(input, dependencies);
  } catch (error) {
    const issue = evidenceError(error, secrets);
    for (const name of PROBE_NAMES) probes.push(resultFor(name, 'blocked', issue, secrets));
    return {
      kind: 'p1-native-acceptance',
      schemaVersion: 1,
      overallStatus: 'blocked',
      startedAt,
      completedAt: new Date().toISOString(),
      probes,
    };
  }

  const { baseUrl, owners } = validated;
  const credentials = new Map();
  try {
    for (const { storage } of owners) {
      const auth = await dependencies.authProvider.getValidatedToken({
        issuer: input.environment.issuer,
        audience: input.environment.audience,
        actorId: storage.actorId,
        tenantId: storage.tenantId,
        ownerId: storage.ownerId,
        projectId: storage.projectId,
        runId: storage.runId,
        environmentId: storage.environmentId,
      });
      if (typeof auth?.token === 'string') secrets.push(auth.token);
      if (!isRecord(auth) || auth.validated !== true || !nonEmpty(auth.token) ||
          auth.issuer !== input.environment.issuer ||
          auth.audience !== input.environment.audience ||
          auth.actorId !== storage.actorId || auth.tenantId !== storage.tenantId) {
        block('validated_owner_token_missing', 'The auth provider did not return a validated token for the exact owner selector.', {
          ownerId: storage.ownerId,
        });
      }
      credentials.set(storage.ownerId, auth.token);
    }
  } catch (error) {
    const issue = evidenceError(error, secrets);
    for (const name of PROBE_NAMES) probes.push(resultFor(name, 'blocked', issue, secrets));
    return {
      kind: 'p1-native-acceptance',
      schemaVersion: 1,
      overallStatus: 'blocked',
      startedAt,
      completedAt: new Date().toISOString(),
      probes,
    };
  }

  const apiAs = async (authOwner, targetOwner, method, suffix, body) => {
    const headers = {
      Accept: 'application/json',
      Authorization: `Bearer ${credentials.get(authOwner.ownerId)}`,
      [TENANT_SELECTOR_HEADER]: authOwner.tenantId,
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await dependencies.fetch(new URL(environmentPath(targetOwner, suffix), baseUrl), {
      method,
      headers,
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      redirect: 'error',
    });
    return { status: response.status, body: await readBody(response) };
  };
  const api = (owner, method, suffix, body) => apiAs(owner, owner, method, suffix, body);
  const authorizeMutation = async (action, owner) => {
    const now = typeof dependencies.now === 'function' ? dependencies.now() : Date.now();
    if (input.pins.fixtureScope.status !== 'ready' ||
        Date.parse(input.pins.fixtureScope.expiresAt) <= now) {
      block('fixture_scope_expired', 'The invocation-owned fixture scope is not currently ready and unexpired.', {
        action,
        ownerId: owner.ownerId,
      });
    }
    const lifecycleResponse = await api(owner, 'GET', '/lifecycle');
    if (lifecycleResponse.status !== 200) {
      block('mutation_owner_fence_unavailable', 'A fresh Environment lifecycle read is required before every mutation.', {
        action,
        ownerId: owner.ownerId,
        status: lifecycleResponse.status,
      });
    }
    const lifecycle = lifecycleResponse.body;
    const fence = environmentFenceFromLifecycle(lifecycle);
    const lifecycleOwner = environmentOwnerFromFence(fence);
    if (!isRecord(fence) || !isRecord(lifecycleOwner) || lifecycle.state !== 'active' ||
        lifecycleOwner.tenantId !== owner.tenantId ||
        lifecycleOwner.projectId !== owner.projectId ||
        lifecycleOwner.runId !== owner.runId ||
        lifecycleOwner.environmentId !== owner.environmentId ||
        !sameJson(fence, input.pins.ownerFences.owners.find(item => item.ownerId === owner.ownerId)?.environmentFence) ||
        !positive(fence.lifecycleGeneration)) {
      block('mutation_owner_fence_mismatch', 'The fresh Environment lifecycle read does not match the exact owner selectors and current fence.', {
        action,
        ownerId: owner.ownerId,
      });
    }
    const ownerState = live.get(owner.ownerId);
    const placementResponse = await api(owner, 'GET', '/sandbox/v1/placement');
    const placementMissingAllowed = placementResponse.status === 404 &&
      [
        'sandbox-provision', 'sandbox-reconcile',
        'workspace-volume-release', 'workspace-volume-bind',
        'network-policy-revoke', 'network-policy-reconcile',
      ]
        .includes(action);
    if (placementResponse.status === 200) {
      const placement = placementResponse.body;
      if (!ownerState || placement?.isCurrent !== true ||
          placement?.tenantId !== owner.tenantId ||
          placement?.projectId !== owner.projectId ||
          placement?.runId !== owner.runId ||
          placement?.environmentId !== owner.environmentId ||
          placement?.lifecycleGeneration !== fence.lifecycleGeneration ||
          !isObservedPlacementFence(placement)) {
        block('mutation_placement_fence_mismatch', 'The fresh current placement does not match the lifecycle owner and fence.', {
          action,
          ownerId: owner.ownerId,
        });
      }
      const prior = ownerState.placement;
      if (prior?.isCurrent === true &&
          placementResourceId(prior) === placementResourceId(placement) &&
          placementResourceGeneration(prior) === placementResourceGeneration(placement) &&
          !sameJson(placementFence(prior), placementFence(placement))) {
        block('mutation_placement_fence_changed', 'The current placement fence changed without an invocation-owned transition receipt.', {
          action,
          ownerId: owner.ownerId,
        });
      }
      ownerState.placement = placement;
    } else if (!placementMissingAllowed || !ownerState?.placement) {
      block('mutation_placement_unavailable', 'The current Sandbox projection is required unless the exact authorized operation expects no active placement.', {
        action,
        ownerId: owner.ownerId,
        status: placementResponse.status,
      });
    }
    const decision = await dependencies.journeyAuthorizer.authorize({
      action,
      fixtureId: input.pins.fixtureScope.fixtureId,
      invocationId: input.pins.fixtureScope.invocationId,
      allowedJourneys: [...input.pins.fixtureScope.allowedJourneys],
      owner: ownerSelector(owner),
    });
    if (!isRecord(decision) || decision.allowed !== true ||
        !input.pins.fixtureScope.allowedJourneys.some(journey => sameJson(journey, decision.journey))) {
      block('fixture_journey_not_authorized', 'The exact mutation is not authorized by an opaque journey value supplied in the invocation fixture scope.', {
        action,
        ownerId: owner.ownerId,
      });
    }
  };
  const native = async (recipeId, args) => {
    if (!SAFE_RECIPES.has(recipeId)) {
      block('unsafe_native_recipe', 'Only source-owned native probe recipes may execute.', { recipeId });
    }
    const result = await dependencies.processProvider.run({
      recipeId,
      args: { ...args, expectedClusterIdentity: input.pins.context.clusterIdentity },
    });
    verifyCluster(result, input.pins.context.clusterIdentity);
    return result;
  };
  const observeSandbox = (owner, placement, options = {}) => {
    const { expectedOperationId, ...nativeOptions } = options;
    const sandboxClaimUid = placementResourceId(placement);
    const expectedLabels = sandboxLabelsForResource(owner.runtime, placement, expectedOperationId);
    return native('observe-sandbox', {
      ...nativeOptions,
      namespace: input.pins.namespace,
      owner: ownerSelector(owner.storage),
      placement,
      expectedOperationId,
      workspaceClaimUid: owner.storage.volume.claimUid,
      sandboxClaimUid,
      expectedLabels,
      expectedPolicyGeneration: input.pins.runtimeReceipt.network.generation,
    });
  };
  const verifyOwnedSandboxResourcesAbsent = async (owner, resources) => {
    const readbacks = [];
    for (const resource of resources) {
      if (!nonEmpty(resource.uid)) {
        block('sandbox_pod_receipt_missing', 'Every invocation-owned Sandbox claim must retain its exact previously observed Pod UID for absence verification.', {
          ownerId: owner.storage.ownerId,
          resourceId: resource.resourceId,
        });
      }
      readbacks.push(await native('observe-owned-resource', {
        namespace: input.pins.namespace,
        owner: ownerSelector(owner.storage),
        resourceId: resource.resourceId,
        expectedUid: resource.resourceId,
        expectedPodUids: [resource.uid],
      }));
    }
    return {
      readbacks,
      confirmed: resources.length > 0 && readbacks.length === resources.length &&
        readbacks.every(readback =>
          readback.exists === false &&
          Array.isArray(readback.dependentPods) &&
          readback.dependentPods.length === 0),
    };
  };
  const validateCurrentRuntimeReadiness = async (owner, state) => {
    const activation = input.pins.runtimeReceipt.activation;
    const currentReadback = await dependencies.runtimeReadinessProvider.readCurrent({
      owner: ownerSelector(owner.storage),
      placement: state.placement,
      expectedPodUid: state.sandboxObservation.pod.uid,
      expectedRuntimeInstanceId: activation.runtimeInstanceId,
    });
    const registration = projectSafeRegistration(currentReadback?.registration);
    const readiness = projectSafeReadinessReceipt(currentReadback?.readinessReceipt);
    if (!registration ||
        !sameJson(registration, projectSafeRegistration(input.pins.runtimeReceipt.registration)) ||
        !readiness ||
        !sameJson(readiness, projectSafeReadinessReceipt(activation)) ||
        !sameJson(currentReadback?.acceptedOptions, input.pins.sourceReceipt.acceptedOptions) ||
        registration.binding.placementUid !== state.sandboxObservation.pod.uid ||
        registration.binding.placementGeneration !== placementResourceGeneration(state.placement)) {
      block('agenthost_current_readback_mismatch', 'The independent current registration, Host readiness receipt, accepted options, and exact Pod placement must match the sanitized pins.', {
        ownerId: owner.storage.ownerId,
      });
    }
    return { registration, readiness };
  };
  const provisionSandbox = async (state, reason) => {
    const { storage } = state;
    const volume = state.volume;
    const allowlist = storage.volume.mountAllowlist?.find(item =>
      item?.volumeName === 'agentweaver-workspace' &&
      item?.containerName === 'agenthost' &&
      item?.pvcName === storage.volume.pvc.name &&
      item?.mountPath === storage.volume.mountPath &&
      item?.readOnly === false);
    if (!allowlist) {
      block('sandbox_provision_mount_pin_missing', 'Sandbox provision requires the exact owner PVC mount allowlist.', {
        ownerId: storage.ownerId,
      });
    }
    await authorizeMutation('sandbox-provision', storage);
    const provision = await api(storage, 'POST', '/sandbox/provision', {
      volumeId: storage.volume.volumeId,
      volumeResourceGeneration: volume.resourceGeneration,
      dataGeneration: volume.dataGeneration,
      mountPath: allowlist.mountPath,
      readOnly: false,
      networkPolicyGeneration: input.pins.runtimeReceipt.network.generation,
      idempotencyKey: `p1-${reason}-${input.pins.fixtureScope.invocationId}-${randomUUID()}`,
    });
    const provisionResource = provision.body?.resource;
    if (provision.status < 200 || provision.status >= 300 ||
        !nonEmpty(provision.body?.operationId) ||
        provisionResource?.providerId !== 'agent-sandbox' ||
        !nonEmpty(provisionResource?.resourceId) ||
        !positive(provisionResource?.generation)) {
      block('sandbox_provision_receipt_missing', 'Environment did not return an observed provision operation and resource receipt.', {
        ownerId: storage.ownerId,
        status: provision.status,
      });
    }
    const placementResponse = await api(storage, 'GET', '/sandbox/v1/placement');
    if (placementResponse.status !== 200 ||
        placementResponse.body?.isCurrent !== true ||
        placementResponse.body?.tenantId !== storage.tenantId ||
        placementResponse.body?.projectId !== storage.projectId ||
        placementResponse.body?.runId !== storage.runId ||
        placementResponse.body?.environmentId !== storage.environmentId ||
        placementResponse.body?.lifecycleGeneration !== state.fence.lifecycleFence.lifecycleGeneration ||
        !isObservedPlacementFence(placementResponse.body) ||
        placementResourceId(placementResponse.body) !== provisionResource.resourceId ||
        placementResourceGeneration(placementResponse.body) !== provisionResource.generation ||
        placementResponse.body?.resource?.providerId !== provisionResource.providerId) {
      block('sandbox_provision_readback_mismatch', 'Current Environment placement does not match the exact provision response.', {
        ownerId: storage.ownerId,
        operationId: provision.body.operationId,
      });
    }
    const observation = await observeSandbox(state, placementResponse.body, {
      expectedOperationId: provision.body.operationId,
    });
    verifyCluster(observation, input.pins.context.clusterIdentity);
    if (!nonEmpty(observation?.pod?.uid) ||
        observation?.pod?.uid === state.runtime.sandboxUid) {
      block('sandbox_provision_owner_readback_mismatch', 'The provisioned Pod lacks exact owner labels or a native UID observation.', {
        ownerId: storage.ownerId,
      });
    }
    verifySandboxOwnership(state, observation, placementResponse.body, provision.body.operationId);
    verifySandboxMount(state, observation);
    const owned = {
      resourceId: provision.body.resource.resourceId,
      uid: observation.pod.uid,
      operationId: provision.body.operationId,
      resourceGeneration: provision.body.resource.generation,
      current: true,
    };
    ownedSandboxResources.get(storage.ownerId).push(owned);
    state.placement = placementResponse.body;
    state.sandboxObservation = observation;
    state.runtime.sandboxResourceGeneration = provision.body.resource.generation;
    return { ...owned, placement: placementResponse.body };
  };
  const abandonCurrentSandbox = async (state, reason) => {
    const { storage } = state;
    const placementResponse = await api(storage, 'GET', '/sandbox/v1/placement');
    if (placementResponse.status !== 200 || placementResponse.body?.isCurrent !== true) {
      block('sandbox_current_placement_missing', 'An owned active Sandbox is required before the requested lifecycle transition.', {
        ownerId: storage.ownerId,
        status: placementResponse.status,
      });
    }
    const placement = placementResponse.body;
    const resourceId = placementResourceId(placement);
    const ownedReceipt = (ownedSandboxResources.get(storage.ownerId) ?? []).find(item =>
      item.resourceId === resourceId &&
      item.resourceGeneration === placementResourceGeneration(placement));
    const observation = await observeSandbox(state, placement, {
      expectedOperationId: ownedReceipt?.operationId,
    });
    const inventory = ownedSandboxResources.get(storage.ownerId) ?? [];
    const owned = inventory.find(item => item.resourceId === resourceId && item.uid === observation?.pod?.uid);
    if (!owned || placement.projectId !== storage.projectId ||
        placement.runId !== storage.runId ||
        placement.environmentId !== storage.environmentId) {
      block('sandbox_transition_not_invocation_owned', 'The exact live Sandbox UID and owner binding were not created by this invocation.', {
        ownerId: storage.ownerId,
        resourceId: resourceId ?? null,
        podUid: observation?.pod?.uid ?? null,
      });
    }
    verifySandboxOwnership(state, observation, placement, owned.operationId);
    state.placement = placement;
    state.sandboxObservation = observation;
    await authorizeMutation('sandbox-abandon', storage);
    const response = await api(storage, 'POST', '/sandbox/abandon', {
      resourceGeneration: placementResourceGeneration(placement),
      providerFencingGeneration: placement.providerFencingGeneration,
      idempotencyKey: `p1-${reason}-${input.pins.fixtureScope.invocationId}-${randomUUID()}`,
    });
    if (response.status !== 200 || !nonEmpty(response.body?.operationId) ||
        response.body?.resource?.resourceId !== resourceId ||
        response.body?.resource?.generation !== placementResourceGeneration(placement)) {
      block('sandbox_abandon_unverified', 'Environment did not return an observed abandonment operation receipt.', {
        ownerId: storage.ownerId,
        status: response.status,
      });
    }
    const after = await api(storage, 'GET', '/sandbox/v1/placement');
    if (after.status !== 404 && after.body?.isCurrent !== false) {
      block('sandbox_release_readback_mismatch', 'The abandoned invocation-owned placement remains current.', {
        ownerId: storage.ownerId,
        status: after.status,
      });
    }
    owned.current = false;
    return {
      resourceId,
      uid: observation.pod.uid,
      operationId: response.body.operationId,
      placement,
    };
  };

  const live = new Map();
  const ownedSandboxResources = new Map();
  const cleanupActions = new Map(PROBE_NAMES.map(name => [name, []]));
  const cleanupErrors = new Map(PROBE_NAMES.map(name => [name, []]));

  const recordCleanup = (probe, action, status, evidence = {}) => {
    cleanupActions.get(probe).push({ action, status, evidence: sanitize(evidence, secrets) });
    if (status !== 'complete') cleanupErrors.get(probe).push(action);
  };

  const readOwner = async ({ storage, fence, runtime, scopedReceipts }) => {
    const [lifecycleResponse, volumeResponse, placementResponse] = await Promise.all([
      api(storage, 'GET', '/lifecycle'),
      api(storage, 'GET', `/workspace-volumes/${encodeURIComponent(storage.volume.volumeId)}`),
      api(storage, 'GET', '/sandbox/v1/placement'),
    ]);
    if (lifecycleResponse.status !== 200 || volumeResponse.status !== 200 ||
        placementResponse.status !== 200) {
      block('live_owner_readback_missing', 'Lifecycle, volume, and current Sandbox placement readbacks are all required.', {
        ownerId: storage.ownerId,
        statuses: [lifecycleResponse.status, volumeResponse.status, placementResponse.status],
      });
    }
    const lifecycle = lifecycleResponse.body;
    const volume = volumeResponse.body;
    const placement = placementResponse.body;
    const currentVolumeOwner = projectVolumeOwner(volume);
    requireOwnerFence(lifecycle, placement, { storage, fence, runtime }, 'Environment');
    if (volume?.resourceGeneration !== storage.volume.resourceGeneration ||
        volume?.dataGeneration !== storage.volume.dataGeneration ||
        volume?.phase !== storage.volume.phase ||
        !sameJson(volume?.providerBinding, storage.volume.providerBinding) ||
        !sameJson(currentVolumeOwner, storage.volume.owner)) {
      block('live_volume_receipt_mismatch', 'The live volume generations, phase, owner, or provider binding differ from the explicit storage receipt.', {
        ownerId: storage.ownerId,
      });
    }
    const resourceId = placementResourceId(placement);
    if (resourceId !== runtime.sandboxResourceId ||
        placementResourceGeneration(placement) !== runtime.sandboxResourceGeneration ||
        placement?.isCurrent !== true ||
        placement?.projectId !== storage.projectId ||
        placement?.runId !== storage.runId ||
        placement?.environmentId !== storage.environmentId) {
      block('active_sandbox_not_invocation_owned', 'An active Sandbox is usable only when its exact resource and owner scope were created by this invocation.', {
        ownerId: storage.ownerId,
        observedResourceId: resourceId ?? null,
      });
    }
    const sandboxReceipt = scopedReceipts.find(receipt => receipt.resourceId === resourceId);
    if (!sandboxReceipt || !nonEmpty(runtime.sandboxUid) ||
        receiptUid(sandboxReceipt.observedReceipt) !== resourceId) {
      block('active_sandbox_receipt_missing', 'The current Sandbox must match an invocation-bound observed UID receipt.', {
        ownerId: storage.ownerId,
        resourceId,
      });
    }
    const creation = {
      resources: {
        environment: findOwnershipReceipt(
          { storage, scopedReceipts }, storage.environmentId),
        volume: findOwnershipReceipt(
          { storage, scopedReceipts }, storage.volume.volumeId),
        sandbox: findOwnershipReceipt(
          { storage, scopedReceipts }, resourceId, resourceId),
        networkPolicy: findOwnershipReceipt(
          { storage, scopedReceipts }, runtime.networkPolicyResourceId, runtime.networkPolicyUid),
        lateSandbox: findOwnershipReceipt(
          { storage, scopedReceipts }, runtime.lateSandboxResourceId, runtime.lateSandboxResourceId),
      },
    };
    const sandboxObservation = await observeSandbox({ storage, runtime }, placement, {
      expectedPodUid: runtime.sandboxUid,
      expectedOperationId: creation.resources.sandbox.operationId,
    });
    verifyCluster(sandboxObservation, input.pins.context.clusterIdentity);
    requireUid(sandboxObservation?.pod?.uid, runtime.sandboxUid,
      'sandbox_uid_mismatch', 'Sandbox Pod');
    verifySandboxOwnership(
      { storage, runtime },
      sandboxObservation,
      placement,
      creation.resources.sandbox.operationId);
    verifySandboxMount({ storage, runtime }, sandboxObservation);
    const state = {
      storage, fence, runtime, creation, scopedReceipts, lifecycle, volume, placement,
      volumeOwner: currentVolumeOwner, sandboxObservation,
    };
    live.set(storage.ownerId, state);
    ownedSandboxResources.set(storage.ownerId, [{
      resourceId: creation.resources.sandbox.resourceId,
      uid: sandboxObservation.pod.uid,
      operationId: creation.resources.sandbox.operationId,
      resourceGeneration: placementResourceGeneration(placement),
      current: true,
    }]);
    return state;
  };

  const runProbe = async (name, action) => {
    try {
      const details = await action();
      probes.push(resultFor(name, 'passed', {
        evidence: details?.evidence ?? details ?? {},
        cleanup: { status: 'not-started', actions: [] },
      }, secrets));
    } catch (error) {
      const issue = evidenceError(error, secrets);
      probes.push(resultFor(name, issue.status, {
        code: issue.code,
        reason: issue.reason,
        evidence: issue.evidence,
      }, secrets));
    }
  };

  const ownerReads = await Promise.allSettled(owners.map(readOwner));
  const failedOwnerRead = ownerReads.find(result => result.status === 'rejected');
  if (failedOwnerRead) {
    const issue = evidenceError(failedOwnerRead.reason, secrets);
    for (const name of PROBE_NAMES) probes.push(resultFor(name, 'blocked', issue, secrets));
    return {
      kind: 'p1-native-acceptance',
      schemaVersion: 1,
      overallStatus: 'blocked',
      startedAt,
      completedAt: new Date().toISOString(),
      probes,
    };
  }
  try {
    const activationOwner = owners.find(item =>
      item.storage.ownerId === input.pins.runtimeReceipt.activation.ownerId);
    const activationState = activationOwner && live.get(activationOwner.storage.ownerId);
    if (!activationOwner || !activationState) {
      block('activation_owner_missing', 'The current Host readiness receipt must bind to one of the exact owner fixtures.');
    }
    await validateCurrentRuntimeReadiness(activationOwner, activationState);
  } catch (error) {
    const issue = evidenceError(error, secrets);
    for (const name of PROBE_NAMES) probes.push(resultFor(name, 'blocked', issue, secrets));
    return {
      kind: 'p1-native-acceptance',
      schemaVersion: 1,
      overallStatus: 'blocked',
      startedAt,
      completedAt: new Date().toISOString(),
      probes,
    };
  }
  try {
    const cluster = await native('observe-cluster', { namespace: input.pins.namespace });
    verifyCluster(cluster, input.pins.context.clusterIdentity);
    let hubble;
    try {
      hubble = await native('verify-hubble', {});
    } catch (error) {
      const cause = evidenceError(error, secrets);
      block('hubble_prerequisite_unavailable',
        'An available Hubble CLI and healthy, owner-verified Relay for the pinned cluster are required before probe mutations.',
        { causeCode: cause.code, relayEndpoint: HUBBLE_RELAY_ENDPOINT });
    }
    if (hubble?.healthy !== true || hubble?.relayEndpoint !== HUBBLE_RELAY_ENDPOINT) {
      block('hubble_prerequisite_unavailable',
        'An available Hubble CLI and healthy, owner-verified Relay for the pinned cluster are required before probe mutations.',
        { relayEndpoint: HUBBLE_RELAY_ENDPOINT });
    }
  } catch (error) {
    const issue = evidenceError(error, secrets);
    for (const name of PROBE_NAMES) probes.push(resultFor(name, 'blocked', issue, secrets));
    return {
      kind: 'p1-native-acceptance',
      schemaVersion: 1,
      overallStatus: 'blocked',
      startedAt,
      completedAt: new Date().toISOString(),
      probes,
    };
  }

  const markerPaths = [];
  const ensureNativeStorage = async state => {
    const { storage, sandboxObservation } = state;
    const volume = storage.volume;
    const allowlist = volume.mountAllowlist.find(item =>
      item?.volumeName === 'agentweaver-workspace' &&
      item?.containerName === 'agenthost' &&
      item?.pvcName === volume.pvc.name &&
      item?.mountPath === volume.mountPath &&
      item?.readOnly === false);
    const mountSegments = typeof allowlist?.mountPath === 'string'
      ? allowlist.mountPath.split('/').filter(Boolean)
      : [];
    if (!allowlist || !nonEmpty(allowlist.mountPath) ||
        !allowlist.mountPath.startsWith('/') ||
        allowlist.mountPath.includes('\\') ||
        mountSegments.length === 0 ||
        mountSegments.some(segment => segment === '.' || segment === '..' || /[\u0000-\u001f]/.test(segment)) ||
        !nonEmpty(volume.pvc.name) || !nonEmpty(volume.pv.name)) {
      block('mount_allowlist_mismatch', 'The exact AgentHost workspace PVC mount must be allowlisted read/write.', {
        ownerId: storage.ownerId,
      });
    }
    const provider = await native('observe-storage', {
      namespace: input.pins.namespace,
      owner: ownerSelector(storage),
      volumeId: storage.volume.volumeId,
      resourceGeneration: storage.volume.resourceGeneration,
      volumeOwner: state.volumeOwner,
      providerBinding: volume.providerBinding,
      pvc: volume.pvc,
      pv: volume.pv,
      claimUid: volume.claimUid,
    });
    verifyCluster(provider, input.pins.context.clusterIdentity);
    requireUid(provider?.pvc?.uid, volume.pvc.uid, 'pvc_uid_mismatch', 'PVC');
    requireUid(provider?.pv?.uid, volume.pv.uid, 'pv_uid_mismatch', 'PV');
    if (provider?.pvc?.name !== volume.pvc.name ||
        provider?.pv?.name !== volume.pv.name ||
        provider?.claimUid !== volume.claimUid) {
      block('native_storage_readback_mismatch', 'Native Storage evidence does not match the pinned PVC/PV identities and claim UID.', {
        ownerId: storage.ownerId,
      });
    }
    const filename = `.agentweaver-p1-${input.pins.fixtureScope.invocationId}-${createHash('sha256')
      .update(storage.ownerId).digest('hex').slice(0, 10)}`;
    const path = `${allowlist.mountPath.replace(/\/+$/, '')}/${filename}`;
    const marker = `agentweaver-p1:${input.pins.fixtureScope.invocationId}:${randomUUID()}`;
    const args = {
      namespace: input.pins.namespace,
      podName: sandboxObservation.pod.name,
      podUid: sandboxObservation.pod.uid,
      expectedPodUid: sandboxObservation.pod.uid,
      ownerId: storage.ownerId,
      owner: ownerSelector(storage),
      workspaceClaimUid: storage.volume.claimUid,
      containerName: 'agenthost',
      mountPath: allowlist.mountPath,
      path,
    };
    const stat = await native('mount-file', { ...args, operation: 'stat' });
    if (stat?.podUid !== sandboxObservation.pod.uid ||
        stat?.ownerId !== storage.ownerId) {
      block('marker_path_owner_readback_mismatch', 'The marker-path readback must identify the exact current owner Pod UID and owner.', {
        ownerId: storage.ownerId,
        expectedPodUid: sandboxObservation.pod.uid,
      });
    }
    if (stat?.exists !== false) {
      block('marker_path_not_exclusive', 'The invocation-owned marker path must be absent before writing.', {
        ownerId: storage.ownerId,
        path,
      });
    }
    markerPaths.push({
      ownerId: storage.ownerId,
      ownerPodUid: sandboxObservation.pod.uid,
      args,
      marker,
    });
    return { path, marker, nativeStorage: provider };
  };

  let storageMarkers = [];
  await runProbe('sandbox-owner-lifecycle', async () => {
    const activationOwnerId = input.pins.runtimeReceipt.activation.ownerId;
    const disposable = [...live.values()].find(state => state.storage.ownerId !== activationOwnerId);
    if (!disposable) {
      block('sandbox_fixture_not_isolated', 'Lifecycle transitions need the separate invocation-owned Sandbox fixture, not the AgentHost activation placement.');
    }
    const { storage, creation } = disposable;
    const initial = await abandonCurrentSandbox(disposable, 'lifecycle-release');
    const late = creation.resources.lateSandbox;
    const lateBefore = await native('observe-owned-resource', {
      namespace: input.pins.namespace,
      owner: ownerSelector(storage),
      resourceId: late.resourceId,
      expectedUid: late.uid,
      expectedPodUids: [],
      allowEmptyPodUidInventory: true,
    });
    if (lateBefore?.exists !== true || lateBefore?.uid !== late.uid ||
        lateBefore?.ownerId !== storage.ownerId ||
        !Array.isArray(lateBefore?.dependentPods)) {
      block('late_resource_receipt_mismatch', 'The late provider resource must be observed with its exact invocation-owned UID before reconcile.', {
        ownerId: storage.ownerId,
        expectedUid: late.uid,
        observedUid: lateBefore?.uid ?? null,
      });
    }
    const latePodUids = lateBefore.dependentPods.map(pod => {
      if (!isRecord(pod) || !nonEmpty(pod.name) || !nonEmpty(pod.uid)) {
        block('late_resource_pod_receipt_mismatch', 'Every linked late-resource Pod must have its actual observed name and UID before reconcile.', {
          ownerId: storage.ownerId,
          resourceId: late.resourceId,
        });
      }
      return pod.uid;
    });
    if (new Set(latePodUids).size !== latePodUids.length) {
      block('late_resource_pod_receipt_ambiguous', 'Late-resource Pod readback contains duplicate UIDs.', {
        ownerId: storage.ownerId,
        resourceId: late.resourceId,
      });
    }
    await authorizeMutation('sandbox-reconcile', storage);
    const lateReconcile = await api(storage, 'POST',
      `/sandbox/reconcile?networkPolicyGeneration=${input.pins.runtimeReceipt.network.generation}`);
    if (lateReconcile.status !== 200) {
      block('late_cleanup_route_failed', 'Environment did not reconcile the actual invocation-owned late resource.', {
        ownerId: storage.ownerId,
        status: lateReconcile.status,
      });
    }
    const lateAfter = await native('observe-owned-resource', {
      namespace: input.pins.namespace,
      owner: ownerSelector(storage),
      resourceId: late.resourceId,
      expectedUid: late.uid,
      expectedPodUids: latePodUids,
      allowEmptyPodUidInventory: latePodUids.length === 0,
    });
    if (typeof lateAfter?.exists !== 'boolean' || !Array.isArray(lateAfter.dependentPods)) {
      block('late_resource_readback_missing', 'Native readback did not report the late resource existence after reconciliation.', {
        ownerId: storage.ownerId,
        resourceId: late.resourceId,
      });
    }
    if (lateAfter.exists || lateAfter.dependentPods.length > 0) {
      fail('late_resource_not_cleaned', 'The accepted reconcile route left the exact owned late resource or a dependent Pod present.', {
        ownerId: storage.ownerId,
        resourceId: late.resourceId,
        uid: late.uid,
        dependentPodUids: lateAfter.dependentPods.map(pod => pod.uid),
      });
    }
    const reclaimed = await provisionSandbox(disposable, 'lifecycle-reclaim');
    const restarted = await abandonCurrentSandbox(disposable, 'lifecycle-restart');
    const restartedAgain = await provisionSandbox(disposable, 'lifecycle-restart');
    if (restartedAgain.uid === reclaimed.uid) {
      block('sandbox_restart_uid_not_changed', 'Restart evidence must show a new native Pod UID for the invocation-owned fixture.');
    }
    return {
      evidence: {
        ownerId: storage.ownerId,
        initialPodUid: initial.uid,
        releasedOperationId: initial.operationId,
        lateResourceUid: late.uid,
        lateResourceCleaned: true,
        reclaimedPodUid: reclaimed.uid,
        restartAbandonedPodUid: restarted.uid,
        restartedPodUid: restartedAgain.uid,
        reconcileStatus: lateReconcile.status,
      },
    };
  });

  await runProbe('storage-owner-isolation', async () => {
    storageMarkers = [];
    const ownerStates = [...live.values()];
    const markerEvidence = [];
    for (const state of ownerStates) {
      const marker = await ensureNativeStorage(state);
      await authorizeMutation('workspace-marker-write', state.storage);
      const written = await native('mount-file', {
        ...markerPaths.at(-1).args,
        operation: 'write',
        content: marker.marker,
      });
      if (typeof written?.written !== 'boolean' || !nonEmpty(written?.sha256) ||
          written?.podUid !== state.sandboxObservation.pod.uid ||
          written?.ownerId !== state.storage.ownerId) {
        block('mounted_write_receipt_missing', 'Native mount write did not return explicit completion and digest evidence.', {
          ownerId: state.storage.ownerId,
        });
      }
      if (written.written !== true || written.sha256 !== createHash('sha256').update(marker.marker).digest('hex')) {
        fail('mounted_write_unverified', 'The native mounted-byte write did not return the exact marker digest.', {
          ownerId: state.storage.ownerId,
        });
      }
      storageMarkers.push(markerPaths.at(-1));
      markerEvidence.push({ ownerId: state.storage.ownerId, path: marker.path, sha256: written.sha256 });
    }
    for (let index = 0; index < ownerStates.length; index += 1) {
      const own = ownerStates[index];
      const other = ownerStates[1 - index];
      const ownMarker = storageMarkers[index];
      const otherMarker = storageMarkers[1 - index];
      const ownRead = await native('mount-file', {
        ...ownMarker.args,
        operation: 'read',
      });
      if (typeof ownRead?.exists !== 'boolean') {
        block('mounted_readback_missing', 'Native mount readback did not report whether the marker exists.', {
          ownerId: own.storage.ownerId,
        });
      }
      if (ownRead?.podUid !== own.sandboxObservation.pod.uid ||
          ownRead?.ownerId !== own.storage.ownerId) {
        block('mounted_readback_owner_mismatch', 'The marker readback must identify the exact current owner Pod UID and owner.', {
          ownerId: own.storage.ownerId,
          expectedPodUid: own.sandboxObservation.pod.uid,
        });
      }
      if (ownRead.exists === false) {
        fail('mounted_readback_missing', 'The owner-scoped marker disappeared from its mounted volume.', {
          ownerId: own.storage.ownerId,
        });
      }
      if (ownRead?.content !== ownMarker.marker) {
        fail('mounted_readback_mismatch', 'The owner did not read its exact mounted marker bytes.', {
          ownerId: own.storage.ownerId,
        });
      }
      const crossRead = await native('mount-file', {
        namespace: input.pins.namespace,
        podName: other.sandboxObservation.pod.name,
        podUid: other.sandboxObservation.pod.uid,
        containerName: 'agenthost',
        ownerId: other.storage.ownerId,
        owner: ownerSelector(other.storage),
        workspaceClaimUid: other.storage.volume.claimUid,
        mountPath: otherMarker.args.mountPath,
        path: ownMarker.args.path,
        operation: 'read',
      });
      if (typeof crossRead?.exists !== 'boolean') {
        block('cross_owner_readback_missing', 'The second owner did not return explicit mount-path existence evidence.', {
          sourceOwnerId: own.storage.ownerId,
          targetOwnerId: other.storage.ownerId,
        });
      }
      if (crossRead?.podUid !== other.sandboxObservation.pod.uid ||
          crossRead?.ownerId !== other.storage.ownerId) {
        block('cross_owner_readback_owner_mismatch', 'Cross-owner visibility must be observed through the exact target owner Pod UID.', {
          sourceOwnerId: own.storage.ownerId,
          targetOwnerId: other.storage.ownerId,
          expectedPodUid: other.sandboxObservation.pod.uid,
        });
      }
      if (crossRead.exists) {
        if (crossRead.content === ownMarker.marker) {
          fail('cross_owner_bytes_visible', 'A second owner read the first owner’s exact marker bytes.', {
            sourceOwnerId: own.storage.ownerId,
            targetOwnerId: other.storage.ownerId,
          });
        }
        if (crossRead.content !== otherMarker.marker) {
          block('cross_owner_path_collision', 'The cross-owner path contains neither an absent path nor the other owner’s exact marker.', {
            sourceOwnerId: own.storage.ownerId,
            targetOwnerId: other.storage.ownerId,
          });
        }
      }
    }

    const primary = ownerStates[0];
    const secondary = ownerStates[1];
    const deniedLifecycle = await apiAs(secondary.storage, primary.storage, 'GET', '/lifecycle');
    await authorizeMutation('cross-owner-binding-denial', secondary.storage);
    const deniedVolume = await apiAs(secondary.storage, primary.storage, 'POST',
      `/workspace-volumes/${encodeURIComponent(primary.storage.volume.volumeId)}/bind`, {
        expectedTransitionRevision: primary.volume.transitionRevision + 1,
        expectedResourceGeneration: primary.volume.resourceGeneration,
        expectedDataGeneration: primary.volume.dataGeneration,
        idempotencyKey: `p1-denied-${input.pins.fixtureScope.invocationId}`,
      });
    if (deniedLifecycle.status !== 403 || deniedVolume.status !== 403) {
      if (deniedLifecycle.status === 200 || deniedVolume.status === 200) {
        fail('cross_owner_authorization_allowed', 'The Environment API accepted a cross-owner lifecycle or binding request.', {
          lifecycleStatus: deniedLifecycle.status,
          bindingStatus: deniedVolume.status,
        });
      }
      block('cross_owner_forbidden_not_proven', 'Both cross-owner lifecycle and binding checks must return HTTP 403.', {
        lifecycleStatus: deniedLifecycle.status,
        bindingStatus: deniedVolume.status,
      });
    }
    const serviceScopes = {
      sourceOwner: primary.storage.ownerId,
      deniedOwner: secondary.storage.ownerId,
      sameActor: primary.storage.actorId === secondary.storage.actorId,
      tenantSelectorsDiffer: primary.storage.tenantId !== secondary.storage.tenantId,
      scopeDiffers: selectorDiffers(primary.storage, secondary.storage),
      observedForbiddenStatuses: [deniedLifecycle.status, deniedVolume.status],
    };
    if (!serviceScopes.scopeDiffers) {
      block('denied_scope_not_distinct', 'A denied-authorization fixture must select a distinct tenant or owner scope.');
    }

    const activationOwnerId = input.pins.runtimeReceipt.activation.ownerId;
    const transitionStates = ownerStates.filter(state => state.storage.ownerId !== activationOwnerId);
    if (transitionStates.length !== 1) {
      block('storage_fixture_not_isolated', 'Release/rebind requires exactly one disposable storage owner separate from the AgentHost activation owner.');
    }
    const retained = [];
    for (const state of transitionStates) {
      const releasedSandbox = await abandonCurrentSandbox(state, 'storage-volume-transition');
      recordCleanup('storage-owner-isolation', 'detach-owned-sandbox-for-volume-transition', 'complete', {
        ownerId: state.storage.ownerId,
        operationId: releasedSandbox.operationId,
      });
      await authorizeMutation('sandbox-reconcile', state.storage);
      const detachedReconcile = await api(state.storage, 'POST',
        `/sandbox/reconcile?networkPolicyGeneration=${input.pins.runtimeReceipt.network.generation}`);
      if (detachedReconcile.status !== 200) {
        block('storage_detach_reconcile_unverified', 'Environment did not reconcile the exact owned Sandbox detach.', {
          ownerId: state.storage.ownerId,
          status: detachedReconcile.status,
        });
      }
      let volume = state.volume;
      for (const operation of ['release', 'bind']) {
        if (!positive(volume.transitionRevision) ||
            volume.resourceGeneration !== state.storage.volume.resourceGeneration ||
            volume.dataGeneration !== state.storage.volume.dataGeneration) {
          block('volume_transition_fence_missing', 'The transition must use a live volume revision and the pinned owner generations.', {
            ownerId: state.storage.ownerId,
            operation,
          });
        }
        await authorizeMutation(`workspace-volume-${operation}`, state.storage);
        const response = await api(state.storage, 'POST',
          `/workspace-volumes/${encodeURIComponent(state.storage.volume.volumeId)}/${operation}`, {
            expectedTransitionRevision: volume.transitionRevision,
            expectedResourceGeneration: volume.resourceGeneration,
            expectedDataGeneration: volume.dataGeneration,
            idempotencyKey: `p1-${operation}-${randomUUID()}`,
          });
        if (response.status < 200 || response.status >= 300) {
          block('volume_transition_unverified', `Environment ${operation} did not complete for the invocation-owned volume.`, {
            ownerId: state.storage.ownerId,
            operation,
            status: response.status,
          });
        }
        const reread = await api(state.storage, 'GET',
          `/workspace-volumes/${encodeURIComponent(state.storage.volume.volumeId)}`);
        if (reread.status !== 200 || !isRecord(reread.body)) {
          block('volume_transition_readback_missing', 'The transitioned volume could not be read back from Environment.', {
            ownerId: state.storage.ownerId,
            operation,
          });
        }
        volume = reread.body;
        const currentVolumeOwner = projectVolumeOwner(volume);
        if (volume.resourceGeneration !== state.storage.volume.resourceGeneration ||
            volume.dataGeneration !== state.storage.volume.dataGeneration ||
            !sameJson(volume.providerBinding, state.storage.volume.providerBinding) ||
            !sameJson(currentVolumeOwner, state.storage.volume.owner)) {
          block('volume_generation_changed', 'Release/rebind changed the pinned owner resource/data generation, owner, or provider binding.', {
            ownerId: state.storage.ownerId,
            operation,
          });
        }
        const provider = await native('observe-storage', {
          namespace: input.pins.namespace,
          owner: ownerSelector(state.storage),
          volumeId: state.storage.volume.volumeId,
          resourceGeneration: state.storage.volume.resourceGeneration,
          volumeOwner: currentVolumeOwner,
          providerBinding: state.storage.volume.providerBinding,
          pvc: state.storage.volume.pvc,
          pv: state.storage.volume.pv,
          claimUid: state.storage.volume.claimUid,
        });
        verifyCluster(provider, input.pins.context.clusterIdentity);
        requireUid(provider?.pvc?.uid, state.storage.volume.pvc.uid, 'retained_pvc_uid_mismatch', 'Retained PVC');
        requireUid(provider?.pv?.uid, state.storage.volume.pv.uid, 'retained_pv_uid_mismatch', 'Retained PV');
        if (provider?.pvc?.name !== state.storage.volume.pvc.name ||
            provider?.pv?.name !== state.storage.volume.pv.name ||
            provider?.claimUid !== state.storage.volume.claimUid) {
          block('retained_storage_readback_mismatch', 'Release/rebind changed the native PVC/PV identities or claim UID.', {
            ownerId: state.storage.ownerId,
            operation,
          });
        }
        retained.push({ ownerId: state.storage.ownerId, operation, pvcUid: provider.pvc.uid, pvUid: provider.pv.uid });
      }
      state.volume = volume;
      const rebound = await provisionSandbox(state, 'storage-rebind');
      const observed = state.sandboxObservation;
      verifySandboxMount(state, observed);
      const marker = storageMarkers.find(item => item.ownerId === state.storage.ownerId);
      if (!marker) {
        block('retention_marker_missing', 'The release/rebind owner has no invocation-created mounted-byte marker.');
      }
      marker.args.podName = observed.pod.name;
      marker.args.podUid = observed.pod.uid;
      const retainedBytes = await native('mount-file', {
        ...marker.args,
        operation: 'read',
      });
      if (typeof retainedBytes?.exists !== 'boolean') {
        block('retained_bytes_readback_missing', 'Native readback did not report marker existence after release/rebind.', {
          ownerId: state.storage.ownerId,
        });
      }
      if (retainedBytes?.podUid !== observed.pod.uid ||
          retainedBytes?.ownerId !== state.storage.ownerId) {
        block('retained_bytes_owner_mismatch', 'The retained marker must be read through the exact current owner Pod UID.', {
          ownerId: state.storage.ownerId,
          expectedPodUid: observed.pod.uid,
        });
      }
      if (!retainedBytes.exists) {
        fail('release_rebind_bytes_lost', 'The owner-scoped mounted bytes did not survive Environment release and rebind.', {
          ownerId: state.storage.ownerId,
        });
      }
      if (retainedBytes?.content !== marker.marker) {
        fail('release_rebind_bytes_lost', 'The owner-scoped mounted bytes did not survive Environment release and rebind.', {
          ownerId: state.storage.ownerId,
        });
      }
      marker.ownerPodUid = observed.pod.uid;
      retained.push({
        ownerId: state.storage.ownerId,
        operation: 'release-rebind',
        pvcUid: state.storage.volume.pvc.uid,
        pvUid: state.storage.volume.pv.uid,
        podUid: rebound.uid,
        bytesRetained: true,
      });
    }
    return { evidence: { mountedMarkers: markerEvidence, deniedOwnerRequests: serviceScopes, retained, transitions: ['release', 'bind'] } };
  });

  await runProbe('network-policy-datapath', async () => {
    const network = input.pins.runtimeReceipt.network;
    const policy = network.policy;
    if (!isRecord(policy) || !nonEmpty(policy.resourceId) || !nonEmpty(policy.uid) ||
        policy.namespace !== input.pins.namespace ||
        policy.generation !== network.generation ||
        policy.annotations?.revoked !== 'false' ||
        !nonEmpty(policy.annotations?.hash)) {
      block('network_policy_receipt_incomplete', 'The current owned policy UID, generation, hash, and non-revoked source annotation are required.');
    }
    const owner = owners.find(item => item.storage.ownerId === network.ownerId);
    const ownerState = owner && live.get(owner.storage.ownerId);
    if (!owner || !ownerState ||
        ownerState.creation.resources.networkPolicy.resourceId !== policy.resourceId ||
        ownerState.creation.resources.networkPolicy.uid !== policy.uid) {
      block('network_policy_not_invocation_owned', 'Only the exact throwaway policy created by this invocation may be probed or revoked.');
    }
    const state = ownerState;
    if (Object.entries(policy.selectorLabels).some(([key, value]) =>
      state.sandboxObservation?.pod?.labels?.[key] !== value)) {
      block('network_selector_pod_mismatch', 'The current owner Pod does not match the exact selector labels pinned by the owned policy.', {
        ownerId: owner.storage.ownerId,
      });
    }
    const actualPolicy = await native('observe-network-policy', {
      namespace: policy.namespace,
      resourceId: policy.resourceId,
      expectedUid: policy.uid,
      expectedGeneration: network.generation,
    });
    verifyCluster(actualPolicy, input.pins.context.clusterIdentity);
    requireUid(actualPolicy?.uid, policy.uid, 'network_policy_uid_mismatch', 'Cilium policy');
    if (actualPolicy?.appliedIntentGeneration !== network.generation ||
        actualPolicy?.intentHash !== policy.annotations.hash ||
        actualPolicy?.revoked !== false ||
        !sameJson(actualPolicy?.selectorLabels, policy.selectorLabels)) {
      block('network_policy_readback_mismatch', 'The live Cilium object does not match the owned policy generation, source hash, or selector.');
    }
    const environmentNetwork = await api(owner.storage, 'GET',
      `/sandbox?networkPolicyGeneration=${network.generation}`);
    const currentPlacement = state.placement;
    const currentResource = placementResourceId(currentPlacement);
    const currentGeneration = placementResourceGeneration(currentPlacement);
    const currentOwnedResource = (ownedSandboxResources.get(owner.storage.ownerId) ?? []).find(resource =>
      resource.resourceId === currentResource &&
      resource.resourceGeneration === currentGeneration);
    const environmentResult = environmentNetwork.body;
    const environmentObservation = environmentResult?.observation;
    if (![200, 202].includes(environmentNetwork.status) ||
        !currentOwnedResource ||
        environmentResult?.operationId !== currentOwnedResource.operationId ||
        environmentResult?.resourceGeneration !== currentGeneration ||
        environmentResult?.providerFencingGeneration !== currentPlacement?.providerFencingGeneration ||
        environmentResult?.currentFencingGeneration !== currentPlacement?.currentFencingGeneration ||
        environmentResult?.resource?.providerId !== 'agent-sandbox' ||
        environmentResult?.resource?.resourceId !== currentResource ||
        environmentResult?.resource?.generation !== currentGeneration ||
        environmentObservation?.resource?.providerId !== 'agent-sandbox' ||
        environmentObservation?.resource?.resourceId !== currentResource ||
        environmentObservation?.resource?.generation !== currentGeneration ||
        !['pending', 'ready'].includes(environmentObservation?.state) ||
        environmentObservation?.fencingGeneration !== currentPlacement?.providerFencingGeneration ||
        environmentObservation?.verifiedNetworkGeneration !== network.generation) {
      block('environment_network_generation_unverified', 'Environment did not confirm the same current network generation as the native policy and datapath probes.', {
        ownerId: owner.storage.ownerId,
        status: environmentNetwork.status,
      });
    }
    const observed = [];
    for (const [kind, identities, expectedConnected] of [
      ['allowed', network.allowedServiceIdentities, true],
      ['denied', network.deniedServiceIdentities, false],
    ]) {
      for (const identity of identities) {
        if (!nonEmpty(identity) || !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\/[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(identity)) {
          block('service_identity_invalid', 'Service targets must use the exact namespace/name identity format.', { identity });
        }
        const result = await native('probe-service', {
          namespace: input.pins.namespace,
          sourcePodUid: state.sandboxObservation.pod.uid,
          serviceIdentity: identity,
          networkPolicyUid: policy.uid,
          networkPolicyGeneration: network.generation,
          networkPolicyResourceId: policy.resourceId,
          timeoutSeconds: 5,
        });
        if (result?.sourcePodUid !== state.sandboxObservation.pod.uid ||
            result?.serviceIdentity !== identity ||
            result?.policyUid !== policy.uid ||
            result?.policyGeneration !== network.generation ||
            !nonEmpty(result?.targetServiceUid) ||
            !Number.isSafeInteger(result?.readyEndpointCount) ||
            result.readyEndpointCount < 1 ||
            !Array.isArray(result?.probedPorts) ||
            result.probedPorts.length === 0 ||
            result.probedPorts.some(port => !positive(port)) ||
            !Array.isArray(result?.portVerdicts) ||
            result.portVerdicts.length !== result.probedPorts.length ||
            result.portVerdicts.some((item, index) =>
              !positive(item?.servicePort) ||
              item.servicePort !== result.probedPorts[index] ||
              !positive(item?.targetPort) ||
              item?.verdict !== (expectedConnected ? 'FORWARDED' : 'DROPPED') ||
              item?.connected !== expectedConnected) ||
            !['allowed', 'denied'].includes(result?.datapathVerdict) ||
            typeof result?.connected !== 'boolean') {
          block('network_probe_evidence_mismatch', 'The datapath result is not bound to the exact current pod, service identity, and policy generation.', {
            kind,
            identity,
          });
        }
        if (result.connected !== expectedConnected) {
          fail(kind === 'allowed' ? 'allowed_service_unreachable' : 'denied_service_reachable',
            kind === 'allowed'
              ? 'The Cilium datapath denied an explicitly allowed in-cluster service.'
              : 'The Cilium datapath allowed an explicitly denied in-cluster service.',
            { kind, identity, connected: result.connected, datapathVerdict: result.datapathVerdict });
        }
        if (result.datapathVerdict !== kind) {
          fail('network_datapath_verdict_mismatch', 'Observed Cilium datapath verdict contradicts the pinned allowed/denied identity.', {
            kind,
            identity,
            datapathVerdict: result.datapathVerdict,
          });
        }
        observed.push({
          kind,
          identity,
          connected: result.connected,
          datapathVerdict: result.datapathVerdict,
          sourcePodUid: result.sourcePodUid,
          targetServiceUid: result.targetServiceUid,
          readyEndpointCount: result.readyEndpointCount,
          probedPorts: result.probedPorts,
          portVerdicts: result.portVerdicts,
        });
      }
    }
    return { evidence: { clusterUid: input.pins.context.clusterIdentity.uid, policyUid: policy.uid, generation: network.generation, traffic: observed } };
  });

  const cleanupSandbox = async () => {
    for (const owner of owners) {
      const state = live.get(owner.storage.ownerId);
      if (!state) {
        recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'uncertain', { ownerId: owner.storage.ownerId });
        continue;
      }
      try {
        const current = await api(owner.storage, 'GET', '/sandbox/v1/placement');
        const resources = ownedSandboxResources.get(owner.storage.ownerId) ?? [];
        if (current.status === 404 ||
            (current.status === 200 && current.body?.isCurrent === false)) {
          const absence = await verifyOwnedSandboxResourcesAbsent(owner, resources);
          recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release',
            absence.confirmed ? 'complete' : 'uncertain', {
            ownerId: owner.storage.ownerId,
            alreadyReleased: absence.confirmed,
            resources: absence.readbacks.map(readback => ({
              exists: readback.exists,
              dependentPodCount: Array.isArray(readback.dependentPods)
                ? readback.dependentPods.length
                : null,
            })),
          });
          continue;
        }
        if (current.status !== 200 || current.body?.isCurrent !== true) {
          recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'uncertain', {
            ownerId: owner.storage.ownerId,
            status: current.status,
          });
          continue;
        }
        const currentId = placementResourceId(current.body);
        const ownedResource = resources.find(resource =>
          resource.resourceId === currentId &&
          resource.resourceGeneration === placementResourceGeneration(current.body));
        const currentNative = await observeSandbox(owner, current.body, {
          expectedOperationId: ownedResource?.operationId,
        });
        const owned = resources.find(resource =>
          resource.resourceId === currentId &&
          resource.resourceGeneration === placementResourceGeneration(current.body) &&
          resource.uid === currentNative?.pod?.uid);
        if (!owned ||
            current.body?.projectId !== owner.storage.projectId ||
            current.body?.runId !== owner.storage.runId ||
            current.body?.environmentId !== owner.storage.environmentId ||
            currentNative?.clusterIdentity?.uid !== input.pins.context.clusterIdentity.uid) {
          recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'uncertain', {
            ownerId: owner.storage.ownerId,
            observedResourceId: currentId ?? null,
            observedPodUid: currentNative?.pod?.uid ?? null,
          });
          continue;
        }
        verifySandboxOwnership(owner, currentNative, current.body, owned.operationId);
        await authorizeMutation('sandbox-abandon', owner.storage);
        const response = await api(owner.storage, 'POST', '/sandbox/abandon', {
          resourceGeneration: placementResourceGeneration(current.body),
          providerFencingGeneration: current.body.providerFencingGeneration,
          idempotencyKey: `p1-final-${input.pins.fixtureScope.invocationId}-${randomUUID()}`,
        });
        if (response.status !== 200 || !nonEmpty(response.body?.operationId) ||
            response.body?.resource?.resourceId !== currentId ||
            response.body?.resource?.generation !== placementResourceGeneration(current.body)) {
          recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'uncertain', {
            ownerId: owner.storage.ownerId,
            status: response.status,
          });
          continue;
        }
        await authorizeMutation('sandbox-reconcile', owner.storage);
        const reconciliation = await api(owner.storage, 'POST',
          `/sandbox/reconcile?networkPolicyGeneration=${input.pins.runtimeReceipt.network.generation}`);
        const readback = await api(owner.storage, 'GET', '/sandbox/v1/placement');
        if (reconciliation.status !== 200 ||
            (readback.status !== 404 && readback.body?.isCurrent !== false)) {
          recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'uncertain', {
            ownerId: owner.storage.ownerId,
            reconcileStatus: reconciliation.status,
            readbackStatus: readback.status,
          });
          continue;
        }
        const absence = await verifyOwnedSandboxResourcesAbsent(owner, resources);
        if (!absence.confirmed) {
          recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'uncertain', {
            ownerId: owner.storage.ownerId,
            resourceId: currentId,
            resources: absence.readbacks.map(readback => ({
              exists: readback.exists,
              dependentPodCount: Array.isArray(readback.dependentPods)
                ? readback.dependentPods.length
                : null,
            })),
          });
          continue;
        }
        recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'complete', {
          ownerId: owner.storage.ownerId,
          resourceId: currentId,
          uid: currentNative.pod.uid,
          operationId: response.body.operationId,
        });
      } catch (error) {
        recordCleanup('sandbox-owner-lifecycle', 'final-sandbox-release', 'uncertain', {
          ownerId: owner.storage.ownerId,
          reason: 'Sandbox cleanup did not complete.',
        });
      }
    }
  };

  const cleanupMarkers = async () => {
    for (const marker of markerPaths) {
      try {
        const state = live.get(marker.ownerId);
        if (!state) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
            ownerId: marker.ownerId,
            path: marker.args.path,
            reason: 'The owner state required for exact marker cleanup is unavailable.',
          });
          continue;
        }
        const owner = state.storage;
        const placementResponse = await api(owner, 'GET', '/sandbox/v1/placement');
        const placement = placementResponse.body;
        if (placementResponse.status !== 200 ||
            placement?.isCurrent !== true ||
            placement?.tenantId !== owner.tenantId ||
            placement?.projectId !== owner.projectId ||
            placement?.runId !== owner.runId ||
            placement?.environmentId !== owner.environmentId ||
            placement?.lifecycleGeneration !== state.fence.lifecycleFence.lifecycleGeneration ||
            !isObservedPlacementFence(placement)) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
            ownerId: owner.ownerId,
            path: marker.args.path,
            reason: 'A fresh current placement with the exact owner and fence is required before marker cleanup.',
            status: placementResponse.status,
          });
          continue;
        }
        const currentResourceId = placementResourceId(placement);
        const owned = (ownedSandboxResources.get(owner.ownerId) ?? []).find(item =>
          item.current === true &&
          item.resourceId === currentResourceId &&
          item.resourceGeneration === placementResourceGeneration(placement));
        if (!owned) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
            ownerId: owner.ownerId,
            path: marker.args.path,
            reason: 'The current placement is not an invocation-owned Sandbox resource.',
            resourceId: currentResourceId,
          });
          continue;
        }
        const currentObservation = await observeSandbox(state, placement, {
          expectedOperationId: owned.operationId,
        });
        verifyCluster(currentObservation, input.pins.context.clusterIdentity);
        if (currentObservation?.pod?.uid !== owned.uid) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
            ownerId: owner.ownerId,
            path: marker.args.path,
            expectedPodUid: owned.uid,
            observedPodUid: currentObservation?.pod?.uid ?? null,
            reason: 'The current Pod UID differs from the invocation-owned placement receipt.',
          });
          continue;
        }
        verifySandboxOwnership(state, currentObservation, placement, owned.operationId);
        verifySandboxMount(state, currentObservation);
        const markerArgs = {
          ...marker.args,
          podName: currentObservation.pod.name,
          podUid: currentObservation.pod.uid,
          expectedPodUid: currentObservation.pod.uid,
        };
        const stat = await native('mount-file', { ...markerArgs, operation: 'stat' });
        if (stat?.podUid !== currentObservation.pod.uid ||
            stat?.ownerId !== marker.ownerId) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
            ownerId: owner.ownerId,
            path: marker.args.path,
            expectedPodUid: currentObservation.pod.uid,
            observedPodUid: stat?.podUid ?? null,
          });
          continue;
        }
        if (stat?.exists === false) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'complete', {
            ownerId: marker.ownerId,
            path: marker.args.path,
            alreadyAbsent: true,
          });
          continue;
        }
        if (stat?.exists !== true || stat?.content !== marker.marker) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
            ownerId: owner.ownerId,
            path: marker.args.path,
          });
          continue;
        }
        await authorizeMutation('workspace-marker-cleanup', owner);
        const removed = await native('mount-file', {
          ...markerArgs,
          operation: 'unlink',
          expectedContent: marker.marker,
          expectedPodUid: currentObservation.pod.uid,
        });
        if (removed?.removed !== true ||
            removed?.podUid !== currentObservation.pod.uid ||
            removed?.ownerId !== marker.ownerId) {
          recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
            ownerId: state.storage.ownerId,
            path: marker.args.path,
            expectedPodUid: currentObservation.pod.uid,
            observedPodUid: removed?.podUid ?? null,
          });
          continue;
        }
        recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'complete', {
          ownerId: state.storage.ownerId,
          path: marker.args.path,
          podUid: currentObservation.pod.uid,
        });
      } catch (error) {
        recordCleanup('storage-owner-isolation', 'remove-owned-marker', 'uncertain', {
          ownerId: marker.ownerId,
          reason: 'Marker cleanup did not complete.',
        });
      }
    }
  };

  const revokeOwnedPolicy = async () => {
    const network = input.pins.runtimeReceipt.network;
    const owner = owners.find(item => item.storage.ownerId === network.ownerId);
    if (!owner || !isRecord(network.revoke) ||
        network.revoke.expectedPreviousPolicyGeneration !== network.generation ||
        !positive(network.revoke.policyGeneration) ||
        network.revoke.policyGeneration <= network.generation ||
        !nonEmpty(network.revoke.idempotencyKey) ||
        !sameJson(network.revoke.fence, owner.fence.environmentFence)) {
      recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
        reason: 'Exact invocation-owned revoke generation, idempotency key, and current Environment fence are missing.',
      });
      return;
    }
    try {
      const currentPolicy = await native('observe-network-policy', {
        namespace: input.pins.namespace,
        resourceId: network.policy.resourceId,
        expectedUid: network.policy.uid,
        expectedGeneration: network.generation,
      });
      verifyCluster(currentPolicy, input.pins.context.clusterIdentity);
      if (currentPolicy?.uid !== network.policy.uid ||
          currentPolicy?.appliedIntentGeneration !== network.generation ||
          currentPolicy?.revoked !== false ||
          currentPolicy?.intentHash !== network.policy.annotations.hash) {
        recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
          reason: 'Current native policy UID, generation, hash, or active state no longer matches the invocation receipt.',
        });
        return;
      }
      const lifecycle = await api(owner.storage, 'GET', '/lifecycle');
      const environmentFence = environmentFenceFromLifecycle(lifecycle.body);
      if (lifecycle.status !== 200 ||
          !sameJson(environmentFence, network.revoke.fence)) {
        recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
          reason: 'The current Environment lifecycle fence no longer matches the exact revoke receipt.',
          status: lifecycle.status,
        });
        return;
      }
      await authorizeMutation('network-policy-revoke', owner.storage);
      const response = await dependencies.fetch(new URL('/api/network-egress/revoke', baseUrl), {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          Authorization: `Bearer ${credentials.get(owner.storage.ownerId)}`,
          [TENANT_SELECTOR_HEADER]: owner.storage.tenantId,
        },
        body: JSON.stringify({
          fence: network.revoke.fence,
          policyGeneration: network.revoke.policyGeneration,
          expectedPreviousPolicyGeneration: network.revoke.expectedPreviousPolicyGeneration,
          idempotencyKey: network.revoke.idempotencyKey,
        }),
        redirect: 'error',
      });
      const body = await readBody(response);
      if (response.status !== 200 || body?.revoked !== true ||
          body?.appliedState?.revoked !== true ||
          body?.appliedState?.appliedIntentGeneration !== network.revoke.policyGeneration) {
        recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
          status: response.status,
          revoked: body?.revoked ?? null,
        });
        return;
      }
      const tombstone = await native('observe-network-policy', {
        namespace: input.pins.namespace,
        resourceId: network.policy.resourceId,
        expectedUid: network.policy.uid,
        expectedGeneration: network.revoke.policyGeneration,
      });
      verifyCluster(tombstone, input.pins.context.clusterIdentity);
      if (tombstone?.uid !== network.policy.uid ||
          tombstone?.appliedIntentGeneration !== network.revoke.policyGeneration ||
          tombstone?.revoked !== true) {
        recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
          policyUid: network.policy.uid,
          observedGeneration: tombstone?.appliedIntentGeneration ?? null,
        });
        return;
      }
      const currentLifecycle = await api(owner.storage, 'GET', '/lifecycle');
      const currentFence = environmentFenceFromLifecycle(currentLifecycle.body);
      if (currentLifecycle.status !== 200 || !isRecord(currentFence)) {
        recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
          reason: 'Environment did not return its current fence for stale-operation reconciliation.',
          status: currentLifecycle.status,
        });
        return;
      }
      await authorizeMutation('network-policy-reconcile', owner.storage);
      const reconciled = await dependencies.fetch(new URL('/api/network-egress/reconcile', baseUrl), {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          Authorization: `Bearer ${credentials.get(owner.storage.ownerId)}`,
          [TENANT_SELECTOR_HEADER]: owner.storage.tenantId,
        },
        body: JSON.stringify({
          fence: currentFence,
          operationId: network.reconcileOperationId,
        }),
        redirect: 'error',
      });
      const reconcileBody = await readBody(reconciled);
      if (reconciled.status !== 200 || reconcileBody?.revoked !== true ||
          reconcileBody?.appliedState?.revoked !== true ||
          reconcileBody?.appliedState?.appliedIntentGeneration !== network.revoke.policyGeneration) {
        recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
          reason: 'Environment did not confirm reconciliation of the actual invocation-owned policy operation.',
          status: reconciled.status,
          revoked: reconcileBody?.revoked ?? null,
        });
        return;
      }
      const stable = await native('observe-network-policy', {
        namespace: input.pins.namespace,
        resourceId: network.policy.resourceId,
        expectedUid: network.policy.uid,
        expectedGeneration: network.revoke.policyGeneration,
      });
      if (stable?.uid !== tombstone.uid ||
          stable?.appliedIntentGeneration !== tombstone.appliedIntentGeneration ||
          stable?.revoked !== true) {
        recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
          reason: 'The tombstone changed during the second native readback.',
        });
        return;
      }
      recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'complete', {
        ownerId: owner.storage.ownerId,
        policyUid: tombstone.uid,
        generation: tombstone.appliedIntentGeneration,
        revoked: true,
      });
    } catch (error) {
      recordCleanup('network-policy-datapath', 'leave-owned-deny-tombstone', 'uncertain', {
        reason: 'Policy revocation did not complete.',
      });
    }
  };

  await cleanupMarkers();
  await runProbe('agenthost-image-readiness', async () => {
    const runtime = input.pins.runtimeReceipt;
    const activation = runtime.activation;
    const owner = owners.find(item => item.storage.ownerId === activation.ownerId);
    if (!owner) block('activation_owner_missing', 'The activation receipt must name one of the two pinned fixture owners.');
    const state = live.get(owner.storage.ownerId);
    const placement = await api(owner.storage, 'GET', '/sandbox/v1/placement');
    if (placement.status !== 200 || placement.body?.isCurrent !== true) {
      block('activation_placement_missing', 'The runtime activation must bind to a current Environment placement.', {
        ownerId: owner.storage.ownerId,
        status: placement.status,
      });
    }
    const lifecycle = await api(owner.storage, 'GET', '/lifecycle');
    if (lifecycle.status !== 200) {
      block('activation_lifecycle_missing', 'The Environment lifecycle must be read back with the current activation placement.', {
        ownerId: owner.storage.ownerId,
        status: lifecycle.status,
      });
    }
    requireOwnerFence(lifecycle.body, placement.body, state, 'AgentHost activation');
    const placementResourceIdValue = placementResourceId(placement.body);
    const placementGeneration = placementResourceGeneration(placement.body);
    const sandboxReceipt = state.creation.resources.sandbox;
    if (placementResourceIdValue !== sandboxReceipt.resourceId ||
        placementGeneration !== owner.runtime.sandboxResourceGeneration) {
      block('activation_placement_receipt_mismatch', 'The current placement resource and generation differ from the invocation-owned activation receipt.', {
        ownerId: owner.storage.ownerId,
        resourceId: placementResourceIdValue,
        resourceGeneration: placementGeneration,
      });
    }
    const readinessPlacement = await api(owner.storage, 'GET', '/sandbox/v1/internal/placement');
    if (readinessPlacement.status !== 200 ||
        readinessPlacement.body?.isCurrent !== true ||
        placementResourceId(readinessPlacement.body) !== placementResourceIdValue ||
        placementResourceGeneration(readinessPlacement.body) !== placementGeneration) {
      block('activation_readiness_placement_missing', 'The current run-bound Environment placement must expose its native startup observations and lease budget.', {
        ownerId: owner.storage.ownerId,
        status: readinessPlacement.status,
      });
    }
    requireOwnerFence(lifecycle.body, readinessPlacement.body, state, 'AgentHost readiness');
    const environmentReadiness = readinessPlacement.body?.runtimeReadiness;
    const providerObservation = environmentReadiness?.observation;
    const startupBudget = input.pins.sourceReceipt.startupBudget;
    const leaseCreatedAt = Date.parse(environmentReadiness?.leaseCreatedAt);
    const now = typeof dependencies.now === 'function' ? dependencies.now() : Date.now();
    if (!Number.isFinite(leaseCreatedAt) ||
        leaseCreatedAt > now ||
        !hasPositiveStartupBudgets(startupBudget) ||
        !sameJson(environmentReadiness?.startupBudgets, startupBudget) ||
        !providerObservation ||
        providerObservation.resource?.resourceId !== placementResourceIdValue ||
        providerObservation.resource?.generation !== placementGeneration ||
        providerObservation.fencingGeneration !== readinessPlacement.body.providerFencingGeneration ||
        !['pending', 'ready'].includes(providerObservation.state) ||
        providerObservation.vmIsolationVerified !== true ||
        providerObservation.workspaceAttachmentVerified !== true ||
        providerObservation.verifiedNetworkGeneration !== runtime.network.generation ||
        providerObservation.startupFailure != null ||
        !Array.isArray(providerObservation.startupPhases) ||
        providerObservation.startupPhases.length !== NATIVE_PROVIDER_PHASES.length ||
        providerObservation.startupPhases.some((phase, index) =>
          phase?.phase !== NATIVE_PROVIDER_PHASES[index] ||
          phase.contractVersion !== 1 ||
          !Number.isFinite(Date.parse(phase.observedAt)))) {
      block('environment_startup_evidence_missing', 'The current Environment placement must provide the exact native provider phases, lease origin, positive budgets, mount/isolation proof, and current network generation.', {
        ownerId: owner.storage.ownerId,
      });
    }
    const providerImagePhase = providerObservation.startupPhases[1];
    if (providerImagePhase.imageDigest !== activation.image.digest ||
        providerImagePhase.compressedPullBytes !== activation.image.compressedPullBytes) {
      block('environment_startup_image_mismatch', 'The native provider ImageReady phase must match the pinned digest and measured pull size.', {
        ownerId: owner.storage.ownerId,
      });
    }
    const providerPhaseTimes = providerObservation.startupPhases.map(phase => Date.parse(phase.observedAt));
    if (providerPhaseTimes.some((time, index) =>
      time < leaseCreatedAt ||
      time > now ||
      (index > 0 && time < providerPhaseTimes[index - 1]))) {
      block('environment_startup_evidence_invalid', 'Native provider startup phases must be chronological and not before the lease or after the current time.', {
        ownerId: owner.storage.ownerId,
      });
    }
    const observed = await observeSandbox(owner, placement.body, {
      expectedPodUid: owner.runtime.sandboxUid,
      expectedOperationId: sandboxReceipt.operationId,
    });
    verifyCluster(observed, input.pins.context.clusterIdentity);
    const { registration, readiness: actualReadiness } =
      await validateCurrentRuntimeReadiness(owner, {
        ...state,
        placement: readinessPlacement.body,
        sandboxObservation: observed,
      });
    const pinnedReadiness = projectSafeReadinessReceipt(activation);
    const binding = registration.binding;
    const placementUid = observed?.pod?.uid;
    verifySandboxOwnership(owner, observed, placement.body, sandboxReceipt.operationId);
    if (registration.runtimeInstanceId !== activation.runtimeInstanceId ||
        registration.revision !== activation.registrationRevision ||
        registration.state !== 'active' ||
        Date.parse(registration.expiresAt) <= (typeof dependencies.now === 'function' ? dependencies.now() : Date.now()) ||
        binding.executionFence !== activation.executionFence ||
        binding.actorId !== owner.storage.actorId ||
        binding.tenantId !== owner.storage.tenantId ||
        binding.projectId !== owner.storage.projectId ||
        binding.runId !== owner.storage.runId ||
        binding.sessionId !== owner.storage.sessionId ||
        binding.environmentId !== owner.storage.environmentId ||
        binding.placementUid !== placementUid ||
        binding.placementGeneration !== placementGeneration ||
        binding.environmentCurrentFencingGeneration !== placement.body.currentFencingGeneration ||
        binding.environmentProviderFencingGeneration !== placement.body.providerFencingGeneration) {
      block('activation_registration_fence_mismatch', 'The current registration binding, owner, execution fence, and placement must match the actual Environment projection.', {
        ownerId: owner.storage.ownerId,
        placementUid,
        placementGeneration,
      });
    }
    if (observed?.pod?.containers?.find(item => item.name === 'agenthost')?.image !== input.pins.imageReceipt.reference ||
        activation.image?.digest !== input.pins.imageReceipt.digest ||
        activation.image.digest !== input.pins.imageReceipt.digest ||
        activation.image.platform !== placement.body.image?.platform ||
        activation.image.compressedPullBytes !== placement.body.image?.compressedPullBytes) {
      block('agenthost_image_receipt_mismatch', 'The live AgentHost image, activation receipt, source grant, and registration fence do not match the exact pins.');
    }
    const phases = actualReadiness.startupPhases;
    if (phases.length !== RUNTIME_HOST_PHASES.length ||
        phases.some((phase, index) =>
          phase.phase !== RUNTIME_HOST_PHASES[index] ||
          phase.contractVersion !== 1 ||
          !Number.isFinite(Date.parse(phase.observedAt)))) {
      block('agenthost_readiness_evidence_missing', 'The actual RuntimeHost receipt must contain Scheduled, ImageReady, Started, Configured, and Ready in contract order.');
    }
    const phaseTimes = phases.map(phase => Date.parse(phase.observedAt));
    if (phaseTimes.some((time, index) =>
      time < leaseCreatedAt ||
      time > now ||
      (index > 0 && time < phaseTimes[index - 1]))) {
      block('agenthost_readiness_evidence_invalid', 'RuntimeHost startup phases must be chronological and not before the current lease or after the current time.');
    }
    let previousAt = leaseCreatedAt;
    const exceeded = [];
    const phaseBudgetFields = STARTUP_BUDGET_FIELDS.slice(0, -1);
    for (let index = 0; index < phases.length; index += 1) {
      const deadline = previousAt + startupBudget[phaseBudgetFields[index]] * 1000;
      if (phaseTimes[index] > deadline) exceeded.push(RUNTIME_HOST_PHASES[index]);
      previousAt = phaseTimes[index];
    }
    const totalDeadline = leaseCreatedAt + startupBudget.totalSeconds * 1000;
    if (phaseTimes.at(-1) > totalDeadline) exceeded.push('total');
    if (exceeded.length > 0) {
      fail('agenthost_startup_budget_exceeded', 'The observed startup phase timestamps exceed the accepted startup budgets.', {
        exceeded,
        leaseCreatedAt: environmentReadiness.leaseCreatedAt,
        phaseTimestamps: phases.map(phase => ({ phase: phase.phase, observedAt: phase.observedAt })),
      });
    }
    return {
      evidence: {
        ownerId: owner.storage.ownerId,
        podUid: placementUid,
        placementGeneration,
        image: input.pins.imageReceipt.reference,
        imageDigest: input.pins.imageReceipt.digest,
        runtimeInstanceId: activation.runtimeInstanceId,
        registrationRevision: registration.revision,
        readinessReceipt: actualReadiness,
      },
    };
  });

  await cleanupSandbox();
  await revokeOwnedPolicy();

  const resultsByName = new Map(probes.map(probe => [probe.name, probe]));
  for (const name of PROBE_NAMES) {
    if (!resultsByName.has(name)) {
      probes.push(resultFor(name, 'blocked', {
        code: 'probe_not_run',
        reason: 'A prerequisite probe or live receipt was unavailable.',
      }, secrets));
    }
  }
  for (const probe of probes) {
    const actions = cleanupActions.get(probe.name) ?? [];
    const failures = cleanupErrors.get(probe.name) ?? [];
    if (actions.length > 0) {
      probe.cleanup = {
        status: failures.length === 0 ? 'complete' : 'uncertain',
        actions,
      };
    } else if (probe.cleanup.status === 'not-started') {
      probe.cleanup = { status: 'not-required', actions: [] };
    }
    if (failures.length > 0 && probe.status === 'passed') {
      probe.status = 'blocked';
      probe.code = 'cleanup_uncertain';
      probe.reason = 'Acceptance evidence passed, but exact invocation-owned cleanup could not be confirmed.';
    }
  }
  const overallStatus = probes.some(probe => probe.status === 'failed')
    ? 'failed'
    : probes.some(probe => probe.status === 'blocked')
      ? 'blocked'
      : 'passed';
  return {
    kind: 'p1-native-acceptance',
    schemaVersion: 1,
    overallStatus,
    startedAt,
    completedAt: new Date().toISOString(),
    probes,
  };
}

/** Seal the completed probe envelope so edits to saved evidence fail validation. */
export async function runP1NativeProbes(input, dependencies) {
  return sealP1NativeProbeEnvelope(await runP1NativeProbesCore(input, dependencies));
}

function hasExactKeys(value, keys) {
  return isRecord(value) &&
    Object.keys(value).length === keys.length &&
    keys.every(key => Object.hasOwn(value, key));
}

function isHttpsUrl(value, path) {
  if (!nonEmpty(value)) return false;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' &&
      url.hostname.length > 0 &&
      !url.username &&
      !url.password &&
      (!path || url.pathname === path) &&
      !url.search &&
      !url.hash;
  } catch {
    return false;
  }
}

function hasCredentialMaterial(value) {
  if (Array.isArray(value)) return value.some(hasCredentialMaterial);
  if (!isRecord(value)) return false;
  return Object.entries(value).some(([key, item]) => {
    if (/^maxPromptTokens$/i.test(key)) {
      return item !== null &&
        (!Number.isSafeInteger(item) || item < 1 || item > 200000);
    }
    return /(?:authorization|bearer|token|credential|password|cookie|secret)/i.test(key) ||
      hasCredentialMaterial(item);
  });
}

function isNonEmptyGuid(value) {
  return typeof value === 'string' && GUID.test(value) &&
    value.toLowerCase() !== EMPTY_GUID;
}

function validateCreditReaderPin(pin, kind) {
  const strings = [
    'ownerId',
    'actorId',
    'tenantId',
    'projectId',
    'runId',
    'sessionId',
    'environmentId',
    'runtimeInstanceId',
    'sourceGrantId',
  ];
  const stage = kind === 'fixedZero' ? 'hostPreQueue' : 'nativeTurnBegin';
  if (!isRecord(pin) ||
      strings.some(key => !nonEmpty(pin[key])) ||
      !isNonEmptyGuid(pin.runtimeInstanceId) ||
      !isNonEmptyGuid(pin.sourceGrantId) ||
      !positive(pin.registrationRevision) ||
      !positive(pin.executionFence) ||
      !positive(pin.sourceGrantRevision) ||
      !isSha256Hash(pin.registrationHash) ||
      !isSha256Hash(pin.sourceHash) ||
      pin.stage !== stage ||
      (kind === 'fixedZero'
        ? pin.expectedHardCreditLimit !== 0 ||
          !isNonEmptyGuid(pin.requestId) ||
          !isSha256Hash(pin.requestHash)
        : !Number.isFinite(pin.expectedHardCreditLimit) ||
          pin.expectedHardCreditLimit <= 0 ||
          !isNonEmptyGuid(pin.dispatchId))) {
    block('credit_reader_input_incomplete', 'Both credit-denial cases require complete non-secret owner, runtime, source-grant, and receipt pins.');
  }
  if (!hasExactKeys(pin.current, ['registration', 'sourceGrant', 'sourceReceipt']) ||
      !hasExactKeys(pin.proof, [
        'contractVersion',
        'registration',
        'sourceGrantId',
        'sourceGrantRevision',
        'purpose',
      ]) ||
      hasCredentialMaterial(pin.current) ||
      hasCredentialMaterial(pin.proof)) {
    block('credit_reader_context_incomplete', 'Each case requires an actual non-secret current registration, grant, source receipt, and Host session proof.');
  }
}

function validateCreditReaderProof(pin) {
  if (pin.proof.contractVersion !== 1 ||
      !sameJson(pin.proof.registration, pin.current.registration) ||
      pin.proof.sourceGrantId !== pin.current.sourceGrant.grantId ||
      pin.proof.sourceGrantRevision !== pin.current.sourceGrant.revision ||
      String(pin.proof.purpose).toLowerCase() !== 'observe') {
    block('credit_reader_proof_mismatch', 'The Host session proof must bind the actual current registration and Observe grant.');
  }
}

function validateCurrentCreditContext(pin, readback, now) {
  if (!hasExactKeys(readback, ['registration', 'sourceGrant', 'sourceReceipt', 'result']) ||
      hasCredentialMaterial(readback)) {
    block('credit_reader_context_incomplete', 'The source reader must return only the current typed registration, grant, source receipt, and read result.');
  }
  const { registration, sourceGrant, sourceReceipt } = readback;
  const binding = registration?.binding;
  const source = sourceReceipt?.source;
  const registrationExpiry = Date.parse(registration?.expiresAt);
  const grantExpiry = Date.parse(sourceGrant?.expiresAt);
  const observationEndpoint = binding?.observationEndpoint;
  const configureEndpoint = binding?.configureEndpoint;
  if (!isRecord(registration) ||
      registration.runtimeInstanceId !== pin.runtimeInstanceId ||
      registration.revision !== pin.registrationRevision ||
      String(registration.state).toLowerCase() !== 'active' ||
      !Number.isFinite(registrationExpiry) ||
      registrationExpiry <= now ||
      !isRecord(binding) ||
      binding.actorId !== pin.actorId ||
      binding.tenantId !== pin.tenantId ||
      binding.projectId !== pin.projectId ||
      binding.runId !== pin.runId ||
      binding.sessionId !== pin.sessionId ||
      binding.environmentId !== pin.environmentId ||
      binding.executionFence !== pin.executionFence ||
      binding.copilotHardCreditLimit !== pin.expectedHardCreditLimit ||
      !isHttpsUrl(configureEndpoint) ||
      !isHttpsUrl(observationEndpoint, '/internal/runtime/observations') ||
      !nonEmpty(binding.actorIssuer) ||
      !nonEmpty(binding.agentId) ||
      !nonEmpty(binding.turnId) ||
      !nonEmpty(binding.placementUid) ||
      !positive(binding.placementGeneration) ||
      !nonEmpty(binding.profileId) ||
      !isSha256Hash(binding.acceptedSelectionHash) ||
      !nonEmpty(binding.modelSelectionReference) ||
      !isRecord(sourceGrant) ||
      sourceGrant.grantId !== pin.sourceGrantId ||
      sourceGrant.runtimeInstanceId !== pin.runtimeInstanceId ||
      sourceGrant.registrationRevision !== pin.registrationRevision ||
      sourceGrant.revision !== pin.sourceGrantRevision ||
      String(sourceGrant.purpose).toLowerCase() !== 'observe' ||
      String(sourceGrant.state).toLowerCase() !== 'active' ||
      sourceGrant.audience !== observationEndpoint ||
      !nonEmpty(sourceGrant.issuer) ||
      !isSha256Hash(sourceGrant.configurationHash) ||
      !Number.isFinite(grantExpiry) ||
      grantExpiry <= now ||
      grantExpiry > registrationExpiry ||
      !Number.isFinite(Date.parse(sourceGrant.recordedAt)) ||
      !isRecord(sourceReceipt) ||
      sourceReceipt.runtimeInstanceId !== pin.runtimeInstanceId ||
      sourceReceipt.registrationRevision !== pin.registrationRevision ||
      sourceReceipt.sourceGrantId !== pin.sourceGrantId ||
      sourceReceipt.canonicalPayloadHash !== pin.sourceHash ||
      !isSha256Hash(sourceReceipt.canonicalPayloadHash) ||
      !Number.isFinite(Date.parse(sourceReceipt.recordedAt)) ||
      !isRecord(source) ||
      source.runtimeInstanceId !== pin.runtimeInstanceId ||
      source.registrationRevision !== pin.registrationRevision ||
      !nonEmpty(source.sdkSessionId) ||
      !nonEmpty(source.sdkVersion) ||
      !nonEmpty(source.runtimeVersion) ||
      source.sourceMode !== 'hosted-copilot' ||
      source.meterSource !== 'copilot.nano_aiu' ||
      source.byokProvider != null ||
      source.modelSelectionReference !== binding.modelSelectionReference ||
      source.acceptedSelectionHash !== binding.acceptedSelectionHash ||
      !nonEmpty(source.modelId) ||
      !isSha256Hash(source.catalogHash)) {
    block('credit_reader_current_context_mismatch', 'The current registration, owner scope, Observe grant, audience, and native source receipt do not match the pinned references.');
  }
  return { registration, sourceGrant, sourceReceipt };
}

function validateFixedZeroCreditRead(pin, readback, now) {
  const current = validateCurrentCreditContext(pin, readback, now);
  const { result } = readback;
  const receipt = result?.receipt;
  const evidence = result?.evidence;
  if (!hasExactKeys(result, ['receipt', 'evidence']) ||
      !isRecord(receipt) ||
      receipt.contractVersion !== 1 ||
      receipt.stage !== 'hostPreQueue' ||
      receipt.code !== 'hardLimitZero' ||
      !sameJson(receipt.registration, current.registration) ||
      receipt.registrationHash !== pin.registrationHash ||
      receipt.requestId !== pin.requestId ||
      receipt.requestHash !== pin.requestHash ||
      receipt.sourceHash !== pin.sourceHash ||
      receipt.sourceGrantId !== pin.sourceGrantId ||
      receipt.sourceGrantRevision !== pin.sourceGrantRevision ||
      !isSha256Hash(receipt.canonicalPayloadHash) ||
      !Number.isFinite(Date.parse(receipt.recordedAt)) ||
      !isRecord(evidence) ||
      evidence.contractVersion !== 1 ||
      evidence.runtimeInstanceId !== pin.runtimeInstanceId ||
      evidence.registrationRevision !== pin.registrationRevision ||
      evidence.executionFence !== pin.executionFence ||
      evidence.requestId !== pin.requestId ||
      evidence.stage !== 'hostPreQueue' ||
      evidence.guardReceiptHash !== receipt.canonicalPayloadHash ||
      evidence.registrationHash !== pin.registrationHash ||
      evidence.sourceHash !== pin.sourceHash ||
      evidence.sourceGrantId !== pin.sourceGrantId ||
      evidence.sourceGrantRevision !== pin.sourceGrantRevision ||
      evidence.observedEventCount !== 0 ||
      evidence.rangeComplete !== true ||
      !isSha256Hash(evidence.nativeSessionHash) ||
      evidence.nativeSessionHash !== sha256Utf8(current.sourceReceipt.source.sdkSessionId) ||
      !isSha256Hash(evidence.beforeCursorHash) ||
      evidence.afterCursorHash !== evidence.beforeCursorHash ||
      !isSha256Hash(evidence.canonicalPayloadHash) ||
      !Number.isFinite(Date.parse(evidence.verifiedAt))) {
    block('credit_reader_receipt_invalid', 'The fixed hostPreQueue receipt must bind the actual zero-limit request and a complete empty native-event range.');
  }
  return {
    ownerId: pin.ownerId,
    runtimeInstanceId: pin.runtimeInstanceId,
    registrationRevision: pin.registrationRevision,
    executionFence: pin.executionFence,
    registrationHash: pin.registrationHash,
    sourceGrantId: pin.sourceGrantId,
    sourceGrantRevision: pin.sourceGrantRevision,
    sourceHash: pin.sourceHash,
    sourceReceiptHash: current.sourceReceipt.canonicalPayloadHash,
    stage: 'hostPreQueue',
    requestId: pin.requestId,
    requestHash: pin.requestHash,
    receiptHash: receipt.canonicalPayloadHash,
    evidenceHash: evidence.canonicalPayloadHash,
  };
}

function validatePositiveCreditDenialRead(pin, readback, now) {
  const current = validateCurrentCreditContext(pin, readback, now);
  const { result } = readback;
  const receipt = result?.receipt;
  const noSend = result?.noSend;
  const validDispatch = receipt?.dispatchStateBefore === 'prepared' &&
    positive(receipt.dispatchRevisionBefore) &&
    receipt.dispatchStateAfter === receipt.dispatchStateBefore &&
    receipt.dispatchRevisionAfter === receipt.dispatchRevisionBefore;
  if (!hasExactKeys(result, ['receipt', 'noSend']) ||
      !isRecord(receipt) ||
      receipt.contractVersion !== 1 ||
      receipt.runtimeInstanceId !== pin.runtimeInstanceId ||
      receipt.registrationRevision !== pin.registrationRevision ||
      receipt.executionFence !== pin.executionFence ||
      receipt.sourceGrantId !== pin.sourceGrantId ||
      receipt.dispatchId !== pin.dispatchId ||
      receipt.stage !== 'nativeTurnBegin' ||
      receipt.code !== 'hardLimitReached' ||
      receipt.registrationHash !== pin.registrationHash ||
      receipt.sourceHash !== pin.sourceHash ||
      receipt.hardCreditLimit !== pin.expectedHardCreditLimit ||
      !Number.isFinite(receipt.observedCredits) ||
      receipt.observedCredits < pin.expectedHardCreditLimit ||
      !Number.isSafeInteger(receipt.ledgerEventCount) ||
      receipt.ledgerEventCount < 0 ||
      !positive(receipt.guardRevisionBefore) ||
      receipt.guardRevisionBefore === Number.MAX_SAFE_INTEGER ||
      receipt.guardRevisionAfter !== receipt.guardRevisionBefore + 1 ||
      !validDispatch ||
      !['dispatchHash', 'checkpointHash', 'ledgerDigest', 'costIdentity', 'canonicalPayloadHash']
        .every(key => isSha256Hash(receipt[key])) ||
      !Number.isFinite(Date.parse(receipt.recordedAt)) ||
      !isRecord(noSend) ||
      noSend.contractVersion !== 1 ||
      noSend.runtimeInstanceId !== pin.runtimeInstanceId ||
      noSend.registrationRevision !== pin.registrationRevision ||
      noSend.dispatchId !== pin.dispatchId ||
      noSend.denialReceiptHash !== receipt.canonicalPayloadHash ||
      noSend.registrationHash !== pin.registrationHash ||
      noSend.sourceHash !== pin.sourceHash ||
      noSend.observedEventCount !== 0 ||
      noSend.rangeComplete !== true ||
      !isSha256Hash(noSend.nativeSessionHash) ||
      noSend.nativeSessionHash !== sha256Utf8(current.sourceReceipt.source.sdkSessionId) ||
      !isSha256Hash(noSend.beforeCursorHash) ||
      noSend.afterCursorHash !== noSend.beforeCursorHash ||
      !isSha256Hash(noSend.canonicalPayloadHash) ||
      !Number.isFinite(Date.parse(noSend.verifiedAt))) {
    block('credit_reader_receipt_invalid', 'The positive NativeTurnBegin denial must bind the configured hard limit and a complete no-send receipt.');
  }
  return {
    ownerId: pin.ownerId,
    runtimeInstanceId: pin.runtimeInstanceId,
    registrationRevision: pin.registrationRevision,
    executionFence: pin.executionFence,
    registrationHash: pin.registrationHash,
    sourceGrantId: pin.sourceGrantId,
    sourceGrantRevision: pin.sourceGrantRevision,
    sourceHash: pin.sourceHash,
    sourceReceiptHash: current.sourceReceipt.canonicalPayloadHash,
    stage: 'nativeTurnBegin',
    dispatchId: pin.dispatchId,
    expectedHardCreditLimit: pin.expectedHardCreditLimit,
    receiptHash: receipt.canonicalPayloadHash,
    noSendReceiptHash: noSend.canonicalPayloadHash,
    observedEventCount: noSend.observedEventCount,
    rangeComplete: noSend.rangeComplete,
  };
}

function creditReaderCaseError(error) {
  if (error instanceof ProbeIssue) {
    return {
      status: error.status,
      code: error.code,
      reason: sanitize(error.message),
      evidence: sanitize(error.evidence),
    };
  }
  return {
    status: 'blocked',
    code: 'credit_source_read_unavailable',
    reason: 'The current authorized-runtime source reader did not return a validated owner readback.',
    evidence: {},
  };
}

function creditReaderOutput(startedAt, cases) {
  return {
    kind: 'p1-native-credit-denial-readers',
    schemaVersion: 1,
    overallStatus: Object.values(cases).every(item => item.status === 'passed')
      ? 'passed'
      : 'blocked',
    startedAt,
    completedAt: new Date().toISOString(),
    cases,
  };
}

/**
 * Exercises read-side credit-denial behavior with injected dependencies and returns its evidence.
 * Requests that do not match the configured tenant and credit boundary are rejected.
 */
export async function runP1NativeCreditDenialReaders(input = {}, dependencies = {}) {
  const fixedZero = input?.fixedZero;
  const positive = input?.positive;
  const startedAt = new Date().toISOString();
  const now = () => typeof dependencies?.now === 'function' ? dependencies.now() : Date.now();
  try {
    inspectInputForSecrets({
      fixedZero: fixedZero && Object.fromEntries(Object.entries(fixedZero)
        .filter(([key]) => key !== 'current' && key !== 'proof')),
      positive: positive && Object.fromEntries(Object.entries(positive)
        .filter(([key]) => key !== 'current' && key !== 'proof')),
    });
    if (hasCredentialMaterial({ fixedZero: fixedZero?.current, positive: positive?.current,
      fixedProof: fixedZero?.proof, positiveProof: positive?.proof })) {
      block('credential_in_input', 'Host proofs and pinned source context must not contain bearer credentials or credential references.');
    }
    validateCreditReaderPin(fixedZero, 'fixedZero');
    validateCreditReaderPin(positive, 'positive');
    if (fixedZero.runtimeInstanceId.toLowerCase() === positive.runtimeInstanceId.toLowerCase()) {
      block('credit_reader_instances_not_distinct', 'The zero and positive cases must pin different current RuntimeHost registrations.');
    }
    validateCreditReaderProof(fixedZero);
    validateCreditReaderProof(positive);
    validateCurrentCreditContext(fixedZero, { ...fixedZero.current, result: {} }, now());
    validateCurrentCreditContext(positive, { ...positive.current, result: {} }, now());
  } catch (error) {
    const failure = creditReaderCaseError(error);
    if (!(error instanceof ProbeIssue)) {
      failure.code = 'credit_reader_input_incomplete';
      failure.reason = 'Both independently pinned credit-denial cases are required.';
    }
    return creditReaderOutput(startedAt, {
      fixedZero: { ...failure },
      positive: { ...failure },
    });
  }

  const hostCaller = dependencies?.hostCaller;
  if (typeof hostCaller?.postJson !== 'function') {
    const failure = {
      status: 'blocked',
      code: 'credit_source_reader_unavailable',
      reason: 'An already-authorized RuntimeHost caller is required for both fixed read routes.',
      evidence: {},
    };
    return creditReaderOutput(startedAt, {
      fixedZero: { ...failure },
      positive: { ...failure },
    });
  }

  const readCase = async (pin, path, body, validate) => {
    let readback;
    try {
      const result = await hostCaller.postJson(path, body);
      readback = { ...pin.current, result };
    } catch {
      return creditReaderCaseError(new Error('source read failed'));
    }
    try {
      return { status: 'passed', evidence: validate(pin, readback, now()) };
    } catch (error) {
      return creditReaderCaseError(error);
    }
  };
  const [fixedZeroResult, positiveResult] = await Promise.all([
    readCase(fixedZero, FIXED_CREDIT_READ_PATH, {
      proof: fixedZero.proof,
      requestId: fixedZero.requestId,
      requestHash: fixedZero.requestHash,
    }, validateFixedZeroCreditRead),
    readCase(positive, NATIVE_CREDIT_READ_PATH, {
      proof: positive.proof,
      dispatchId: positive.dispatchId,
      stage: positive.stage,
    }, validatePositiveCreditDenialRead),
  ]);
  return creditReaderOutput(startedAt, {
    fixedZero: fixedZeroResult,
    positive: positiveResult,
  });
}
