import { createHash } from 'node:crypto';

const route = '/internal/runtime/actions/numeric-denials/read';
// Declaration order and PascalCase are from RuntimeNumericLimitDenialReceipt's default
// JsonSerializer serialization, not the camelCase HTTP serializer.
const fields = [
  'contractVersion', 'receiptId', 'projectId', 'runId', 'rootSessionId', 'sessionId',
  'runtimeInstanceId', 'registrationRevision', 'sourceRegistrationRevision', 'sourceGrantId',
  'sourceHash', 'grantId', 'grantRevision', 'executionFence', 'acceptedSelectionHash',
  'ownerRevision', 'limitKind', 'acceptedLimit', 'observedCount', 'attemptId', 'requestHash',
  'dispatchId', 'checkpointId', 'checkpointRevision', 'checkpointHash',
  'checkpointDecisionStateVersion', 'workPlanId', 'associationId', 'sendStateBeforeHash',
  'sendStateAfterHash', 'recordedAt', 'canonicalPayloadHash',
];
const expectedFields = fields.filter(key => ![
  'contractVersion', 'receiptId', 'observedCount', 'sendStateBeforeHash',
  'sendStateAfterHash', 'recordedAt', 'canonicalPayloadHash',
].includes(key));
const guidFields = ['receiptId', 'runtimeInstanceId', 'sourceGrantId', 'grantId', 'attemptId', 'dispatchId'];
const hashFields = ['sourceHash', 'acceptedSelectionHash', 'requestHash', 'checkpointHash',
  'sendStateBeforeHash', 'sendStateAfterHash', 'canonicalPayloadHash'];
const identifierFields = ['projectId', 'runId', 'rootSessionId', 'sessionId',
  'checkpointId', 'workPlanId', 'associationId'];
const positiveFields = ['registrationRevision', 'sourceRegistrationRevision', 'executionFence',
  'ownerRevision', 'acceptedLimit', 'checkpointRevision', 'checkpointDecisionStateVersion'];
const checkpointFields = ['checkpointId', 'checkpointRevision', 'checkpointHash',
  'checkpointDecisionStateVersion', 'workPlanId', 'associationId'];
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const positive = value => Number.isSafeInteger(value) && value > 0;
const guid = value => typeof value === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) &&
  value !== '00000000-0000-0000-0000-000000000000';
const hash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const identifier = value => typeof value === 'string' && /^[A-Za-z0-9._:-]{1,256}$/.test(value);

function requireShape(value, keys) {
  if (!record(value) || Object.keys(value).length !== keys.length ||
      keys.some(key => !Object.hasOwn(value, key))) {
    throw new Error('Numeric denial evidence has missing or unknown fields.');
  }
}

function validateIdentity(value) {
  if (guidFields.filter(key => key !== 'receiptId')
    .some(key => !(key === 'dispatchId' && value[key] === null) && !guid(value[key])) ||
      hashFields.filter(key => expectedFields.includes(key))
        .some(key => !(key === 'checkpointHash' && value[key] === null) && !hash(value[key])) ||
      identifierFields.some(key => !(checkpointFields.includes(key) && value[key] === null) &&
        !identifier(value[key])) ||
      positiveFields.some(key => !(checkpointFields.includes(key) && value[key] === null) &&
        !positive(value[key])) ||
      value.sourceRegistrationRevision !== value.registrationRevision ||
      typeof value.grantRevision !== 'string' || !value.grantRevision.trim() ||
      !['ModelTurns', 'ToolCalls'].includes(value.limitKind) ||
      value.acceptedLimit > 2147483647 ||
      checkpointFields.some(key => (value[key] === null) !== (value.checkpointId === null))) {
    throw new Error('Numeric denial owner, source, limit, or checkpoint identity is invalid.');
  }
}

// Preserve all seven .NET fractional digits and the offset; Date.toISOString would
// lose ticks and change the bytes hashed by the producer.
function timestamp(value) {
  const match = typeof value === 'string' &&
    /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) throw new Error('Numeric denial timestamp is malformed.');
  const [, date, time, fraction = '', offset] = match;
  const [year, month, day] = date.split('-').map(Number);
  const [hour, minute, second] = time.split(':').map(Number);
  const [offsetHour, offsetMinute] = offset === 'Z' ? [0, 0] : offset.slice(1).split(':').map(Number);
  if (year < 1 || month < 1 || month > 12 || day < 1 ||
      day > new Date(Date.UTC(year === 1 ? 2001 : year, month, 0)).getUTCDate() ||
      hour > 23 || minute > 59 || second > 59 ||
      offsetHour > 14 || offsetMinute > 59 || (offsetHour === 14 && offsetMinute !== 0)) {
    throw new Error('Numeric denial timestamp is invalid.');
  }
  const trimmed = fraction.replace(/0+$/, '');
  const canonical = `${date}T${time}${trimmed ? `.${trimmed}` : ''}${
    offset === 'Z' || offsetHour + offsetMinute === 0 ? '+00:00' : offset}`;
  const milliseconds = Date.parse(canonical);
  if (!Number.isFinite(milliseconds)) throw new Error('Numeric denial timestamp is invalid.');
  return {
    canonical,
    ticks: BigInt(milliseconds) * 10000n + BigInt(fraction.padEnd(7, '0').slice(3)),
  };
}

