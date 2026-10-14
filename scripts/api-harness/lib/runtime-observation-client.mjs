import { isDeepStrictEqual } from 'node:util';

const RUNTIME_OBSERVATION_OPERATION = 'readRuntimeObservationV1';
const MAX_ENVIRONMENT_ITEMS = 100;
const SHA256 = /^[a-f0-9]{64}$/i;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const IDENTIFIER = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$/;

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function exactKeys(value, keys, label) {
  if (!isRecord(value) || Object.keys(value).length !== keys.length
    || keys.some(key => !Object.hasOwn(value, key))) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
}

function requireString(value, label, pattern = IDENTIFIER) {
  if (typeof value !== 'string' || !pattern.test(value)) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
}

function requireNullableString(value, label, pattern = IDENTIFIER) {
  if (value !== null) requireString(value, label, pattern);
}

function requirePositiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value < 1) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
}

function requireNonNegativeInteger(value, label) {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
}

function requireDate(value, label) {
  if (typeof value !== 'string' || !Number.isFinite(Date.parse(value))) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
}

function requireHash(value, label) {
  if (typeof value !== 'string' || !SHA256.test(value)) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
}

function requireUuid(value, label) {
  if (typeof value !== 'string' || !UUID.test(value)) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
}

function validateSection(value, label, validateValue) {
  exactKeys(value, ['state', 'value'], label);
  if (!['Available', 'Unknown', 'Unavailable'].includes(value.state)
    || (value.state === 'Available') !== (value.value !== null)) {
    throw new TypeError(`${label} does not match the runtime observation contract.`);
  }
  if (value.value !== null) validateValue(value.value);
}

function validateRegistration(value) {
  exactKeys(value, ['runtimeInstanceId', 'revision', 'state', 'expiresAt'], 'registration');
  requireUuid(value.runtimeInstanceId, 'registration.runtimeInstanceId');
  requirePositiveInteger(value.revision, 'registration.revision');
  if (!['Active', 'Revoked'].includes(value.state)) {
    throw new TypeError('registration.state does not match the runtime observation contract.');
  }
  requireDate(value.expiresAt, 'registration.expiresAt');
}

function validateExecution(value, expected) {
  const required = [
    'contractVersion', 'rootSessionId', 'sessionId', 'executionFence', 'workPlanId',
    'associationId', 'checkpointRevision', 'checkpointId', 'checkpointDecisionStateVersion',
    'currentDecisionStateVersion', 'status',
  ];
  const optional = ['pendingDispatch', 'resultDigest'];
  if (!isRecord(value) || required.some(key => !Object.hasOwn(value, key))
    || Object.keys(value).some(key => !required.includes(key) && !optional.includes(key))) {
    throw new TypeError('execution observation does not match the runtime observation contract.');
  }
  if (value.contractVersion !== 1 || value.rootSessionId !== expected.rootSessionId
    || value.sessionId !== expected.sessionId || value.executionFence !== expected.executionFence
    || !['Pending', 'Running', 'Succeeded', 'Failed', 'Indeterminate'].includes(value.status)) {
    throw new TypeError('execution observation does not match the requested owner fence.');
  }
  for (const key of ['rootSessionId', 'sessionId', 'workPlanId', 'associationId', 'checkpointId']) {
    requireString(value[key], `execution.${key}`);
  }
  for (const key of [
    'executionFence', 'checkpointRevision', 'checkpointDecisionStateVersion',
    'currentDecisionStateVersion',
  ]) {
    requirePositiveInteger(value[key], `execution.${key}`);
  }
  if (value.checkpointDecisionStateVersion > value.currentDecisionStateVersion) {
    throw new TypeError('execution decision versions are inconsistent.');
  }
  if (Object.hasOwn(value, 'pendingDispatch')) {
    exactKeys(value.pendingDispatch, ['messageId'], 'execution.pendingDispatch');
    requireUuid(value.pendingDispatch.messageId, 'execution.pendingDispatch.messageId');
    if (value.status !== 'Running' || Object.hasOwn(value, 'resultDigest')) {
      throw new TypeError('execution pending dispatch is inconsistent.');
    }
  }
  if (Object.hasOwn(value, 'resultDigest')) {
    exactKeys(value.resultDigest, ['sha256', 'utf8ByteLength'], 'execution.resultDigest');
    requireHash(value.resultDigest.sha256, 'execution.resultDigest.sha256');
    requireNonNegativeInteger(value.resultDigest.utf8ByteLength, 'execution.resultDigest.utf8ByteLength');
    if (value.status !== 'Succeeded') throw new TypeError('execution result digest is inconsistent.');
  }
}

