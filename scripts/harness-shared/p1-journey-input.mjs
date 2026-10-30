import { validateNetworkTarget } from './target-guard.mjs';

/** Receipt fields that must be pinned before a P1 surface journey can use owner-owned resources. */
const PIN_RECEIPTS = [
  'providerReceipt',
  'runtimeReceipt',
  'storageReceipt',
];
const SECRET_PROPERTY = /^(?:accessToken|authorization|connectionString|password|secret|setupToken|token)$/i;
const SURFACE_JOURNEYS = new Set(['gate-answer', 'gate-approve', 'gate-reject', 'message', 'journal']);
const SOURCE_SHA = /^[a-f0-9]{40}$/;

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function requireString(value, label) {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error(`An explicit ${label} is required.`);
  }
}

function requireExactKeys(value, keys, label) {
  if (!isRecord(value) || Object.keys(value).some(key => !keys.includes(key))
    || keys.some(key => !Object.hasOwn(value, key))) {
    throw new Error(`The ${label} must contain exactly: ${keys.join(', ')}.`);
  }
}

function rejectSecrets(value) {
  if (Array.isArray(value)) {
    for (const item of value) rejectSecrets(item);
    return;
  }
  if (!isRecord(value)) return;
  for (const [name, child] of Object.entries(value)) {
    if (SECRET_PROPERTY.test(name)) throw new Error('Credentials must not be included in P1 runner input.');
    rejectSecrets(child);
  }
}

/** Validate the direct target and live environment inputs for a P1 product journey. */
function validateP1StaticInput(input) {
  if (!isRecord(input) || input.executionIntent !== 'execute') {
    throw new Error('P1 runner input must explicitly set executionIntent to "execute".');
  }
  rejectSecrets(input);

  const environment = input.environment;
  if (!isRecord(environment)) throw new Error('The direct Environment target is required.');
  validateNetworkTarget(environment.baseUrl, { exactPath: '/' });
  for (const name of ['issuer', 'audience', 'actorId', 'tenantId']) {
    requireString(environment[name], `Environment ${name}`);
  }

  const pins = input.pins;
  if (!isRecord(pins)) throw new Error('Pinned P1 environment receipts are required.');
  requireString(pins.context, 'Kubernetes context');
  requireString(pins.namespace, 'Kubernetes namespace');
  if (!/^[A-Za-z0-9._:/@-]{1,128}$/.test(pins.context)
    || !/^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$/.test(pins.namespace)) {
    throw new Error('The exact Kubernetes context or namespace is invalid.');
  }
  for (const name of PIN_RECEIPTS) {
    if (!isRecord(pins[name]) || !Object.keys(pins[name]).length) {
      throw new Error(`A pinned ${name} is required.`);
    }
  }
  requireExactKeys(pins.providerReceipt,
    ['configMapName', 'sourceCommit', 'uid', 'resourceVersion'], 'provider catalog receipt pin');
  requireString(pins.providerReceipt.configMapName, 'provider catalog ConfigMap name');
  requireString(pins.providerReceipt.uid, 'provider catalog ConfigMap UID');
  requireString(pins.providerReceipt.resourceVersion, 'provider catalog resource version');
  if (!SOURCE_SHA.test(pins.providerReceipt.sourceCommit ?? '')) {
    throw new Error('A pinned provider catalog source commit is required.');
  }

  return { executionIntent: input.executionIntent, environment, pins };
}

