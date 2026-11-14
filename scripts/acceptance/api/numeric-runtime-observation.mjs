#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, stat, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const CASE_ID = 'numeric-runtime-observation';
const MODEL_TURN_CASE_ID = 'numeric-model-turn-no-send';
const HARD_CREDIT_CASE_ID = 'numeric-hard-credit-no-send';
const COMMON_INPUT_KEYS = [
  'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
  'approvedNonProduction', 'expectedActorId', 'target',
];
const CASE_INPUT_KEYS = {
  [CASE_ID]: [...COMMON_INPUT_KEYS, 'expectedMaxToolCalls'],
  [MODEL_TURN_CASE_ID]: [...COMMON_INPUT_KEYS, 'expectedMaxModelTurns'],
  [HARD_CREDIT_CASE_ID]: [...COMMON_INPUT_KEYS, 'expectedSoftWarning'],
};
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 4 * 1024 * 1024;
const COMMAND_TIMEOUT_MS = 60_000;
const PAGE_SIZE = 100;
const TARGET_KEYS = [
  'gatewayUrl', 'brokerIssuer', 'clientId', 'apiAudience', 'tenantId',
  'projectId', 'runId', 'sessionId',
];
const TARGET_ENV = [
  'AGENTWEAVER_GATEWAY_URL',
  'AGENTWEAVER_BROKER_ISSUER',
  'AGENTWEAVER_CLIENT_ID',
  'AGENTWEAVER_API_AUDIENCE',
  'AGENTWEAVER_TENANT_ID',
  'AGENTWEAVER_PROJECT_ID',
  'AGENTWEAVER_RUN_ID',
  'AGENTWEAVER_P1_SETUP_TOKEN',
];
const TOOL_LIMIT_DENIAL = 'runtime_tool_call_limit_exhausted';

export function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const option = argv[index];
    if (option === '--dry-run' || option === '--help') {
      if (options[option] !== undefined) throw new Error(`${option} can only be used once.`);
      options[option] = true;
      continue;
    }
    if (!['--input', '--output'].includes(option)
      || typeof argv[index + 1] !== 'string'
      || argv[index + 1].startsWith('--')
      || options[option] !== undefined) {
      throw new Error('Use --input <case.json> --output <new-evidence-directory> [--dry-run].');
    }
    options[option] = argv[++index];
  }
  if (!options['--help'] && (!options['--input'] || !options['--output'])) {
    throw new Error('Use --input <case.json> --output <new-evidence-directory> [--dry-run].');
  }
  return {
    help: options['--help'] === true,
    dryRun: options['--dry-run'] === true,
    inputPath: options['--input'],
    outputPath: options['--output'],
  };
}

function exactKeys(value, expected) {
  return value !== null
    && typeof value === 'object'
    && !Array.isArray(value)
    && Object.keys(value).length === expected.length
    && expected.every(key => Object.hasOwn(value, key));
}

function nonEmpty(value) {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= 512;
}

function validateUrl(value, name, originOnly) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`The ${name} must be an absolute HTTPS URL.`);
  }
  const localHttp = url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  if ((!localHttp && url.protocol !== 'https:')
    || url.username
    || url.password
    || url.search
    || url.hash) {
    throw new Error(`The ${name} must be an HTTPS URL without user information, query, or fragment.`);
  }
  if (originOnly && url.pathname !== '/') throw new Error('The Gateway URL must be an origin without a path.');
  return originOnly ? url.origin : value;
}

function isIdentifier(value) {
  return typeof value === 'string'
    && value !== '.'
    && value !== '..'
    && /^[A-Za-z0-9._:-]{1,256}$/.test(value);
}

function validCaseLimit(value) {
  switch (value.caseId) {
    case CASE_ID:
      return Number.isInteger(value.expectedMaxToolCalls)
        && value.expectedMaxToolCalls >= 1
        && value.expectedMaxToolCalls <= 10_000;
    case MODEL_TURN_CASE_ID:
      return Number.isInteger(value.expectedMaxModelTurns)
        && value.expectedMaxModelTurns >= 1
        && value.expectedMaxModelTurns <= 10_000;
    case HARD_CREDIT_CASE_ID:
      return typeof value.expectedSoftWarning === 'boolean';
    default:
      return false;
  }
}