function validateExecutionV2(value, expected) {
  exactKeys(value, ['contractVersion', 'observation', 'checkpointParent'], 'execution');
  if (value.contractVersion !== 2) {
    throw new TypeError('execution contract version is invalid.');
  }
  validateExecution(value.observation, expected);
  exactKeys(value.checkpointParent, ['hasParentMetadata', 'parentCheckpointId'], 'execution.checkpointParent');
  if (typeof value.checkpointParent.hasParentMetadata !== 'boolean'
    || value.checkpointParent.parentCheckpointId !== null
      && (typeof value.checkpointParent.parentCheckpointId !== 'string'
        || !/^[A-Za-z0-9._:-]{1,128}$/.test(value.checkpointParent.parentCheckpointId))
    || !value.checkpointParent.hasParentMetadata && value.checkpointParent.parentCheckpointId !== null) {
    throw new TypeError('execution checkpoint parent does not match the runtime observation contract.');
  }
}

function validateCreditGuard(value) {
  exactKeys(value, [
    'copilotAicFloor', 'accountedThroughLedgerEventCount', 'ledgerDigest', 'guardRevision',
    'softWarningEmitted', 'hardCapExhausted', 'updatedAt',
  ], 'creditGuard');
  if (typeof value.copilotAicFloor !== 'number' || !Number.isFinite(value.copilotAicFloor)
    || value.copilotAicFloor < 0
    || typeof value.softWarningEmitted !== 'boolean'
    || typeof value.hardCapExhausted !== 'boolean') {
    throw new TypeError('credit guard does not match the runtime observation contract.');
  }
  requireNonNegativeInteger(value.accountedThroughLedgerEventCount, 'creditGuard.accountedThroughLedgerEventCount');
  requireHash(value.ledgerDigest, 'creditGuard.ledgerDigest');
  requirePositiveInteger(value.guardRevision, 'creditGuard.guardRevision');
  requireDate(value.updatedAt, 'creditGuard.updatedAt');
}

function validateDispatches(value) {
  exactKeys(value, ['items', 'truncated'], 'dispatches');
  if (!Array.isArray(value.items) || value.items.length > 100 || typeof value.truncated !== 'boolean'
    || value.truncated && value.items.length !== 100) {
    throw new TypeError('dispatches do not match the runtime observation contract.');
  }
  const seen = new Set();
  for (const item of value.items) {
    exactKeys(item, [
      'dispatchId', 'rootSessionId', 'childSessionId', 'workPlanId', 'associationId',
      'checkpointId', 'checkpointRevision', 'executionFence', 'ownerRevision', 'state',
      'reportState', 'accountingState', 'witnessWatermark', 'witnessDigest', 'createdAt', 'updatedAt',
    ], 'dispatch');
    requireUuid(item.dispatchId, 'dispatch.dispatchId');
    if (seen.has(item.dispatchId)) throw new TypeError('dispatch IDs must be unique.');
    seen.add(item.dispatchId);
    for (const key of ['rootSessionId', 'childSessionId', 'workPlanId', 'associationId', 'checkpointId']) {
      requireString(item[key], `dispatch.${key}`);
    }
    for (const key of ['checkpointRevision', 'executionFence', 'ownerRevision']) {
      requirePositiveInteger(item[key], `dispatch.${key}`);
    }
    for (const key of ['state', 'reportState', 'accountingState']) {
      if (key === 'state' || item[key] !== null) requireString(item[key], `dispatch.${key}`);
    }
    if (item.witnessWatermark !== null) requireNonNegativeInteger(item.witnessWatermark, 'dispatch.witnessWatermark');
    if (item.witnessDigest !== null) requireHash(item.witnessDigest, 'dispatch.witnessDigest');
    if ((item.witnessWatermark === null) !== (item.witnessDigest === null)) {
      throw new TypeError('dispatch witness fields are inconsistent.');
    }
    requireDate(item.createdAt, 'dispatch.createdAt');
    requireDate(item.updatedAt, 'dispatch.updatedAt');
  }
}