function dotnetString(value) {
  const short = { '\b': '\\b', '\t': '\\t', '\n': '\\n', '\f': '\\f', '\r': '\\r', '\\': '\\\\' };
  let encoded = '"';
  for (let index = 0; index < value.length; index++) {
    const character = value[index];
    const code = value.charCodeAt(index);
    encoded += short[character] ?? (code < 0x20 || code > 0x7e || /["&'<>+`]/.test(character)
      ? `\\u${code.toString(16).toUpperCase().padStart(4, '0')}` : character);
  }
  return `${encoded}"`;
}

export function numericDenialPayloadHash(receipt) {
  const entries = fields.map(key => {
    let value = key === 'canonicalPayloadHash' ? '' : receipt[key];
    if (key === 'recordedAt') value = timestamp(value).canonical;
    return `${dotnetString(key[0].toUpperCase() + key.slice(1))}:${
      // The DateTimeOffset writer emits '+' literally, unlike the default string encoder.
      typeof value === 'string' && key !== 'recordedAt' ? dotnetString(value) : JSON.stringify(value)}`;
  });
  return createHash('sha256').update(`{${entries.join(',')}}`, 'utf8').digest('hex');
}

export function validateNumericDenialInput(input) {
  if (!record(input) || Object.keys(input).length === 0 ||
      Object.keys(input).some(key => !['modelTurns', 'toolCalls'].includes(key))) {
    throw new Error('Numeric denial input accepts only modelTurns and toolCalls lookup pins.');
  }
  for (const [name, pin] of Object.entries(input)) {
    requireShape(pin, ['expected', 'recordedAfter', 'recordedBefore']);
    requireShape(pin.expected, expectedFields);
    validateIdentity(pin.expected);
    const after = timestamp(pin.recordedAfter).ticks;
    const before = timestamp(pin.recordedBefore).ticks;
    if (after > before || pin.expected.limitKind !== (name === 'modelTurns' ? 'ModelTurns' : 'ToolCalls')) {
      throw new Error('Numeric denial case or observation interval is invalid.');
    }
  }
}

function validateReceipt(result, pin) {
  requireShape(result, ['receipt']);
  if (result.receipt === null) return null;
  const receipt = result.receipt;
  requireShape(receipt, fields);
  validateIdentity(receipt);
  if (receipt.contractVersion !== 1 || !guid(receipt.receiptId) ||
      !Number.isSafeInteger(receipt.observedCount) || receipt.observedCount < receipt.acceptedLimit ||
      hashFields.some(key => !(key === 'checkpointHash' && receipt[key] === null) && !hash(receipt[key])) ||
      receipt.sendStateBeforeHash !== receipt.sendStateAfterHash ||
      expectedFields.some(key => receipt[key] !== pin.expected[key])) {
    throw new Error('Numeric denial receipt does not match the exact pinned attempt and owner.');
  }
  const recorded = timestamp(receipt.recordedAt).ticks;
  if (recorded < timestamp(pin.recordedAfter).ticks || recorded > timestamp(pin.recordedBefore).ticks ||
      recorded > BigInt(Date.now()) * 10000n) {
    throw new Error('Numeric denial receipt is outside the expected observation interval or in the future.');
  }
  if (numericDenialPayloadHash(receipt) !== receipt.canonicalPayloadHash) {
    throw new Error('Numeric denial receipt canonical payload hash is invalid.');
  }
  return receipt;
}

export async function readNumericDenials(input, dependencies) {
  validateNumericDenialInput(input);
  if (typeof dependencies?.ownerCaller?.postJson !== 'function') {
    throw new Error('An authorized Orchestrator ownerCaller.postJson reader is required.');
  }
  const cases = {};
  for (const name of ['modelTurns', 'toolCalls']) {
    if (!input[name]) {
      cases[name] = { status: 'blocked', code: 'numeric_denial_lookup_missing' };
      continue;
    }
    try {
      const pin = input[name];
      const { runtimeInstanceId, registrationRevision, executionFence, attemptId } = pin.expected;
      const result = await dependencies.ownerCaller.postJson(route, {
        runtimeInstanceId, registrationRevision, executionFence, attemptId,
      });
      const receipt = validateReceipt(result, pin);
      cases[name] = receipt === null
        ? { status: 'blocked', code: 'numeric_denial_receipt_missing' }
        : {
          status: 'observed',
          evidence: {
            kind: 'persisted-owner-numeric-denial',
            acceptanceEvidence: false,
            persistedSendStateUnchanged: true,
            nativeNoSendProven: false,
            receipt,
          },
        };
    } catch {
      // Do not echo provider errors or malformed payloads: they may contain credentials.
      cases[name] = { status: 'blocked', code: 'numeric_denial_read_invalid',
        reason: 'The authenticated numeric denial read failed or returned mismatched, stale, or malformed evidence.' };
    }
  }
  return { status: Object.values(cases).some(item => item.status === 'observed') ? 'partial' : 'blocked', cases };
}
