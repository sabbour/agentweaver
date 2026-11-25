#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { mkdir, open, readFile, stat, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const CASE_ID = 'checkpoint-recovery-observation';
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 4 * 1024 * 1024;
const EXECUTION_PLAN_MAX_OUTPUT = 12 * 1024 * 1024;
const COMMAND_TIMEOUT_MS = 60_000;
const PAGE_SIZE = 100;
const RECOVERY_EVENT_PAGE_SIZE = 100;
const MAX_PAGES = 1000;
const TARGET_KEYS = [
  'gatewayUrl', 'brokerIssuer', 'clientId', 'apiAudience', 'tenantId',
  'projectId', 'runId', 'sessionId',
];
const TARGET_ENV = [
  'AGENTWEAVER_GATEWAY_URL', 'AGENTWEAVER_BROKER_ISSUER', 'AGENTWEAVER_CLIENT_ID',
  'AGENTWEAVER_API_AUDIENCE', 'AGENTWEAVER_TENANT_ID', 'AGENTWEAVER_PROJECT_ID',
  'AGENTWEAVER_RUN_ID', 'AGENTWEAVER_P1_SETUP_TOKEN', 'AGENTWEAVER_CLI_PATH',
];
const OPERATIONS = [
  'readRunStatus', 'readSessionStatus', 'readDecisions', 'readSessionTree', 'replayRunEvents',
];
const SAVED_PLAN_OPERATIONS = [...OPERATIONS, 'readExecutionPlan'];
const discoveryOperations = input => [
  ...(input.caseKind === 'pending-gate' ? OPERATIONS : SAVED_PLAN_OPERATIONS),
  ...(input.mode === 'after-observation' ? ['readCheckpointRecoveryEvents'] : []),
];
const discoveryCommandCount = input => 3 + discoveryOperations(input).length;
const EVENT_KINDS = new Set([
  'turn', 'tool_call', 'policy_evaluation', 'decision_accepted', 'effect_accepted',
  'artifact_reference', 'cache_reference', 'addressed_message', 'produced_run_capture',
  'runtime_configuration',
]);
const EVENT_PAYLOAD_FIELDS = {
  turn: ['role', 'content'],
  tool_call: ['callId', 'toolName', 'state'],
  policy_evaluation: [
    'actorId', 'tenantId', 'stepId', 'grantId', 'grantRevision', 'purpose', 'actionId',
    'outcome', 'reasonCode', 'fence', 'providerId', 'adapterVersion',
    'optionsSchemaVersion', 'optionsRevision',
  ],
  decision_accepted: ['decisionId', 'decisionType', 'selectedOption', 'acceptedEffectIds'],
  effect_accepted: ['effectId', 'effectType'],
  artifact_reference: ['artifact'],
  cache_reference: ['cache', 'runtimeVersion', 'bindingId'],
  addressed_message: ['messageId', 'sender', 'recipient', 'threadId', 'threadSequence', 'purpose'],
  produced_run_capture: ['capture', 'package'],
  runtime_configuration: ['configuration'],
};

export class CaseError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

export function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const option = argv[index];
    if (option === '--dry-run' || option === '--help') {
      if (options[option] !== undefined) throw new Error(`${option} can only be used once.`);
      options[option] = true;
      continue;
    }
    if (!['--input', '--output', '--before-evidence'].includes(option)
      || typeof argv[index + 1] !== 'string'
      || argv[index + 1].startsWith('--')
      || options[option] !== undefined) {
      throw new Error('Use --input <case.json> --output <new-evidence-directory> [--before-evidence <capture.json>] [--dry-run].');
    }
    options[option] = argv[++index];
  }
  if (!options['--help'] && (!options['--input'] || !options['--output'])) {
    throw new Error('Use --input <case.json> --output <new-evidence-directory> [--before-evidence <capture.json>] [--dry-run].');
  }
  return {
    help: options['--help'] === true,
    dryRun: options['--dry-run'] === true,
    inputPath: options['--input'],
    outputPath: options['--output'],
    beforeEvidencePath: options['--before-evidence'],
  };
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function exactKeys(value, expected) {
  return isObject(value)
    && Object.keys(value).length === expected.length
    && expected.every(key => Object.hasOwn(value, key));
}

function nonEmpty(value) {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= 512;
}

function isIdentifier(value) {
  return typeof value === 'string' && /^[A-Za-z0-9._:-]{1,256}$/.test(value);
}

function isAssociationId(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(value);
}

function isUuid(value) {
  return typeof value === 'string'
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function isOwnerUuid(value) {
  return isUuid(value) && value !== '00000000-0000-0000-0000-000000000000';
}

function sameOwnerUuid(left, right) {
  return isOwnerUuid(left) && isOwnerUuid(right) && left.toLowerCase() === right.toLowerCase();
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
    || url.username || url.password || url.search || url.hash) {
    throw new Error(`The ${name} must be an HTTPS URL without user information, query, or fragment.`);
  }
  if (originOnly && url.pathname !== '/') throw new Error('The Gateway URL must be an origin without a path.');
  return originOnly ? url.origin : value;
}

export function validateInput(value) {
  const common = [
    'caseId', 'mode', 'caseKind', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'expectedActorId', 'expectedRecoveryOperationId',
    'target',
  ];
  if (value?.caseKind === 'pending-gate') common.push('expectedGateRequestId');
  if (value?.caseKind === 'committed-effect') common.push('expectedEffectOperationId');
  const beforeKeys = common;
  const afterKeys = [...common, 'beforeCaptureSha256'];
  const allowedAfterKeys = new Set(afterKeys);
  const modeIsValid = value?.mode === 'before-capture'
    ? exactKeys(value, beforeKeys)
    : value?.mode === 'after-observation'
      && isObject(value)
      && Object.keys(value).every(key => allowedAfterKeys.has(key))
      && common.every(key => Object.hasOwn(value, key))
      && (Object.keys(value).length === common.length
        || Object.keys(value).length === afterKeys.length);
  if (!modeIsValid
    || value.caseId !== CASE_ID
    || !['pending-gate', 'child-join', 'committed-effect'].includes(value.caseKind)
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || !nonEmpty(value.expectedActorId)
    || !isUuid(value.expectedRecoveryOperationId)
    || value.caseKind !== 'pending-gate' && !isOwnerUuid(value.expectedRecoveryOperationId)
    || value.caseKind === 'pending-gate' && !isIdentifier(value.expectedGateRequestId)
    || value.caseKind === 'committed-effect' && (!isOwnerUuid(value.expectedEffectOperationId)
      || value.expectedEffectOperationId.toLowerCase() === value.expectedRecoveryOperationId.toLowerCase())
    || value.mode === 'after-observation' && value.beforeCaptureSha256 !== undefined
      && !/^[a-f0-9]{64}$/i.test(value.beforeCaptureSha256)
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))
    || !isIdentifier(value.target.projectId)
    || !isIdentifier(value.target.runId)
    || !isIdentifier(value.target.sessionId)) {
    throw new Error('The case input does not match the checkpoint recovery observation schema.');
  }
  return {
    ...value,
    sourceCommit: value.sourceCommit.toLowerCase(),
    targetCommit: value.targetCommit.toLowerCase(),
    apphostSha256: value.apphostSha256.toLowerCase(),
    expectedRecoveryOperationId: value.expectedRecoveryOperationId.toLowerCase(),
    ...(value.caseKind === 'committed-effect'
      ? { expectedEffectOperationId: value.expectedEffectOperationId.toLowerCase() } : {}),
    beforeCaptureSha256: value.beforeCaptureSha256?.toLowerCase(),
    target: {
      ...value.target,
      gatewayUrl: validateUrl(value.target.gatewayUrl, 'Gateway URL', true),
      brokerIssuer: validateUrl(value.target.brokerIssuer, 'Broker issuer', false),
    },
  };
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

function invokeArgs(input, operation, query = {}) {
  const { target } = input;
  const args = [
    'api', 'invoke', operation,
    '--path', `projectId=${target.projectId}`,
    '--path', `runId=${target.runId}`,
    '--scope', 'api.read', '--scope', 'projects.orchestrator',
    ...targetArgs(target),
  ];
  if (['readSessionStatus', 'readDecisions', 'readSessionTree', 'readExecutionPlan',
    'readCheckpointRecoveryEvents'].includes(operation)) {
    args.splice(5, 0, '--path', `sessionId=${target.sessionId}`);
  }
  if (operation === 'readCheckpointRecoveryEvents') {
    args.splice(7, 0, '--path', `operationId=${input.expectedRecoveryOperationId}`);
  }
  for (const [key, value] of Object.entries(query)) args.push('--query', `${key}=${value}`);
  return args;
}

function restArgs(input, route) {
  return [
    'rest', '--method', 'GET', '--url', route,
    '--scope', 'api.read', '--scope', 'projects.orchestrator',
    ...targetArgs(input.target),
  ];
}

export function buildCommands(input, legacy = false) {
  const unbound = targetArgs(input.target, false);
  const bound = targetArgs(input.target);
  const operations = legacy
    ? [...OPERATIONS, ...(input.mode === 'after-observation' ? ['readCheckpointRecoveryEvents'] : [])]
    : discoveryOperations(input);
  return [
    { id: 'version', args: ['--version'] },
    { id: 'auth-status', args: ['auth', 'status', '--scope', 'api.read', '--scope', 'projects.orchestrator', ...bound] },
    { id: 'api-operations', args: ['api', 'operations', ...unbound] },
    ...operations.map(operation => ({
      id: `api-describe-${operation}`,
      args: ['api', 'describe', operation, ...unbound],
    })),
    {
      id: 'rest-authorization-context',
      args: restArgs(input, '/api/v1/authorization/context'),
    },
    { id: 'run-status', args: invokeArgs(input, 'readRunStatus') },
    { id: 'run-status-rest', args: restArgs(input,
      `/api/v1/projects/${encodeURIComponent(input.target.projectId)}/runs/${encodeURIComponent(input.target.runId)}/coordination/status`) },
    { id: 'session-status', args: invokeArgs(input, 'readSessionStatus') },
    { id: 'session-status-rest', args: restArgs(input,
      `/api/v1/projects/${encodeURIComponent(input.target.projectId)}/runs/${encodeURIComponent(input.target.runId)}/coordination/sessions/${encodeURIComponent(input.target.sessionId)}/status`) },
    { id: 'decisions', args: invokeArgs(input, 'readDecisions') },
    { id: 'session-tree', args: invokeArgs(input, 'readSessionTree') },
    ...(!legacy && input.caseKind !== 'pending-gate' ? [{
      id: 'execution-plan', args: invokeArgs(input, 'readExecutionPlan'),
    }] : []),
  ];
}

function childEnvironment(env) {
  // The case supplies target selectors explicitly; ambient values must not redirect a command.
  const childEnv = { ...env };
  for (const key of Object.keys(childEnv)) {
    if (TARGET_ENV.includes(key.toUpperCase())) delete childEnv[key];
  }
  return childEnv;
}