function validateToolReservations(value) {
  exactKeys(value, ['items', 'truncated'], 'toolReservations');
  if (!Array.isArray(value.items) || value.items.length > 100 || typeof value.truncated !== 'boolean'
    || value.truncated && value.items.length !== 100) {
    throw new TypeError('tool reservations do not match the runtime observation contract.');
  }
  const seen = new Set();
  for (const item of value.items) {
    exactKeys(item, ['eventId', 'sessionId', 'executionFence', 'occurredAt'], 'tool reservation');
    requireUuid(item.eventId, 'toolReservation.eventId');
    if (seen.has(item.eventId)) throw new TypeError('tool reservation event IDs must be unique.');
    seen.add(item.eventId);
    requireString(item.sessionId, 'toolReservation.sessionId');
    requirePositiveInteger(item.executionFence, 'toolReservation.executionFence');
    requireDate(item.occurredAt, 'toolReservation.occurredAt');
  }
}

export function validateRuntimeObservationV1(value, expected) {
  exactKeys(value, [
    'contractVersion', 'registration', 'execution', 'creditGuard', 'dispatches', 'toolReservations',
  ], 'runtime observation');
  if (value.contractVersion !== 1 || !isRecord(expected)
    || !['projectId', 'runId', 'rootSessionId', 'sessionId'].every(key =>
      typeof expected[key] === 'string' && expected[key].length > 0)) {
    throw new TypeError('runtime observation identity is incomplete.');
  }
  requirePositiveInteger(expected.executionFence, 'expected.executionFence');
  validateSection(value.registration, 'registration', validateRegistration);
  validateSection(value.execution, 'execution', item => validateExecutionV2(item, expected));
  validateSection(value.creditGuard, 'creditGuard', validateCreditGuard);
  validateSection(value.dispatches, 'dispatches', validateDispatches);
  validateSection(value.toolReservations, 'toolReservations', validateToolReservations);
  if (value.registration.state !== 'Available'
    || value.registration.value?.state !== 'Active'
    || value.execution.state !== 'Available'
    || value.dispatches.state !== 'Available'
    || value.toolReservations.state !== 'Available') {
    throw new TypeError('Required runtime observation sections are not currently available.');
  }
  return value;
}

function noStore(call, label) {
  const directives = (call.cacheControl ?? '')
    .split(',')
    .map(value => value.trim().toLowerCase());
  if (!directives.includes('no-store')) {
    throw new Error(`${label} did not satisfy the no-store response requirement.`);
  }
}

function requireHttp(call, label) {
  if (call?.status !== 200) {
    throw new Error(`${label} returned an unexpected HTTP status.`);
  }
  noStore(call, label);
  if (!isRecord(call.transientResponseBody)) {
    throw new TypeError(`${label} returned an invalid JSON response.`);
  }
  return call.transientResponseBody;
}

function requireOwner(value, expected) {
  exactKeys(value, ['tenantId', 'projectId', 'runId', 'environmentId'], 'Environment owner');
  for (const key of ['tenantId', 'projectId', 'runId', 'environmentId']) {
    requireString(value[key], `Environment owner.${key}`);
  }
  if (value.tenantId !== expected.tenantId || value.projectId !== expected.projectId
    || value.runId !== expected.runId) {
    throw new Error('The Environment owner does not match the selected tenant, project, and run.');
  }
}

function requireEnvironmentFence(value, expectedOwner, label) {
  exactKeys(value, ['owner', 'lifecycleGeneration'], label);
  requireOwner(value.owner, expectedOwner);
  requirePositiveInteger(value.lifecycleGeneration, `${label}.lifecycleGeneration`);
}

function requireSameFence(actual, expected, label) {
  if (!isDeepStrictEqual(actual, expected)) {
    throw new Error(`${label} changed while Environment evidence was being read.`);
  }
}

