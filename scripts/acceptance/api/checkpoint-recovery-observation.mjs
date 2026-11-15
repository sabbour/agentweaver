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
const COMMAND_TIMEOUT_MS = 60_000;
const PAGE_SIZE = 100;
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
const DISCOVERY_COMMAND_COUNT = 3 + OPERATIONS.length;
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

function isUuid(value) {
  return typeof value === 'string'
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
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
    'expectedGateRequestId', 'target',
  ];
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
    || value.caseKind !== 'pending-gate'
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || !nonEmpty(value.expectedActorId)
    || !isUuid(value.expectedRecoveryOperationId)
    || !isIdentifier(value.expectedGateRequestId)
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
  if (['readSessionStatus', 'readDecisions', 'readSessionTree'].includes(operation)) {
    args.splice(5, 0, '--path', `sessionId=${target.sessionId}`);
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

export function buildCommands(input) {
  const unbound = targetArgs(input.target, false);
  const bound = targetArgs(input.target);
  return [
    { id: 'version', args: ['--version'] },
    { id: 'auth-status', args: ['auth', 'status', '--scope', 'api.read', '--scope', 'projects.orchestrator', ...bound] },
    { id: 'api-operations', args: ['api', 'operations', ...unbound] },
    ...OPERATIONS.map(operation => ({
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

export function compareStatusApiAndRest(apiValue, restValue, label) {
  if (!isDeepStrictEqual(apiValue, restValue)) {
    throw new CaseError('failed', `${label} returned different API and REST status bodies.`);
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

function statusBinding(runStatus, sessionStatus, decisions) {
  const recovery = runStatus?.checkpointRecovery;
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
    recovery: recovery && {
      operationId: recovery.operationId,
      phase: recovery.phase,
      currentStep: recovery.currentStep,
      executionFence: recovery.executionFence,
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

function verifyBeforeCommandRecords(capture, input, records) {
  if (!Array.isArray(records)) {
    throw new CaseError('blocked', 'The retained before-capture command records are missing.');
  }
  const commands = buildCommands(input);
  if (records.length < commands.length + 6) {
    throw new CaseError('blocked', 'The retained before-capture command records are incomplete.');
  }
  let index = 0;
  for (const command of commands) {
    const commandId = index < DISCOVERY_COMMAND_COUNT
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
  if (OPERATIONS.some(operation => !operationIds.has(operation))) {
    throw new CaseError('blocked', 'The retained API discovery record does not list every required operation.');
  }
  const { outputs } = capture;
  const context = restBody(commandRecord(records, 8, 'initial-authorization-context',
    commands[8].args), 'rest-authorization-context');
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
    || capture.formatVersion !== 1
    || capture.status !== 'captured'
    || capture.caseId !== CASE_ID
    || capture.caseKind !== 'pending-gate'
    || capture.mode !== 'before-capture'
    || capture.sourceCommit !== input.sourceCommit
    || capture.targetCommit !== input.targetCommit
    || capture.apphostSha256 !== apphostSha256
    || capture.scriptSha256 !== scriptSha256
    || capture.actorId !== input.expectedActorId
    || !sameTarget(capture.target, input.target)
    || !isObject(capture.input)
    || capture.input.sha256 !== inputSha256
    || capture.input.gateRequestId !== input.expectedGateRequestId
    || capture.commandsSha256 !== rawCommandSha256
    || !isObject(capture.gate)
    || capture.gate.requestId !== input.expectedGateRequestId
    || capture.gate.actorId !== input.expectedActorId
    || !Array.isArray(capture.gate.choices)
    || typeof capture.gate.allowsFreeform !== 'boolean'
    || !isObject(capture.outputs)
    || !Array.isArray(capture.outputs.events)) {
    throw new CaseError('blocked', 'The before-capture evidence is missing, incomplete, or bound to another case.');
  }
  const beforeGate = gateFacts(
    capture.outputs.decisions,
    capture.outputs.sessionStatus,
    input.expectedGateRequestId,
    input,
  );
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
  if (!isDeepStrictEqual(beforeGate, capture.gate)
    || beforeGate.fence !== capture.outputs.runStatus.executionFence) {
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

  async function readSnapshot(prefix) {
    const commands = buildCommands(input);
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
      for (let index = 3; index < 3 + OPERATIONS.length; index += 1) {
        const operation = OPERATIONS[index - 3];
        if (!operationIds.has(operation)) {
          throw new CaseError('blocked', `Gateway discovery does not list ${operation}.`);
        }
        await execute(`api-describe-${operation}`, commands[index].args);
      }
    }
    const base = DISCOVERY_COMMAND_COUNT;
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
    compareStatusApiAndRest(runStatus, runStatusRest, 'Run status');
    const sessionStatus = apiResponseBody(
      await execute(`${prefix}-session-status`, commands[base + 3].args), `${prefix}-session-status`,
    );
    const sessionStatusRest = restBody(
      await execute(`${prefix}-session-status-rest`, commands[base + 4].args, 'text'),
      `${prefix}-session-status-rest`,
    );
    compareStatusApiAndRest(sessionStatus, sessionStatusRest, 'Root-session status');
    const decisions = apiResponseBody(
      await execute(`${prefix}-decisions`, commands[base + 5].args), `${prefix}-decisions`,
    );
    const tree = apiResponseBody(
      await execute(`${prefix}-session-tree`, commands[base + 6].args), `${prefix}-session-tree`,
    );
    validateRootBindings(runStatus, sessionStatus, decisions, tree, input);

    const events = [];
    const seenCursors = new Set();
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
    const finalRunStatus = apiResponseBody(
      await execute(`${prefix}-final-run-status`, commands[base + 1].args),
      `${prefix}-final-run-status`,
    );
    const finalRunStatusRest = restBody(
      await execute(`${prefix}-final-run-status-rest`, commands[base + 2].args, 'text'),
      `${prefix}-final-run-status-rest`,
    );
    compareStatusApiAndRest(finalRunStatus, finalRunStatusRest, `${prefix} final run status`);
    const finalSessionStatus = apiResponseBody(
      await execute(`${prefix}-final-session-status`, commands[base + 3].args),
      `${prefix}-final-session-status`,
    );
    const finalSessionStatusRest = restBody(
      await execute(`${prefix}-final-session-status-rest`, commands[base + 4].args, 'text'),
      `${prefix}-final-session-status-rest`,
    );
    compareStatusApiAndRest(finalSessionStatus, finalSessionStatusRest, `${prefix} final root-session status`);
    const finalDecisions = apiResponseBody(
      await execute(`${prefix}-final-decisions`, commands[base + 5].args),
      `${prefix}-final-decisions`,
    );
    validateRootBindings(finalRunStatus, finalSessionStatus, finalDecisions, tree, input);
    if (!isDeepStrictEqual(
      statusBinding(runStatus, sessionStatus, decisions),
      statusBinding(finalRunStatus, finalSessionStatus, finalDecisions),
    )) {
      throw new CaseError('blocked', 'Run, root-session, or gate authority changed during journal paging.');
    }
    return {
      context,
      runStatus,
      sessionStatus,
      decisions,
      tree,
      events,
      journal,
      finalAuthority: {
        runStatus: finalRunStatus,
        sessionStatus: finalSessionStatus,
        decisions: finalDecisions,
      },
    };
  }

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
    const snapshot = await readSnapshot('initial');
    const gate = gateFacts(
      snapshot.decisions, snapshot.sessionStatus, input.expectedGateRequestId, input,
    );
    if (input.mode === 'before-capture') {
      if (gate.fence !== snapshot.runStatus.executionFence) {
        throw new CaseError('blocked', 'The captured gate fence does not match the run fence.');
      }
      const capture = {
        formatVersion: 1,
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
          gateRequestId: input.expectedGateRequestId,
        },
        commandsSha256: await sha256(commandsPath),
        gate,
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
        gateRequestId: gate.requestId,
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
    const finalSnapshot = await readSnapshot('final');
    const { gate: currentGate, recovery } = validateAfterObservation(
      input, before, snapshot, finalSnapshot,
    );
    const result = {
      ...common,
      status: 'passed',
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
      committedRecoveryEventProof: {
        status: 'blocked',
        reason: 'The public session journal has no supported checkpoint-recovery progress projection.',
      },
      finishedAt: new Date().toISOString(),
    };
    await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
    await handle.sync();
    return result;
  } catch (error) {
    const result = {
      ...common,
      status: error instanceof CaseError ? error.status : 'blocked',
      reason: error instanceof Error ? error.message : 'The case did not complete.',
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