export function runCommand(cliPath, args, env) {
  const startedAt = new Date();
  const result = spawnSync(cliPath, args, {
    shell: false,
    windowsHide: true,
    encoding: 'utf8',
    timeout: COMMAND_TIMEOUT_MS,
    maxBuffer: args.includes('readExecutionPlan') ? EXECUTION_PLAN_MAX_OUTPUT : MAX_OUTPUT,
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
    throw new CaseError('blocked', `${step} did not return valid JSON.`);
  }
}

function requireSuccess(record, step) {
  if (record.error || record.exitCode !== 0 || record.signal || record.timedOut) {
    throw new CaseError('blocked', `${step} did not complete successfully.`);
  }
}

export function apiResponseBody(response, step) {
  if (response?.status !== 200 || !isObject(response.body)) {
    throw new CaseError('blocked', `${step} did not return HTTP 200 with a JSON body.`);
  }
  return response.body;
}

function restBody(record, step) {
  const response = parseJson(record.stdout, step);
  if (response.status !== 200 || !isObject(response.body)) {
    throw new CaseError('blocked', `${step} did not return HTTP 200 with a JSON body.`);
  }
  return response.body;
}

function validateAuthorizationContext(context, input) {
  if (context?.actorId !== input.expectedActorId
    || context?.tenantId !== input.target.tenantId
    || context?.boundProjectId !== input.target.projectId
    || context?.boundRunId !== input.target.runId) {
    throw new CaseError('blocked', 'The authorization context does not match the case actor, tenant, project, and run.');
  }
  return context;
}

export function compareStatusApiAndRest(apiValue, restValue, label, mismatchStatus = 'failed') {
  if (!isDeepStrictEqual(apiValue, restValue)) {
    throw new CaseError(mismatchStatus, `${label} returned different API and REST status bodies.`);
  }
}

function gateFacts(decisions, sessionStatus, expectedRequestId, input) {
  const gate = decisions?.pendingGate;
  const blockers = sessionStatus?.blockers;
  const blockerMatches = Array.isArray(blockers)
    ? blockers.filter(item => item?.requestId === expectedRequestId)
    : [];
  if (!isObject(gate)
    || gate.requestId !== expectedRequestId
    || gate.authorizedActorId !== input.expectedActorId
    || !Array.isArray(gate.allowedChoices)
    || typeof gate.allowsFreeform !== 'boolean'
    || blockerMatches.length !== 1
    || !isDeepStrictEqual(gate.allowedChoices, blockerMatches[0].choices)
    || gate.allowsFreeform !== blockerMatches[0].allowsFreeform) {
    throw new CaseError('failed', 'The expected gate is not pending with matching decision and status fields.');
  }
  return {
    requestId: gate.requestId,
    actorId: gate.authorizedActorId,
    choices: gate.allowedChoices,
    allowsFreeform: gate.allowsFreeform,
    fence: gate.fence,
  };
}

function validateRootBindings(runStatus, sessionStatus, decisions, tree, input) {
  const target = input.target;
  const rootNode = Array.isArray(tree?.nodes)
    ? tree.nodes.find(node => node?.identity?.sessionId === target.sessionId)
    : null;
  if (runStatus?.projectId !== target.projectId
    || runStatus?.runId !== target.runId
    || runStatus?.rootSessionId !== target.sessionId
    || sessionStatus?.identity?.projectId !== target.projectId
    || sessionStatus?.identity?.runId !== target.runId
    || sessionStatus?.identity?.sessionId !== target.sessionId
    || sessionStatus?.rootSessionId !== target.sessionId
    || tree?.rootSessionId !== target.sessionId
    || rootNode?.identity?.projectId !== target.projectId
    || rootNode?.identity?.runId !== target.runId
    || rootNode?.rootSessionId !== target.sessionId
    || rootNode?.executionFence !== runStatus.executionFence
    || sessionStatus.executionFence !== runStatus.executionFence
    || decisions?.executionFence !== runStatus.executionFence) {
    throw new CaseError('blocked', 'Run, root session, decisions, and tree do not share the selected project, run, root, and fence.');
  }
  return rootNode;
}

function validObjectReference(value) {
  return isObject(value)
    && nonEmpty(value.key)
    && nonEmpty(value.purpose)
    && (value.byteLength === undefined || value.byteLength === null
      || Number.isInteger(value.byteLength) && value.byteLength >= 0);
}

function validSessionIdentity(value) {
  return isObject(value)
    && isIdentifier(value.projectId)
    && isIdentifier(value.runId)
    && isIdentifier(value.sessionId);
}

function sameUrl(left, right, name, originOnly) {
  const leftUrl = new URL(validateUrl(left, name, originOnly));
  const rightUrl = new URL(validateUrl(right, name, originOnly));
  return originOnly ? leftUrl.origin === rightUrl.origin : leftUrl.href === rightUrl.href;
}

function sameTarget(left, right) {
  try {
    return isObject(left)
      && TARGET_KEYS.every(key => key === 'gatewayUrl'
        ? sameUrl(left[key], right[key], 'Gateway URL', true)
        : key === 'brokerIssuer'
          ? sameUrl(left[key], right[key], 'Broker issuer', false)
        : left[key] === right[key]);
  } catch {
    return false;
  }
}

function validEventPayload(payload) {
  if (EVENT_PAYLOAD_FIELDS[payload.kind].some(field => !Object.hasOwn(payload, field))) return false;
  switch (payload.kind) {
    case 'turn':
      return nonEmpty(payload.role) && validObjectReference(payload.content);
    case 'tool_call':
      return ['callId', 'toolName', 'state'].every(field => nonEmpty(payload[field]))
        && (payload.arguments === undefined || payload.arguments === null
          || validObjectReference(payload.arguments))
        && (payload.result === undefined || payload.result === null
          || validObjectReference(payload.result));
    case 'policy_evaluation':
      return [
        'actorId', 'tenantId', 'stepId', 'grantId', 'grantRevision', 'purpose', 'actionId',
        'outcome', 'reasonCode', 'providerId', 'adapterVersion', 'optionsRevision',
      ].every(field => nonEmpty(payload[field]))
        && Number.isInteger(payload.fence)
        && Number.isInteger(payload.optionsSchemaVersion);
    case 'decision_accepted':
      return ['decisionId', 'decisionType', 'selectedOption'].every(field => nonEmpty(payload[field]))
        && Array.isArray(payload.acceptedEffectIds)
        && payload.acceptedEffectIds.every(nonEmpty)
        && (payload.rationale === undefined || payload.rationale === null
          || validObjectReference(payload.rationale));
    case 'effect_accepted':
      return nonEmpty(payload.effectId) && nonEmpty(payload.effectType)
        && (payload.receipt === undefined || payload.receipt === null
          || validObjectReference(payload.receipt));
    case 'artifact_reference':
      return validObjectReference(payload.artifact);
    case 'cache_reference':
      return validObjectReference(payload.cache)
        && nonEmpty(payload.runtimeVersion) && nonEmpty(payload.bindingId);
    case 'addressed_message':
      return isUuid(payload.messageId)
        && validSessionIdentity(payload.sender)
        && validSessionIdentity(payload.recipient)
        && isUuid(payload.threadId)
        && Number.isInteger(payload.threadSequence) && payload.threadSequence > 0
        && nonEmpty(payload.purpose);
    case 'produced_run_capture':
      return isObject(payload.capture)
        && nonEmpty(payload.capture.captureId)
        && isUuid(payload.capture.eventId)
        && validSessionIdentity(payload.capture.identity)
        && validObjectReference(payload.package);
    case 'runtime_configuration':
      return payload.configuration === null || validObjectReference(payload.configuration);
    default:
      return false;
  }
}

export function validateJournalPages(pages, input) {
  if (!Array.isArray(pages) || pages.length === 0 || pages.length > MAX_PAGES) {
    throw new CaseError('blocked', 'The session journal has no complete page set.');
  }
  const eventIds = new Set();
  let lastPosition = 0;
  for (let pageIndex = 0; pageIndex < pages.length; pageIndex += 1) {
    const page = pages[pageIndex];
    if (!isObject(page)
      || !Array.isArray(page.events)
      || typeof page.hasMore !== 'boolean'
      || !(page.nextCursor === null || typeof page.nextCursor === 'string')) {
      throw new CaseError('blocked', 'The session journal returned an invalid page shape.');
    }
    if (page.hasMore !== (pageIndex < pages.length - 1)
      || page.hasMore && (page.events.length === 0
        || !page.nextCursor
        || page.nextCursor === pages[pageIndex - 1]?.nextCursor)) {
      throw new CaseError('blocked', 'The session journal pages are incomplete or have invalid cursors.');
    }
    for (const event of page.events) {
      if (!isObject(event)
        || !isUuid(event.eventId)
        || !Number.isInteger(event.schemaVersion) || event.schemaVersion < 1
        || !Number.isInteger(event.eventVersion) || event.eventVersion < 1
        || !Number.isInteger(event.position) || event.position !== lastPosition + 1
        || typeof event.occurredAt !== 'string' || Number.isNaN(Date.parse(event.occurredAt))
        || event.identity?.projectId !== input.target.projectId
        || event.identity?.runId !== input.target.runId
        || !isIdentifier(event.identity?.sessionId)
        || !isObject(event.payload)
        || !EVENT_KINDS.has(event.payload.kind)
        || !validEventPayload(event.payload)
        || !Array.isArray(event.objectReferences)
        || eventIds.has(event.eventId)) {
        throw new CaseError('blocked', 'The session journal has an invalid envelope, scope, order, or duplicate event.');
      }
      eventIds.add(event.eventId);
      lastPosition = event.position;
    }
  }
  return { pagesRead: pages.length, eventCount: eventIds.size, lastPosition };
}

const RECOVERY_PROOF_FIELDS = [
  'sourceCheckpointId', 'sourceCheckpointRevision', 'previousExecutionFence',
  'executionFence', 'previousRuntimeInstanceId', 'runtimeInstanceId',
  'previousRegistrationRevision', 'registrationRevision', 'recoveryMaterialMode',
];
const RECOVERY_EVENT_STATUS_REQUIRED = [
  'phase', 'sourceCheckpointId', 'sourceCheckpointRevision', 'previousExecutionFence',
  'executionFence', 'previousRuntimeInstanceId', 'previousRegistrationRevision',
];
const RECOVERY_EVENT_STATUS_OPTIONAL = [
  'currentStep', 'reasonCode', 'recoveryMaterialMode', 'boundaryKind', 'dispatchHeld',
  'sourceCheckpointHash', 'recoveryCheckpointId', 'recoveryCheckpointRevision',
  'recoveryCheckpointHash', 'decisionStateVersion', 'runtimeInstanceId',
  'registrationRevision', 'childManifestSha256',
];

function validRecoveryEventStatus(status) {
  const allowed = new Set([...RECOVERY_EVENT_STATUS_REQUIRED, ...RECOVERY_EVENT_STATUS_OPTIONAL]);
  return isObject(status)
    && RECOVERY_EVENT_STATUS_REQUIRED.every(field => Object.hasOwn(status, field))
    && Object.keys(status).every(field => allowed.has(field))
    && ['pending', 'ready', 'blocked', 'indeterminate', 'failed'].includes(status.phase)
    && nonEmpty(status.sourceCheckpointId)
    && safePositive(status.sourceCheckpointRevision)
    && safePositive(status.previousExecutionFence)
    && safePositive(status.executionFence)
    && isOwnerUuid(status.previousRuntimeInstanceId)
    && safePositive(status.previousRegistrationRevision);
}

function validReadyRecoveryEventStatus(status) {
  return validRecoveryEventStatus(status)
    && status.phase === 'ready'
    && Object.hasOwn(status, 'runtimeInstanceId')
    && isOwnerUuid(status.runtimeInstanceId)
    && Object.hasOwn(status, 'registrationRevision')
    && safePositive(status.registrationRevision)
    && Object.hasOwn(status, 'recoveryMaterialMode')
    && ['nativeCache', 'journalRebuild'].includes(status.recoveryMaterialMode);
}

function recoveryProofBinding(runStatus, sessionStatus, before, input) {
  const run = runStatus?.checkpointRecovery;
  const session = sessionStatus?.checkpointRecovery;
  if (!isObject(run) || !isObject(session)
    || run.operationId !== input.expectedRecoveryOperationId
    || session.operationId !== input.expectedRecoveryOperationId
    || RECOVERY_PROOF_FIELDS.some(field => !Object.hasOwn(run, field)
      || !Object.hasOwn(session, field)
      || !isDeepStrictEqual(run[field], session[field]))) {
    throw new CaseError('blocked', 'The recovery proof is missing or not bound on run and root-session status.');
  }
  if (run.phase !== 'ready' || session.phase !== 'ready') {
    throw new CaseError('blocked', 'The selected recovery proof is not ready on both status surfaces.');
  }
  for (const field of [
    'sourceCheckpointRevision', 'previousExecutionFence', 'executionFence',
    'previousRegistrationRevision', 'registrationRevision',
  ]) requireSafe(run[field], `Recovery ${field}`);
  if (!nonEmpty(run.sourceCheckpointId)
    || !isOwnerUuid(run.previousRuntimeInstanceId) || !isOwnerUuid(run.runtimeInstanceId)
    || !['nativeCache', 'journalRebuild'].includes(run.recoveryMaterialMode)) {
    throw new CaseError('blocked', 'The recovery proof binding is incomplete or unsupported.');
  }
  if (run.previousExecutionFence !== before.outputs.runStatus.executionFence) {
    throw new CaseError('blocked', 'The recovery proof is not bound to the captured fence.');
  }
  if (run.previousRuntimeInstanceId.toLowerCase() === run.runtimeInstanceId.toLowerCase()
    || run.executionFence !== run.previousExecutionFence + 1
    || run.executionFence !== runStatus.executionFence
    || run.executionFence !== sessionStatus.executionFence) {
    throw new CaseError('failed', 'The ready recovery status has an unchanged runtime or invalid fence transition.');
  }
  if (input.caseKind !== 'pending-gate') {
    const saved = savedChildFacts(before.outputs, input);
    if (run.sourceCheckpointId !== saved.checkpointId
      || run.sourceCheckpointRevision !== saved.checkpointRevision) {
      throw new CaseError('blocked', 'The recovery proof is not bound to the captured source checkpoint.');
    }
  }
  return Object.fromEntries([
    ['operationId', input.expectedRecoveryOperationId],
    ...RECOVERY_PROOF_FIELDS.map(field => [field, run[field]]),
  ]);
}

function extractRecoveryProofBinding(runStatus, sessionStatus, input) {
  const run = runStatus?.checkpointRecovery;
  const session = sessionStatus?.checkpointRecovery;
  if (!isObject(run) || !isObject(session)
    || run.operationId !== input.expectedRecoveryOperationId
    || session.operationId !== input.expectedRecoveryOperationId
    || RECOVERY_PROOF_FIELDS.some(field => !Object.hasOwn(run, field)
      || !Object.hasOwn(session, field)
      || !isDeepStrictEqual(run[field], session[field]))) {
    throw new CaseError('blocked', 'The recovery proof is missing or not bound on run and root-session status.');
  }
  return Object.fromEntries([
    ['operationId', input.expectedRecoveryOperationId],
    ...RECOVERY_PROOF_FIELDS.map(field => [field, run[field]]),
  ]);
}

export function committedRecoveryEventProof(snapshot, before, input) {
  if (snapshot.recoveryEventError) {
    return {
      status: 'blocked',
      reason: snapshot.recoveryEventError.message ?? 'The recovery event read is unavailable.',
    };
  }
  try {
    const initialBinding = extractRecoveryProofBinding(
      snapshot.runStatus, snapshot.sessionStatus, input,
    );
    const finalBinding = extractRecoveryProofBinding(
      snapshot.finalAuthority.runStatus, snapshot.finalAuthority.sessionStatus, input,
    );
    if (!isDeepStrictEqual(initialBinding, finalBinding)) {
      throw new CaseError(
        'blocked', 'The recovery status binding changed while event pages were read.',
      );
    }
    const initial = recoveryProofBinding(
      snapshot.runStatus, snapshot.sessionStatus, before, input,
    );
    recoveryProofBinding(
      snapshot.finalAuthority.runStatus, snapshot.finalAuthority.sessionStatus, before, input,
    );
    return validateRecoveryEventPages(snapshot.recoveryEvents, initial, input);
  } catch (error) {
    return {
      status: error instanceof CaseError ? error.status : 'blocked',
      reason: error instanceof Error ? error.message : 'The recovery event proof is unavailable.',
    };
  }
}

function validateRecoveryEventPages(pages, binding, input) {
  if (!Array.isArray(pages) || pages.length === 0 || pages.length > MAX_PAGES) {
    throw new CaseError('blocked', 'The checkpoint recovery event read has no complete page set.');
  }
  const operationId = input.expectedRecoveryOperationId;
  const firstWatermark = pages[0]?.watermark;
  const seenIds = new Set();
  const seenCursors = new Set();
  const events = [];
  let lastSequence = 0;
  for (let pageIndex = 0; pageIndex < pages.length; pageIndex += 1) {
    const page = pages[pageIndex];
    if (!exactKeys(page, ['session', 'operationId', 'watermark', 'events', 'nextCursor', 'hasMore'])
      || !validSessionIdentity(page.session)
      || !Array.isArray(page.events)
      || page.events.length > 200
      || !safePositive(page.watermark)
      || typeof page.hasMore !== 'boolean'
      || !(page.nextCursor === null || typeof page.nextCursor === 'string')) {
      throw new CaseError('blocked', 'The checkpoint recovery events returned an invalid page envelope.');
    }
    if (page.session.projectId !== input.target.projectId
      || page.session.runId !== input.target.runId
      || page.session.sessionId !== input.target.sessionId
      || page.operationId !== operationId) {
      throw new CaseError('blocked', 'The checkpoint recovery event page is not bound to the selected operation.');
    }
    if (page.watermark !== firstWatermark) {
      throw new CaseError('blocked', 'The checkpoint recovery event watermark changed during paging.');
    }
    if (page.hasMore !== (pageIndex < pages.length - 1)
      || page.hasMore && (!page.nextCursor || seenCursors.has(page.nextCursor))
      || !page.hasMore && page.nextCursor !== null) {
      throw new CaseError('blocked', 'The checkpoint recovery event pages have invalid or incomplete cursors.');
    }
    if (page.nextCursor) seenCursors.add(page.nextCursor);
    for (const event of page.events) {
      const baseKeys = [
        'eventId', 'sequence', 'eventType', 'eventVersion', 'occurredAt', 'operationId', 'status',
      ];
      if (!isObject(event)
        || ![baseKeys, [...baseKeys, 'runtimeEffectsState']].some(keys => exactKeys(event, keys))
        || !isOwnerUuid(event.eventId)
        || !safePositive(event.sequence)
        || event.sequence > firstWatermark
        || !nonEmpty(event.eventType)
        || event.eventVersion !== 1
        || typeof event.occurredAt !== 'string' || Number.isNaN(Date.parse(event.occurredAt))
        || !validRecoveryEventStatus(event.status)) {
        throw new CaseError('blocked', 'The checkpoint recovery event page contains an invalid event.');
      }
      if (event.operationId !== operationId) {
        throw new CaseError('blocked', 'A checkpoint recovery event is not bound to the selected operation.');
      }
      if (seenIds.has(event.eventId)) {
        throw new CaseError('blocked', 'The checkpoint recovery event pages contain a duplicate event ID.');
      }
      if (event.sequence <= lastSequence) {
        throw new CaseError('blocked', 'Checkpoint recovery event sequences do not increase across pages.');
      }
      seenIds.add(event.eventId);
      lastSequence = event.sequence;
      events.push(event);
    }
  }
  const admissions = events.filter(event => event.eventId === operationId && event.status.phase === 'pending');
  if (admissions.length !== 1) {
    throw new CaseError('blocked', 'The selected recovery operation has no unique pending admission event.');
  }
  const allowedAdmissionTypes = input.caseKind === 'pending-gate'
    ? new Set(['orchestrator.run.checkpoint_recovery_pending'])
    : new Set([
      'orchestrator.run.checkpoint_recovery_blocked',
      'orchestrator.run.checkpoint_recovery_indeterminate',
    ]);
  if (!allowedAdmissionTypes.has(admissions[0].eventType)) {
    throw new CaseError('blocked', 'The recovery admission event has the wrong type for this checkpoint boundary.');
  }
  const ready = events.filter(event =>
    event.eventVersion === 1
    && event.eventType === 'orchestrator.run.checkpoint_recovery_ready'
    && event.status.phase === 'ready');
  if (ready.length !== 1 || ready[0].operationId !== operationId) {
    throw new CaseError('blocked', 'The selected recovery operation has no unique ready witness.');
  }
  if (admissions[0].sequence >= ready[0].sequence) {
    throw new CaseError('blocked', 'The recovery admission does not precede the ready witness.');
  }
  if (!validReadyRecoveryEventStatus(ready[0].status)) {
    throw new CaseError('blocked', 'The ready checkpoint recovery witness has invalid or missing fields.');
  }
  for (const field of RECOVERY_PROOF_FIELDS) {
    if (ready[0].status[field] !== binding[field]) {
      throw new CaseError('failed', 'The selected recovery event contradicts the stable status binding.');
    }
  }
  for (const field of [
    'sourceCheckpointId', 'sourceCheckpointRevision', 'previousExecutionFence',
    'executionFence', 'previousRuntimeInstanceId', 'previousRegistrationRevision',
  ]) {
    if (admissions[0].status[field] !== ready[0].status[field]) {
      throw new CaseError('failed', 'The ready witness changed the immutable admission binding.');
    }
  }
  return {
    status: 'passed',
    operationId,
    witnessEventId: ready[0].eventId,
    witnessSequence: ready[0].sequence,
    pagesRead: pages.length,
    watermark: firstWatermark,
  };
}

export { validateRecoveryEventPages };

function statusBinding(runStatus, sessionStatus, decisions) {
  return {
    run: {
      projectId: runStatus?.projectId,
      runId: runStatus?.runId,
      rootSessionId: runStatus?.rootSessionId,
      executionFence: runStatus?.executionFence,
      stateVersion: runStatus?.stateVersion,
      executionState: runStatus?.executionState,
    },
    session: {
      projectId: sessionStatus?.identity?.projectId,
      runId: sessionStatus?.identity?.runId,
      sessionId: sessionStatus?.identity?.sessionId,
      rootSessionId: sessionStatus?.rootSessionId,
      executionFence: sessionStatus?.executionFence,
      stateVersion: sessionStatus?.stateVersion,
      lifecycle: sessionStatus?.lifecycle,
    },
    decision: {
      stateVersion: decisions?.stateVersion,
      executionFence: decisions?.executionFence,
      gate: decisions?.pendingGate && {
        requestId: decisions.pendingGate.requestId,
        authorizedActorId: decisions.pendingGate.authorizedActorId,
        fence: decisions.pendingGate.fence,
        allowedChoices: decisions.pendingGate.allowedChoices,
        allowsFreeform: decisions.pendingGate.allowsFreeform,
      },
    },
  };
}

function commandJson(record, step) {
  return parseJson(record.stdout, step);
}

function commandRecord(records, index, id, args) {
  const record = records[index];
  const expectedArgs = logArgs(args);
  let sameArgs = Array.isArray(record?.args) && record.args.length === expectedArgs.length;
  for (let argumentIndex = 0; sameArgs && argumentIndex < expectedArgs.length; argumentIndex += 1) {
    const previous = expectedArgs[argumentIndex - 1];
    if (previous === '--gateway-url' || previous === '--broker-issuer') {
      try {
        sameArgs = sameUrl(
          record.args[argumentIndex],
          expectedArgs[argumentIndex],
          previous === '--gateway-url' ? 'Gateway URL' : 'Broker issuer',
          previous === '--gateway-url',
        );
      } catch {
        sameArgs = false;
      }
    } else {
      sameArgs = record.args[argumentIndex] === expectedArgs[argumentIndex];
    }
  }
  if (!isObject(record)
    || record.id !== id
    || !sameArgs
    || record.exitCode !== 0
    || record.signal !== null
    || record.timedOut !== false
    || record.error !== null
    || typeof record.stdout !== 'string') {
    throw new CaseError('blocked', `The retained ${id} command record is missing or does not match its command.`);
  }
  return record;
}

function canonical(value) {
  if (Array.isArray(value)) {
    return value.map(canonical).sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
  }
  if (isObject(value)) {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  }
  return value;
}

function sameFacts(left, right) {
  return isDeepStrictEqual(canonical(left), canonical(right));
}

function safePositive(value) {
  return Number.isSafeInteger(value) && value >= 1;
}

function requireSafe(value, label) {
  if (!safePositive(value)) throw new CaseError('blocked', `${label} is missing or is not a positive safe integer.`);
}

function nextSafe(value, label) {
  requireSafe(value, label);
  if (value === Number.MAX_SAFE_INTEGER) throw new CaseError('blocked', `${label} cannot advance safely.`);
  return value + 1;
}

function isHash(value) {
  return typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value);
}