export function validateInput(value) {
  const caseKeys = Object.hasOwn(CASE_INPUT_KEYS, value?.caseId) ? CASE_INPUT_KEYS[value.caseId] : null;
  if (!caseKeys
    || !exactKeys(value, caseKeys)
    || !validCaseLimit(value)
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || !nonEmpty(value.expectedActorId)
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))
    || !isIdentifier(value.target.projectId)
    || !isIdentifier(value.target.runId)
    || !isIdentifier(value.target.sessionId)) {
    throw new Error('The case input does not match the numeric runtime observation schema.');
  }
  const target = {
    ...value.target,
    gatewayUrl: validateUrl(value.target.gatewayUrl, 'Gateway URL', true),
    brokerIssuer: validateUrl(value.target.brokerIssuer, 'Broker issuer', false),
  };
  const parsed = {
    caseId: value.caseId,
    sourceCommit: value.sourceCommit.toLowerCase(),
    targetCommit: value.targetCommit.toLowerCase(),
    apphostSha256: value.apphostSha256.toLowerCase(),
    approvedNonProduction: true,
    expectedActorId: value.expectedActorId,
  };
  if (value.caseId === CASE_ID) parsed.expectedMaxToolCalls = value.expectedMaxToolCalls;
  if (value.caseId === MODEL_TURN_CASE_ID) parsed.expectedMaxModelTurns = value.expectedMaxModelTurns;
  if (value.caseId === HARD_CREDIT_CASE_ID) parsed.expectedSoftWarning = value.expectedSoftWarning;
  parsed.target = target;
  return parsed;
}

export function resolveCliPath(value, platform = process.platform) {
  if (typeof value !== 'string' || !path.isAbsolute(value)) {
    throw new Error('Set AGENTWEAVER_CLI_PATH to the absolute path of the approved native apphost.');
  }
  const expectedName = platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  if (path.basename(value).toLowerCase() !== expectedName) {
    throw new Error(`AGENTWEAVER_CLI_PATH must name the native ${expectedName} apphost.`);
  }
  return path.resolve(value);
}

function targetArgs(target, includeBinding = true) {
  const args = [
    '--gateway-url', target.gatewayUrl,
    '--broker-issuer', target.brokerIssuer,
    '--client-id', target.clientId,
    '--api-audience', target.apiAudience,
    '--tenant-id', target.tenantId,
  ];
  if (includeBinding) args.push('--project-id', target.projectId, '--run-id', target.runId);
  return args;
}

function observationArgs(input, query) {
  return [
    'api', 'invoke', 'readRuntimeObservationV2',
    '--path', `projectId=${input.target.projectId}`,
    '--path', `runId=${input.target.runId}`,
    '--path', `sessionId=${input.target.sessionId}`,
    '--scope', 'api.read',
    '--scope', 'projects.orchestrator',
    ...targetArgs(input.target),
    ...Object.entries(query).flatMap(([key, value]) => ['--query', `${key}=${value}`]),
  ];
}

export function buildCommands(input) {
  const { target } = input;
  const unbound = targetArgs(target, false);
  const bound = targetArgs(target);
  return [
    { id: 'version', args: ['--version'] },
    {
      id: 'auth-status',
      args: [
        'auth', 'status', '--scope', 'api.read', '--scope', 'projects.orchestrator', ...bound,
      ],
    },
    { id: 'api-operations', args: ['api', 'operations', ...unbound] },
    {
      id: 'api-describe-runtime-observation',
      args: ['api', 'describe', 'readRuntimeObservationV2', ...unbound],
    },
    {
      id: 'rest-authorization-context',
      args: [
        'rest', '--method', 'GET', '--url', '/api/v1/authorization/context',
        '--scope', 'api.read', '--scope', 'projects.orchestrator', ...bound,
      ],
    },
    {
      id: 'run-status',
      args: [
        'api', 'invoke', 'readRunStatus',
        '--path', `projectId=${target.projectId}`, '--path', `runId=${target.runId}`,
        '--scope', 'api.read', '--scope', 'projects.orchestrator', ...bound,
      ],
    },
    {
      id: 'project-usage',
      args: [
        'api', 'invoke', 'readProjectUsage', '--path', `projectId=${target.projectId}`,
        '--scope', 'api.read', '--scope', 'projects.orchestrator', ...bound,
      ],
    },
    { id: 'observation-page-1', args: observationArgs(input, { pageSize: PAGE_SIZE }) },
  ];
}