/** Check owner fences and invocation receipts for a live, unexpired runner-owned fixture. */
function validateFixtureOwnership(input, pins, now) {
  const fences = pins.ownerFences;
  if (!isRecord(fences)) throw new Error('Current owner fences are required.');
  for (const name of ['projectId', 'runId', 'rootSessionId', 'sessionId']) {
    requireString(fences[name], `owner-fenced ${name}`);
  }
  if (!Number.isSafeInteger(fences.executionFence) || fences.executionFence < 1) {
    throw new Error('A positive current owner execution fence is required.');
  }

  const fixture = pins.fixtureScope;
  if (!isRecord(fixture) || fixture.kind !== 'p1-runner-owned'
    || fixture.status !== 'ready') {
    throw new Error('An active P1 runner-owned fixture scope is required.');
  }
  requireString(fixture.invocationId, 'acceptance invocation ID');
  requireString(fixture.fixtureId, 'runner-owned fixture ID');
  for (const name of ['projectId', 'runId', 'rootSessionId', 'sessionId']) {
    if (fixture[name] !== fences[name]) {
      throw new Error(`The fixture scope does not match the owner-fenced ${name}.`);
    }
  }
  const expiresAt = Date.parse(fixture.expiresAt ?? '');
  if (!Number.isFinite(expiresAt) || expiresAt <= now()) {
    throw new Error('The runner-owned fixture scope is expired or has no valid expiry.');
  }
  if (!Array.isArray(fixture.allowedJourneys) || !fixture.allowedJourneys.length
    || fixture.allowedJourneys.some(journey => !SURFACE_JOURNEYS.has(journey))
    || new Set(fixture.allowedJourneys).size !== fixture.allowedJourneys.length) {
    throw new Error('The fixture scope must list its exact allowed P1 journeys.');
  }
  if (!Array.isArray(fixture.ownershipReceipts) || fixture.ownershipReceipts.length === 0) {
    throw new Error('Invocation-bound owner creation and readback receipts are required.');
  }
  const receiptKeys = new Set();
  for (const receipt of fixture.ownershipReceipts) {
    if (!isRecord(receipt) || typeof receipt.resourceType !== 'string' || !receipt.resourceType.trim()
      || typeof receipt.resourceId !== 'string' || !receipt.resourceId.trim()
      || receipt.invocationId !== fixture.invocationId
      || !isRecord(receipt.creationReceipt) || !Object.keys(receipt.creationReceipt).length
      || !isRecord(receipt.observedReceipt) || !Object.keys(receipt.observedReceipt).length
      || receipt.creationReceipt.resourceId !== receipt.resourceId
      || receipt.observedReceipt.resourceId !== receipt.resourceId
      || !Number.isSafeInteger(receipt.ownerFence) || receipt.ownerFence < 1) {
      throw new Error('Each owner resource must have an invocation-bound creation receipt, readback, and current fence.');
    }
    const key = `${receipt.resourceType}\u0000${receipt.resourceId}`;
    if (receiptKeys.has(key)) throw new Error('The fixture scope contains duplicate owner resource receipts.');
    receiptKeys.add(key);
  }
  for (const resourceId of new Set([fences.projectId, fences.runId, fences.rootSessionId, fences.sessionId])) {
    if (!fixture.ownershipReceipts.some(receipt =>
      receipt.resourceId === resourceId && receipt.ownerFence === fences.executionFence)) {
      throw new Error('The invocation-owned receipts do not cover every pinned project, run, and session fence.');
    }
  }
  if (typeof input.journey !== 'string' || !fixture.allowedJourneys.includes(input.journey)) {
    throw new Error('The selected journey is not allowed by the active fixture scope.');
  }
  return { fences, fixture };
}