function childAuthority(context, input) {
  validateAuthorizationContext(context, input);
  requireSafe(context.membershipRevision, 'Membership revision');
  if (!nonEmpty(context.issuer) || !safePositive(context.contractVersion)
    || !Array.isArray(context.effectiveAuthority)
    || context.effectiveAuthority.some(item => !nonEmpty(item?.resourceType)
      || !nonEmpty(item?.resourceId) || !Array.isArray(item?.permissions)
      || item.permissions.some(grant => !nonEmpty(grant?.permission) || !safePositive(grant?.roleRevision)))) {
    throw new CaseError('blocked', 'The effective authorization context is incomplete.');
  }
  let issuer;
  try {
    issuer = new URL(context.issuer).href;
  } catch {
    throw new CaseError('blocked', 'The authorization issuer is invalid.');
  }
  return { ...context, issuer };
}

function childSnapshotFacts(snapshot, input) {
  const { runStatus, sessionStatus, decisions, tree, context } = snapshot;
  childAuthority(context, input);
  const root = validateRootBindings(runStatus, sessionStatus, decisions, tree, input);
  if (sessionStatus.kind !== 'coordinator' || sessionStatus.lifecycle !== 'active'
    || root.kind !== 'coordinator' || root.lifecycle !== 'active'
    || root.detached !== false || root.parentSessionId !== null) {
    throw new CaseError('blocked', 'The selected root is not an active attached coordinator.');
  }
  for (const [label, value] of [
    ['Run fence', runStatus.executionFence], ['Run state version', runStatus.stateVersion],
    ['Root state version', sessionStatus.stateVersion], ['Decision version', decisions.stateVersion],
  ]) requireSafe(value, label);
  for (const field of [
    'sourceCheckpointRevision', 'recoveryCheckpointRevision', 'previousExecutionFence',
    'executionFence', 'previousRegistrationRevision', 'registrationRevision', 'decisionStateVersion',
  ]) {
    const value = runStatus.checkpointRecovery?.[field];
    if (value != null) requireSafe(value, `Recovery ${field}`);
  }
  const projection = sessionStatus.executionRecovery;
  if (!isObject(projection) || projection.state !== 'available'
    || !nonEmpty(projection.checkpointId) || !nonEmpty(projection.workPlanId)
    || !Array.isArray(projection.steps) || !Array.isArray(projection.pendingChildDispatches)
    || !Array.isArray(projection.platformGateOperations) || !Array.isArray(projection.failedDependencyIds)
    || !Array.isArray(tree.nodes)) {
    throw new CaseError('blocked', 'The execution projection or session tree is incomplete.');
  }
  for (const [label, value] of [
    ['Checkpoint revision', projection.checkpointRevision],
    ['Checkpoint decision version', projection.checkpointDecisionStateVersion],
    ['Projection current decision version', projection.currentDecisionStateVersion],
  ]) requireSafe(value, label);
  if (projection.currentDecisionStateVersion !== decisions.stateVersion
    || projection.checkpointDecisionStateVersion !== decisions.stateVersion) {
    throw new CaseError('blocked', 'The checkpoint and current decision versions are not bound.');
  }
  const selectionHash = sessionStatus.runtimeStartup?.acceptedSelectionHash;
  for (const [label, value] of [
    ['Runtime registration revision', sessionStatus.runtimeStartup?.registrationRevision],
    ['Runtime startup fence', sessionStatus.runtimeStartup?.executionFence],
  ]) if (value != null) requireSafe(value, label);
  if (selectionHash != null && !isHash(selectionHash)) {
    throw new CaseError('blocked', 'The accepted selection hash is invalid.');
  }
  const nodes = new Map();
  for (const node of tree.nodes) {
    if (!validSessionIdentity(node?.identity)
      || node.identity.projectId !== input.target.projectId || node.identity.runId !== input.target.runId
      || node.rootSessionId !== input.target.sessionId || !nonEmpty(node.kind)
      || typeof node.detached !== 'boolean' || !nonEmpty(node.lifecycle)) {
      throw new CaseError('blocked', 'The session tree has an incomplete node.');
    }
    requireSafe(node.executionFence, 'Tree node fence');
    requireSafe(node.stateVersion, 'Tree node state version');
    if (nodes.has(node.identity.sessionId)) {
      throw new CaseError('failed', `Duplicate tree identity: ${node.identity.sessionId}.`);
    }
    nodes.set(node.identity.sessionId, node);
  }
  const steps = new Map();
  for (const step of projection.steps) {
    if (!nonEmpty(step?.stepId) || !nonEmpty(step.checkpointStatus)
      || !(step.associationId === null || nonEmpty(step.associationId))
      || !(step.workItemId === null || nonEmpty(step.workItemId))) {
      throw new CaseError('blocked', 'The execution projection has an incomplete step.');
    }
    if (step.associationId !== null) {
      if (steps.has(step.associationId)) {
        throw new CaseError('failed', `Duplicate association: ${step.associationId}.`);
      }
      steps.set(step.associationId, step);
    }
  }
  const tuples = new Map();
  const children = new Set();
  const messages = new Set();
  for (const tuple of projection.pendingChildDispatches) {
    if (!nonEmpty(tuple?.associationId) || !isIdentifier(tuple.childSessionId) || !isOwnerUuid(tuple.messageId)
      || tuple.effectRecoveryState !== 'mayRepeat') {
      throw new CaseError('blocked', 'A pending child tuple is incomplete.');
    }
    if (tuples.has(tuple.associationId) || children.has(tuple.childSessionId)
      || messages.has(tuple.messageId.toLowerCase())) {
      throw new CaseError('failed', `Duplicate child tuple: ${JSON.stringify(tuple)}.`);
    }
    tuples.set(tuple.associationId, tuple);
    children.add(tuple.childSessionId);
    messages.add(tuple.messageId.toLowerCase());
  }
  return { projection, nodes, steps, tuples, selectionHash };
}