export function validateAuthorizationContext(context, input) {
  if (context?.actorId !== input.expectedActorId
    || context?.tenantId !== input.target.tenantId
    || context?.boundProjectId !== input.target.projectId
    || context?.boundRunId !== input.target.runId) {
    throw new Error('The Gateway authorization context does not match the case actor, tenant, and bound run.');
  }
  return context;
}

export function childEnvironment(env) {
  const childEnv = { ...env };
  const excluded = new Set([...TARGET_ENV, 'AGENTWEAVER_CLI_PATH']);
  for (const key of Object.keys(childEnv)) {
    if (excluded.has(key.toUpperCase())) delete childEnv[key];
  }
  return childEnv;
}

function matchesToolLimitRejection(fact, sessionId) {
  return fact?.kind === 'toolPermissionRejected'
    && fact.limitKind === 'toolCalls'
    && fact.stage === 'toolPermission'
    && fact.reasonCode === TOOL_LIMIT_DENIAL
    && fact.sessionId === sessionId
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(fact.attemptId ?? '')
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(fact.dispatchId ?? '')
    && fact.ownerDecision?.state === 'available'
    && fact.toolPermission?.state === 'available'
    && fact.nativeNoSend?.state === 'unavailable'
    && fact.toolExecution?.state === 'unavailable';
}

function historyFacts(pages, firstHistory) {
  if (pages.some(page => page.history?.complete !== true
    || !Array.isArray(page.history?.items)
    || !Object.hasOwn(page.history, 'nextCursor')
    || (page.history.nextCursor !== null && typeof page.history.nextCursor !== 'string')
    || page.history?.snapshotId !== firstHistory?.snapshotId
    || page.history?.highWatermark !== firstHistory?.highWatermark)) {
    throw new Error('The history is incomplete, malformed, or its pages do not share one snapshot.');
  }

  const factsById = new Map();
  for (const page of pages) {
    for (const fact of page.history.items ?? []) {
      if (factsById.has(fact.factId)) throw new Error('The paged history contains a duplicate fact.');
      factsById.set(fact.factId, fact);
    }
  }
  return [...factsById.values()];
}

export function validateObservation(input, pages, exact, restExact) {
  switch (input.caseId) {
    case MODEL_TURN_CASE_ID:
      return validateModelTurnObservation(input, pages, exact, restExact);
    case HARD_CREDIT_CASE_ID:
      return validateHardCreditObservation(input, pages, exact, restExact);
    default:
      return validateToolObservation(input, pages, exact, restExact);
  }
}