function rejectSensitiveFields(value, label = 'Environment evidence') {
  if (Array.isArray(value)) {
    for (const item of value) rejectSensitiveFields(item, label);
    return;
  }
  if (!isRecord(value)) return;
  for (const [key, item] of Object.entries(value)) {
    if (/(?:authorization|credential|password|secret|token|cookie|prompt|rawOutcome|providerOutcome|rawOutput|rawPayload|responseBody|resultText)/i
      .test(key) || /^outcome$/i.test(key)) {
      throw new TypeError(`${label} contains a forbidden field.`);
    }
    rejectSensitiveFields(item, label);
  }
}

function validateProviderAttempt(attempt, operationId, expectedFence) {
  exactKeys(attempt, [
    'contractVersion', 'attemptId', 'operationId', 'lifecycleGeneration', 'resourceGeneration',
    'providerFencingGeneration', 'effectKind', 'state', 'requestFingerprint',
    'lateResourceFingerprint', 'failureCode', 'startedAt', 'outcomeRecordedAt', 'resolvedAt',
  ], 'provider attempt');
  if (attempt.contractVersion !== 1) throw new TypeError('Provider attempt contract version is invalid.');
  requireUuid(attempt.attemptId, 'providerAttempt.attemptId');
  requireUuid(attempt.operationId, 'providerAttempt.operationId');
  if (attempt.operationId !== operationId) throw new Error('A provider attempt belongs to another operation.');
  for (const key of ['lifecycleGeneration', 'resourceGeneration', 'providerFencingGeneration']) {
    requirePositiveInteger(attempt[key], `providerAttempt.${key}`);
  }
  if (attempt.lifecycleGeneration > expectedFence.lifecycleGeneration) {
    throw new Error('A provider attempt belongs to a future Environment lifecycle fence.');
  }
  if (!['provision', 'release', 'partialRelease', 'lateResourceRelease', 'legacyUnknown']
    .includes(attempt.effectKind)
    || !['inProgress', 'resultRecorded', 'failedNoEffect', 'unknown', 'completed', 'legacyUnknown']
      .includes(attempt.state)) {
    throw new TypeError('Provider attempt kind or state is invalid.');
  }
  requireHash(attempt.requestFingerprint, 'providerAttempt.requestFingerprint');
  requireNullableString(attempt.lateResourceFingerprint, 'providerAttempt.lateResourceFingerprint', SHA256);
  requireNullableString(attempt.failureCode, 'providerAttempt.failureCode');
  requireDate(attempt.startedAt, 'providerAttempt.startedAt');
  for (const key of ['outcomeRecordedAt', 'resolvedAt']) {
    if (attempt[key] !== null) requireDate(attempt[key], `providerAttempt.${key}`);
  }
}

function validateProviderAttempts(value, expectedFence, operationId) {
  exactKeys(value, ['contractVersion', 'currentFence', 'operationId', 'attempts'], 'provider attempts');
  if (value.contractVersion !== 1 || value.operationId !== operationId
    || !Array.isArray(value.attempts) || value.attempts.length > MAX_ENVIRONMENT_ITEMS) {
    throw new TypeError('provider attempts do not match the Environment read contract.');
  }
  requireSameFence(value.currentFence, expectedFence, 'Provider attempt owner fence');
  const seen = new Set();
  for (const attempt of value.attempts) {
    validateProviderAttempt(attempt, operationId, expectedFence);
    rejectSensitiveFields(attempt, 'Provider attempt');
    if (seen.has(attempt.attemptId)) throw new TypeError('Provider attempt IDs must be unique.');
    seen.add(attempt.attemptId);
  }
  return value;
}