function savedChildFacts(snapshot, input) {
  const facts = childSnapshotFacts(snapshot, input);
  if (facts.tuples.size === 0) throw new CaseError('blocked', 'No outstanding child tuple is visible.');
  for (const step of facts.steps.values()) {
    if (step.checkpointStatus === 'running' && !facts.tuples.has(step.associationId)) {
      throw new CaseError('blocked', `The message identity for ${step.associationId} is unavailable.`);
    }
  }
  const members = [];
  for (const tuple of facts.tuples.values()) {
    const step = facts.steps.get(tuple.associationId);
    const node = facts.nodes.get(tuple.childSessionId);
    if (!step || !node || node.kind !== 'childWork' || node.detached
      || node.parentSessionId !== input.target.sessionId || node.rootSessionId !== input.target.sessionId
      || node.lifecycle !== 'active' || node.archivedAt != null
      || node.executionFence !== snapshot.runStatus.executionFence
      || !['pending', 'running'].includes(step.checkpointStatus)
      || step.effectRecoveryState !== 'mayRepeat'
      || step.workItemId !== null && step.workItemId !== tuple.associationId) {
      throw new CaseError('blocked', `The saved child binding is incomplete: ${JSON.stringify(tuple)}.`);
    }
    members.push({ tuple, stepId: step.stepId, workItemId: step.workItemId, node });
  }
  let effect = null;
  if (input.caseKind === 'committed-effect') {
    const operations = facts.projection.platformGateOperations;
    const matches = operations.filter(item => sameOwnerUuid(item?.operationId, input.expectedEffectOperationId));
    if (matches.length !== 1 || operations.filter(item => item?.stepId === matches[0].stepId).length !== 1) {
      throw new CaseError('blocked', 'The selected OpenPullRequest intent is missing or ambiguous.');
    }
    effect = matches[0];
    const step = facts.projection.steps.filter(item => item.stepId === effect.stepId && item.associationId === null);
    if (effect.gate !== 'openPullRequest' || effect.evidenceState !== 'intentOnly'
      || effect.effectRecoveryState !== 'mayRepeat' || step.length !== 1
      || step[0].checkpointStatus !== 'running' || step[0].effectRecoveryState !== 'mayRepeat') {
      throw new CaseError('blocked', 'The selected OpenPullRequest is not a running intent without a receipt.');
    }
  }
  return {
    planId: facts.projection.workPlanId,
    checkpointId: facts.projection.checkpointId,
    checkpointRevision: facts.projection.checkpointRevision,
    decisionVersion: snapshot.decisions.stateVersion,
    fence: snapshot.runStatus.executionFence,
    selectionHash: facts.selectionHash ?? null,
    members,
    effect,
  };
}

function savedPlanFacts(value, snapshot, input) {
  const fingerprint = value?.savedPlanFingerprint;
  const checkpoint = value?.checkpoint;
  const projection = snapshot.sessionStatus?.executionRecovery;
  const expectedSelectionHash = snapshot.sessionStatus?.runtimeStartup?.acceptedSelectionHash;
  if (!isObject(value)
    || value.session?.projectId !== input.target.projectId
    || value.session?.runId !== input.target.runId
    || value.session?.sessionId !== input.target.sessionId
    || !isHash(value.acceptedSelectionHash)
    || !isHash(expectedSelectionHash)
    || value.acceptedSelectionHash.toLowerCase() !== expectedSelectionHash.toLowerCase()
    || value.executionFence !== snapshot.runStatus?.executionFence
    || value.executionFence !== snapshot.sessionStatus?.executionFence
    || value.executionFence !== snapshot.decisions?.executionFence
    || value.decisionStateVersion !== snapshot.decisions?.stateVersion
    || value.workPlan?.plan?.id !== projection?.workPlanId
    || !isObject(checkpoint)
    || checkpoint.workPlanId !== projection?.workPlanId
    || checkpoint.checkpointId !== projection?.checkpointId
    || checkpoint.revision !== projection?.checkpointRevision
    || checkpoint.decisionStateVersion !== snapshot.decisions?.stateVersion
    || !isObject(fingerprint)
    || !exactKeys(fingerprint, ['version', 'sha256'])
    || fingerprint.version !== 1
    || !isHash(fingerprint.sha256)
    || fingerprint.sha256 !== fingerprint.sha256.toLowerCase()) {
    throw new CaseError('blocked', 'The execution-plan read is missing, malformed, unsupported, or unbound.');
  }
  return {
    acceptedSelectionHash: value.acceptedSelectionHash.toLowerCase(),
    executionFence: value.executionFence,
    decisionStateVersion: value.decisionStateVersion,
    workPlanId: value.workPlan.plan.id,
    checkpointId: checkpoint.checkpointId,
    checkpointRevision: checkpoint.revision,
    fingerprint,
  };
}

function boundSavedPlanPair(beforePlan, afterPlan, snapshot, input) {
  const before = savedPlanFacts(beforePlan, snapshot, input);
  const after = savedPlanFacts(afterPlan, snapshot, input);
  if (!isDeepStrictEqual(before, after)) {
    throw new CaseError('blocked', 'The execution-plan read changed during journal paging.');
  }
  return { ...before, childReads: [beforePlan.children, afterPlan.children] };
}

function completedChildProof(before, observed, savedPlan) {
  const completed = observed.members.filter(member => member.outcome === 'checkpointResultRecorded');
  if (before.formatVersion !== 2 || completed.length === 0) {
    return { status: 'blocked', reason: 'No completed child has a supported before capture.' };
  }
  let incomplete = false;
  for (const member of completed) {
    const reads = savedPlan.childReads.map(children => {
      if (!Array.isArray(children)) return null;
      return children.filter(child =>
        child?.sessionId === member.childSessionId || child?.associationId === member.associationId)
        .map(child => ({
          sessionId: child.sessionId,
          associationId: child.associationId,
          dispatchMessageId: child.dispatchMessageId,
          receiptHash: child.turnReceiptHash,
          link: child.completedReceiptLink,
        }));
    });
    if (!isDeepStrictEqual(reads[0], reads[1]) || !reads[0]?.length
      || reads[0].some(child => !isIdentifier(child.sessionId) || !isAssociationId(child.associationId)
        || !isObject(child.link) || !exactKeys(child.link, ['messageId', 'receiptHash'])
        || !isOwnerUuid(child.link.messageId) || !isOwnerUuid(child.dispatchMessageId)
        || !isHash(child.link.receiptHash) || child.link.receiptHash !== child.link.receiptHash.toLowerCase()
        || !isHash(child.receiptHash))) {
      incomplete = true;
      continue;
    }
    const [child] = reads[0];
    if (reads[0].length !== 1 || child.sessionId !== member.childSessionId
      || child.associationId !== member.associationId) {
      throw new CaseError('failed', 'The completed receipt has a contradictory child binding.');
    }
    if (!sameOwnerUuid(child.link.messageId, member.messageId)
      || !sameOwnerUuid(child.dispatchMessageId, member.messageId)
      || child.link.receiptHash !== child.receiptHash) {
      throw new CaseError('failed', 'The completed receipt does not match the original child message or receipt hash.');
    }
  }
  return incomplete
    ? { status: 'blocked', reason: 'A completed child link is missing, malformed, or changed during observation.' }
    : { status: 'passed' };
}

function stableChildSnapshot(initial, final, input) {
  const initialContext = childAuthority(initial.context, input);
  const finalContext = childAuthority(final.context, input);
  validateRootBindings(final.runStatus, final.sessionStatus, final.decisions, final.tree, input);
  if (!sameFacts(initialContext, finalContext)
    || !sameFacts(initial.tree, final.tree)
    || !sameFacts(initial.sessionStatus.executionRecovery, final.sessionStatus.executionRecovery)
    || !sameFacts(initial.runStatus.checkpointRecovery, final.runStatus.checkpointRecovery)
    || !sameFacts(initial.sessionStatus.checkpointRecovery, final.sessionStatus.checkpointRecovery)
    || initial.sessionStatus.kind !== final.sessionStatus.kind
    || initial.sessionStatus.detached !== final.sessionStatus.detached
    || initial.sessionStatus.parentSessionId !== final.sessionStatus.parentSessionId
    || !sameFacts(initial.sessionStatus.runtimeStartup?.acceptedSelectionHash ?? null,
      final.sessionStatus.runtimeStartup?.acceptedSelectionHash ?? null)
    || !sameFacts(statusBinding(initial.runStatus, initial.sessionStatus, initial.decisions),
      statusBinding(final.runStatus, final.sessionStatus, final.decisions))) {
    throw new CaseError('blocked', 'Authorization, tree, checkpoint, or recovery changed during observation.');
  }
}

function boundChildRecovery(input, before, snapshot, recovery) {
  const saved = savedChildFacts(before.outputs, input);
  const projection = snapshot.sessionStatus?.executionRecovery;
  const nextVersion = nextSafe(saved.decisionVersion, 'Before decision version');
  const nextFence = nextSafe(saved.fence, 'Before fence');
  const nextRevision = nextSafe(saved.checkpointRevision, 'Source checkpoint revision');
  if (!sameFacts(childAuthority(before.outputs.context, input), childAuthority(snapshot.context, input))) {
    throw new CaseError('blocked', 'The authorization context changed after the before capture.');
  }
  if (!isObject(recovery) || !isOwnerUuid(recovery.operationId)
    || recovery.operationId.toLowerCase() !== input.expectedRecoveryOperationId
    || recovery.supersededByOperationId != null || recovery.supersededByReceiptId != null
    || recovery.supersededByPayloadHash != null) {
    throw new CaseError('blocked', 'The current recovery identity is missing, different, or superseded.');
  }
  if (recovery.phase !== 'ready') {
    throw new CaseError(['failed', 'indeterminate'].includes(recovery.phase) ? 'failed' : 'blocked',
      `The selected recovery operation is ${recovery.phase ?? 'unavailable'}, not ready.`);
  }
  if (recovery.currentStep !== 'ready' || recovery.boundaryKind !== 'childJoin') {
    throw new CaseError('blocked', 'The ready child recovery boundary is unavailable.');
  }
  for (const [label, value] of [
    ['Recovery source revision', recovery.sourceCheckpointRevision],
    ['Recovery checkpoint revision', recovery.recoveryCheckpointRevision],
    ['Previous recovery fence', recovery.previousExecutionFence],
    ['Recovery fence', recovery.executionFence],
    ['Recovery decision version', recovery.decisionStateVersion],
    ['Previous registration revision', recovery.previousRegistrationRevision],
  ]) requireSafe(value, label);
  if (recovery.registrationRevision != null) requireSafe(recovery.registrationRevision, 'Recovery registration revision');
  if (recovery.sourceCheckpointId !== saved.checkpointId
    || recovery.sourceCheckpointRevision !== saved.checkpointRevision
    || recovery.previousExecutionFence !== saved.fence
    || recovery.executionFence !== nextFence
    || recovery.executionFence !== snapshot.runStatus?.executionFence
    || recovery.executionFence !== snapshot.sessionStatus?.executionFence
    || recovery.executionFence !== snapshot.decisions?.executionFence
    || recovery.decisionStateVersion !== nextVersion
    || snapshot.decisions?.stateVersion !== nextVersion
    || !isObject(projection) || projection.state !== 'available'
    || projection.workPlanId !== saved.planId
    || projection.currentDecisionStateVersion !== nextVersion
    || projection.checkpointDecisionStateVersion !== nextVersion
    || !isHash(recovery.sourceCheckpointHash) || !isHash(recovery.recoveryCheckpointHash)
    || !isHash(recovery.childManifestSha256) || !nonEmpty(recovery.recoveryCheckpointId)
    || recovery.recoveryCheckpointRevision !== nextRevision
    || !safePositive(projection.checkpointRevision) || !nonEmpty(projection.checkpointId)
    || projection.checkpointRevision < nextRevision
    || projection.checkpointRevision === nextRevision
      && projection.checkpointId !== recovery.recoveryCheckpointId) {
    throw new CaseError('blocked', 'The recovery source, plan, versions, or checkpoint lineage is not bound to the capture.');
  }
  if (typeof recovery.dispatchHeld !== 'boolean' || !nonEmpty(recovery.recoveryMaterialMode)
    || !isOwnerUuid(recovery.previousRuntimeInstanceId) || !isOwnerUuid(recovery.runtimeInstanceId)) {
    throw new CaseError('blocked', 'The recovery hold, material, or runtime identity is unavailable.');
  }
  const facts = childSnapshotFacts(snapshot, input);
  if ((saved.selectionHash ?? null) !== (facts.selectionHash ?? null)) {
    throw new CaseError('blocked', 'The accepted selection hash changed or is unavailable.');
  }
  return { saved, recovery, projection, nextFence, facts };
}