function validateToolObservation(input, pages, exact, restExact) {
  if (!Array.isArray(pages) || pages.length === 0 || !pages[0]) {
    throw new Error('The observation history is empty or unavailable.');
  }
  const first = pages[0];
  const expected = input.target;
  const firstHistory = first.history;
  if (first.contractVersion !== 2
    || first.binding?.projectId !== expected.projectId
    || first.binding?.runId !== expected.runId
    || first.binding?.sessionId !== expected.sessionId
    || first.binding?.isRoot !== false
    || first.binding?.currentExecutionFence !== first.binding?.sessionExecutionFence) {
    throw new Error('The observation binding does not match the selected child session and current fence.');
  }
  if (first.acceptedLimits?.maxToolCalls !== input.expectedMaxToolCalls) {
    throw new Error('The accepted maxToolCalls value does not match the case input.');
  }
  const facts = historyFacts(pages, firstHistory);
  const rejections = facts.filter(fact => matchesToolLimitRejection(fact, expected.sessionId));
  if (rejections.length === 0) {
    throw new Error('The selected session has no retained numeric tool-permission rejection.');
  }
  const rejection = rejections[0];
  if (!facts.some(fact => fact.kind === 'toolInvocationReserved'
    && fact.limitKind === 'toolCalls'
    && fact.sessionId === expected.sessionId
    && fact.attemptId !== rejection.attemptId)) {
    throw new Error('The history does not show a separate admitted tool reservation.');
  }
  const total = first.totals?.toolSlotsReserved;
  if (total?.state !== 'available' || total.value !== input.expectedMaxToolCalls) {
    throw new Error('Run-wide tool reservations do not match the configured test limit.');
  }
  const exactItems = exact?.history?.items;
  if (exact?.history?.exactAttempt !== true
    || !Array.isArray(exactItems)
    || !exactItems.some(fact => isDeepStrictEqual(fact, rejection)
      && matchesToolLimitRejection(fact, expected.sessionId))) {
    throw new Error('Exact attempt lookup did not return the retained rejection fact.');
  }
  const restItems = restExact?.history?.items;
  if (restExact?.history?.exactAttempt !== true
    || !Array.isArray(restItems)
    || !restItems.some(fact => isDeepStrictEqual(fact, rejection)
      && matchesToolLimitRejection(fact, expected.sessionId))) {
    throw new Error('The REST exact lookup did not return the same rejection fact.');
  }
  return {
    attemptId: rejection.attemptId,
    dispatchId: rejection.dispatchId,
    factId: rejection.factId,
    pagesRead: pages.length,
    totalFacts: facts.length,
    runWideToolSlotsReserved: total.value,
    conclusion: 'The owner recorded a tool-permission rejection. This does not prove that the SDK received it or that the tool did not run.',
  };
}

function isUuid(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value ?? '');
}

// The history of a root binding contains facts from all sessions, fences, and selections of the run.
function authorityMismatch(fact, binding, sessionId) {
  if (fact?.sessionId !== sessionId) return 'another session';
  if (fact.executionFence !== binding.currentExecutionFence) return 'a stale or different execution fence';
  if (fact.acceptedSelectionHash !== binding.acceptedSelectionHash) return 'another accepted selection';
  return null;
}

function currentAuthorityFacts(candidates, binding, sessionId, label) {
  if (candidates.length === 0) throw new Error(`The history has no retained ${label}.`);
  const current = candidates.filter(fact => authorityMismatch(fact, binding, sessionId) === null);
  if (current.length === 0) {
    const mismatches = [...new Set(candidates.map(fact => authorityMismatch(fact, binding, sessionId)))];
    throw new Error(`No retained ${label} matches the current authority. Mismatch: ${mismatches.join('; ')}.`);
  }
  return current;
}

function nativeNoSendProven(fact) {
  return fact.nativeNoSend?.state === 'available'
    && fact.nativeNoSend.rangeComplete === true
    && fact.nativeNoSend.eventCount === 0;
}

function rootHistory(input, pages) {
  if (!Array.isArray(pages) || pages.length === 0 || !pages[0]) {
    throw new Error('The observation history is empty or unavailable.');
  }
  const first = pages[0];
  const binding = first.binding;
  const expected = input.target;
  if (first.contractVersion !== 2
    || binding?.projectId !== expected.projectId
    || binding?.runId !== expected.runId
    || binding?.sessionId !== expected.sessionId
    || binding?.isRoot !== true
    || !Number.isInteger(binding?.currentExecutionFence)
    || binding.currentExecutionFence !== binding.sessionExecutionFence
    || !nonEmpty(binding.acceptedSelectionHash)) {
    throw new Error('The observation binding does not match the selected root session and current fence.');
  }
  return { first, binding, facts: historyFacts(pages, first.history) };
}

