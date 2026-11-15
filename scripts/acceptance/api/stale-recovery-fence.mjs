#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { mkdir, open, readFile, stat, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const CASE_ID = 'stale-recovery-fence';
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
const READ_OPERATIONS = [
  'readRunStatus', 'readSessionStatus', 'readDecisions', 'readSessionTree', 'replayRunEvents',
];
const EVENT_PAYLOAD_FIELDS = {
  turn: ['role', 'content'],
  tool_call: ['callId', 'toolName', 'state'],
  policy_evaluation: ['actorId', 'tenantId', 'stepId', 'grantId', 'grantRevision', 'purpose', 'actionId',
    'outcome', 'reasonCode', 'fence', 'providerId', 'adapterVersion', 'optionsSchemaVersion', 'optionsRevision'],
  decision_accepted: ['decisionId', 'decisionType', 'selectedOption', 'acceptedEffectIds'],
  effect_accepted: ['effectId', 'effectType'],
  artifact_reference: ['artifact'],
  cache_reference: ['cache', 'runtimeVersion', 'bindingId'],
  addressed_message: ['messageId', 'sender', 'recipient', 'threadId', 'threadSequence', 'purpose'],
  produced_run_capture: ['capture', 'package'],
  runtime_configuration: ['configuration'],
};

export class CaseError extends Error {
  constructor(status, message, details = {}) {
    super(message);
    this.status = status;
    Object.assign(this, details);
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

function isIdentifier(value, maxLength = 128) {
  return typeof value === 'string' && value.length <= maxLength
    && /^[A-Za-z0-9._:-]{1,128}$/.test(value);
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
  const keys = [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256', 'approvedNonProduction',
    'approvedExecutionPermission', 'expectedActorId', 'expectedRecoveryOperationId',
    'originalRecoveryIdempotencyKey', 'caseInvocationId', 'target',
  ];
  if (!exactKeys(value, keys)
    || value.caseId !== CASE_ID
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || value.approvedExecutionPermission !== true
    || !nonEmpty(value.expectedActorId)
    || !isUuid(value.expectedRecoveryOperationId)
    || !isIdentifier(value.originalRecoveryIdempotencyKey)
    || !isIdentifier(value.caseInvocationId, 64)
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))
    || !isIdentifier(value.target.projectId)
    || !isIdentifier(value.target.runId)
    || !isIdentifier(value.target.sessionId)) {
    throw new Error('The case input does not match the stale recovery fence schema or lacks explicit permission.');
  }
  const normalized = {
    ...value,
    sourceCommit: value.sourceCommit.toLowerCase(),
    targetCommit: value.targetCommit.toLowerCase(),
    apphostSha256: value.apphostSha256.toLowerCase(),
    expectedRecoveryOperationId: value.expectedRecoveryOperationId.toLowerCase(),
    target: {
      ...value.target,
      gatewayUrl: validateUrl(value.target.gatewayUrl, 'Gateway URL', true),
      brokerIssuer: validateUrl(value.target.brokerIssuer, 'Broker issuer', false),
    },
  };
  const keysForRequests = requestKeys(normalized);
  if (keysForRequests.api === normalized.originalRecoveryIdempotencyKey
    || keysForRequests.rest === normalized.originalRecoveryIdempotencyKey) {
    throw new Error('The stale-request keys must differ from the successful recovery key.');
  }
  return normalized;
}