function validateChildObservation(input, before, snapshot) {
  if (!isDeepStrictEqual(snapshot.runStatus?.checkpointRecovery, snapshot.sessionStatus?.checkpointRecovery)) {
    throw new CaseError('blocked', 'The current recovery identity is missing, different, or superseded.');
  }
  const { saved, recovery, projection, nextFence, facts } =
    boundChildRecovery(input, before, snapshot, snapshot.runStatus?.checkpointRecovery);
  if (before.formatVersion === 2
    && (!isObject(before.savedPlanFingerprint) || !isObject(snapshot.savedPlan?.fingerprint)
      || !isDeepStrictEqual(before.savedPlanFingerprint, snapshot.savedPlan.fingerprint))) {
    if (isObject(before.savedPlanFingerprint)
      && isObject(snapshot.savedPlan?.fingerprint)
      && before.savedPlanFingerprint.version === 1
      && snapshot.savedPlan.fingerprint.version === 1
      && isHash(before.savedPlanFingerprint.sha256)
      && isHash(snapshot.savedPlan.fingerprint.sha256)) {
      throw new CaseError('failed', 'The saved plan changed during recovery.');
    }
    throw new CaseError('blocked', 'The saved-plan fingerprint is missing or invalid.');
  }
  if (recovery.dispatchHeld !== false
    || !['nativeCache', 'journalRebuild'].includes(recovery.recoveryMaterialMode)
    || recovery.previousRuntimeInstanceId.toLowerCase() === recovery.runtimeInstanceId.toLowerCase()
    || recovery.previousExecutionFence !== saved.fence
    || recovery.executionFence !== nextFence
    || recovery.executionFence !== snapshot.runStatus.executionFence
    || recovery.executionFence !== snapshot.sessionStatus.executionFence
    || recovery.executionFence !== snapshot.decisions.executionFence) {
    throw new CaseError('failed', 'The settled recovery has an invalid hold, material, runtime, or fence.');
  }
  const beforeNodes = new Map(before.outputs.tree.nodes.map(node => [node.identity.sessionId, node]));
  const savedIds = new Set(saved.members.map(member => member.tuple.childSessionId));
  for (const [id, node] of beforeNodes) {
    const current = facts.nodes.get(id);
    if (!current) {
      throw new CaseError(savedIds.has(id) ? 'failed' : 'blocked', `Original tree node is missing: ${id}.`);
    }
    if (node.kind !== current.kind || node.parentSessionId !== current.parentSessionId
      || node.rootSessionId !== current.rootSessionId) {
      throw new CaseError(savedIds.has(id) ? 'failed' : 'blocked', `Original tree identity changed: ${id}.`);
    }
    if (!savedIds.has(id) && id !== input.target.sessionId
      && (node.lifecycle !== current.lifecycle || node.detached !== current.detached
        || node.archivedAt !== current.archivedAt)) {
      throw new CaseError('blocked', `An unrelated tree node changed: ${id}.`);
    }
  }
  const results = [];
  for (const member of saved.members) {
    const { tuple } = member;
    const node = facts.nodes.get(tuple.childSessionId);
    const step = facts.steps.get(tuple.associationId);
    const current = facts.tuples.get(tuple.associationId);
    const replacement = [...facts.tuples.values()].find(item =>
      item.childSessionId === tuple.childSessionId && item.associationId !== tuple.associationId);
    if (replacement || current && !sameFacts(current, tuple)) {
      throw new CaseError('failed', `Saved child tuple changed: ${JSON.stringify(tuple)}; observed ${JSON.stringify(replacement ?? current)}.`);
    }
    if (!step) throw new CaseError('blocked', `The saved association step is unavailable: ${tuple.associationId}.`);
    if (step.stepId !== member.stepId || step.workItemId !== member.workItemId) {
      throw new CaseError('failed', `The saved association binding changed: ${tuple.associationId}.`);
    }
    if (node.detached || node.archivedAt != null || ['cancelled', 'archived'].includes(node.lifecycle)) {
      throw new CaseError('blocked', `The original child is detached, archived, or cancelled: ${tuple.childSessionId}.`);
    }
    if (node.executionFence !== recovery.executionFence) {
      throw new CaseError('failed', `The original child has a different fence: ${tuple.childSessionId}.`);
    }
    if (current && node.lifecycle === 'active' && ['pending', 'running'].includes(step.checkpointStatus)
      && step.effectRecoveryState === 'mayRepeat') {
      results.push({ ...tuple, stepId: member.stepId, workItemId: member.workItemId, outcome: 'pending' });
    } else if (!current && node.lifecycle === 'completed' && step.checkpointStatus === 'succeeded'
      && step.effectRecoveryState === 'checkpointResultRecorded') {
      results.push({ ...tuple, stepId: member.stepId, workItemId: member.workItemId,
        outcome: 'checkpointResultRecorded', checkpointId: projection.checkpointId,
        checkpointRevision: projection.checkpointRevision });
    } else if (current && (node.lifecycle === 'completed' || step.checkpointStatus === 'succeeded'
      || step.effectRecoveryState === 'checkpointResultRecorded')
      || step.effectRecoveryState === 'checkpointResultRecorded' && step.checkpointStatus !== 'succeeded') {
      throw new CaseError('failed', `The saved child has contradictory result evidence: ${JSON.stringify(tuple)}.`);
    } else {
      throw new CaseError('blocked', `The saved child outcome is not established: ${JSON.stringify(tuple)}.`);
    }
  }
  if ([...facts.tuples.keys()].some(id => !saved.members.some(member => member.tuple.associationId === id))) {
    throw new CaseError('blocked', 'The projection contains unexplained new child work.');
  }
  if ([...facts.nodes.keys()].some(id => !beforeNodes.has(id))
    || [...facts.steps.values()].some(step => step.checkpointStatus === 'running'
      && !saved.members.some(member => member.tuple.associationId === step.associationId))) {
    throw new CaseError('blocked', 'The projection or session tree contains unexplained new child work.');
  }
  const beforeSteps = before.outputs.sessionStatus.executionRecovery.steps;
  for (const step of projection.steps) {
    if (saved.members.some(member => member.tuple.associationId === step.associationId)
      || input.caseKind === 'committed-effect' && step.stepId === saved.effect.stepId
        && step.associationId === null) continue;
    const previous = beforeSteps.filter(item =>
      item.stepId === step.stepId && item.associationId === step.associationId
      && item.workItemId === step.workItemId);
    if (previous.length !== 1 || previous[0].checkpointStatus !== step.checkpointStatus
      || previous[0].effectRecoveryState !== step.effectRecoveryState) {
      throw new CaseError('blocked', `An unrelated checkpoint step changed: ${step.stepId}.`);
    }
  }
  if (projection.steps.length !== beforeSteps.length) {
    throw new CaseError('blocked', 'The checkpoint step set changed.');
  }
  if (input.caseKind === 'child-join'
    && !sameFacts(projection.platformGateOperations, before.outputs.sessionStatus.executionRecovery.platformGateOperations)) {
    throw new CaseError('blocked', 'Platform operation evidence changed outside this child observation.');
  }
  let effect;
  if (input.caseKind === 'committed-effect') {
    const operations = projection.platformGateOperations;
    if (!Array.isArray(operations)) throw new CaseError('blocked', 'The effect projection is unavailable.');
    const matching = operations.filter(item => item?.stepId === saved.effect.stepId);
    if (matching.length > 1 || operations.filter(item =>
      sameOwnerUuid(item?.operationId, saved.effect.operationId)).length > 1) {
      throw new CaseError('failed', `Duplicate OpenPullRequest operation: ${saved.effect.operationId}.`);
    }
    if (matching.length === 0) {
      const displaced = operations.find(item => sameOwnerUuid(item?.operationId, saved.effect.operationId));
      if (displaced) throw new CaseError('failed', `The OpenPullRequest step changed: ${JSON.stringify(displaced)}.`);
      throw new CaseError('blocked', 'The selected OpenPullRequest operation is unavailable.');
    }
    effect = matching[0];
    if (!isOwnerUuid(effect.operationId) || !nonEmpty(effect.gate)) {
      throw new CaseError('blocked', 'The OpenPullRequest identity or gate is unavailable.');
    }
    if (!sameOwnerUuid(effect.operationId, saved.effect.operationId) || effect.gate !== saved.effect.gate) {
      throw new CaseError('failed', `The OpenPullRequest operation changed: ${JSON.stringify(effect)}.`);
    }
    if (!sameFacts(operations.filter(item => item !== effect),
      before.outputs.sessionStatus.executionRecovery.platformGateOperations.filter(item =>
        !sameOwnerUuid(item?.operationId, saved.effect.operationId)))) {
      throw new CaseError('blocked', 'Unrelated platform operation evidence changed.');
    }
    const steps = projection.steps.filter(step => step.stepId === effect.stepId && step.associationId === null);
    if (steps.length !== 1) throw new CaseError('blocked', 'The selected OpenPullRequest step is unavailable.');
    const step = steps[0];
    if (effect.evidenceState === 'terminalReceiptRecorded' && effect.effectRecoveryState == null) {
      if (step.checkpointStatus === 'failed') {
        throw new CaseError('failed', `The recorded receipt has a failed step: ${effect.operationId}.`);
      }
      if (step.checkpointStatus !== 'succeeded' || step.effectRecoveryState !== 'terminalReceiptRecorded') {
        throw new CaseError('failed', `The recorded receipt has contradictory step evidence: ${effect.operationId}.`);
      }
    } else if (effect.evidenceState === 'committedAwaitingReceipt'
      && effect.effectRecoveryState === 'reconcileCommitted'
      && step.checkpointStatus === 'running' && step.effectRecoveryState === 'reconcileCommitted') {
      throw new CaseError('blocked', `The OpenPullRequest operation awaits reconciliation: ${effect.operationId}.`);
    } else if (effect.evidenceState === 'intentOnly' && effect.effectRecoveryState === 'mayRepeat') {
      throw new CaseError('blocked', `The OpenPullRequest commit is not established: ${effect.operationId}.`);
    } else if (['terminalReceiptRecorded', 'committedAwaitingReceipt'].includes(effect.evidenceState)
      && ['mayRepeat', 'reconcileCommitted', 'terminalReceiptRecorded', null].includes(effect.effectRecoveryState ?? null)) {
      throw new CaseError('failed', `The OpenPullRequest classification contradicts its step: ${effect.operationId}.`);
    } else {
      throw new CaseError('blocked', `The OpenPullRequest classification is unavailable: ${effect.operationId}.`);
    }
  }
  return { recovery, members: results, effect, checkpointId: projection.checkpointId,
    checkpointRevision: projection.checkpointRevision };
}