function modelTurnEvidence(input, pages) {
  const { first, binding, facts } = rootHistory(input, pages);
  const sessionId = input.target.sessionId;
  const limit = input.expectedMaxModelTurns;
  if (first.acceptedLimits?.maxModelTurns !== limit) {
    throw new Error('The accepted maxModelTurns value does not match the case input.');
  }
  const total = first.totals?.modelSlotsReserved;
  if (total?.state !== 'available' || !Number.isInteger(total.value)) {
    throw new Error('Run-wide model-turn reservations are not available.');
  }
  if (total.value > limit) {
    throw new Error('Run-wide model-turn reservations exceed the configured test limit (over-admission).');
  }
  if (total.value < limit) {
    throw new Error('Run-wide model-turn reservations do not reach the configured test limit.');
  }
  const reservations = facts.filter(fact => fact?.kind === 'modelTurnReserved' && fact.limitKind === 'modelTurns');
  const currentReservations = reservations.filter(fact => authorityMismatch(fact, binding, sessionId) === null);
  if (currentReservations.length < limit) {
    throw new Error(`The history has ${currentReservations.length} model-turn reservations that match the current authority; the case needs ${limit}. ${reservations.length - currentReservations.length} other reservations fail the current-authority rule.`);
  }
  const denials = currentAuthorityFacts(
    facts.filter(fact => fact?.kind === 'denied'
      && fact.limitKind === 'modelTurns'
      && fact.stage === 'rootAuthorization'
      && fact.reasonCode === 'limit_reached'
      && fact.ownerDecision?.state === 'available'
      && isUuid(fact.attemptId)),
    binding, sessionId, 'root model-turn denial',
  );
  const noSends = currentAuthorityFacts(
    facts.filter(fact => fact?.kind === 'nativeNoSend'
      && fact.limitKind === 'modelTurns'
      && denials.some(denial => denial.attemptId === fact.attemptId)),
    binding, sessionId, 'model-turn native no-send fact for a denied attempt',
  );
  const noSend = noSends.find(nativeNoSendProven);
  if (!noSend) {
    throw new Error('The model-turn native no-send section is not available with a complete range and zero events.');
  }
  const denial = denials.find(fact => fact.attemptId === noSend.attemptId);
  return { binding, facts, denial, noSend, reservations: currentReservations, total };
}

function hardCreditEvidence(input, pages) {
  const { first, binding, facts } = rootHistory(input, pages);
  const sessionId = input.target.sessionId;
  const limit = first.acceptedLimits?.copilotHardCreditLimit;
  if (typeof limit !== 'number') {
    throw new Error('The accepted copilotHardCreditLimit value is not set.');
  }
  const denials = currentAuthorityFacts(
    facts.filter(fact => fact?.kind === 'denied'
      && fact.limitKind === 'copilotHardCredits'
      && fact.stage === 'nativeTurnBegin'
      && fact.reasonCode === 'hard_credit_limit_reached'
      && fact.unit === 'nanoAiu'
      && fact.ownerDecision?.state === 'available'
      && isUuid(fact.attemptId)
      && isUuid(fact.dispatchId)),
    binding, sessionId, 'hard-credit denial',
  );
  const configured = denials.filter(fact => fact.configuredValue === limit);
  if (configured.length === 0) {
    throw new Error('The current hard-credit denial has a configured value that is different from the accepted copilotHardCreditLimit. The denial is from an old configuration.');
  }
  const exceeded = configured.filter(fact => typeof fact.observedValue === 'number'
    && fact.observedValue >= fact.configuredValue);
  if (exceeded.length === 0) {
    throw new Error('The hard-credit denial observed value is less than its configured value.');
  }
  const noSends = currentAuthorityFacts(
    facts.filter(fact => fact?.kind === 'nativeNoSend'
      && fact.limitKind === 'copilotHardCredits'
      && exceeded.some(denial => denial.dispatchId === fact.dispatchId)),
    binding, sessionId, 'hard-credit native no-send fact for a denied dispatch',
  );
  const sameConfiguration = noSends.filter(fact => exceeded.some(denial => denial.dispatchId === fact.dispatchId
    && denial.configuredValue === fact.configuredValue));
  if (sameConfiguration.length === 0) {
    throw new Error('The hard-credit native no-send fact has a configured value that is different from the denial.');
  }
  const noSend = sameConfiguration.find(nativeNoSendProven);
  if (!noSend) {
    throw new Error('The hard-credit native no-send section is not available with a complete range and zero events.');
  }
  const denial = exceeded.find(fact => fact.dispatchId === noSend.dispatchId
    && fact.configuredValue === noSend.configuredValue);
  const guards = currentAuthorityFacts(
    facts.filter(fact => fact?.kind === 'creditGuardChanged'),
    binding, sessionId, 'credit-guard fact',
  );
  if (guards.some(fact => !Number.isInteger(fact.sequence) || typeof fact.observedValue !== 'number')) {
    throw new Error('A credit-guard fact has no sequence or observed value.');
  }
  guards.sort((left, right) => left.sequence - right.sequence);
  for (let index = 1; index < guards.length; index += 1) {
    if (guards[index].observedValue < guards[index - 1].observedValue) {
      throw new Error('The credit-guard floor decreased in sequence order.');
    }
  }
  if (!guards.some(fact => fact.hardCapExhausted === true)) {
    throw new Error('No current credit-guard fact shows that the hard cap is exhausted.');
  }
  if (input.expectedSoftWarning && !guards.some(fact => fact.softWarningEmitted === true)) {
    throw new Error('No current credit-guard fact shows the expected soft warning.');
  }
  return { binding, facts, denial, noSend, guards, limit };
}