export function requestKeys(input) {
  const prefix = `${CASE_ID}:${input.caseInvocationId}`;
  const keys = { api: `${prefix}:api`, rest: `${prefix}:rest` };
  if (!isIdentifier(keys.api) || !isIdentifier(keys.rest)) {
    throw new Error('The case invocation ID does not produce valid request keys.');
  }
  return keys;
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

function restArgs(input, method, route, body) {
  return [
    'rest', '--method', method, '--url', route,
    '--scope', 'api.read', '--scope', 'projects.orchestrator',
    ...targetArgs(input.target),
    ...(body === undefined ? [] : ['--body', JSON.stringify(body)]),
  ];
}

function recoveryRoute(input) {
  return `/api/v1/projects/${encodeURIComponent(input.target.projectId)}`
    + `/runs/${encodeURIComponent(input.target.runId)}/coordination/recovery`;
}

export function recoveryRequest(input, idempotencyKey, executionFence, expectedRunStateVersion) {
  return {
    executionFence,
    expectedRunStateVersion,
    idempotencyKey,
    causeCode: 'stale_recovery_fence_case',
    reference: input.caseInvocationId,
  };
}

export function buildReadCommands(input, prefix) {
  return [
    { id: `${prefix}-run-status`, args: invokeArgs(input, 'readRunStatus') },
    { id: `${prefix}-root-status`, args: invokeArgs(input, 'readSessionStatus') },
    { id: `${prefix}-decisions`, args: invokeArgs(input, 'readDecisions') },
    { id: `${prefix}-tree`, args: invokeArgs(input, 'readSessionTree') },
  ];
}

function discoveryCommands(input) {
  const unbound = targetArgs(input.target, false);
  return [
    { id: 'version', args: ['--version'] },
    { id: 'auth-status', args: ['auth', 'status', '--scope', 'api.read', '--scope', 'projects.orchestrator', ...targetArgs(input.target)] },
    { id: 'api-operations', args: ['api', 'operations', ...unbound] },
    ...[...READ_OPERATIONS, 'recoverRun'].map(operation => ({
      id: `api-describe-${operation}`,
      args: ['api', 'describe', operation, ...unbound],
    })),
    { id: 'authorization-context', args: restArgs(input, 'GET', '/api/v1/authorization/context') },
  ];
}

export function childEnvironment(env) {
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

function parseResponse(record, step) {
  const response = parseJson(record.stdout, step);
  if (!isObject(response) || !Number.isInteger(response.status)
    || !Object.hasOwn(response, 'body')) {
    throw new CaseError('blocked', `${step} did not return a response envelope.`);
  }
  return response;
}

function requireReadSuccess(record, step) {
  if (record.error || record.exitCode !== 0 || record.signal || record.timedOut) {
    throw new CaseError('blocked', `${step} did not complete successfully.`);
  }
}

function readBody(response, step) {
  if (response.status !== 200 || !isObject(response.body)) {
    throw new CaseError('blocked', `${step} did not return HTTP 200 with a JSON object.`);
  }
  return response.body;
}

export function classifyNegativeResponse(record, step) {
  if (record.error || record.timedOut || record.signal || record.exitCode === null
    || record.exitCode === undefined || typeof record.stdout !== 'string' || !record.stdout.trim()) {
    throw new CaseError('blocked', `${step} delivery or response is uncertain.`);
  }
  let response;
  try {
    response = parseResponse(record, step);
  } catch {
    throw new CaseError('blocked', `${step} did not return a classifiable response.`);
  }
  if (response.status >= 200 && response.status < 300) {
    throw new CaseError('failed', `${step} was accepted instead of rejected.`, {
      cleanupStatus: 'blocked',
      response,
    });
  }
  if (response.status !== 409 || response.body?.error !== 'execution_recovery_stale') {
    throw new CaseError('blocked', `${step} did not return HTTP 409 execution_recovery_stale.`, {
      response,
    });
  }
  return { httpStatus: response.status, error: response.body.error, exitCode: record.exitCode };
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

function validEventPayload(payload) {
  const required = EVENT_PAYLOAD_FIELDS[payload?.kind];
  if (!required || required.some(field => !Object.hasOwn(payload, field))) return false;
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
      return ['actorId', 'tenantId', 'stepId', 'grantId', 'grantRevision', 'purpose', 'actionId',
        'outcome', 'reasonCode', 'providerId', 'adapterVersion', 'optionsRevision']
        .every(field => nonEmpty(payload[field]))
        && Number.isInteger(payload.fence) && Number.isInteger(payload.optionsSchemaVersion);
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
  let position = 0;
  for (let index = 0; index < pages.length; index += 1) {
    const page = pages[index];
    if (!isObject(page) || !Array.isArray(page.events)
      || typeof page.hasMore !== 'boolean'
      || !(page.nextCursor === null || typeof page.nextCursor === 'string')
      || page.nextCursor === ''
      || page.hasMore !== (index < pages.length - 1)
      || page.events.length > 0 && !page.nextCursor
      || page.hasMore && !page.events.length
      || page.nextCursor && page.nextCursor === pages[index - 1]?.nextCursor) {
      throw new CaseError('blocked', 'The session journal pages or cursors are incomplete.');
    }
    for (const event of page.events) {
      if (!isObject(event) || !isUuid(event.eventId)
        || !Number.isInteger(event.schemaVersion) || event.schemaVersion < 1
        || !Number.isInteger(event.eventVersion) || event.eventVersion < 1
        || event.position !== position + 1
        || typeof event.occurredAt !== 'string' || Number.isNaN(Date.parse(event.occurredAt))
        || event.identity?.projectId !== input.target.projectId
        || event.identity?.runId !== input.target.runId
        || !isIdentifier(event.identity?.sessionId)
        || !isObject(event.payload) || !validEventPayload(event.payload)
        || !Array.isArray(event.objectReferences) || eventIds.has(event.eventId)) {
        throw new CaseError('blocked', 'The session journal event has an invalid type, scope, order, or identity.');
      }
      eventIds.add(event.eventId);
      position = event.position;
    }
  }
  return {
    pagesRead: pages.length,
    eventCount: eventIds.size,
    lastPosition: position,
    events: pages.flatMap(page => page.events),
  };
}

function bindingView(runStatus, sessionStatus, decisions, tree) {
  return {
    run: runStatus,
    root: sessionStatus,
    decisions: {
      stateVersion: decisions?.stateVersion,
      executionFence: decisions?.executionFence,
      pendingGate: decisions?.pendingGate ?? null,
    },
    tree: {
      rootSessionId: tree?.rootSessionId,
      nodes: Array.isArray(tree?.nodes)
        ? [...tree.nodes].sort((a, b) => String(a?.identity?.sessionId).localeCompare(String(b?.identity?.sessionId)))
        : tree?.nodes,
    },
  };
}

function validateStatusBindings(input, runStatus, sessionStatus, decisions, tree) {
  const { target } = input;
  const nodes = tree?.nodes;
  const nodeIds = new Set();
  if (!Array.isArray(nodes)) {
    throw new CaseError('blocked', 'The session tree is incomplete or contains duplicate or foreign sessions.');
  }
  for (const node of nodes) {
    if (!isObject(node)
      || node.identity?.projectId !== target.projectId
      || node.identity?.runId !== target.runId
      || !isIdentifier(node.identity?.sessionId)
      || node.rootSessionId !== target.sessionId
      || !Number.isInteger(node.executionFence) || node.executionFence < 1
      || !Number.isInteger(node.stateVersion) || node.stateVersion < 1
      || nodeIds.has(node.identity.sessionId)) {
      throw new CaseError('blocked', 'The session tree is incomplete or contains duplicate or foreign sessions.');
    }
    nodeIds.add(node.identity.sessionId);
  }
  const roots = nodes.filter(node => node.identity.sessionId === target.sessionId);
  const root = roots.length === 1 ? roots[0] : null;
  if (runStatus?.projectId !== target.projectId
    || runStatus?.runId !== target.runId
    || runStatus?.rootSessionId !== target.sessionId
    || !Number.isInteger(runStatus.executionFence) || runStatus.executionFence < 1
    || !Number.isInteger(runStatus.stateVersion) || runStatus.stateVersion < 1
    || sessionStatus?.identity?.projectId !== target.projectId
    || sessionStatus?.identity?.runId !== target.runId
    || sessionStatus?.identity?.sessionId !== target.sessionId
    || sessionStatus?.rootSessionId !== target.sessionId
    || !Number.isInteger(sessionStatus.executionFence) || sessionStatus.executionFence < 1
    || !Number.isInteger(sessionStatus.stateVersion) || sessionStatus.stateVersion < 1
    || !Number.isInteger(decisions?.stateVersion) || decisions.stateVersion < 1
    || tree?.rootSessionId !== target.sessionId
    || root?.identity?.projectId !== target.projectId
    || root?.identity?.runId !== target.runId
    || root?.rootSessionId !== target.sessionId
    || runStatus.executionFence !== sessionStatus.executionFence
    || runStatus.executionFence !== decisions.executionFence
    || runStatus.executionFence !== root.executionFence) {
    throw new CaseError('blocked', 'Run, root, decisions, and tree do not share the selected binding.');
  }
  if (!isDeepStrictEqual(runStatus.checkpointRecovery, sessionStatus.checkpointRecovery)) {
    throw new CaseError('blocked', 'Run and root status do not agree on the current recovery operation.');
  }
}

function requireSettledRecovery(input, runStatus, sessionStatus) {
  const recovery = runStatus.checkpointRecovery;
  if (!isObject(recovery)
    || !isUuid(recovery.operationId)
    || recovery.operationId.toLowerCase() !== input.expectedRecoveryOperationId
    || recovery.phase !== 'ready'
    || recovery.currentStep !== 'ready'
    || recovery.executionFence !== runStatus.executionFence
    || !Number.isInteger(recovery.previousExecutionFence)
    || recovery.previousExecutionFence < 1
    || recovery.previousExecutionFence >= runStatus.executionFence
    || !isUuid(recovery.previousRuntimeInstanceId)
    || !isUuid(recovery.runtimeInstanceId)
    || recovery.runtimeInstanceId === recovery.previousRuntimeInstanceId
    || !Number.isInteger(recovery.previousRegistrationRevision)
    || recovery.previousRegistrationRevision < 1
    || !Number.isInteger(recovery.registrationRevision)
    || recovery.registrationRevision < 1
    || !Number.isInteger(recovery.decisionStateVersion)
    || recovery.decisionStateVersion < 1
    || !['nativeCache', 'journalRebuild'].includes(recovery.recoveryMaterialMode)
    || recovery.dispatchHeld === true
    || recovery.boundaryKind === 'childJoin' && recovery.dispatchHeld !== false
    || !isDeepStrictEqual(recovery, sessionStatus.checkpointRecovery)) {
    throw new CaseError('blocked', 'The selected recovery is not settled and ready at a newer fence.');
  }
  return recovery;
}

function pageArgs(input, cursor) {
  const args = invokeArgs(input, 'replayRunEvents', { limit: PAGE_SIZE });
  if (cursor) args.push('--query', `cursor=${cursor}`);
  return args;
}

function logArgs(args) {
  return args.map((value, index) => (
    args[index - 1] === '--query' && value.startsWith('cursor=') ? 'cursor=[REDACTED]' : value
  ));
}

export function compareSnapshots(left, right) {
  if (!isDeepStrictEqual(left.binding, right.binding)
    || !isDeepStrictEqual(left.journal.events, right.journal.events)
    || left.journal.lastPosition !== right.journal.lastPosition
    || left.journal.eventCount !== right.journal.eventCount) {
    throw new CaseError('blocked', 'Observable run, recovery, runtime, gate, child, or journal state changed.');
  }
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
      inputBytes: bytes,
      inputSha256: createHash('sha256').update(bytes).digest('hex'),
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

export async function runCase(options, env = process.env, commandRunner = runCommand) {
  const { input, inputBytes, inputSha256 } = await readInput(options.inputPath);
  const cliPath = resolveCliPath(env.AGENTWEAVER_CLI_PATH);
  const cliInfo = await stat(cliPath).catch(() => null);
  if (!cliInfo?.isFile()) throw new Error('AGENTWEAVER_CLI_PATH does not exist as a file.');
  const apphostSha256 = await sha256(cliPath);
  if (apphostSha256 !== input.apphostSha256) throw new Error('The apphost SHA-256 hash does not match the case input.');
  const evidencePath = await requireNewOutputDirectory(options.outputPath);
  const scriptSha256 = await sha256(CASE_SCRIPT_PATH);
  if (options.dryRun) {
    return {
      status: 'validated-not-run',
      caseId: CASE_ID,
      sourceCommit: input.sourceCommit,
      targetCommit: input.targetCommit,
      apphostSha256,
      scriptSha256,
      network: 'not-used',
      evidence: 'not-created',
    };
  }

  await mkdir(evidencePath);
  await writeFile(path.join(evidencePath, 'case-input.json'), inputBytes, {
    encoding: 'utf8',
    flag: 'wx',
    mode: 0o600,
  });
  const keys = requestKeys(input);
  await writeFile(path.join(evidencePath, 'request-keys.json'), `${JSON.stringify(keys, null, 2)}\n`, {
    encoding: 'utf8',
    flag: 'wx',
    mode: 0o600,
  });
  const commandsPath = path.join(evidencePath, 'commands.jsonl');
  const resultPath = path.join(evidencePath, 'result.json');
  const handle = await open(resultPath, 'wx', 0o600);
  const common = {
    caseId: CASE_ID,
    sourceCommit: input.sourceCommit,
    targetCommit: input.targetCommit,
    apphostSha256,
    scriptSha256,
    inputSha256,
    requestKeys: keys,
    startedAt: new Date().toISOString(),
    status: 'blocked',
    operationOutboxInventoryProof: {
      status: 'blocked',
      reason: 'No supported public projection exposes Orchestrator recovery operations and outbox contents.',
    },
  };
  const attempts = [];
  async function execute(id, args) {
    const record = commandRunner(cliPath, args, env);
    await appendRedactedJsonLine(commandsPath, {
      id, args: logArgs(args), ...record,
    });
    return record;
  }
  async function executeRead(id, args) {
    const record = await execute(id, args);
    const response = parseResponse(record, id);
    requireReadSuccess(record, id);
    return response;
  }
  async function readSnapshot(prefix) {
    const commands = buildReadCommands(input, prefix);
    const runStatus = readBody(await executeRead(commands[0].id, commands[0].args), commands[0].id);
    const sessionStatus = readBody(await executeRead(commands[1].id, commands[1].args), commands[1].id);
    const decisions = readBody(await executeRead(commands[2].id, commands[2].args), commands[2].id);
    const tree = readBody(await executeRead(commands[3].id, commands[3].args), commands[3].id);
    validateStatusBindings(input, runStatus, sessionStatus, decisions, tree);
    const pages = [];
    const seenCursors = new Set();
    let cursor = null;
    for (let pageNumber = 1; pageNumber <= MAX_PAGES; pageNumber += 1) {
      const args = pageArgs(input, cursor);
      const pageResponse = await executeRead(`${prefix}-journal-page-${pageNumber}`, args);
      if (pageResponse.status !== 200 || !isObject(pageResponse.body)) {
        throw new CaseError('blocked', `${prefix} journal page did not return HTTP 200 with an object body.`);
      }
      pages.push(pageResponse.body);
      if (pageResponse.body.hasMore !== true) break;
      const nextCursor = pageResponse.body.nextCursor;
      if (typeof nextCursor !== 'string' || !nextCursor || seenCursors.has(nextCursor)) {
        throw new CaseError('blocked', 'The session journal returned a missing or repeated cursor.');
      }
      seenCursors.add(nextCursor);
      cursor = nextCursor;
      if (pageNumber === MAX_PAGES) throw new CaseError('blocked', 'The session journal exceeded its page limit.');
    }
    const journal = validateJournalPages(pages, input);
    const finalRun = readBody(await executeRead(`${prefix}-final-run-status`, commands[0].args), `${prefix}-final-run-status`);
    const finalRoot = readBody(await executeRead(`${prefix}-final-root-status`, commands[1].args), `${prefix}-final-root-status`);
    const finalDecisions = readBody(await executeRead(`${prefix}-final-decisions`, commands[2].args), `${prefix}-final-decisions`);
    const finalTree = readBody(await executeRead(`${prefix}-final-tree`, commands[3].args), `${prefix}-final-tree`);
    validateStatusBindings(input, finalRun, finalRoot, finalDecisions, finalTree);
    if (!isDeepStrictEqual(
      bindingView(runStatus, sessionStatus, decisions, tree),
      bindingView(finalRun, finalRoot, finalDecisions, finalTree),
    )) {
      throw new CaseError('blocked', 'Run, root, or gate state changed while the session journal was read.');
    }
    return {
      binding: bindingView(runStatus, sessionStatus, decisions, tree),
      journal: {
        pagesRead: journal.pagesRead,
        eventCount: journal.eventCount,
        lastPosition: journal.lastPosition,
        events: journal.events,
      },
    };
  }
  async function runDiscovery() {
    const commands = discoveryCommands(input);
    for (const command of commands.slice(0, 2)) {
      const record = await execute(command.id, command.args);
      requireReadSuccess(record, command.id);
      if (command.id === 'auth-status') {
        const auth = parseJson(record.stdout, command.id);
        if (auth.isSignedIn !== true || auth.tenantId !== input.target.tenantId) {
          throw new CaseError('blocked', 'The selected CLI identity is not signed in for the case tenant.');
        }
      }
    }
    const listedRecord = await execute(commands[2].id, commands[2].args);
    requireReadSuccess(listedRecord, commands[2].id);
    const listed = parseJson(listedRecord.stdout, commands[2].id);
    const operationIds = new Set(Array.isArray(listed)
      ? listed.map(item => typeof item === 'string' ? item : item?.operationId)
      : []);
    for (const command of commands.slice(3, 3 + READ_OPERATIONS.length + 1)) {
      const operation = command.id.slice('api-describe-'.length);
      if (!operationIds.has(operation)) throw new CaseError('blocked', `Gateway discovery does not list ${operation}.`);
      const record = await execute(command.id, command.args);
      requireReadSuccess(record, command.id);
      parseJson(record.stdout, command.id);
    }
    const contextRecord = await execute(commands.at(-1).id, commands.at(-1).args);
    const context = parseResponse(contextRecord, 'authorization-context');
    requireReadSuccess(contextRecord, 'authorization-context');
    if (context.status !== 200) throw new CaseError('blocked', 'Authorization context did not return HTTP 200.');
    validateAuthorizationContext(context.body, input);
  }
  async function rejectRequest(id, args) {
    const record = await execute(id, args);
    try {
      const result = classifyNegativeResponse(record, id);
      attempts.push({ id, ...result, classification: 'rejected' });
      return result;
    } catch (error) {
      let response = null;
      try {
        response = parseResponse(record, id);
      } catch {
        response = null;
      }
      attempts.push({
        id,
        classification: error instanceof CaseError ? error.status : 'blocked',
        exitCode: record.exitCode,
        signal: record.signal,
        timedOut: record.timedOut,
        ...(response ? {
          httpStatus: response.status,
          error: isObject(response.body) ? response.body.error : undefined,
        } : {}),
      });
      throw error;
    }
  }

  try {
    await runDiscovery();
    const before = await readSnapshot('before');
    const runStatus = before.binding.run;
    const recovery = requireSettledRecovery(input, runStatus, before.binding.root);
    if (recovery.previousExecutionFence >= runStatus.executionFence) {
      throw new CaseError('blocked', 'The recorded recovery fence is not older than the current fence.');
    }
    const request = recoveryRequest(input, keys.api, recovery.previousExecutionFence, runStatus.stateVersion);
    const apiArgs = [
      'api', 'invoke', 'recoverRun',
      '--path', `projectId=${input.target.projectId}`,
      '--path', `runId=${input.target.runId}`,
      '--scope', 'api.read', '--scope', 'projects.orchestrator',
      ...targetArgs(input.target),
      '--body', JSON.stringify(request),
    ];
    await rejectRequest('stale-request-api-invoke', apiArgs);

    const recheck = await readSnapshot('after-api');
    compareSnapshots(before, recheck);

    const restRequest = { ...request, idempotencyKey: keys.rest };
    await rejectRequest('stale-request-rest', restArgs(input, 'POST', recoveryRoute(input), restRequest));

    const after = await readSnapshot('after-rest');
    compareSnapshots(before, after);
    compareSnapshots(recheck, after);
    const result = {
      ...common,
      status: 'passed',
      cleanupStatus: 'not-required',
      actorId: input.expectedActorId,
      operationId: input.expectedRecoveryOperationId,
      previousExecutionFence: recovery.previousExecutionFence,
      executionFence: runStatus.executionFence,
      expectedRunStateVersion: runStatus.stateVersion,
      publicRequestPredicate: 'Both public recovery requests using the prior fence were rejected, and the observable bindings stayed unchanged.',
      attempts,
      bindingsUnchanged: true,
      journal: {
        pagesRead: before.journal.pagesRead,
        eventCount: before.journal.eventCount,
        lastPosition: before.journal.lastPosition,
      },
      nativeNoSendProof: 'not-produced',
      physicalEffectProof: 'not-produced',
      finishedAt: new Date().toISOString(),
    };
    await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
    await handle.sync();
    return result;
  } catch (error) {
    const result = {
      ...common,
      status: error instanceof CaseError ? error.status : 'blocked',
      ...(error?.cleanupStatus ? { cleanupStatus: error.cleanupStatus } : {}),
      ...(error?.response ? { unexpectedResponse: error.response } : {}),
      attempts,
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
  return status === 'passed' || status === 'validated-not-run' ? 0
    : status === 'failed' ? 1 : 2;
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
    return caseExitCode(result.status);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    return 2;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === CASE_SCRIPT_PATH) {
  process.exitCode = await main();
}