function verifyBeforeCommandRecords(capture, input, records) {
  if (!Array.isArray(records)) {
    throw new CaseError('blocked', 'The retained before-capture command records are missing.');
  }
  const beforeInput = { ...input, mode: 'before-capture' };
  const commands = buildCommands(beforeInput, capture.formatVersion === 1);
  const hasPlanReads = capture.formatVersion === 2;
  const initialCommands = hasPlanReads ? commands.filter(command => command.id !== 'execution-plan') : commands;
  if (records.length < initialCommands.length + (hasPlanReads ? 8 : 6)) {
    throw new CaseError('blocked', 'The retained before-capture command records are incomplete.');
  }
  let index = 0;
  const initialDiscoveryCount = capture.formatVersion === 1 ? 8 : discoveryCommandCount(beforeInput);
  for (const command of initialCommands) {
    const commandId = index < initialDiscoveryCount
      ? command.id
      : `initial-${command.id === 'rest-authorization-context'
        ? 'authorization-context'
        : command.id}`;
    const record = commandRecord(records, index++, commandId, command.args);
    if (command.id.startsWith('api-describe-')) commandJson(record, command.id);
  }
  const auth = commandJson(records[1], 'auth-status');
  if (auth.isSignedIn !== true || auth.tenantId !== input.target.tenantId) {
    throw new CaseError('blocked', 'The retained auth-status record does not match the case tenant.');
  }
  const operationIds = new Set(commandJson(records[2], 'api-operations').map(item => (
    typeof item === 'string' ? item : item?.operationId
  )));
  if (discoveryOperations({
    caseKind: capture.formatVersion === 1 ? 'pending-gate' : input.caseKind,
    mode: 'before-capture',
  })
    .some(operation => !operationIds.has(operation))) {
    throw new CaseError('blocked', 'The retained API discovery record does not list every required operation.');
  }
  const { outputs } = capture;
  const context = restBody(commandRecord(records, initialDiscoveryCount, 'initial-authorization-context',
    commands[initialDiscoveryCount].args), 'rest-authorization-context');
  if (!isDeepStrictEqual(validateAuthorizationContext(context, input), outputs.context)) {
    throw new CaseError('blocked', 'The retained authorization context does not match the captured output.');
  }
  const outputCommands = [
    ['run-status', outputs.runStatus, 'api'],
    ['run-status-rest', outputs.runStatus, 'rest'],
    ['session-status', outputs.sessionStatus, 'api'],
    ['session-status-rest', outputs.sessionStatus, 'rest'],
    ['decisions', outputs.decisions, 'api'],
    ['session-tree', outputs.tree, 'api'],
  ];
  for (const [id, expected, kind] of outputCommands) {
    const commandIndex = commands.findIndex(command => command.id === id);
    const record = records[commandIndex];
    const actual = kind === 'api'
      ? apiResponseBody(commandJson(record, id), id)
      : restBody(record, id);
    if (!isDeepStrictEqual(actual, expected)) {
      throw new CaseError('blocked', `The retained ${id} record does not match the captured output.`);
    }
  }
  let planFacts = null;
  if (hasPlanReads) {
    const command = commands.find(item => item.id === 'execution-plan');
    const beforePlan = apiResponseBody(commandJson(commandRecord(
      records, index++, 'initial-execution-plan-before-journal', command.args,
    ), 'initial-execution-plan-before-journal'), 'initial-execution-plan-before-journal');
    const pageRecords = [];
    let cursor = null;
    for (;;) {
      const pageNumber = pageRecords.length + 1;
      const args = runEventArgs(input, cursor);
      const record = commandRecord(
        records, index++, `initial-journal-page-${pageNumber}`, args,
      );
      const page = apiResponseBody(commandJson(record, `journal-page-${pageNumber}`), `journal-page-${pageNumber}`);
      pageRecords.push(page);
      if (page.hasMore !== true) break;
      cursor = page.nextCursor;
    }
    const afterPlan = apiResponseBody(commandJson(commandRecord(
      records, index++, 'initial-execution-plan-after-journal', command.args,
    ), 'initial-execution-plan-after-journal'), 'initial-execution-plan-after-journal');
    if (!isDeepStrictEqual(pageRecords, outputs.events)) {
      throw new CaseError('blocked', 'The retained journal command records do not match the captured pages.');
    }
    planFacts = boundSavedPlanPair(beforePlan, afterPlan, outputs, input);
    if (!isDeepStrictEqual(planFacts.fingerprint, capture.savedPlanFingerprint)) {
      throw new CaseError('blocked', 'The retained plan-read fingerprint does not match the capture.');
    }
  } else {
    const pageRecords = [];
    let cursor = null;
    for (;;) {
      const pageNumber = pageRecords.length + 1;
      const args = runEventArgs(input, cursor);
      const record = commandRecord(
        records, index++, `initial-journal-page-${pageNumber}`, args,
      );
      const page = apiResponseBody(commandJson(record, `journal-page-${pageNumber}`), `journal-page-${pageNumber}`);
      pageRecords.push(page);
      if (page.hasMore !== true) break;
      cursor = page.nextCursor;
    }
    if (!isDeepStrictEqual(pageRecords, outputs.events)) {
      throw new CaseError('blocked', 'The retained journal command records do not match the captured pages.');
    }
  }
  const finalCommands = [
    ['initial-final-run-status', commands.find(command => command.id === 'run-status')],
    ['initial-final-run-status-rest', commands.find(command => command.id === 'run-status-rest')],
    ['initial-final-session-status', commands.find(command => command.id === 'session-status')],
    ['initial-final-session-status-rest', commands.find(command => command.id === 'session-status-rest')],
    ['initial-final-decisions', commands.find(command => command.id === 'decisions')],
  ];
  const finalOutputs = {};
  for (const [id, command] of finalCommands) {
    const record = commandRecord(records, index++, id, command.args);
    finalOutputs[id] = id.endsWith('-rest')
      ? restBody(record, id)
      : apiResponseBody(commandJson(record, id), id);
  }
  if (input.caseKind !== 'pending-gate') {
    const finalContext = restBody(commandRecord(records, index++, 'initial-final-authorization-context',
      commands[initialDiscoveryCount].args), 'initial-final-authorization-context');
    const treeCommand = commands.find(command => command.id === 'session-tree');
    const finalTree = apiResponseBody(commandJson(commandRecord(records, index++,
      'initial-final-session-tree', treeCommand.args), 'initial-final-session-tree'), 'initial-final-session-tree');
    if (index !== records.length) throw new CaseError('blocked', 'The retained command sequence has extra records.');
    const final = {
      context: finalContext,
      tree: finalTree,
      runStatus: finalOutputs['initial-final-run-status'],
      sessionStatus: finalOutputs['initial-final-session-status'],
      decisions: finalOutputs['initial-final-decisions'],
    };
    try {
      compareStatusApiAndRest(final.runStatus, finalOutputs['initial-final-run-status-rest'], 'Final run status');
      compareStatusApiAndRest(final.sessionStatus, finalOutputs['initial-final-session-status-rest'], 'Final root status');
      stableChildSnapshot(outputs, final, input);
      if (!sameFacts(savedChildFacts(outputs, input), capture.childFacts)) {
        throw new CaseError('blocked', 'The retained child facts do not match the capture.');
      }
      if (capture.formatVersion === 2
        && !isDeepStrictEqual(planFacts?.fingerprint, capture.savedPlanFingerprint)) {
        throw new CaseError('blocked', 'The retained plan-read evidence is unavailable.');
      }
    } catch (error) {
      throw new CaseError('blocked', `The retained before reads are incomplete or inconsistent: ${error.message}`);
    }
    return;
  }
  const initialBinding = statusBinding(outputs.runStatus, outputs.sessionStatus, outputs.decisions);
  const finalApiBinding = statusBinding(
    finalOutputs['initial-final-run-status'],
    finalOutputs['initial-final-session-status'],
    finalOutputs['initial-final-decisions'],
  );
  const finalRestBinding = statusBinding(
    finalOutputs['initial-final-run-status-rest'],
    finalOutputs['initial-final-session-status-rest'],
    finalOutputs['initial-final-decisions'],
  );
  let finalGate;
  try {
    finalGate = gateFacts(
      finalOutputs['initial-final-decisions'],
      finalOutputs['initial-final-session-status'],
      input.expectedGateRequestId,
      input,
    );
  } catch {
    throw new CaseError('blocked', 'The retained final authority reads do not preserve the pending gate.');
  }
  if (index !== records.length
    || !isDeepStrictEqual(finalApiBinding, initialBinding)
    || !isDeepStrictEqual(finalRestBinding, initialBinding)
    || !isDeepStrictEqual(finalGate, capture.gate)) {
    throw new CaseError('blocked', 'The retained final authority reads do not match the captured run, session, and decision outputs.');
  }
}

export function verifyBeforeCapture(
  capture, input, beforeCaptureSha256, scriptSha256, apphostSha256,
  inputSha256, rawCommandRecords, rawCommandSha256,
) {
  if (!isObject(capture)
    || ![1, 2].includes(capture.formatVersion)
    || capture.formatVersion === 2 && input.caseKind === 'pending-gate'
    || capture.formatVersion === 1 && input.caseKind !== 'pending-gate'
      && capture.savedPlanFingerprint !== undefined
    || capture.status !== 'captured'
    || capture.caseId !== CASE_ID
    || capture.caseKind !== input.caseKind
    || capture.mode !== 'before-capture'
    || capture.sourceCommit !== input.sourceCommit
    || capture.targetCommit !== input.targetCommit
    || capture.apphostSha256 !== apphostSha256
    || capture.scriptSha256 !== scriptSha256
    || capture.actorId !== input.expectedActorId
    || !sameTarget(capture.target, input.target)
    || !isObject(capture.input)
    || capture.input.sha256 !== inputSha256
    || capture.commandsSha256 !== rawCommandSha256
    || input.caseKind === 'pending-gate' && (
      capture.input.gateRequestId !== input.expectedGateRequestId
      || !isObject(capture.gate)
      || capture.gate.requestId !== input.expectedGateRequestId
      || capture.gate.actorId !== input.expectedActorId
      || !Array.isArray(capture.gate.choices)
      || typeof capture.gate.allowsFreeform !== 'boolean')
    || input.caseKind === 'committed-effect'
      && capture.input.effectOperationId !== input.expectedEffectOperationId
    || !isObject(capture.outputs)
    || !Array.isArray(capture.outputs.events)) {
    throw new CaseError('blocked', 'The before-capture evidence is missing, incomplete, or bound to another case.');
  }
  const beforeGate = input.caseKind === 'pending-gate' ? gateFacts(
    capture.outputs.decisions, capture.outputs.sessionStatus, input.expectedGateRequestId, input,
  ) : null;
  validateRootBindings(
    capture.outputs.runStatus,
    capture.outputs.sessionStatus,
    capture.outputs.decisions,
    capture.outputs.tree,
    input,
  );
  const journal = validateJournalPages(capture.outputs.events, input);
  if (!isDeepStrictEqual(journal, capture.journal)) {
    throw new CaseError('blocked', 'The before-capture journal summary does not match its saved pages.');
  }
  if (input.caseKind === 'pending-gate' && (!isDeepStrictEqual(beforeGate, capture.gate)
    || beforeGate.fence !== capture.outputs.runStatus.executionFence)) {
    throw new CaseError('blocked', 'The before-capture gate does not match its saved status and decision outputs.');
  }
  if (capture.actorId !== capture.outputs.context?.actorId) {
    throw new CaseError('blocked', 'The before-capture actor does not match its retained authorization context.');
  }
  verifyBeforeCommandRecords(capture, input, rawCommandRecords);
  if (beforeCaptureSha256 !== input.beforeCaptureSha256) {
    throw new CaseError('blocked', 'The before-capture file hash does not match the case input.');
  }
  return capture;
}

export function validateAfterObservation(input, before, snapshot, finalSnapshot) {
  if (!before) throw new CaseError('blocked', 'The before-capture evidence is missing.');
  if (!finalSnapshot) throw new CaseError('blocked', 'The final authority read is missing.');
  const afterBinding = statusBinding(snapshot.runStatus, snapshot.sessionStatus, snapshot.decisions);
  const finalAuthority = finalSnapshot.finalAuthority ?? finalSnapshot;
  if (!isDeepStrictEqual(
    afterBinding,
    statusBinding(finalAuthority.runStatus, finalAuthority.sessionStatus, finalAuthority.decisions),
  )) {
    throw new CaseError('blocked', 'Run, root-session, or gate authority changed during observation.');
  }
  if (!isDeepStrictEqual(snapshot.runStatus.checkpointRecovery,
    finalAuthority.runStatus.checkpointRecovery)
    || !isDeepStrictEqual(snapshot.sessionStatus.checkpointRecovery,
      finalAuthority.sessionStatus.checkpointRecovery)) {
    throw new CaseError('blocked', 'Recovery status changed during observation.');
  }
  const gate = gateFacts(
    snapshot.decisions, snapshot.sessionStatus, input.expectedGateRequestId, input,
  );
  let finalGate;
  try {
    finalGate = gateFacts(
      finalAuthority.decisions,
      finalAuthority.sessionStatus,
      input.expectedGateRequestId,
      input,
    );
  } catch {
    throw new CaseError('blocked', 'The final authority read does not contain the expected pending gate.');
  }
  if (!isDeepStrictEqual(gate, finalGate)) {
    throw new CaseError('blocked', 'The pending gate changed during observation.');
  }
  const recovery = snapshot.runStatus.checkpointRecovery;
  if (!isObject(recovery)
    || recovery.operationId !== input.expectedRecoveryOperationId
    || snapshot.sessionStatus.checkpointRecovery?.operationId !== input.expectedRecoveryOperationId
    || !isDeepStrictEqual(recovery, snapshot.sessionStatus.checkpointRecovery)) {
    throw new CaseError('blocked', 'Run and root-session status do not identify the expected recovery operation.');
  }
  if (recovery.phase !== 'ready') {
    const status = ['failed', 'indeterminate'].includes(recovery.phase) ? 'failed' : 'blocked';
    throw new CaseError(status, `The selected recovery operation is ${recovery.phase ?? 'unavailable'}, not ready.`);
  }
  if (recovery.recoveryMaterialMode == null || recovery.recoveryMaterialMode === '') {
    throw new CaseError('blocked', 'The selected recovery operation has no recovery material mode.');
  }
  if (recovery.currentStep !== 'ready'
    || !['nativeCache', 'journalRebuild'].includes(recovery.recoveryMaterialMode)
    || !isUuid(recovery.runtimeInstanceId)
    || recovery.runtimeInstanceId === recovery.previousRuntimeInstanceId
    || recovery.previousExecutionFence !== before.outputs.runStatus.executionFence
    || recovery.executionFence !== recovery.previousExecutionFence + 1
    || recovery.executionFence !== snapshot.runStatus.executionFence
    || recovery.executionFence !== snapshot.sessionStatus.executionFence
    || recovery.executionFence !== snapshot.decisions.executionFence
    || recovery.decisionStateVersion !== snapshot.decisions.stateVersion) {
    throw new CaseError('failed', 'The ready recovery status does not match the next runtime, fence, and decision state.');
  }
  if (recovery.gateRequestId !== before.gate.requestId
    || recovery.gateAuthorizedActorId !== before.gate.actorId
    || gate.requestId !== before.gate.requestId
    || gate.actorId !== before.gate.actorId
    || !isDeepStrictEqual(gate.choices, before.gate.choices)
    || gate.allowsFreeform !== before.gate.allowsFreeform
    || gate.fence !== recovery.executionFence) {
    throw new CaseError('failed', 'Recovery changed the pending gate request, actor, choices, or freeform setting.');
  }
  return { gate, recovery };
}

async function sha256(filePath) {
  return createHash('sha256').update(await readFile(filePath)).digest('hex');
}