function requireExactNoSend(response, noSend, binding, sessionId, label) {
  const items = response?.history?.items;
  if (response?.history?.exactAttempt !== true || !Array.isArray(items)) {
    throw new Error(`${label} did not return an exact attempt history.`);
  }
  const match = items.find(fact => fact?.factId === noSend.factId);
  if (!match) throw new Error(`${label} did not return the native no-send fact.`);
  const mismatch = authorityMismatch(match, binding, sessionId);
  if (mismatch) {
    throw new Error(`${label} returned a native no-send fact that does not match the current authority. Mismatch: ${mismatch}.`);
  }
  if (!isDeepStrictEqual(match, noSend)) {
    throw new Error(`${label} returned a different native no-send fact.`);
  }
}

function validateModelTurnObservation(input, pages, exact, restExact) {
  const evidence = modelTurnEvidence(input, pages);
  const sessionId = input.target.sessionId;
  requireExactNoSend(exact, evidence.noSend, evidence.binding, sessionId, 'Exact attempt lookup');
  requireExactNoSend(restExact, evidence.noSend, evidence.binding, sessionId, 'The REST exact lookup');
  return {
    attemptId: evidence.denial.attemptId,
    dispatchId: evidence.denial.dispatchId,
    factId: evidence.noSend.factId,
    denialFactId: evidence.denial.factId,
    executionFence: evidence.binding.currentExecutionFence,
    pagesRead: pages.length,
    totalFacts: evidence.facts.length,
    currentModelTurnReservations: evidence.reservations.length,
    runWideModelSlotsReserved: evidence.total.value,
    conclusion: 'The owner recorded a root model-turn denial and a native no-send witness for the same attempt in the current execution fence and selection. This does not cover child sessions or prompt-token limits.',
  };
}

function validateHardCreditObservation(input, pages, exact, restExact) {
  const evidence = hardCreditEvidence(input, pages);
  const sessionId = input.target.sessionId;
  requireExactNoSend(exact, evidence.noSend, evidence.binding, sessionId, 'Exact attempt lookup');
  requireExactNoSend(restExact, evidence.noSend, evidence.binding, sessionId, 'The REST exact lookup');
  return {
    attemptId: evidence.denial.attemptId,
    dispatchId: evidence.denial.dispatchId,
    factId: evidence.noSend.factId,
    denialFactId: evidence.denial.factId,
    executionFence: evidence.binding.currentExecutionFence,
    pagesRead: pages.length,
    totalFacts: evidence.facts.length,
    hardCreditLimitNanoAiu: evidence.limit,
    observedCreditsNanoAiu: evidence.denial.observedValue,
    creditGuardFacts: evidence.guards.length,
    softWarningObserved: evidence.guards.some(fact => fact.softWarningEmitted === true),
    conclusion: 'The owner recorded a root hard-credit denial at native turn begin and a native no-send witness for the same dispatch in the current execution fence and selection. This does not prove the exact spend of other model turns.',
  };
}

