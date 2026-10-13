const sha256 = {
  type: 'string',
  pattern: '^[a-f0-9]{64}$',
};

const runtimeCredentialHttpRequest = {
  type: 'object',
  additionalProperties: false,
  required: [
    'grantId', 'runtimeInstanceId', 'revision', 'purpose', 'audience',
    'configurationHash', 'credentialValue', 'credentialExpiresAt', 'operationId',
  ],
  properties: {
    grantId: { type: 'string', format: 'uuid' },
    runtimeInstanceId: { type: 'string', format: 'uuid' },
    revision: { type: 'integer', format: 'int64', minimum: 1 },
    purpose: { type: 'string', enum: ['Configure', 'Observe'] },
    audience: { type: 'string', format: 'uri' },
    configurationHash: sha256,
    credentialValue: { type: 'string', minLength: 1 },
    credentialExpiresAt: { type: 'string', format: 'date-time' },
    operationId: { type: 'string', format: 'uuid' },
  },
};

const intent = {
  type: 'object',
  additionalProperties: false,
  required: [
    'contractVersion', 'runtimeInstanceId', 'registrationRevision', 'tenantId',
    'projectId', 'runId', 'rootSessionId', 'acceptedSelectionHash',
    'executionFence', 'softCreditLimit', 'hardCreditLimit',
  ],
  properties: {
    contractVersion: { const: 1 },
    runtimeInstanceId: { type: 'string', format: 'uuid' },
    registrationRevision: { type: 'integer', format: 'int64', minimum: 1 },
    tenantId: { type: 'string', minLength: 1 },
    projectId: { type: 'string', minLength: 1 },
    runId: { type: 'string', minLength: 1 },
    rootSessionId: { type: 'string', minLength: 1 },
    acceptedSelectionHash: sha256,
    executionFence: { type: 'integer', format: 'int64', minimum: 1 },
    softCreditLimit: { type: ['number', 'null'], minimum: 0 },
    hardCreditLimit: { type: ['number', 'null'], minimum: 0 },
  },
};

const snapshot = {
  type: 'object',
  additionalProperties: false,
  required: [
    'intent', 'registrationHash', 'costIdentity', 'costUnit', 'costScope',
    'copilotAicFloor', 'accountedThroughLedgerEventCount', 'ledgerDigest',
    'guardRevision', 'softWarningEmitted', 'hardCapExhausted', 'updatedAt',
    'canonicalPayloadHash',
  ],
  properties: {
    intent: { $ref: '#/components/schemas/RuntimeCreditGuardIntent' },
    registrationHash: sha256,
    costIdentity: sha256,
    costUnit: { const: 'AIC' },
    costScope: { const: 'root-run' },
    copilotAicFloor: { type: 'number', minimum: 0 },
    accountedThroughLedgerEventCount: { type: 'integer', format: 'int64', minimum: 0 },
    ledgerDigest: sha256,
    guardRevision: { type: 'integer', format: 'int64', minimum: 1 },
    softWarningEmitted: { type: 'boolean' },
    hardCapExhausted: { type: 'boolean' },
    updatedAt: { type: 'string', format: 'date-time' },
    canonicalPayloadHash: sha256,
  },
};

const errorResponse = {
  type: 'object',
  additionalProperties: true,
  required: ['code'],
  properties: { code: { type: 'string' } },
};

const noStoreHeader = {
  description: 'Responses are not cacheable.',
  required: true,
  schema: { type: 'string', pattern: '(^|,\\s*)no-store(\\s*(,|$))' },
};

const contract = {
  openapi: '3.1.0',
  info: {
    title: 'Agentweaver Orchestrator Runtime Credit Guard API',
    version: '1.0.0',
    description: 'Static API-harness contract for the direct Orchestrator owner route.',
  },
  paths: {
    '/internal/runtime/credit-guards/read': {
      post: {
        operationId: 'readRuntimeCreditGuardSnapshot',
        security: [{ ProjectsActor: [] }],
        requestBody: {
          required: true,
          content: {
            'application/json': {
              schema: { $ref: '#/components/schemas/RuntimeCreditGuardReadRequest' },
            },
          },
        },
        responses: {
          '200': {
            description: 'The durable guard snapshot matching the exact read intent.',
            headers: { 'Cache-Control': noStoreHeader },
            content: {
              'application/json': {
                schema: { $ref: '#/components/schemas/RuntimeCreditGuardSnapshot' },
              },
            },
          },
          '404': {
            description: 'No durable guard exists for the requested exact intent.',
            headers: { 'Cache-Control': noStoreHeader },
            content: {
              'application/problem+json': {
                schema: {
                  type: 'object',
                  additionalProperties: true,
                  required: ['code'],
                  properties: { code: { const: 'runtime_credit_guard_unknown' } },
                },
              },
            },
          },
          default: {
            description: 'An authorization, validation, or owner error; never a success result.',
            headers: { 'Cache-Control': noStoreHeader },
            content: { 'application/problem+json': { schema: errorResponse } },
          },
        },
      },
    },
  },
  components: {
    securitySchemes: {
      ProjectsActor: { type: 'http', scheme: 'bearer' },
    },
    schemas: {
      RuntimeCredentialHttpRequest: runtimeCredentialHttpRequest,
      RuntimeCreditGuardIntent: intent,
      RuntimeCreditGuardReadRequest: {
        type: 'object',
        additionalProperties: false,
        required: ['authorization', 'intent'],
        properties: {
          authorization: { $ref: '#/components/schemas/RuntimeCredentialHttpRequest' },
          intent: { $ref: '#/components/schemas/RuntimeCreditGuardIntent' },
        },
      },
      RuntimeCreditGuardSnapshot: snapshot,
    },
  },
};