/** Validate the requested journey and fixture setup plan without accepting caller-supplied ownership. */
function validatePlan(input, { environment, pins }) {
  if (Object.hasOwn(pins, 'ownerFences') || Object.hasOwn(pins, 'fixtureScope')) {
    throw new Error('Runner plans cannot contain caller-supplied owner fences or fixture receipts.');
  }
  if (typeof input.journey !== 'string' || !SURFACE_JOURNEYS.has(input.journey)) {
    throw new Error('A supported P1 journey is required.');
  }
  const plan = input.fixturePlan;
  if (!isRecord(plan)) throw new Error('An explicit runner fixture plan is required.');
  const fixturePlanKeys = [
    'expectedPlatformRuntimeRevision', 'selectionContext', 'gate', 'message',
    ...(Object.hasOwn(plan, 'deniedActor') ? ['deniedActor'] : []),
  ];
  requireExactKeys(plan, fixturePlanKeys, 'runner fixture plan');
  if (!Number.isSafeInteger(plan.expectedPlatformRuntimeRevision)
    || plan.expectedPlatformRuntimeRevision < 1) {
    throw new Error('A positive expected platform runtime revision is required in the runner fixture plan.');
  }
  const context = plan.selectionContext;
  requireExactKeys(context,
    ['revision', 'availableModelSelectionReferences', 'providerRequirements', 'requiredEgress'],
    'run selection context');
  requireString(context.revision, 'run selection context revision');
  for (const field of ['availableModelSelectionReferences', 'providerRequirements', 'requiredEgress']) {
    if (!Array.isArray(context[field])) throw new Error(`The run selection ${field} must be an array.`);
  }
  if (context.availableModelSelectionReferences.some(value => typeof value !== 'string' || !value.trim())
    || context.providerRequirements.some(value => !isRecord(value))
    || context.requiredEgress.some(value => !isRecord(value))) {
    throw new Error('The run selection context contains an invalid reference or requirement.');
  }

  if (Object.hasOwn(plan, 'deniedActor')) {
    const deniedActor = plan.deniedActor;
    requireExactKeys(deniedActor, ['actorId', 'tenantId'], 'denied actor');
    requireString(deniedActor.actorId, 'denied actor ID');
    requireString(deniedActor.tenantId, 'denied actor tenant ID');
    if (deniedActor.actorId === environment.actorId && deniedActor.tenantId === environment.tenantId) {
      throw new Error('The denied actor must be different from the selected P1 actor.');
    }
  }

  if (input.journey.startsWith('gate-')) {
    const gate = plan.gate;
    requireExactKeys(gate, ['prompt', 'allowedChoices', 'allowsFreeform', 'answer'], 'gate plan');
    requireString(gate.prompt, 'gate prompt');
    if (!Array.isArray(gate.allowedChoices)
      || gate.allowedChoices.some(choice => typeof choice !== 'string' || !choice.trim())
      || typeof gate.allowsFreeform !== 'boolean'
      || typeof gate.answer !== 'string' || !gate.answer.trim()) {
      throw new Error('The gate plan must contain exact choices, freeform behavior, and an answer.');
    }
    if (input.journey === 'gate-answer'
      && !gate.allowedChoices.includes(gate.answer)
      && !(gate.allowsFreeform && !gate.allowedChoices.includes(gate.answer))) {
      throw new Error('The planned gate does not allow its exact answer.');
    }
  } else if (plan.gate !== null) {
    throw new Error('A gate plan is only valid for a gate journey.');
  }
  if (input.journey === 'message') {
    requireExactKeys(plan.message, ['text'], 'message plan');
    requireString(plan.message.text, 'addressed-message text');
  } else if (plan.message !== null) {
    throw new Error('A message plan is only valid for the message journey.');
  }
  return { fixturePlan: plan, journey: input.journey };
}

/** Validate a setup plan that will be used to create a runner-owned P1 fixture. */
export function validateP1RunnerPlanInput(input, options = {}) {
  const base = validateP1StaticInput(input);
  return { ...base, ...validatePlan(input, base) };
}

/** Validate the exact environment pins and active fixture scope before a P1 journey runs. */
export function validateP1JourneyInput(input, { now = () => Date.now() } = {}) {
  const base = validateP1StaticInput(input);
  const { fences, fixture } = validateFixtureOwnership(input, base.pins, now);
  return { ...base, fences, fixture, journey: input.journey };
}

/** Require each named resource to have a receipt tied to this fixture invocation. */
export function requireP1OwnedResources(fixture, requiredResources) {
  for (const { resourceType, resourceId } of requiredResources) {
    if (!fixture.ownershipReceipts.some(receipt =>
      receipt.resourceType === resourceType && receipt.resourceId === resourceId
      && receipt.invocationId === fixture.invocationId)) {
      throw new Error(`The invocation does not own the exact ${resourceType} fixture resource.`);
    }
  }
}