export function exactLookupFact(input, pages) {
  if (input.caseId === MODEL_TURN_CASE_ID) return modelTurnEvidence(input, pages).noSend;
  if (input.caseId === HARD_CREDIT_CASE_ID) return hardCreditEvidence(input, pages).noSend;
  const facts = pages.flatMap(page => page.history.items ?? []);
  const rejection = facts.find(fact => matchesToolLimitRejection(fact, input.target.sessionId));
  if (!rejection?.attemptId) throw new Error('The selected session has no retained numeric tool-permission rejection.');
  return rejection;
}

async function sha256(filePath) {
  const bytes = await readFile(filePath);
  return createHash('sha256').update(bytes).digest('hex');
}

function runCommand(cliPath, args, env) {
  const startedAt = new Date();
  const result = spawnSync(cliPath, args, {
    shell: false,
    windowsHide: true,
    encoding: 'utf8',
    timeout: COMMAND_TIMEOUT_MS,
    maxBuffer: MAX_OUTPUT,
    env: childEnvironment(env),
  });
  const completedAt = new Date();
  return {
    startedAt: startedAt.toISOString(),
    completedAt: completedAt.toISOString(),
    durationMs: completedAt.getTime() - startedAt.getTime(),
    exitCode: result.status,
    signal: result.signal,
    stdout: result.stdout ?? '',
    stderr: result.stderr ?? '',
    timedOut: result.error?.code === 'ETIMEDOUT',
    error: result.error?.message ?? null,
  };
}