function deepFreeze(value) {
  if (value && typeof value === 'object' && !Object.isFrozen(value)) {
    Object.freeze(value);
    for (const item of Object.values(value)) deepFreeze(item);
  }
  return value;
}

export const RUNTIME_CREDIT_GUARD_READ_CONTRACT = deepFreeze(contract);
export const RUNTIME_CREDIT_GUARD_READ_PATH = '/internal/runtime/credit-guards/read';

function exactKeys(value, expected, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || Object.keys(value).length !== expected.length
    || expected.some(key => !Object.hasOwn(value, key))) {
    throw new TypeError(`${label} does not match the static API contract.`);
  }
}

function nonEmptyString(value) {
  return typeof value === 'string' && value.length > 0;
}

function positiveSafeInteger(value) {
  return Number.isSafeInteger(value) && value > 0;
}

function nonNegativeFiniteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

function sha256String(value) {
  return typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
}

function uuid(value) {
  return typeof value === 'string'
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function dateTime(value) {
  return typeof value === 'string'
    && /^\d{4}-\d{2}-\d{2}T/.test(value)
    && Number.isFinite(Date.parse(value));
}

function validateIntent(value, label = 'intent') {
  const fields = [
    'contractVersion', 'runtimeInstanceId', 'registrationRevision', 'tenantId',
    'projectId', 'runId', 'rootSessionId', 'acceptedSelectionHash',
    'executionFence', 'softCreditLimit', 'hardCreditLimit',
  ];
  exactKeys(value, fields, label);
  if (value.contractVersion !== 1
    || !uuid(value.runtimeInstanceId)
    || !positiveSafeInteger(value.registrationRevision)
    || !['tenantId', 'projectId', 'runId', 'rootSessionId'].every(key => nonEmptyString(value[key]))
    || !sha256String(value.acceptedSelectionHash)
    || !positiveSafeInteger(value.executionFence)
    || ![value.softCreditLimit, value.hardCreditLimit].every(limit =>
      limit === null || nonNegativeFiniteNumber(limit))
    || value.softCreditLimit !== null && value.hardCreditLimit !== null
      && value.softCreditLimit > value.hardCreditLimit) {
    throw new TypeError(`${label} does not match the static API contract.`);
  }
}

function validateCredentialProof(value, runtimeInstanceId) {
  const fields = [
    'grantId', 'runtimeInstanceId', 'revision', 'purpose', 'audience',
    'configurationHash', 'credentialValue', 'credentialExpiresAt', 'operationId',
  ];
  exactKeys(value, fields, 'authorization');
  let audience;
  try {
    audience = new URL(value.audience);
  } catch {
    throw new TypeError('authorization does not match the static API contract.');
  }
  if (!uuid(value.grantId)
    || !uuid(value.runtimeInstanceId)
    || value.runtimeInstanceId !== runtimeInstanceId
    || !positiveSafeInteger(value.revision)
    || value.purpose !== 'Observe'
    || !['http:', 'https:'].includes(audience.protocol)
    || !sha256String(value.configurationHash)
    || !nonEmptyString(value.credentialValue)
    || !dateTime(value.credentialExpiresAt)
    || !uuid(value.operationId)) {
    throw new TypeError('authorization does not match the static API contract.');
  }
}

export function validateRuntimeCreditGuardReadRequest(request) {
  exactKeys(request, ['authorization', 'intent'], 'request');
  validateIntent(request.intent);
  validateCredentialProof(request.authorization, request.intent.runtimeInstanceId);
}

export function validateRuntimeCreditGuardSnapshot(value, requestedIntent) {
  const fields = [
    'intent', 'registrationHash', 'costIdentity', 'costUnit', 'costScope',
    'copilotAicFloor', 'accountedThroughLedgerEventCount', 'ledgerDigest',
    'guardRevision', 'softWarningEmitted', 'hardCapExhausted', 'updatedAt',
    'canonicalPayloadHash',
  ];
  exactKeys(value, fields, 'snapshot');
  validateIntent(value.intent, 'snapshot intent');
  const intentFields = [
    'contractVersion', 'runtimeInstanceId', 'registrationRevision', 'tenantId',
    'projectId', 'runId', 'rootSessionId', 'acceptedSelectionHash',
    'executionFence', 'softCreditLimit', 'hardCreditLimit',
  ];
  if (!requestedIntent || intentFields.some(field => value.intent[field] !== requestedIntent[field])
    || !sha256String(value.registrationHash)
    || !sha256String(value.costIdentity)
    || value.costUnit !== 'AIC'
    || value.costScope !== 'root-run'
    || !nonNegativeFiniteNumber(value.copilotAicFloor)
    || !Number.isSafeInteger(value.accountedThroughLedgerEventCount)
    || value.accountedThroughLedgerEventCount < 0
    || !sha256String(value.ledgerDigest)
    || !positiveSafeInteger(value.guardRevision)
    || typeof value.softWarningEmitted !== 'boolean'
    || typeof value.hardCapExhausted !== 'boolean'
    || !dateTime(value.updatedAt)
    || !sha256String(value.canonicalPayloadHash)) {
    throw new TypeError('snapshot does not match the requested static API contract.');
  }
}