function validateLateCleanupItem(item, expectedFence, expectedOwner) {
  exactKeys(item, [
    'sourceFence', 'operationId', 'resourceGeneration', 'providerFencingGeneration',
    'currentFencingGeneration', 'resourceFingerprint', 'resource', 'provisionAttempt',
    'releaseAttempt', 'createdAt', 'updatedAt', 'claimExpiresAt', 'releaseReceipt',
    'providerObservation',
  ], 'late-resource cleanup item');
  requireEnvironmentFence(item.sourceFence, expectedOwner, 'lateCleanup.sourceFence');
  if (item.sourceFence.lifecycleGeneration > expectedFence.lifecycleGeneration) {
    throw new Error('A late-resource cleanup source fence is newer than the current Environment fence.');
  }
  requireUuid(item.operationId, 'lateCleanup.operationId');
  for (const key of ['resourceGeneration', 'providerFencingGeneration', 'currentFencingGeneration']) {
    requirePositiveInteger(item[key], `lateCleanup.${key}`);
  }
  if (item.currentFencingGeneration < item.providerFencingGeneration) {
    throw new Error('Late-resource cleanup current fence precedes its provider fence.');
  }
  exactKeys(item.resource, ['seam', 'providerId', 'resourceId', 'generation'], 'lateCleanup.resource');
  if (item.resource.seam !== 'sandbox') throw new TypeError('Late-cleanup resource seam is invalid.');
  requireString(item.resource.providerId, 'lateCleanup.resource.providerId');
  requireString(item.resource.resourceId, 'lateCleanup.resource.resourceId');
  requirePositiveInteger(item.resource.generation, 'lateCleanup.resource.generation');
  if (item.resource.generation !== item.resourceGeneration) {
    throw new Error('Late-cleanup resource generation does not match its owner receipt.');
  }
  requireHash(item.resourceFingerprint, 'lateCleanup.resourceFingerprint');
  if (item.provisionAttempt !== null) {
    if (!isRecord(item.provisionAttempt)) throw new TypeError('Late-cleanup provision attempt is invalid.');
    validateProviderAttempt(item.provisionAttempt, item.provisionAttempt.operationId, expectedFence);
    if (item.provisionAttempt.resourceGeneration !== item.resourceGeneration
      || item.provisionAttempt.providerFencingGeneration !== item.providerFencingGeneration) {
      throw new Error('Late-cleanup provision attempt does not match its resource fences.');
    }
  }
  if (item.releaseAttempt !== null) {
    if (!isRecord(item.releaseAttempt)) throw new TypeError('Late-cleanup release attempt is invalid.');
    validateProviderAttempt(item.releaseAttempt, item.releaseAttempt.operationId, expectedFence);
    if (item.releaseAttempt.resourceGeneration !== item.resourceGeneration
      || item.releaseAttempt.providerFencingGeneration !== item.providerFencingGeneration) {
      throw new Error('Late-cleanup release attempt does not match its resource fences.');
    }
  }
  requireDate(item.createdAt, 'lateCleanup.createdAt');
  requireDate(item.updatedAt, 'lateCleanup.updatedAt');
  if (item.claimExpiresAt !== null) requireDate(item.claimExpiresAt, 'lateCleanup.claimExpiresAt');
  if (item.releaseReceipt !== null) {
    exactKeys(item.releaseReceipt, ['resource', 'idempotencyKey', 'disposition'], 'lateCleanup.releaseReceipt');
    if (!isDeepStrictEqual(item.releaseReceipt.resource, item.resource)
      || !['released', 'knownOwnedAbsent'].includes(item.releaseReceipt.disposition)) {
      throw new TypeError('Late-cleanup release receipt does not identify the exact resource and result.');
    }
    requireString(item.releaseReceipt.idempotencyKey, 'lateCleanup.releaseReceipt.idempotencyKey');
  }
  if (item.providerObservation !== null && !isRecord(item.providerObservation)) {
    throw new TypeError('Late-cleanup provider observation is invalid.');
  }
  rejectSensitiveFields(item, 'Late-resource cleanup');
}

function validateLateResourceCleanups(value, expectedFence, expectedOwner) {
  exactKeys(value, ['contractVersion', 'currentFence', 'items', 'hasMore'], 'late-resource cleanups');
  if (value.contractVersion !== 1 || !Array.isArray(value.items)
    || value.items.length > MAX_ENVIRONMENT_ITEMS || typeof value.hasMore !== 'boolean') {
    throw new TypeError('late-resource cleanups do not match the Environment read contract.');
  }
  requireSameFence(value.currentFence, expectedFence, 'Late-resource cleanup owner fence');
  const seen = new Set();
  for (const item of value.items) {
    validateLateCleanupItem(item, expectedFence, expectedOwner);
    const key = `${item.operationId}\u0000${item.resourceFingerprint}`;
    if (seen.has(key)) throw new TypeError('Late-resource cleanup identities must be unique.');
    seen.add(key);
  }
  return value;
}