async function readInput(inputPath) {
  let bytes;
  try {
    bytes = await readFile(inputPath);
    return {
      input: validateInput(JSON.parse(bytes.toString('utf8'))),
      inputSha256: createHash('sha256').update(bytes).digest('hex'),
      inputBytes: bytes,
    };
  } catch (error) {
    if (error instanceof Error && error.message.includes('case input')) throw error;
    throw new Error('The case input file is missing or is not valid JSON.');
  }
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

function runEventArgs(input, cursor) {
  const args = invokeArgs(input, 'replayRunEvents', { limit: PAGE_SIZE });
  if (cursor) args.push('--query', `cursor=${cursor}`);
  return args;
}

function recoveryEventArgs(input, cursor) {
  const args = invokeArgs(input, 'readCheckpointRecoveryEvents', { limit: RECOVERY_EVENT_PAGE_SIZE });
  if (cursor) args.push('--query', `cursor=${cursor}`);
  return args;
}

function logArgs(args) {
  return args.map((value, index) => (
    args[index - 1] === '--query' && value.startsWith('cursor=') ? 'cursor=[REDACTED]' : value
  ));
}

async function retainedCommandRecords(beforeEvidencePath) {
  const commandsPath = path.join(path.dirname(beforeEvidencePath), 'commands.jsonl');
  const bytes = await readFile(commandsPath).catch(() => null);
  if (!bytes) return { records: null, sha256: null };
  try {
    return {
      records: bytes.toString('utf8').trimEnd().split(/\r?\n/).map(line => JSON.parse(line)),
      sha256: createHash('sha256').update(bytes).digest('hex'),
    };
  } catch {
    return { records: null, sha256: null };
  }
}

async function retainedBeforeInput(beforeEvidencePath, input) {
  const inputPath = path.join(path.dirname(beforeEvidencePath), 'case-input.json');
  const bytes = await readFile(inputPath).catch(() => null);
  if (!bytes) throw new CaseError('blocked', 'The retained before-capture input is missing.');
  let retained;
  try {
    retained = validateInput(JSON.parse(bytes.toString('utf8')));
  } catch {
    throw new CaseError('blocked', 'The retained before-capture input is invalid.');
  }
  if (retained.mode !== 'before-capture'
    || retained.caseId !== input.caseId
    || retained.caseKind !== input.caseKind
    || retained.sourceCommit !== input.sourceCommit
    || retained.targetCommit !== input.targetCommit
    || retained.apphostSha256 !== input.apphostSha256
    || retained.expectedActorId !== input.expectedActorId
    || retained.expectedGateRequestId !== input.expectedGateRequestId
    || retained.expectedEffectOperationId !== input.expectedEffectOperationId
    || !sameTarget(retained.target, input.target)) {
    throw new CaseError('blocked', 'The retained before-capture input does not match the current case binding.');
  }
  return {
    inputSha256: createHash('sha256').update(bytes).digest('hex'),
    inputBytes: bytes,
  };
}

export async function runCase(options, env = process.env, commandRunner = runCommand) {
  const { input, inputSha256, inputBytes } = await readInput(options.inputPath);
  const cliPath = resolveCliPath(env.AGENTWEAVER_CLI_PATH);
  const cliInfo = await stat(cliPath).catch(() => null);
  if (!cliInfo?.isFile()) throw new Error('AGENTWEAVER_CLI_PATH does not exist as a file.');
  const apphostSha256 = await sha256(cliPath);
  if (apphostSha256 !== input.apphostSha256) throw new Error('The apphost SHA-256 hash does not match the case input.');
  const evidencePath = await requireNewOutputDirectory(options.outputPath);
  const scriptSha256 = await sha256(CASE_SCRIPT_PATH);
  if (options.dryRun) {
    if (input.mode === 'after-observation'
      && (!input.beforeCaptureSha256 || !options.beforeEvidencePath)) {
      throw new CaseError('blocked', 'After-observation mode needs the before-capture hash and capture file.');
    }
    if (input.mode === 'after-observation') {
      const beforeFile = await readFile(options.beforeEvidencePath).catch(() => null);
      if (!beforeFile
        || createHash('sha256').update(beforeFile).digest('hex') !== input.beforeCaptureSha256) {
        throw new CaseError('blocked', 'The before-capture file is missing or its hash does not match the case input.');
      }
      const retainedInput = await retainedBeforeInput(options.beforeEvidencePath, input);
      const retained = await retainedCommandRecords(options.beforeEvidencePath);
      verifyBeforeCapture(
        parseJson(beforeFile.toString('utf8'), 'before-capture evidence'),
        input, input.beforeCaptureSha256, scriptSha256, apphostSha256,
        retainedInput.inputSha256, retained.records, retained.sha256,
      );
    }
    return {
      status: 'validated-not-run',
      caseId: CASE_ID,
      mode: input.mode,
      sourceCommit: input.sourceCommit,
      targetCommit: input.targetCommit,
      apphostSha256,
      scriptSha256,
      network: 'not-used',
      evidence: 'not-created',
    };
  }

  if (input.mode === 'after-observation'
    && (!input.beforeCaptureSha256 || !options.beforeEvidencePath)) {
    throw new CaseError('blocked', 'After-observation mode needs the before-capture hash and capture file.');
  }
  await mkdir(evidencePath);
  await writeFile(path.join(evidencePath, 'case-input.json'), inputBytes, {
    encoding: 'utf8',
    flag: 'wx',
    mode: 0o600,
  });
  const commandsPath = path.join(evidencePath, 'commands.jsonl');
  const resultPath = path.join(evidencePath, 'result.json');
  const handle = await open(resultPath, 'wx', 0o600);
  const common = {
    caseId: CASE_ID,
    mode: input.mode,
    caseKind: input.caseKind,
    sourceCommit: input.sourceCommit,
    targetCommit: input.targetCommit,
    apphostSha256,
    scriptSha256,
    inputSha256,
    ...(input.beforeCaptureSha256 ? { beforeCaptureSha256: input.beforeCaptureSha256 } : {}),
    startedAt: new Date().toISOString(),
    status: 'blocked',
  };
  async function execute(id, args, outputKind = 'json') {
    const record = commandRunner(cliPath, args, env);
    const commandRecordValue = JSON.parse(serializeRedactedJsonLine({
      id, args: logArgs(args), ...record,
    }));
    await appendRedactedJsonLine(commandsPath, commandRecordValue);
    requireSuccess(record, id);
    return outputKind === 'json' ? parseJson(record.stdout, id) : record;
  }

  async function readSnapshot(prefix, before = null) {
    const legacy = before?.formatVersion === 1;
    const commands = buildCommands(input, legacy);
    const operations = legacy
      ? [...OPERATIONS, ...(input.mode === 'after-observation' ? ['readCheckpointRecoveryEvents'] : [])]
      : discoveryOperations(input);
    const initialDiscoveryCount = 3 + operations.length;
    let recoveryEventError = null;
    if (prefix === 'initial') {
      await execute('version', commands[0].args, 'text');
      const auth = await execute('auth-status', commands[1].args);
      if (auth.isSignedIn !== true || auth.tenantId !== input.target.tenantId) {
        throw new CaseError('blocked', 'The selected CLI identity is not signed in for the case tenant.');
      }
      const listed = await execute('api-operations', commands[2].args);
      const operationIds = new Set(Array.isArray(listed)
        ? listed.map(item => typeof item === 'string' ? item : item?.operationId)
        : []);
      for (let index = 3; index < initialDiscoveryCount; index += 1) {
        const operation = operations[index - 3];
        if (!operationIds.has(operation)) {
          if (operation === 'readCheckpointRecoveryEvents' && input.mode === 'after-observation') {
            recoveryEventError = new CaseError(
              'blocked', 'Gateway discovery does not list readCheckpointRecoveryEvents.',
            );
            continue;
          }
          throw new CaseError('blocked', `Gateway discovery does not list ${operation}.`);
        }
        try {
          await execute(`api-describe-${operation}`, commands[index].args);
        } catch (error) {
          if (operation !== 'readCheckpointRecoveryEvents' || input.mode !== 'after-observation') throw error;
          recoveryEventError = error;
        }
      }
    }
    const base = initialDiscoveryCount;
    const contextRecord = await execute(`${prefix}-authorization-context`, commands[base].args, 'text');
    const context = validateAuthorizationContext(
      restBody(contextRecord, `${prefix}-authorization-context`), input,
    );
    const runStatus = apiResponseBody(
      await execute(`${prefix}-run-status`, commands[base + 1].args), `${prefix}-run-status`,
    );
    const runStatusRest = restBody(
      await execute(`${prefix}-run-status-rest`, commands[base + 2].args, 'text'),
      `${prefix}-run-status-rest`,
    );
    if (input.caseKind === 'pending-gate') {
      compareStatusApiAndRest(runStatus, runStatusRest, 'Run status');
    }
    const sessionStatus = apiResponseBody(
      await execute(`${prefix}-session-status`, commands[base + 3].args), `${prefix}-session-status`,
    );
    const sessionStatusRest = restBody(
      await execute(`${prefix}-session-status-rest`, commands[base + 4].args, 'text'),
      `${prefix}-session-status-rest`,
    );
    if (input.caseKind === 'pending-gate') {
      compareStatusApiAndRest(sessionStatus, sessionStatusRest, 'Root-session status');
    }
    const decisions = apiResponseBody(
      await execute(`${prefix}-decisions`, commands[base + 5].args), `${prefix}-decisions`,
    );
    const tree = apiResponseBody(
      await execute(`${prefix}-session-tree`, commands[base + 6].args), `${prefix}-session-tree`,
    );
    validateRootBindings(runStatus, sessionStatus, decisions, tree, input);

    const events = [];
    const seenCursors = new Set();
    let beforePlan = null;
    if (input.caseKind !== 'pending-gate' && !legacy) {
      beforePlan = apiResponseBody(
        await execute(`${prefix}-execution-plan-before-journal`, commands.at(-1).args),
        `${prefix}-execution-plan-before-journal`,
      );
    }
    let cursor = null;
    for (let pageNumber = 1; pageNumber <= MAX_PAGES; pageNumber += 1) {
      const pageResponse = await execute(
        `${prefix}-journal-page-${pageNumber}`,
        runEventArgs(input, cursor),
      );
      const page = apiResponseBody(pageResponse, `${prefix}-journal-page-${pageNumber}`);
      events.push(page);
      if (page.hasMore !== true) break;
      if (typeof page.nextCursor !== 'string'
        || page.nextCursor.length === 0
        || seenCursors.has(page.nextCursor)) {
        throw new CaseError('blocked', 'The session journal returned a missing or repeated cursor.');
      }
      seenCursors.add(page.nextCursor);
      cursor = page.nextCursor;
      if (pageNumber === MAX_PAGES) {
        throw new CaseError('blocked', 'The session journal exceeded the 1000-page safety limit.');
      }
    }
    const journal = validateJournalPages(events, input);
    let savedPlan;
    if (beforePlan) {
      const afterPlan = apiResponseBody(
        await execute(`${prefix}-execution-plan-after-journal`, commands.at(-1).args),
        `${prefix}-execution-plan-after-journal`,
      );
      savedPlan = boundSavedPlanPair(
        beforePlan,
        afterPlan,
        { context, runStatus, sessionStatus, decisions, tree },
        input,
      );
    }
    const recoveryEventPages = [];
    if (prefix === 'initial' && input.mode === 'after-observation' && !recoveryEventError) {
      const seenCursors = new Set();
      let cursor = null;
      try {
        for (let pageNumber = 1; pageNumber <= MAX_PAGES; pageNumber += 1) {
          const pageResponse = await execute(
            `${prefix}-recovery-events-page-${pageNumber}`,
            recoveryEventArgs(input, cursor),
          );
          const page = apiResponseBody(
            pageResponse, `${prefix}-recovery-events-page-${pageNumber}`,
          );
          recoveryEventPages.push(page);
          if (page.hasMore !== true) break;
          if (typeof page.nextCursor !== 'string'
            || page.nextCursor.length === 0
            || seenCursors.has(page.nextCursor)) {
            throw new CaseError('blocked', 'The recovery event read returned a missing or repeated cursor.');
          }
          seenCursors.add(page.nextCursor);
          cursor = page.nextCursor;
          if (pageNumber === MAX_PAGES) {
            throw new CaseError('blocked', 'The recovery event read exceeded the 1000-page safety limit.');
          }
        }
      } catch (error) {
        recoveryEventError = error;
      }
    }
    const finalRunStatus = apiResponseBody(
      await execute(`${prefix}-final-run-status`, commands[base + 1].args),
      `${prefix}-final-run-status`,
    );
    const finalRunStatusRest = restBody(
      await execute(`${prefix}-final-run-status-rest`, commands[base + 2].args, 'text'),
      `${prefix}-final-run-status-rest`,
    );
    if (input.caseKind === 'pending-gate') {
      compareStatusApiAndRest(finalRunStatus, finalRunStatusRest, `${prefix} final run status`);
    }
    const finalSessionStatus = apiResponseBody(
      await execute(`${prefix}-final-session-status`, commands[base + 3].args),
      `${prefix}-final-session-status`,
    );
    const finalSessionStatusRest = restBody(
      await execute(`${prefix}-final-session-status-rest`, commands[base + 4].args, 'text'),
      `${prefix}-final-session-status-rest`,
    );
    if (input.caseKind === 'pending-gate') {
      compareStatusApiAndRest(finalSessionStatus, finalSessionStatusRest, `${prefix} final root-session status`);
    }
    const finalDecisions = apiResponseBody(
      await execute(`${prefix}-final-decisions`, commands[base + 5].args),
      `${prefix}-final-decisions`,
    );
    let finalContext = context;
    let finalTree = tree;
    if (input.caseKind !== 'pending-gate') {
      finalContext = validateAuthorizationContext(restBody(
        await execute(`${prefix}-final-authorization-context`, commands[base].args, 'text'),
        `${prefix}-final-authorization-context`,
      ), input);
      finalTree = apiResponseBody(
        await execute(`${prefix}-final-session-tree`, commands[base + 6].args),
        `${prefix}-final-session-tree`,
      );
      stableChildSnapshot(
        { context, runStatus, sessionStatus, decisions, tree },
        { context: finalContext, runStatus: finalRunStatus, sessionStatus: finalSessionStatus,
          decisions: finalDecisions, tree: finalTree },
        input,
      );
      const runMismatch = !isDeepStrictEqual(runStatus, runStatusRest)
        || !isDeepStrictEqual(finalRunStatus, finalRunStatusRest);
      const sessionMismatch = !isDeepStrictEqual(sessionStatus, sessionStatusRest)
        || !isDeepStrictEqual(finalSessionStatus, finalSessionStatusRest);
      if (runMismatch || sessionMismatch) {
        if (!isDeepStrictEqual(runStatus, finalRunStatus)
          || !isDeepStrictEqual(runStatusRest, finalRunStatusRest)
          || !isDeepStrictEqual(sessionStatus, finalSessionStatus)
          || !isDeepStrictEqual(sessionStatusRest, finalSessionStatusRest)) {
          throw new CaseError('blocked', 'API and REST status bodies changed during observation.');
        }
        const restSnapshot = { context, runStatus: runStatusRest, sessionStatus: sessionStatusRest, decisions, tree };
        try {
          validateRootBindings(runStatusRest, sessionStatusRest, decisions, tree, input);
          stableChildSnapshot(restSnapshot,
            { context: finalContext, runStatus: finalRunStatusRest, sessionStatus: finalSessionStatusRest,
              decisions: finalDecisions, tree: finalTree }, input);
          for (const candidate of [
            { context, runStatus, sessionStatus, decisions, tree }, restSnapshot,
          ]) {
            if (before) {
              const reference = runStatus.checkpointRecovery;
              if (!sameFacts(statusBinding(candidate.runStatus, candidate.sessionStatus, decisions),
                statusBinding(runStatus, sessionStatus, decisions))
                || ['kind', 'detached', 'parentSessionId'].some(field =>
                  !isDeepStrictEqual(candidate.sessionStatus[field], sessionStatus[field]))
                || ['registrationRevision', 'executionFence', 'acceptedSelectionHash'].some(field =>
                  !isDeepStrictEqual(candidate.sessionStatus.runtimeStartup?.[field], sessionStatus.runtimeStartup?.[field]))
                || ['checkpointId', 'checkpointRevision', 'workPlanId',
                  'checkpointDecisionStateVersion', 'currentDecisionStateVersion'].some(field =>
                  !isDeepStrictEqual(candidate.sessionStatus.executionRecovery?.[field],
                    sessionStatus.executionRecovery?.[field]))) {
                throw new CaseError('blocked', 'The status authority or checkpoint lineage differs between sources.');
              }
              for (const recovery of [candidate.runStatus.checkpointRecovery, candidate.sessionStatus.checkpointRecovery]) {
                const { saved, nextFence } = boundChildRecovery(input, before, candidate, recovery);
                const bindingFields = [
                  'operationId', 'phase', 'currentStep', 'boundaryKind', 'dispatchHeld',
                  'sourceCheckpointId', 'sourceCheckpointRevision', 'sourceCheckpointHash',
                  'recoveryCheckpointId', 'recoveryCheckpointRevision', 'recoveryCheckpointHash',
                  'childManifestSha256', 'previousExecutionFence', 'executionFence', 'decisionStateVersion',
                  'previousRegistrationRevision', 'registrationRevision', 'previousRuntimeInstanceId',
                  'runtimeInstanceId', 'recoveryMaterialMode', 'gateRequestId', 'gateAuthorizedActorId',
                  'supersedesOperationId', 'supersessionReceiptId', 'supersessionPayloadHash',
                ];
                if (bindingFields.some(field => !isDeepStrictEqual(recovery[field], reference[field]))
                  || recovery.previousExecutionFence !== saved.fence
                  || recovery.executionFence !== nextFence
                  || recovery.executionFence !== candidate.runStatus.executionFence) {
                  throw new CaseError('blocked', 'The recovery binding differs between status sources.');
                }
              }
            } else savedChildFacts(candidate, input);
          }
        } catch (error) {
          throw new CaseError('blocked', `The mismatched status sources are not bound: ${error.message}`);
        }
        throw new CaseError('failed', `${runMismatch ? 'Run status' : 'Root-session status'} returned different API and REST status bodies.`);
      }
    }
    validateRootBindings(finalRunStatus, finalSessionStatus, finalDecisions, finalTree, input);
    if (!isDeepStrictEqual(
      statusBinding(runStatus, sessionStatus, decisions),
      statusBinding(finalRunStatus, finalSessionStatus, finalDecisions),
    )) {
      throw new CaseError('blocked', 'Run, root-session, or gate authority changed during journal paging.');
    }
    if (!isDeepStrictEqual(runStatus.checkpointRecovery, finalRunStatus.checkpointRecovery)
      || !isDeepStrictEqual(sessionStatus.checkpointRecovery,
        finalSessionStatus.checkpointRecovery)) {
      throw new CaseError('blocked', 'Recovery status changed during event paging.');
    }
    return {
      context,
      runStatus,
      sessionStatus,
      decisions,
      tree,
      events,
      journal,
      ...(savedPlan ? { savedPlan } : {}),
      recoveryEvents: recoveryEventPages,
      recoveryEventError,
      finalAuthority: {
        runStatus: finalRunStatus,
        sessionStatus: finalSessionStatus,
        decisions: finalDecisions,
      },
    };
  }

  let recoveryEventResult = null;
  try {
    let before = null;
    if (input.mode === 'after-observation') {
      const beforeFile = await readFile(options.beforeEvidencePath).catch(() => null);
      if (!beforeFile
        || createHash('sha256').update(beforeFile).digest('hex') !== input.beforeCaptureSha256) {
        throw new CaseError('blocked', 'The before-capture file is missing or its hash does not match the case input.');
      }
      const retainedInput = await retainedBeforeInput(options.beforeEvidencePath, input);
      const retained = await retainedCommandRecords(options.beforeEvidencePath);
      before = verifyBeforeCapture(
        parseJson(beforeFile.toString('utf8'), 'before-capture evidence'),
        input, input.beforeCaptureSha256, scriptSha256, apphostSha256,
        retainedInput.inputSha256, retained.records, retained.sha256,
      );
    }
    const snapshot = await readSnapshot('initial', before);
    if (input.mode === 'after-observation') {
      recoveryEventResult = committedRecoveryEventProof(snapshot, before, input);
    }
    const gate = input.caseKind === 'pending-gate' ? gateFacts(
      snapshot.decisions, snapshot.sessionStatus, input.expectedGateRequestId, input,
    ) : null;
    if (input.mode === 'before-capture') {
      if (gate && gate.fence !== snapshot.runStatus.executionFence) {
        throw new CaseError('blocked', 'The captured gate fence does not match the run fence.');
      }
      let childFacts;
      if (input.caseKind !== 'pending-gate') {
        try {
          childFacts = savedChildFacts(snapshot, input);
        } catch (error) {
          throw new CaseError('blocked', error.message);
        }
      }
      const capture = {
        formatVersion: input.caseKind === 'pending-gate' ? 1 : 2,
        status: 'captured',
        caseId: CASE_ID,
        caseKind: input.caseKind,
        mode: input.mode,
        sourceCommit: input.sourceCommit,
        targetCommit: input.targetCommit,
        apphostSha256,
        scriptSha256,
        actorId: snapshot.context.actorId,
        target: input.target,
        input: {
          sha256: inputSha256,
          ...(gate ? { gateRequestId: input.expectedGateRequestId } : {}),
          ...(input.caseKind === 'committed-effect'
            ? { effectOperationId: input.expectedEffectOperationId } : {}),
        },
        commandsSha256: await sha256(commandsPath),
        ...(snapshot.savedPlan ? { savedPlanFingerprint: snapshot.savedPlan.fingerprint } : {}),
        ...(gate ? { gate } : { childFacts }),
        outputs: {
          context: snapshot.context,
          runStatus: snapshot.runStatus,
          sessionStatus: snapshot.sessionStatus,
          decisions: snapshot.decisions,
          tree: snapshot.tree,
          events: snapshot.events,
        },
        journal: snapshot.journal,
        capturedAt: new Date().toISOString(),
      };
      const capturePath = path.join(evidencePath, 'capture.json');
      const captureText = serializeRedactedJsonLine(capture) + '\n';
      await writeFile(capturePath, captureText, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
      const result = {
        ...common,
        status: 'captured',
        actorId: snapshot.context.actorId,
        ...(gate ? { gateRequestId: gate.requestId } : { childFacts }),
        ...(childFacts ? {
          fullPlanContentProof: { status: snapshot.savedPlan ? 'captured' : 'blocked' },
          dependencyGraphProof: { status: snapshot.savedPlan ? 'captured' : 'blocked' },
          completedChildProof: { status: 'blocked' },
          committedRecoveryEventProof: { status: 'blocked' },
          ...(input.caseKind === 'committed-effect' ? {
            physicalNoDuplicateProof: { status: 'blocked' },
            checkpointRestartDuplicateFence: { status: 'blocked' },
          } : {}),
        } : {}),
        capturePath,
        captureSha256: await sha256(capturePath),
        journal: snapshot.journal,
        finishedAt: new Date().toISOString(),
      };
      await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
      await handle.sync();
      return result;
    }

    if (!before) throw new CaseError('blocked', 'The before-capture evidence is missing.');
    if (input.caseKind !== 'pending-gate') {
      const observed = validateChildObservation(input, before, snapshot);
      const result = {
        ...common,
        status: recoveryEventResult?.status === 'failed' ? 'failed' : 'passed',
        ...(recoveryEventResult?.status === 'failed'
          ? { reason: recoveryEventResult.reason } : {}),
        actorId: snapshot.context.actorId,
        tenantId: snapshot.context.tenantId,
        operationId: observed.recovery.operationId,
        recovery: observed.recovery,
        checkpointId: observed.checkpointId,
        checkpointRevision: observed.checkpointRevision,
        members: observed.members,
        ...(observed.effect ? { effect: observed.effect } : {}),
        journal: snapshot.journal,
        fullPlanContentProof: {
          status: before.formatVersion === 2 ? 'passed' : 'blocked',
          ...(before.formatVersion === 1 ? { reason: 'The historical capture has no saved-plan fingerprint.' } : {}),
        },
        dependencyGraphProof: {
          status: before.formatVersion === 2 ? 'passed' : 'blocked',
          ...(before.formatVersion === 1 ? { reason: 'The historical capture has no saved-plan fingerprint.' } : {}),
        },
        completedChildProof: completedChildProof(before, observed, snapshot.savedPlan),
        committedRecoveryEventProof: recoveryEventResult,
        ...(input.caseKind === 'committed-effect' ? {
          physicalNoDuplicateProof: { status: 'blocked', reason: 'The public reads do not contain a complete effect and native-call inventory.' },
          checkpointRestartDuplicateFence: {
            status: 'blocked',
            reason: 'Native-send and external-effect evidence is unavailable.',
          },
        } : {}),
        finishedAt: new Date().toISOString(),
      };
      await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
      await handle.sync();
      return result;
    }
    const finalSnapshot = await readSnapshot('final');
    const { gate: currentGate, recovery } = validateAfterObservation(
      input, before, snapshot, finalSnapshot,
    );
    const result = {
      ...common,
      status: recoveryEventResult?.status === 'failed' ? 'failed' : 'passed',
      ...(recoveryEventResult?.status === 'failed'
        ? { reason: recoveryEventResult.reason } : {}),
      actorId: snapshot.context.actorId,
      tenantId: snapshot.context.tenantId,
      operationId: recovery.operationId,
      gate: {
        requestId: currentGate.requestId,
        actorId: currentGate.actorId,
        choices: currentGate.choices,
        allowsFreeform: currentGate.allowsFreeform,
        remainsPending: true,
      },
      recovery: {
        phase: recovery.phase,
        executionFence: recovery.executionFence,
        runtimeInstanceId: recovery.runtimeInstanceId,
        recoveryMaterialMode: recovery.recoveryMaterialMode,
      },
      journal: snapshot.journal,
      committedRecoveryEventProof: recoveryEventResult,
      finishedAt: new Date().toISOString(),
    };
    await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
    await handle.sync();
    return result;
  } catch (error) {
    const eventFailed = recoveryEventResult?.status === 'failed';
    const result = {
      ...common,
      status: eventFailed ? 'failed' : error instanceof CaseError ? error.status : 'blocked',
      reason: error instanceof Error ? error.message : 'The case did not complete.',
      ...(input.caseKind !== 'pending-gate' ? {
        fullPlanContentProof: { status: 'blocked' },
        dependencyGraphProof: { status: 'blocked' },
        completedChildProof: { status: 'blocked' },
        committedRecoveryEventProof: recoveryEventResult ?? { status: 'blocked' },
        ...(input.caseKind === 'committed-effect' ? {
          physicalNoDuplicateProof: { status: 'blocked' },
          checkpointRestartDuplicateFence: { status: 'blocked' },
        } : {}),
      } : {
        committedRecoveryEventProof: recoveryEventResult ?? { status: 'blocked' },
      }),
      finishedAt: new Date().toISOString(),
    };
    await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
    await handle.sync();
    return result;
  } finally {
    await handle.close();
  }
}

export function caseExitCode(status) {
  return ['passed', 'captured', 'validated-not-run'].includes(status) ? 0
    : status === 'failed' ? 1 : 2;
}

async function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
    if (options.help) {
      process.stdout.write('Use --input <case.json> --output <new-evidence-directory> [--before-evidence <capture.json>] [--dry-run].\n');
      return 0;
    }
    const result = await runCase(options);
    process.stdout.write(`${JSON.stringify(result)}\n`);
    return caseExitCode(result.status);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    return 2;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === CASE_SCRIPT_PATH) {
  process.exitCode = await main();
}