function parseJson(text, step) {
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${step} did not return valid JSON.`);
  }
}

function requireSuccess(record, step) {
  if (record.error || record.exitCode !== 0 || record.signal || record.timedOut) {
    throw new Error(`${step} did not complete successfully.`);
  }
}

function restBody(record, step) {
  const response = parseJson(record.stdout, step);
  if (response.status !== 200 || response.body === null || typeof response.body !== 'object') {
    throw new Error(`${step} did not return HTTP 200 with a JSON body.`);
  }
  return response.body;
}

export function apiResponseBody(response, step) {
  if (response?.status !== 200
    || response.body === null
    || typeof response.body !== 'object'
    || Array.isArray(response.body)) {
    throw new Error(`${step} did not return HTTP 200 with a JSON body.`);
  }
  return response.body;
}

async function readJsonInput(inputPath) {
  let value;
  try {
    value = JSON.parse(await readFile(inputPath, 'utf8'));
  } catch {
    throw new Error('The case input file is missing or is not valid JSON.');
  }
  return validateInput(value);
}

async function requireNewOutputDirectory(outputPath) {
  const resolved = path.resolve(outputPath);
  try {
    await stat(resolved);
    throw new Error('The evidence directory already exists; choose a new path.');
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
  }
  return resolved;
}

async function persistResult(handle, result) {
  await handle.truncate(0);
  await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
  await handle.sync();
}

function recordArgs(args) {
  return args.map((value, index) => {
    if (args[index - 1] === '--query' && value.startsWith('cursor=')) return 'cursor=[REDACTED]';
    return value;
  });
}

async function runCase(options, env = process.env) {
  const input = await readJsonInput(options.inputPath);
  const cliPath = resolveCliPath(env.AGENTWEAVER_CLI_PATH);
  const cliInfo = await stat(cliPath).catch(() => null);
  if (!cliInfo?.isFile()) throw new Error('AGENTWEAVER_CLI_PATH does not exist as a file.');
  const actualHash = await sha256(cliPath);
  if (actualHash !== input.apphostSha256) throw new Error('The apphost SHA-256 hash does not match the case input.');

  const evidencePath = await requireNewOutputDirectory(options.outputPath);
  const scriptSha256 = await sha256(CASE_SCRIPT_PATH);
  if (options.dryRun) {
    return {
      status: 'validated-not-run',
      caseId: input.caseId,
      sourceCommit: input.sourceCommit,
      targetCommit: input.targetCommit,
      apphostSha256: actualHash,
      scriptSha256,
      network: 'not-used',
      evidence: 'not-created',
    };
  }

  await mkdir(evidencePath);
  const commandsPath = path.join(evidencePath, 'commands.jsonl');
  const resultPath = path.join(evidencePath, 'result.json');
  const handle = await open(resultPath, 'wx', 0o600);
  const common = {
    caseId: input.caseId,
    sourceCommit: input.sourceCommit,
    targetCommit: input.targetCommit,
    apphostSha256: actualHash,
    scriptSha256,
    startedAt: new Date().toISOString(),
    status: 'failed',
  };

  async function execute(id, args, outputKind = 'json') {
    const record = runCommand(cliPath, args, env);
    await appendRedactedJsonLine(commandsPath, {
      id,
      args: recordArgs(args),
      ...record,
    });
    requireSuccess(record, id);
    return outputKind === 'json' ? parseJson(record.stdout, id) : record;
  }

  try {
    const commands = buildCommands(input);
    await execute('version', commands[0].args, 'text');
    const auth = await execute('auth-status', commands[1].args);
    if (auth.isSignedIn !== true || auth.tenantId !== input.target.tenantId) {
      throw new Error('The selected CLI identity is not signed in for the case tenant.');
    }
    await execute('api-operations', commands[2].args);
    await execute('api-describe-runtime-observation', commands[3].args);
    const contextRecord = await execute('rest-authorization-context', commands[4].args, 'text');
    const context = validateAuthorizationContext(
      restBody(contextRecord, 'rest-authorization-context'),
      input,
    );
    const runStatus = apiResponseBody(await execute('run-status', commands[5].args), 'run-status');
    const usage = apiResponseBody(await execute('project-usage', commands[6].args), 'project-usage');

    const pages = [];
    let query = { pageSize: PAGE_SIZE };
    let pageNumber = 0;
    const seenCursors = new Set();
    while (true) {
      pageNumber += 1;
      if (pageNumber > 1000) throw new Error('The history exceeded the 1000-page safety limit.');
      const response = await execute(
        `observation-page-${pageNumber}`,
        observationArgs(input, query),
      );
      const page = apiResponseBody(response, `observation-page-${pageNumber}`);
      pages.push(page);
      const cursor = page.history?.nextCursor;
      if (cursor === null) break;
      if (typeof cursor !== 'string' || cursor.length === 0) throw new Error('The owner returned an invalid history cursor.');
      if (seenCursors.has(cursor)) throw new Error('The owner repeated a history cursor.');
      seenCursors.add(cursor);
      query = { cursor, pageSize: PAGE_SIZE };
    }

    const target = exactLookupFact(input, pages);

    const exactResponse = await execute(
      'observation-exact-attempt',
      observationArgs(input, { attemptId: target.attemptId }),
    );
    const exact = apiResponseBody(exactResponse, 'observation-exact-attempt');
    const restUrl = new URL(
      `/api/v1/projects/${input.target.projectId}/runs/${input.target.runId}/coordination/sessions/${input.target.sessionId}/runtime-observation/v2`,
      input.target.gatewayUrl,
    );
    restUrl.searchParams.set('attemptId', target.attemptId);
    const restArgs = [
      'rest', '--method', 'GET', '--url', `${restUrl.pathname}${restUrl.search}`,
      '--scope', 'api.read', '--scope', 'projects.orchestrator', ...targetArgs(input.target),
    ];
    const restRecord = await execute('rest-exact-attempt', restArgs, 'text');
    const restExact = restBody(restRecord, 'rest-exact-attempt');
    const summary = validateObservation(input, pages, exact, restExact);
    const result = {
      ...common,
      status: 'passed',
      actorId: context.actorId,
      tenantId: context.tenantId,
      runStatus,
      usage,
      ...summary,
      finishedAt: new Date().toISOString(),
    };
    await persistResult(handle, result);
    return result;
  } catch (error) {
    const result = {
      ...common,
      status: 'blocked',
      reason: error instanceof Error ? error.message : 'The case did not complete.',
      finishedAt: new Date().toISOString(),
    };
    await persistResult(handle, result);
    return result;
  } finally {
    await handle.close();
  }
}

async function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
    if (options.help) {
      process.stdout.write('Use --input <case.json> --output <new-evidence-directory> [--dry-run].\n');
      return 0;
    }
    const result = await runCase(options);
    process.stdout.write(`${JSON.stringify(result)}\n`);
    return result.status === 'passed' || result.status === 'validated-not-run' ? 0 : 2;
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    return 2;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === CASE_SCRIPT_PATH) {
  process.exitCode = await main();
}