export class RuntimeObservationClient {
  constructor({ api }) {
    if (typeof api?.invoke !== 'function' || typeof api?.call !== 'function') {
      throw new Error('A discovered Gateway API client is required for runtime observation reads.');
    }
    this.api = api;
  }

  async readRuntimeObservationV1(expected) {
    const call = await this.api.invoke(RUNTIME_OBSERVATION_OPERATION, {
      pathParameters: {
        projectId: expected?.projectId,
        runId: expected?.runId,
        sessionId: expected?.sessionId,
      },
      headers: { 'Cache-Control': 'no-store' },
    });
    if (call?.operationId !== RUNTIME_OBSERVATION_OPERATION) {
      throw new Error('The Gateway did not return the discovered runtime observation operation.');
    }
    return validateRuntimeObservationV1(
      requireHttp(call, 'Runtime observation'),
      expected,
    );
  }

  async readEnvironmentEvidence({ environmentCreation, owner, operationId }) {
    if (!isRecord(owner)) throw new TypeError('The selected Environment owner is required.');
    const createdSnapshot = environmentCreation?.snapshot;
    if (!isRecord(createdSnapshot) || typeof createdSnapshot.state !== 'string'
      || createdSnapshot.state.toLowerCase() !== 'active') {
      throw new TypeError('The Environment creation receipt is missing its active snapshot.');
    }
    requireEnvironmentFence(
      createdSnapshot.fence,
      owner,
      'Environment creation fence',
    );
    requireOwner({
      tenantId: owner.tenantId,
      projectId: owner.projectId,
      runId: owner.runId,
      environmentId: createdSnapshot.fence.owner.environmentId,
    }, owner);
    requireUuid(operationId, 'Environment operation ID');
    const { tenantId, projectId, runId, environmentId } = {
      ...owner,
      environmentId: environmentCreation.snapshot.fence.owner.environmentId,
    };
    const base = `/api/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}` +
      `/environments/${encodeURIComponent(environmentId)}/sandbox/v1`;
    const readLifecycle = async () => {
      const call = await this.api.call('GET', `${base}/lifecycle`, undefined, {
        headers: { 'Cache-Control': 'no-store' },
      });
      const body = requireHttp(call, 'Environment lifecycle');
      exactKeys(body, ['fence', 'state'], 'Environment lifecycle');
      requireEnvironmentFence(body.fence, { tenantId, projectId, runId }, 'Environment lifecycle fence');
      if (typeof body.state !== 'string' || body.state.toLowerCase() !== 'active') {
        throw new Error('The Environment is not active for the selected owner.');
      }
      return body;
    };

    const before = await readLifecycle();
    const operationPath = `${base}/operations/${encodeURIComponent(operationId)}`;
    const [attemptCall, cleanupCall] = await Promise.all([
      this.api.call('GET', `${operationPath}/provider-attempts`, undefined, {
        headers: { 'Cache-Control': 'no-store' },
      }),
      this.api.call('GET', `${base}/late-resource-cleanups`, undefined, {
        headers: { 'Cache-Control': 'no-store' },
      }),
    ]);
    const attempts = validateProviderAttempts(
      requireHttp(attemptCall, 'Environment provider attempts'),
      before.fence,
      operationId,
    );
    const cleanups = validateLateResourceCleanups(
      requireHttp(cleanupCall, 'Environment late-resource cleanups'),
      before.fence,
      { tenantId, projectId, runId },
    );
    const after = await readLifecycle();
    requireSameFence(after.fence, before.fence, 'Environment lifecycle fence');

    return {
      environmentId,
      fence: before.fence,
      providerAttempts: attempts,
      lateResourceCleanups: cleanups,
    };
  }
}
