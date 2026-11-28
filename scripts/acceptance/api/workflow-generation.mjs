#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, rm, stat } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';
import { buildNumericMatrix } from './numeric-case-matrix.mjs';
import {
  TURN_READ_OPERATION, TURN_READ_ROUTE, buildTurnDigestCommand, hasTurnDigestHelp, supportsTurnDigest,
} from './turn-digest.mjs';

const CASE_ID = 'workflow-generation';
const CREDIT_ZERO_CASE_ID = 'workflow-generation-credit-zero';
const SUPPORTED_CASE_IDS = new Set([CASE_ID, CREDIT_ZERO_CASE_ID]);
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 8 * 1024 * 1024;
const COMMAND_TIMEOUT_MS = 60_000;
const JOB_DEADLINE_MS = 10 * 60_000;
const JOB_POLL_INTERVAL_MS = 2_000;
const CLEANUP_DEADLINE_MS = 90_000;
const CLEANUP_REPLAY_INTERVAL_MS = 5_000;
const CLEANUP_POLL_INTERVAL_MS = 1_000;
const MAX_CLEANUP_REPLAYS = 2;
const TARGET_KEYS = [
  'gatewayUrl', 'brokerIssuer', 'clientId', 'apiAudience', 'tenantId',
  'projectId', 'runId', 'sessionId',
];
const SELECTION_KEYS = [
  'projectConfigurationRevision', 'modelSelectionReference', 'runLimits',
];
const RUN_LIMIT_KEYS = [
  'maxModelTurns', 'maxToolCalls', 'maxChildren', 'maxConcurrentChildren',
  'maxWallTimeSeconds', 'maxPromptTokens',
];
const CREDIT_ZERO_RUN_LIMIT_KEYS = [...RUN_LIMIT_KEYS, 'copilotHardCreditLimit'];
const TARGET_OPTIONS = [
  ['--gateway-url', 'gatewayUrl'],
  ['--broker-issuer', 'brokerIssuer'],
  ['--client-id', 'clientId'],
  ['--api-audience', 'apiAudience'],
  ['--tenant-id', 'tenantId'],
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
const REQUIRED_OPERATIONS = [
  'getAuthorizationContext',
  'getRunSelection',
  'generateProjectWorkflow',
  'readProjectWorkflowGenerationJob',
  'readProjectWorkflowGenerationResult',
  'cancelProjectRun',
  'readRunStatus',
];
const CREDIT_ZERO_REQUIRED_OPERATIONS = [
  'getAuthorizationContext',
  'getRunSelection',
  'readSessionStatus',
  'readDecisions',
  'generateProjectWorkflow',
  'readProjectWorkflowGenerationJob',
  'cancelProjectRun',
  'readRunStatus',
];
const RUNTIME_START_QUEUE_FULL = 'runtime_start_queue_full';
const USAGE = 'Use --input <case.json> --output <new-evidence-directory> [--dry-run].';

class BlockedCaseError extends Error {}

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
      throw new Error(USAGE);
    }
    options[option] = argv[++index];
  }
  if (!options['--help'] && (!options['--input'] || !options['--output'])) {
    throw new Error(USAGE);
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

function nonEmpty(value, maximum = 512) {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= maximum;
}

function positiveInteger(value) {
  return Number.isSafeInteger(value) && value > 0;
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

function isExplicitZeroToken(token) {
  return typeof token === 'string' && /^0(?:\.0+)?(?:[eE][+-]?\d+)?$/.test(token);
}

function parseJsonWithCreditToken(text, step) {
  const tokens = new WeakMap();
  let value;
  try {
    value = JSON.parse(text, function captureCreditToken(key, item, context) {
      if (key === 'copilotHardCreditLimit' && typeof item === 'number' && context && typeof context.source === 'string') {
        let map = tokens.get(this);
        if (!map) {
          map = new Map();
          tokens.set(this, map);
        }
        map.set(key, context.source);
      }
      return item;
    });
  } catch (error) {
    if (error instanceof SyntaxError) {
      throw new Error(`${step} did not return valid JSON.`);
    }
    throw error;
  }
  return { value, tokens };
}

function validateRunLimits(value, creditZero = false, numberTokens = null) {
  const keys = creditZero ? CREDIT_ZERO_RUN_LIMIT_KEYS : RUN_LIMIT_KEYS;
  if (!exactKeys(value, keys)
    || !RUN_LIMIT_KEYS.every(key => Number.isSafeInteger(value[key]))
    || value.maxModelTurns < 1
    || value.maxToolCalls < 0
    || value.maxChildren < 0
    || value.maxConcurrentChildren < 0
    || value.maxConcurrentChildren > value.maxChildren
    || value.maxWallTimeSeconds < 1
    || value.maxPromptTokens < 1024) {
    throw new Error('The expected run limits must be finite, valid Copilot limits.');
  }
  if (creditZero) {
    if (value.copilotHardCreditLimit !== 0) {
      throw new Error('The credit-zero case requires runLimits.copilotHardCreditLimit to be numeric 0.');
    }
    if (numberTokens !== null && !isExplicitZeroToken(numberTokens.get(value)?.get('copilotHardCreditLimit'))) {
      throw new Error('The credit-zero case requires an explicit zero JSON number for copilotHardCreditLimit.');
    }
  }
  return { ...value };
}

export function validateInput(value, numberTokens = null) {
  const isCreditZero = value?.caseId === CREDIT_ZERO_CASE_ID;
  const limitKeys = isCreditZero ? CREDIT_ZERO_RUN_LIMIT_KEYS : RUN_LIMIT_KEYS;
  if (!exactKeys(value, [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'approvedDisposableRun', 'expectedActorId',
    'expectedDecisionStateVersion', 'expectedSelection', 'prompt', 'target',
  ])
    || !SUPPORTED_CASE_IDS.has(value.caseId)
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || value.approvedDisposableRun !== true
    || !nonEmpty(value.expectedActorId)
    || !positiveInteger(value.expectedDecisionStateVersion)
    || (isCreditZero && value.expectedDecisionStateVersion !== 1)
    || !nonEmpty(value.prompt, 2048)
    || value.prompt.includes('\0')
    || [...value.prompt].some(character => /\p{Cc}/u.test(character) && !['\r', '\n', '\t'].includes(character))
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))
    || !nonEmpty(value.target.projectId, 256)
    || !nonEmpty(value.target.runId, 256)
    || !nonEmpty(value.target.sessionId, 256)
    || !exactKeys(value.expectedSelection, SELECTION_KEYS)
    || !positiveInteger(value.expectedSelection.projectConfigurationRevision)
    || !nonEmpty(value.expectedSelection.modelSelectionReference, 256)
    || !exactKeys(value.expectedSelection.runLimits, limitKeys)) {
    throw new Error('The case input does not match the workflow-generation case schema.');
  }
  const target = {
    ...value.target,
    gatewayUrl: validateUrl(value.target.gatewayUrl, 'Gateway URL', true),
    brokerIssuer: validateUrl(value.target.brokerIssuer, 'Broker issuer', false),
  };
  return {
    caseId: value.caseId,
    sourceCommit: value.sourceCommit.toLowerCase(),
    targetCommit: value.targetCommit.toLowerCase(),
    apphostSha256: value.apphostSha256.toLowerCase(),
    approvedNonProduction: true,
    approvedDisposableRun: true,
    expectedActorId: value.expectedActorId,
    expectedDecisionStateVersion: value.expectedDecisionStateVersion,
    expectedSelection: {
      projectConfigurationRevision: value.expectedSelection.projectConfigurationRevision,
      modelSelectionReference: value.expectedSelection.modelSelectionReference,
      runLimits: validateRunLimits(value.expectedSelection.runLimits, isCreditZero, numberTokens),
    },
    prompt: value.prompt,
    target,
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

function targetArgs(input) {
  return TARGET_OPTIONS.flatMap(([option, key]) => [option, input.target[key]]);
}

function runBoundArgs(input) {
  return [
    '--scope', 'projects.orchestrator',
    '--project-id', input.target.projectId,
    '--run-id', input.target.runId,
  ];
}

export function buildCommands(input) {
  const target = targetArgs(input);
  const run = runBoundArgs(input);
  const commands = [
    { id: 'version', args: ['--version'] },
    ...(input.caseId === CREDIT_ZERO_CASE_ID ? [] : [{ id: 'help', args: ['--help'], metadataOnly: true }]),
    { id: 'api-operations', args: ['api', 'operations', ...target] },
    ...(input.caseId === CREDIT_ZERO_CASE_ID
      ? []
      : [{
          id: 'api-describe-turn-digest',
          args: ['api', 'describe', TURN_READ_OPERATION, ...target],
          metadataOnly: true,
        }]),
    { id: 'auth-status-run', args: ['auth', 'status', ...target, ...run] },
    { id: 'auth-status-project-writer', args: ['auth', 'status', ...target, '--scope', 'projects.admin'] },
    {
      id: 'api-invoke-authorization-context',
      args: ['api', 'invoke', 'getAuthorizationContext', ...target, ...run],
    },
    {
      id: 'api-invoke-admin-authorization-context',
      args: ['api', 'invoke', 'getAuthorizationContext', ...target, '--scope', 'projects.admin'],
    },
    {
      id: 'api-invoke-run-selection',
      args: [
        'api', 'invoke', 'getRunSelection', ...target,
        '--path', `projectId=${input.target.projectId}`,
        '--path', `runId=${input.target.runId}`,
        ...run,
      ],
    },
  ];
  if (input.caseId === CREDIT_ZERO_CASE_ID) {
    commands.push(
      {
        id: 'api-invoke-session-status',
        args: [
          'api', 'invoke', 'readSessionStatus', ...target,
          '--path', `projectId=${input.target.projectId}`,
          '--path', `runId=${input.target.runId}`,
          '--path', `sessionId=${input.target.sessionId}`,
          ...run,
        ],
      },
      {
        id: 'api-invoke-decisions',
        args: [
          'api', 'invoke', 'readDecisions', ...target,
          '--path', `projectId=${input.target.projectId}`,
          '--path', `runId=${input.target.runId}`,
          '--path', `sessionId=${input.target.sessionId}`,
          ...run,
        ],
      },
    );
  }
  return commands;
}

export function buildGenerateCommand(input, idempotencyKey) {
  return {
    id: 'api-invoke-generate-workflow',
    args: [
      'api', 'invoke', 'generateProjectWorkflow',
      ...targetArgs(input),
      '--path', `projectId=${input.target.projectId}`,
      ...runBoundArgs(input),
      '--header', `Idempotency-Key=${idempotencyKey}`,
      '--body', JSON.stringify({
        runId: input.target.runId,
        sessionId: input.target.sessionId,
        expectedDecisionStateVersion: input.expectedDecisionStateVersion,
        prompt: input.prompt,
      }),
    ],
  };
}

export function buildJobStatusCommand(input, jobId) {
  return {
    id: 'api-invoke-generation-job-status',
    args: [
      'api', 'invoke', 'readProjectWorkflowGenerationJob',
      ...targetArgs(input),
      '--path', `projectId=${input.target.projectId}`,
      '--path', `jobId=${jobId}`,
      ...runBoundArgs(input),
    ],
  };
}

export function buildJobResultCommand(input, jobId) {
  return {
    id: 'api-invoke-generation-result',
    args: [
      'api', 'invoke', 'readProjectWorkflowGenerationResult',
      ...targetArgs(input),
      '--path', `projectId=${input.target.projectId}`,
      '--path', `jobId=${jobId}`,
      ...runBoundArgs(input),
    ],
  };
}

export function buildCleanupCommands(input, idempotencyKey, body = { reason: 'workflow-generation acceptance cleanup' }) {
  return [
    {
      id: 'api-invoke-cancel-disposable-run',
      args: [
        'api', 'invoke', 'cancelProjectRun',
        ...targetArgs(input),
        '--path', `projectId=${input.target.projectId}`,
        '--path', `runId=${input.target.runId}`,
        '--scope', 'projects.admin',
        '--header', `Idempotency-Key=${idempotencyKey}`,
        '--body', JSON.stringify(body),
      ],
    },
    {
      id: 'api-invoke-verify-run-cleanup',
      args: [
        'api', 'invoke', 'readRunStatus',
        ...targetArgs(input),
        '--path', `projectId=${input.target.projectId}`,
        '--path', `runId=${input.target.runId}`,
        ...runBoundArgs(input),
      ],
    },
  ];
}

export function childEnvironment(env) {
  const childEnv = { ...env };
  const excluded = new Set([...TARGET_ENV, 'AGENTWEAVER_CLI_PATH']);
  for (const key of Object.keys(childEnv)) {
    if (excluded.has(key.toUpperCase())) delete childEnv[key];
  }
  return childEnv;
}

async function sha256(filePath) {
  return createHash('sha256').update(await readFile(filePath)).digest('hex');
}

function runCommand(cliPath, args, env, timeoutMs = COMMAND_TIMEOUT_MS) {
  const startedAt = new Date();
  const result = spawnSync(cliPath, args, {
    shell: false,
    windowsHide: true,
    encoding: 'utf8',
    timeout: timeoutMs,
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

function safeArguments(args) {
  const safe = [];
  for (let index = 0; index < args.length; index += 1) {
    const value = args[index];
    safe.push(value);
    if (value === '--body' && typeof args[index + 1] === 'string') {
      const body = args[++index];
      safe.push(`<redacted sha256=${createHash('sha256').update(body).digest('hex')}>`);
    } else if (value === '--header' && typeof args[index + 1] === 'string') {
      const header = args[++index];
      const separator = header.indexOf('=');
      safe.push(separator < 0 ? '<redacted>' : `${header.slice(0, separator)}=<redacted>`);
    }
  }
  return safe;
}

function requireSuccess(record, step) {
  if (record.error || record.exitCode !== 0 || record.signal || record.timedOut) {
    throw new Error(`${step} did not complete successfully.`);
  }
}

function processMetadata(record) {
  return {
    startedAt: record.startedAt,
    completedAt: record.completedAt,
    durationMs: record.durationMs,
    exitCode: record.exitCode,
    signal: record.signal,
    timedOut: record.timedOut,
    processError: Boolean(record.error),
  };
}

function parseJson(text, step) {
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${step} did not return valid JSON.`);
  }
}

function apiResponse(record, step, expectedStatus) {
  const response = parseJson(record.stdout, step);
  if (response?.status !== expectedStatus || response.body === undefined || response.body === null) {
    throw new Error(`${step} did not return HTTP ${expectedStatus}.`);
  }
  return response.body;
}

const QUEUE_FULL = 'workflow_generation_queue_full';

function isQueueFull(record) {
  if (record.exitCode !== 1 || record.error || record.signal || record.timedOut) return false;
  try {
    const response = JSON.parse(record.stdout);
    return response?.status === 503 && response.body?.error === QUEUE_FULL;
  } catch {
    return false;
  }
}

export function validateAuthStatus(status, input, bound) {
  if (status?.isSignedIn !== true) return false;
  if (status.tenantId !== input.target.tenantId) {
    throw new Error('auth status returned a tenant that does not match the case input.');
  }
  if (bound && (status.projectId !== input.target.projectId || status.runId !== input.target.runId)) {
    throw new Error('auth status returned a project or run that does not match the case input.');
  }
  if (!bound && (status.projectId != null || status.runId != null)) {
    throw new Error('auth status returned a bound identity for the project writer.');
  }
  return true;
}

export function validateCallerContext(context, input, bound = true) {
  if (context?.actorId !== input.expectedActorId || context?.tenantId !== input.target.tenantId) {
    throw new Error('The current caller context does not match the expected actor and tenant.');
  }
  if (bound && (context?.boundProjectId !== input.target.projectId || context?.boundRunId !== input.target.runId)) {
    throw new Error('The current caller context does not match the expected bound project and run.');
  }
  if (!bound && (context?.boundProjectId != null || context?.boundRunId != null)) {
    throw new Error('The admin caller context must be unbound.');
  }
  return context;
}

export function validateAcceptedSelection(selection, input, selectionTokens = null) {
  const expected = input.expectedSelection;
  const limits = selection?.runLimits;
  const mismatches = [];
  if (selection?.projectId !== input.target.projectId || selection?.runId !== input.target.runId)
    mismatches.push('project or run identity');
  if (selection?.projectConfigurationRevision !== expected.projectConfigurationRevision)
    mismatches.push('project configuration revision');
  if (selection?.modelSelection?.reference !== expected.modelSelectionReference)
    mismatches.push('model selection');
  if (input.caseId === CREDIT_ZERO_CASE_ID && selection?.modelSelection?.sourceMode !== 'hostedCopilot')
    mismatches.push('model selection sourceMode');
  if (!limits || RUN_LIMIT_KEYS.some(key => limits[key] !== expected.runLimits[key]))
    mismatches.push('run limits');
  if (input.caseId === CREDIT_ZERO_CASE_ID) {
    if (limits?.copilotHardCreditLimit !== 0
      || (selectionTokens !== null
        && !isExplicitZeroToken(selectionTokens.get(limits)?.get('copilotHardCreditLimit')))) {
      mismatches.push('copilotHardCreditLimit');
    }
  }
  if (mismatches.length > 0) {
    throw new Error(`The accepted ${mismatches.join(', ')} do not match the case input.`);
  }
}

export function validateReadyRootSession(sessionStatus, input) {
  const startup = sessionStatus?.runtimeStartup;
  const identity = sessionStatus?.identity;
  if (identity?.projectId !== input.target.projectId
    || identity?.runId !== input.target.runId
    || identity?.sessionId !== input.target.sessionId
    || sessionStatus?.parentSessionId != null
    || sessionStatus?.rootSessionId !== input.target.sessionId
    || sessionStatus?.kind !== 'coordinator'
    || sessionStatus?.lifecycle !== 'active'
    || !['idle', 'active', 'blocked'].includes(sessionStatus?.runExecution?.state)
    || startup?.state !== 'ready'
    || !isUuid(startup?.runtimeInstanceId)
    || !positiveInteger(startup?.registrationRevision)
    || !positiveInteger(sessionStatus?.executionFence)
    || startup?.executionFence !== sessionStatus.executionFence) {
    throw new Error('The session status does not identify a ready nonterminal coordinator root.');
  }
  return {
    runtimeInstanceId: startup.runtimeInstanceId,
    registrationRevision: startup.registrationRevision,
    executionFence: sessionStatus.executionFence,
  };
}

export function validateInitialDecisions(decisions, input, readySession) {
  if (decisions?.executionFence !== readySession.executionFence
    || decisions?.stateVersion !== 1
    || decisions?.stateVersion !== input.expectedDecisionStateVersion) {
    throw new Error('The coordinator decision state is not an initial version-1 state in the current fence.');
  }
}

export function validateZeroCreditNoSend(proof, readySession) {
  if (!proof
    || proof.runtimeInstanceId?.toLowerCase() !== readySession.runtimeInstanceId.toLowerCase()
    || proof.registrationRevision !== readySession.registrationRevision
    || proof.executionFence !== readySession.executionFence
    || !isUuid(proof.nativeRequestId)
    || !isSha256(proof.receiptSha256)
    || !isSha256(proof.beforeCursorSha256)
    || !isSha256(proof.afterCursorSha256)
    || proof.beforeCursorSha256.toLowerCase() !== proof.afterCursorSha256.toLowerCase()
    || !Number.isFinite(Date.parse(proof.recordedAt))
    || !Number.isFinite(Date.parse(proof.verifiedAt))
    || proof.stage !== 'hostPreQueue'
    || proof.code !== 'hardLimitZero'
    || proof.observedEventCount !== 0
    || proof.rangeComplete !== true) {
    throw new Error('The zeroCreditNoSend proof does not match the ready runtime registration and no-send contract.');
  }
  return {
    nativeRequestId: proof.nativeRequestId,
    runtimeInstanceId: proof.runtimeInstanceId,
    registrationRevision: proof.registrationRevision,
    executionFence: proof.executionFence,
    stage: proof.stage,
    code: proof.code,
    receiptSha256: proof.receiptSha256,
    observedEventCount: proof.observedEventCount,
    rangeComplete: proof.rangeComplete,
    beforeCursorSha256: proof.beforeCursorSha256,
    afterCursorSha256: proof.afterCursorSha256,
    recordedAt: proof.recordedAt,
    verifiedAt: proof.verifiedAt,
  };
}

function isUuid(value) {
  return typeof value === 'string'
    && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(value);
}

function isSha256(value) {
  return typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value);
}

function expectedPublicUrl(value, gatewayUrl, expectedPath, name) {
  let url;
  try {
    url = new URL(value, gatewayUrl);
  } catch {
    throw new Error(`The owner returned an invalid ${name}.`);
  }
  if (url.origin !== gatewayUrl
    || url.pathname !== expectedPath
    || url.search
    || url.hash) {
    throw new Error(`The ${name} does not use the public Gateway route.`);
  }
}

function workflowJobPath(input, jobId) {
  return `/api/v1/projects/${encodeURIComponent(input.target.projectId)}/workflows/generation-jobs/${encodeURIComponent(jobId)}`;
}

export function validateJobEnvelope(envelope, input, expectedJobId = null) {
  const jobId = envelope?.jobId;
  if (!isUuid(jobId)
    || (expectedJobId !== null && jobId.toLowerCase() !== expectedJobId.toLowerCase())
    || envelope.projectId !== input.target.projectId
    || envelope.runId !== input.target.runId
    || envelope.sessionId !== input.target.sessionId
    || !['queued', 'running', 'completed', 'failed', 'cancelled'].includes(envelope.status)) {
    throw new Error('The workflow-generation job identity or state is invalid.');
  }
  const statusPath = workflowJobPath(input, jobId);
  expectedPublicUrl(envelope.statusUrl, input.target.gatewayUrl, statusPath, 'job status URL');
  expectedPublicUrl(envelope.resultUrl, input.target.gatewayUrl, `${statusPath}/result`, 'job result URL');
  return jobId;
}

function systemTextJsonStringify(value) {
  return JSON.stringify(value)
    .replace(/\\"/g, '\\u0022')
    .replace(/[\u007f-\uffff&'`+<>]/g, character =>
      `\\u${character.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0')}`);
}

function calculatedArtifactId(jobId, workflowId, revision, definitionSha256) {
  const input = `${jobId.toLowerCase()}\0${workflowId}\0${revision}\0${definitionSha256.toLowerCase()}`;
  const digest = createHash('sha256').update(input).digest('hex').slice(0, 32);
  return `${digest.slice(0, 8)}-${digest.slice(8, 12)}-${digest.slice(12, 16)}-${digest.slice(16, 20)}-${digest.slice(20)}`;
}

function validateNativeCompletion(completion, input) {
  const requiredIds = [
    'requestId', 'runtimeInstanceId', 'observeGrantId', 'nativeMessageId',
    'nativeTurnStartEventId', 'nativeTurnEndEventId', 'nativeCompletionReceiptEventId',
  ];
  const requiredHashes = [
    'promptSha256', 'outputSha256', 'registrationSha256', 'acceptedSelectionHash',
    'sourceSha256', 'sdkSessionSha256', 'nativeCursorBeforeSha256',
    'nativeCursorAfterSha256', 'nativeEventIntervalSha256',
  ];
  if (completion?.contractVersion !== 1
    || requiredIds.some(key => !isUuid(completion[key]))
    || requiredHashes.some(key => !isSha256(completion[key]))
    || !Number.isSafeInteger(completion.registrationRevision)
    || completion.registrationRevision < 1
    || !Number.isSafeInteger(completion.executionFence)
    || completion.executionFence < 1
    || completion.modelSelectionReference !== input.expectedSelection.modelSelectionReference
    || completion.maxPromptTokens !== input.expectedSelection.runLimits.maxPromptTokens
    || !Number.isSafeInteger(completion.nativeEventCount)
    || completion.nativeEventCount < 1
    || !Number.isFinite(Date.parse(completion.nativeCompletedAt))
    || completion.cacheBaselineIsComplete !== true) {
    throw new Error('The result does not contain valid native completion evidence for the accepted model and budget.');
  }
}

export function validateGenerationResult(result, envelope, input, selection) {
  const definition = result?.workflowDefinition;
  if (result?.jobId?.toLowerCase() !== envelope.jobId.toLowerCase()
    || !isUuid(result.artifactId)
    || !definition
    || definition.origin !== 'generated'
    || !nonEmpty(definition.id, 256)
    || !nonEmpty(definition.revision, 256)
    || !Array.isArray(definition.steps)
    || definition.steps.length === 0
    || !isSha256(result.definitionSha256)
    || result.configurationRevision !== selection.projectConfigurationRevision
    || !isSha256(result.acceptedSelectionHash)
    || result.acceptedSelectionHash !== result.nativeCompletion?.acceptedSelectionHash) {
    throw new Error('The workflow result does not match the completed job and accepted selection.');
  }
  const calculatedHash = createHash('sha256')
    .update(systemTextJsonStringify(definition), 'utf8')
    .digest('hex');
  if (calculatedHash !== result.definitionSha256.toLowerCase()) {
    throw new Error('The workflow definition hash does not match its artifact.');
  }
  if (calculatedArtifactId(
    envelope.jobId,
    definition.id,
    definition.revision,
    result.definitionSha256,
  ) !== result.artifactId.toLowerCase()) {
    throw new Error('The workflow artifact ID does not match its job and definition hash.');
  }
  if (envelope.artifact?.artifactId?.toLowerCase() !== result.artifactId.toLowerCase()
    || envelope.artifact.workflowId !== definition.id
    || envelope.artifact.definitionRevision !== definition.revision) {
    throw new Error('The job status artifact does not match the workflow result.');
  }
  if (result.configurationRevision !== input.expectedSelection.projectConfigurationRevision
    || result.nativeCompletion?.acceptedSelectionHash !== result.acceptedSelectionHash) {
    throw new Error('The result does not match the accepted selection revision.');
  }
  validateNativeCompletion(result.nativeCompletion, input);
}

async function readJsonInput(inputPath) {
  try {
    const raw = await readFile(inputPath, 'utf8');
    const { value, tokens } = parseJsonWithCreditToken(raw, 'case input');
    return validateInput(value, tokens);
  } catch (error) {
    if (error instanceof SyntaxError) {
      throw new Error('The case input file is missing or is not valid JSON.');
    }
    throw error;
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

async function persistResult(handle, result) {
  await handle.truncate(0);
  await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
  await handle.sync();
}

function currentWindowsUserSid() {
  const result = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', '[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value'],
    { shell: false, windowsHide: true, encoding: 'utf8' },
  );
  const sid = result.status === 0 ? result.stdout.trim() : '';
  if (!/^S-1-\d+(?:-\d+)+$/.test(sid)) {
    throw new Error('Could not resolve the current Windows process token SID.');
  }
  return sid;
}

export async function createPrivateDirectory(outputPath) {
  await mkdir(outputPath, { recursive: false, mode: 0o700 });
  if (process.platform === 'win32') {
    try {
      const sid = currentWindowsUserSid();
      const systemSid = 'S-1-5-18';
      const acl = spawnSync(
        'icacls',
        [outputPath, '/inheritance:r', '/grant:r', `*${systemSid}:(OI)(CI)F`, '/grant:r', `*${sid}:(OI)(CI)F`],
        { shell: false, windowsHide: true, encoding: 'utf8' },
      );
      if (acl.error || acl.status !== 0) {
        throw new Error('Could not restrict Windows permissions on the evidence directory.');
      }
      const verifyScript = [
        '$acl = [System.IO.DirectoryInfo]::new($env:AGENTWEAVER_ACL_PATH).GetAccessControl()',
        'if ($acl.AreAccessRulesProtected -ne $true) { exit 2 }',
        '$rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])',
        '$sids = @($rules | ForEach-Object { $_.IdentityReference.Value })',
        'if ($sids -notcontains $env:AGENTWEAVER_ACL_SYS -or $sids -notcontains $env:AGENTWEAVER_ACL_USR) { exit 3 }',
        '$bad = @($sids | Where-Object { $_ -notin @($env:AGENTWEAVER_ACL_SYS, $env:AGENTWEAVER_ACL_USR) })',
        'if ($bad.Count -gt 0) { exit 4 }',
      ].join('; ');
      const verify = spawnSync(
        'powershell.exe',
        ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(verifyScript, 'utf16le').toString('base64')],
        {
          shell: false,
          windowsHide: true,
          encoding: 'utf8',
          env: {
            ...process.env,
            AGENTWEAVER_ACL_PATH: outputPath,
            AGENTWEAVER_ACL_SYS: systemSid,
            AGENTWEAVER_ACL_USR: sid,
          },
        },
      );
      if (verify.error || verify.status !== 0) {
        throw new Error('Windows permissions on the evidence directory could not be verified as private.');
      }
    } catch (error) {
      await rm(outputPath, { recursive: true, force: true }).catch(() => {});
      throw error;
    }
  }
}

async function writeRestrictedJson(filePath, value) {
  const handle = await open(filePath, 'wx', 0o600);
  try {
    await handle.writeFile(serializeRedactedJsonLine(value) + '\n', 'utf8');
    await handle.sync();
  } finally {
    await handle.close();
  }
}

function isAuthorityDenialStderr(stderr) {
  if (typeof stderr !== 'string' || !stderr.trim()) return false;
  return /\b(?:401|403|unauthorized|forbidden)\b|not signed in|run login (?:first|again)|sign in with|authorization[- ]context request was denied|permission denied|do not belong to the same actor/i.test(stderr);
}

function classifyCancelRecord(record, input, options = {}) {
  if (record.error || record.signal || record.timedOut) {
    return { kind: 'lost', failure: 'Run cancellation response was lost or timed out.' };
  }
  const stdout = typeof record.stdout === 'string' ? record.stdout.trim() : '';
  if (!stdout) {
    if (isAuthorityDenialStderr(record.stderr)) {
      return { kind: 'stop', failure: 'Run cancellation was denied by authority.' };
    }
    return { kind: 'lost', failure: 'Run cancellation returned no response.' };
  }
  let response;
  try {
    response = JSON.parse(stdout);
  } catch {
    if (isAuthorityDenialStderr(record.stderr)) {
      return { kind: 'stop', failure: 'Run cancellation was denied by authority.' };
    }
    return { kind: 'lost', failure: 'Run cancellation did not return valid JSON.' };
  }
  if (response?.status === 401 || response?.status === 403) {
    return { kind: 'stop', failure: 'Run cancellation was denied by authority.' };
  }
  if (response?.status === 409) {
    return { kind: 'stop', failure: 'Run cancellation conflicted with the current run state.' };
  }
  if (response?.status === 503 && response.body?.error === RUNTIME_START_QUEUE_FULL) {
    return { kind: 'queue_full', failure: 'Run cancellation cleanup queue was full.' };
  }
  if (options.allowOwnerTimeout && response?.status === 504 && response.body?.code === 'owner_timeout') {
    return { kind: 'lost', failure: 'Run cancellation timed out at the Gateway owner.' };
  }
  if (response?.status !== 200
    || response.body?.runId !== input.target.runId
    || response.body?.state !== 'cancelled') {
    const statusText = typeof response?.status === 'number' ? `HTTP ${response.status}` : 'unexpected output';
    return { kind: 'stop', failure: `Run cancellation failed with ${statusText}.` };
  }
  return { kind: 'ok', response: response.body };
}

function classifyRunStatusRecord(record, input, options = {}) {
  if (record.timedOut) {
    return { kind: 'transient', failure: 'Run status check timed out.' };
  }
  if (record.error || record.signal) {
    return {
      kind: options.strictTransient ? 'stop' : 'transient',
      failure: 'Run status check did not complete.',
    };
  }
  const stdout = typeof record.stdout === 'string' ? record.stdout.trim() : '';
  if (!stdout) {
    if (isAuthorityDenialStderr(record.stderr)) {
      return { kind: 'stop', failure: 'Run status check was denied by authority.' };
    }
    if (options.strictTransient && typeof record.stderr === 'string'
      && record.stderr.trim() === 'The Gateway request timed out.') {
      return { kind: 'transient', failure: 'Run status check timed out.' };
    }
    return {
      kind: options.strictTransient ? 'stop' : 'transient',
      failure: 'Run status check returned no response.',
    };
  }
  let response;
  try {
    response = JSON.parse(stdout);
  } catch {
    if (isAuthorityDenialStderr(record.stderr)) {
      return { kind: 'stop', failure: 'Run status check was denied by authority.' };
    }
    return {
      kind: options.strictTransient ? 'stop' : 'transient',
      failure: 'Run status check did not return valid JSON.',
    };
  }
  if (response?.status === 401 || response?.status === 403) {
    return { kind: 'stop', failure: 'Run status check was denied by authority.' };
  }
  if (response?.status === 409) {
    return { kind: 'stop', failure: 'Run status check conflicted with the current run state.' };
  }
  if (typeof response?.status === 'number' && response.status >= 500) {
    return { kind: 'transient', failure: `Run status check returned HTTP ${response.status}.` };
  }
  if (response?.status !== 200
    || response.body?.projectId !== input.target.projectId
    || response.body?.runId !== input.target.runId) {
    return { kind: 'stop', failure: 'Run status output did not match the target project and run.' };
  }
  return { kind: 'ok', status: response.body };
}

function isCompleteRuntimeCleanup(cleanupSummary) {
  return cleanupSummary?.state === 'complete'
    && Number.isSafeInteger(cleanupSummary.total)
    && cleanupSummary.total > 0
    && cleanupSummary.pending === 0
    && cleanupSummary.complete === cleanupSummary.total;
}

function classifyStandardRuntimeCleanup(cleanupSummary) {
  if (cleanupSummary === undefined || cleanupSummary === null) {
    return { kind: 'pending', failure: 'The disposable run cleanup summary was absent.' };
  }
  if (typeof cleanupSummary !== 'object' || Array.isArray(cleanupSummary)) {
    return { kind: 'malformed', failure: 'The disposable run cleanup summary was malformed or inconsistent.' };
  }
  const { state, total, pending, complete } = cleanupSummary;
  if (state === 'failed') {
    return { kind: 'failed', failure: 'The disposable run cleanup failed.' };
  }
  const validCounts = Number.isSafeInteger(total)
    && total > 0
    && Number.isSafeInteger(pending)
    && pending >= 0
    && Number.isSafeInteger(complete)
    && complete >= 0
    && pending + complete === total;
  if (!validCounts) {
    return { kind: 'malformed', failure: 'The disposable run cleanup summary was malformed or inconsistent.' };
  }
  if (state === 'complete' && pending === 0 && complete === total) {
    return { kind: 'complete', summary: { state, total, pending, complete } };
  }
  if (state === 'pending' && pending > 0) {
    return { kind: 'pending', failure: 'The disposable run cleanup remained pending.' };
  }
  return { kind: 'malformed', failure: 'The disposable run cleanup summary was malformed or inconsistent.' };
}

export async function runCase(options, env = process.env, dependencies = {}) {
  const input = await readJsonInput(path.resolve(options.inputPath));
  const isCreditZero = input.caseId === CREDIT_ZERO_CASE_ID;
  const cliPath = resolveCliPath(env.AGENTWEAVER_CLI_PATH);
  const outputPath = await requireNewOutputDirectory(options.outputPath);
  const apphostStat = await stat(cliPath).catch(() => null);
  if (!apphostStat?.isFile()) throw new Error('The configured native apphost does not exist as a file.');
  const apphostHash = await sha256(cliPath);
  if (input.apphostSha256 !== apphostHash) {
    throw new Error('The configured apphost hash does not match the approved case input.');
  }
  const scriptHash = await sha256(CASE_SCRIPT_PATH);
  if (options.dryRun) {
    console.log(JSON.stringify({
      status: 'validated-not-run',
      caseId: input.caseId,
      sourceCommit: input.sourceCommit,
      targetCommit: input.targetCommit,
      apphost: path.basename(cliPath),
      apphostSha256: apphostHash,
      scriptSha256: scriptHash,
      network: 'not-used',
      evidence: 'not-created',
    }));
    return 0;
  }

  const runId = randomUUID();
  await createPrivateDirectory(outputPath);
  const commandsPath = path.join(outputPath, 'commands.jsonl');
  const commandsHandle = await open(commandsPath, 'wx', 0o600);
  await commandsHandle.close();
  const resultHandle = await open(path.join(outputPath, 'result.json'), 'wx', 0o600);
  await resultHandle.writeFile('{"status":"incomplete"}\n', 'utf8');
  const startedAt = new Date().toISOString();
  const generation = {
    status: 'not-started',
    jobId: null,
    artifactId: null,
    definitionSha256: null,
    storedTurn: null,
    ...(isCreditZero ? { zeroCreditNoSend: null } : {}),
    failure: null,
  };
  const cleanup = { status: 'not-required', failure: null };
  let status = 'failed';
  let failure = null;
  let version = null;
  let runIdentityVerified = false;
  let verifiedAdminActorId = null;
  let preflightPassed = false;
  let cancellationRequestPersisted = false;
  let readySession = null;
  let selection = null;
  let commandCount = 0;
  let cancellationKey = null;
  let cancellationBody = null;
  const runCli = dependencies.runCommand ?? runCommand;
  const now = dependencies.now ?? (() => Number(process.hrtime.bigint() / 1_000_000n));
  const sleep = dependencies.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)));

  async function logCommand(command, evidence) {
    await appendRedactedJsonLine(commandsPath, {
      caseId: input.caseId,
      runId,
      stepId: command.id,
      executable: path.basename(cliPath),
      ...evidence,
    });
    commandCount += 1;
  }

  async function invokeLogged(command, timeoutMs = COMMAND_TIMEOUT_MS) {
    let record;
    try {
      record = runCli(cliPath, command.args, env, timeoutMs);
    } catch {
      throw new Error(`${command.id} could not start.`);
    }
    await logCommand(command, command.metadataOnly
      ? processMetadata(record)
      : { arguments: safeArguments(command.args), ...record });
    return record;
  }

  async function execute(command) {
    const record = await invokeLogged(command, command.timeoutMs ?? COMMAND_TIMEOUT_MS);
    requireSuccess(record, command.id);
    return record;
  }

  async function submitGeneration(command, attempt, allowQueueFullRetry = true) {
    const record = runCli(cliPath, command.args, env, command.timeoutMs ?? COMMAND_TIMEOUT_MS);
    const queueFull = isQueueFull(record);
    await logCommand(command, {
      attempt,
      ...processMetadata(record),
      ...(queueFull ? { code: QUEUE_FULL } : {}),
    });
    if (allowQueueFullRetry && attempt === 1 && queueFull) return null;
    if (isCreditZero && (record.error || record.exitCode !== 0 || record.signal || record.timedOut)) {
      throw new BlockedCaseError('Workflow generation submission did not return a job ID.');
    }
    requireSuccess(record, command.id);
    return apiResponse(record, 'generateProjectWorkflow', 202);
  }

  try {
    for (const command of buildCommands(input)) {
      const record = await execute(command).catch(error => {
        if (command.id === 'api-describe-turn-digest') {
          throw new BlockedCaseError('The CLI could not describe the Gateway turn read contract.');
        }
        throw error;
      });
      if (command.id === 'version') {
        version = record.stdout.trim();
        if (!version) throw new Error('The apphost did not report its version.');
      } else if (command.id === 'help') {
        if (!hasTurnDigestHelp(record.stdout)) {
          throw new BlockedCaseError('The pinned apphost does not support turn content digests.');
        }
      } else if (command.id === 'api-operations') {
        const operations = parseJson(record.stdout, command.id);
        const available = Array.isArray(operations) ? operations : [];
        const requiredOps = isCreditZero ? CREDIT_ZERO_REQUIRED_OPERATIONS : REQUIRED_OPERATIONS;
        const missingTurnOp = !isCreditZero && !available.some(item => item?.operationId === TURN_READ_OPERATION
          && item.method === 'GET' && item.path === TURN_READ_ROUTE);
        if (requiredOps.some(operation => !available.some(item => item?.operationId === operation)) || missingTurnOp) {
          throw new BlockedCaseError('The Gateway does not expose every operation required by this case.');
        }
      } else if (command.id === 'api-describe-turn-digest') {
        if (!supportsTurnDigest(parseJson(record.stdout, command.id))) {
          throw new BlockedCaseError('The Gateway turn read contract does not support this case.');
        }
      } else if (command.id === 'auth-status-run') {
        const signedIn = validateAuthStatus(parseJson(record.stdout, command.id), input, true);
        if (!signedIn) {
          throw new BlockedCaseError('Sign in with the run-bound projects.orchestrator identity before this case.');
        }
      } else if (command.id === 'auth-status-project-writer') {
        const signedIn = validateAuthStatus(parseJson(record.stdout, command.id), input, false);
        if (!signedIn) {
          throw new BlockedCaseError('Sign in with the unbound projects.admin identity before this case.');
        }
      } else if (command.id === 'api-invoke-authorization-context') {
        try {
          validateCallerContext(apiResponse(record, command.id, 200), input, true);
        } catch (error) {
          throw new BlockedCaseError(error.message);
        }
        runIdentityVerified = true;
      } else if (command.id === 'api-invoke-admin-authorization-context') {
        const adminContext = apiResponse(record, command.id, 200);
        try {
          validateCallerContext(adminContext, input, false);
        } catch (error) {
          throw new BlockedCaseError(error.message);
        }
        verifiedAdminActorId = adminContext.actorId;
      } else if (command.id === 'api-invoke-run-selection') {
        const parsedSelection = parseJsonWithCreditToken(record.stdout, command.id);
        const envelope = parsedSelection.value;
        if (envelope?.status !== 200 || envelope.body === undefined || envelope.body === null) {
          throw new Error(`${command.id} did not return HTTP 200.`);
        }
        selection = envelope.body;
        try {
          validateAcceptedSelection(selection, input, isCreditZero ? parsedSelection.tokens : null);
        } catch (error) {
          throw new BlockedCaseError(error.message);
        }
      } else if (command.id === 'api-invoke-session-status') {
        const sessionStatus = apiResponse(record, command.id, 200);
        try {
          readySession = validateReadyRootSession(sessionStatus, input);
        } catch (error) {
          throw new BlockedCaseError(error.message);
        }
      } else if (command.id === 'api-invoke-decisions') {
        const decisions = apiResponse(record, command.id, 200);
        try {
          validateInitialDecisions(decisions, input, readySession);
        } catch (error) {
          throw new BlockedCaseError(error.message);
        }
      }
    }
    preflightPassed = true;

    if (isCreditZero) {
      cancellationKey = randomUUID();
      cancellationBody = { reason: 'workflow-generation-credit-zero acceptance cleanup' };
      const writeCancel = dependencies.writeCancellationRequest
        ?? (filePath => writeRestrictedJson(filePath, {
          gatewayUrl: input.target.gatewayUrl,
          tenantId: input.target.tenantId,
          projectId: input.target.projectId,
          runId: input.target.runId,
          expectedAdminActorId: verifiedAdminActorId,
          idempotencyKey: cancellationKey,
          body: cancellationBody,
        }));
      await writeCancel(path.join(outputPath, 'cancellation-request.json'));
      cancellationRequestPersisted = true;

      generation.status = 'running';
      const submit = buildGenerateCommand(input, randomUUID());
      const envelope = await submitGeneration(submit, 1, false);
      let jobId;
      try {
        jobId = validateJobEnvelope(envelope, input);
      } catch (error) {
        throw new BlockedCaseError(error.message);
      }
      generation.jobId = jobId;

      const deadline = now() + (dependencies.jobDeadlineMs ?? JOB_DEADLINE_MS);
      let proofSaved = false;
      while (now() <= deadline) {
        const remainingForRead = deadline - now();
        if (remainingForRead <= 0) break;
        const jobCommand = buildJobStatusCommand(input, jobId);
        const statusRecord = await invokeLogged(
          jobCommand,
          Math.min(COMMAND_TIMEOUT_MS, Math.max(1, remainingForRead)),
        );
        if (statusRecord.timedOut || now() > deadline) {
          throw new BlockedCaseError('Workflow generation did not produce zeroCreditNoSend proof before its deadline.');
        }
        requireSuccess(statusRecord, jobCommand.id);
        const current = apiResponse(statusRecord, 'readProjectWorkflowGenerationJob', 200);
        validateJobEnvelope(current, input, jobId);
        if (current.status === 'completed' || current.artifact != null) {
          throw new Error('Workflow generation unexpectedly completed or produced an artifact under a zero-credit limit.');
        }
        if (['running', 'failed'].includes(current.status) && current.zeroCreditNoSend != null) {
          const validatedProof = validateZeroCreditNoSend(current.zeroCreditNoSend, readySession);
          await writeRestrictedJson(path.join(outputPath, 'zero-credit-proof.json'), {
            projectId: input.target.projectId,
            runId: input.target.runId,
            sessionId: input.target.sessionId,
            jobId,
            zeroCreditNoSend: validatedProof,
          });
          generation.zeroCreditNoSend = validatedProof;
          generation.status = 'passed';
          proofSaved = true;
          break;
        }
        if (current.status === 'failed' || current.status === 'cancelled') {
          const code = current.failure?.code ?? current.status;
          throw new BlockedCaseError(`Workflow generation ended with ${code} without zeroCreditNoSend proof.`);
        }
        const remainingForSleep = deadline - now();
        if (remainingForSleep <= 0) break;
        await sleep(Math.min(dependencies.pollIntervalMs ?? JOB_POLL_INTERVAL_MS, remainingForSleep));
      }
      if (!proofSaved) {
        throw new BlockedCaseError('Workflow generation did not produce zeroCreditNoSend proof before its deadline.');
      }
    } else {
      generation.status = 'running';
      const submit = buildGenerateCommand(input, randomUUID());
      const envelope = await submitGeneration(submit, 1, true) ?? await submitGeneration(submit, 2, true);
      const jobId = validateJobEnvelope(envelope, input);
      generation.jobId = jobId;

      const deadline = now() + (dependencies.jobDeadlineMs ?? JOB_DEADLINE_MS);
      let completedEnvelope = null;
      while (now() <= deadline) {
        const statusRecord = await execute(buildJobStatusCommand(input, jobId));
        const current = apiResponse(statusRecord, 'readProjectWorkflowGenerationJob', 200);
        validateJobEnvelope(current, input, jobId);
        if (current.status === 'completed') {
          completedEnvelope = current;
          break;
        }
        if (current.status === 'failed' || current.status === 'cancelled') {
          const code = current.failure?.code ?? current.status;
          throw new Error(`Workflow generation ended with ${code}.`);
        }
        await sleep(dependencies.pollIntervalMs ?? JOB_POLL_INTERVAL_MS);
      }
      if (completedEnvelope === null) throw new Error('Workflow generation did not complete before its deadline.');

      const resultRecord = await execute(buildJobResultCommand(input, jobId));
      const result = apiResponse(resultRecord, 'readProjectWorkflowGenerationResult', 200);
      validateGenerationResult(result, completedEnvelope, input, selection);
      generation.artifactId = result.artifactId;
      generation.definitionSha256 = result.definitionSha256;
      const { assistantMaterial, outputSha256 } = result.nativeCompletion;
      if (assistantMaterial?.kind !== 'turnContent'
        || !isUuid(assistantMaterial.eventId) || !isSha256(assistantMaterial.contentSha256)) {
        throw new Error('The workflow result does not contain a valid assistant reply receipt.');
      }
      const { projectId, runId: targetRunId, sessionId } = input.target;
      const eventId = assistantMaterial.eventId;
      const turnCommand = buildTurnDigestCommand([...targetArgs(input), ...runBoundArgs(input)], {
        projectId, runId: targetRunId, sessionId, eventId,
      });
      const turn = parseJson((await execute({ ...turnCommand, metadataOnly: true })).stdout, turnCommand.id);
      if (turn?.projectId !== projectId
        || Object.keys(turn).length !== 5
        || turn.runId !== targetRunId || turn.sessionId !== sessionId || turn.eventId !== eventId
        || turn.contentSha256 !== assistantMaterial.contentSha256 || turn.contentSha256 !== outputSha256) {
        throw new Error('The stored assistant reply does not match the workflow completion receipt.');
      }
      generation.storedTurn = { projectId, runId: targetRunId, sessionId, eventId, contentSha256: turn.contentSha256 };
      generation.status = 'passed';
    }
  } catch (error) {
    generation.status = error instanceof BlockedCaseError ? 'blocked' : 'failed';
    generation.failure = error.message;
    failure = error.message;
  }

  if (isCreditZero) {
    if (preflightPassed && cancellationRequestPersisted && input.approvedDisposableRun) {
      cleanup.status = 'running';
      cleanup.replays = 0;
      const cleanupDeadline = now() + (dependencies.cleanupDeadlineMs ?? CLEANUP_DEADLINE_MS);
      const replayIntervalMs = dependencies.cleanupReplayIntervalMs ?? CLEANUP_REPLAY_INTERVAL_MS;
      const pollIntervalMs = dependencies.cleanupPollIntervalMs ?? CLEANUP_POLL_INTERVAL_MS;
      const [cancelCommand, verifyCommand] = buildCleanupCommands(input, cancellationKey, cancellationBody);
      let replaysUsed = 0;
      let lastCancelAt = now();
      let cancelResponseUncertain = false;
      let lastFailure = 'The disposable run cleanup did not complete before the 90-second deadline.';

      try {
        const remainingForInitial = Math.max(1, Math.min(COMMAND_TIMEOUT_MS, cleanupDeadline - now()));
        const initialCancel = classifyCancelRecord(await invokeLogged(cancelCommand, remainingForInitial), input);
        lastCancelAt = now();
        if (initialCancel.kind === 'stop') throw new Error(initialCancel.failure);
        if (initialCancel.kind === 'lost' || initialCancel.kind === 'queue_full') {
          cancelResponseUncertain = true;
          lastFailure = initialCancel.failure;
        }

        while (now() <= cleanupDeadline) {
          const remainingForRead = cleanupDeadline - now();
          if (remainingForRead <= 0) break;
          let freshPending = false;
          const statusOutcome = classifyRunStatusRecord(
            await invokeLogged(verifyCommand, Math.min(COMMAND_TIMEOUT_MS, Math.max(1, remainingForRead))),
            input,
          );
          if (statusOutcome.kind === 'stop') throw new Error(statusOutcome.failure);
          if (statusOutcome.kind === 'ok') {
            const runStatus = statusOutcome.status;
            if (runStatus.executionState === 'cancelled' && isCompleteRuntimeCleanup(runStatus.runtimeCleanup)) {
              cleanup.status = 'passed';
              cleanup.runtimeCleanup = runStatus.runtimeCleanup;
              cleanup.replays = replaysUsed;
              break;
            }
            if (['completed', 'failed', 'indeterminate'].includes(runStatus.executionState)) {
              throw new Error(`The disposable run ended in incompatible terminal state ${runStatus.executionState}.`);
            }
            if (runStatus.runtimeCleanup?.state === 'failed') {
              throw new Error('The disposable run cleanup failed.');
            }
            if (runStatus.runtimeCleanup?.state === 'pending' || (runStatus.runtimeCleanup?.pending ?? 0) > 0) {
              freshPending = true;
              lastFailure = 'The disposable run cleanup remained pending.';
            }
          } else {
            lastFailure = statusOutcome.failure;
          }

          const remainingAfterRead = cleanupDeadline - now();
          if (remainingAfterRead <= 0) break;

          if ((cancelResponseUncertain || freshPending)
            && replaysUsed < MAX_CLEANUP_REPLAYS
            && (now() - lastCancelAt) >= replayIntervalMs) {
            replaysUsed += 1;
            cleanup.replays = replaysUsed;
            const replayOutcome = classifyCancelRecord(
              await invokeLogged(cancelCommand, Math.min(COMMAND_TIMEOUT_MS, Math.max(1, remainingAfterRead))),
              input,
            );
            lastCancelAt = now();
            if (replayOutcome.kind === 'stop') throw new Error(replayOutcome.failure);
            if (replayOutcome.kind === 'ok') {
              cancelResponseUncertain = false;
            } else {
              cancelResponseUncertain = true;
              lastFailure = replayOutcome.failure;
            }
          }

          const remainingAfterReplay = cleanupDeadline - now();
          if (remainingAfterReplay <= 0) break;
          await sleep(Math.min(pollIntervalMs, remainingAfterReplay));
        }

        if (cleanup.status !== 'passed') {
          throw new Error(lastFailure);
        }
      } catch (error) {
        cleanup.status = 'failed';
        cleanup.failure = error.message;
        cleanup.replays = replaysUsed;
        cleanup.cancellationRetained = cancellationRequestPersisted;
      }
    } else {
      cleanup.status = 'not-attempted';
      cleanup.failure = preflightPassed
        ? 'Cancellation request record could not be saved.'
        : 'Preflight did not complete.';
      cleanup.cancellationRetained = false;
    }
  } else if (runIdentityVerified && verifiedAdminActorId !== null && input.approvedDisposableRun) {
    cleanup.status = 'running';
    const cleanupDeadline = now() + (dependencies.cleanupDeadlineMs ?? CLEANUP_DEADLINE_MS);
    const pollIntervalMs = dependencies.cleanupPollIntervalMs ?? CLEANUP_POLL_INTERVAL_MS;
    const [rawCancelCommand, rawVerifyCommand] = buildCleanupCommands(input, randomUUID());
    const cancelCommand = { ...rawCancelCommand, metadataOnly: true };
    const verifyCommand = { ...rawVerifyCommand, metadataOnly: true };
    let lastFailure = 'The disposable run cleanup did not complete before the 90-second deadline.';

    try {
      const remainingForCancel = cleanupDeadline - now();
      if (remainingForCancel <= 0) {
        throw new Error(lastFailure);
      }
      const initialCancel = classifyCancelRecord(
        await invokeLogged(cancelCommand, Math.min(COMMAND_TIMEOUT_MS, remainingForCancel)),
        input,
        { allowOwnerTimeout: true },
      );
      if (initialCancel.kind === 'stop' || initialCancel.kind === 'queue_full') {
        throw new Error(initialCancel.failure);
      }
      if (initialCancel.kind === 'lost') {
        lastFailure = initialCancel.failure;
      }

      while (now() < cleanupDeadline) {
        const remainingForRead = cleanupDeadline - now();
        if (remainingForRead <= 0) break;
        const statusRecord = await invokeLogged(
          verifyCommand,
          Math.min(COMMAND_TIMEOUT_MS, remainingForRead),
        );
        if (now() >= cleanupDeadline) break;

        const statusOutcome = classifyRunStatusRecord(statusRecord, input, { strictTransient: true });
        if (statusOutcome.kind === 'stop') {
          throw new Error(statusOutcome.failure);
        }
        if (statusOutcome.kind === 'ok') {
          const runStatus = statusOutcome.status;
          if (runStatus.rootSessionId !== input.target.sessionId) {
            throw new Error('Run status output did not match the target root session.');
          }
          if (['completed', 'failed', 'indeterminate'].includes(runStatus.executionState)) {
            throw new Error(`The disposable run ended in incompatible terminal state ${runStatus.executionState}.`);
          }
          const summaryOutcome = classifyStandardRuntimeCleanup(runStatus.runtimeCleanup);
          if (summaryOutcome.kind === 'failed' || summaryOutcome.kind === 'malformed') {
            throw new Error(summaryOutcome.failure);
          }
          if (runStatus.executionState === 'cancelled' && summaryOutcome.kind === 'complete') {
            cleanup.status = 'passed';
            cleanup.runtimeCleanup = summaryOutcome.summary;
            break;
          }
          lastFailure = runStatus.executionState !== 'cancelled'
            ? 'The disposable run did not reach the cancelled state.'
            : summaryOutcome.failure;
        } else {
          lastFailure = statusOutcome.failure;
        }

        const remainingForSleep = cleanupDeadline - now();
        if (remainingForSleep <= 0) break;
        await sleep(Math.min(pollIntervalMs, remainingForSleep));
      }

      if (cleanup.status !== 'passed') {
        throw new Error(lastFailure);
      }
    } catch (error) {
      cleanup.status = 'failed';
      cleanup.failure = error.message;
    }
  } else {
    cleanup.status = 'not-attempted';
    cleanup.failure = 'The caller contexts did not match the case input.';
  }

  if (generation.status === 'passed' && cleanup.status === 'passed') {
    status = 'passed';
    failure = null;
  } else if (generation.status === 'blocked' && cleanup.status !== 'failed') {
    status = 'blocked';
  } else {
    status = 'failed';
    failure ??= cleanup.failure ?? generation.failure;
  }

  const result = {
    schemaVersion: 1,
    caseId: input.caseId,
    runId,
    scope: isCreditZero
      ? 'zero-credit workflow-generation no-send proof and bounded-replay cleanup through the public Gateway'
      : 'workflow generation through the public Gateway and verified disposable-run cancellation',
    status,
    startedAt,
    completedAt: new Date().toISOString(),
    sourceCommit: input.sourceCommit,
    targetCommit: input.targetCommit,
    target: {
      approvedNonProduction: input.approvedNonProduction,
      approvedDisposableRun: input.approvedDisposableRun,
      gatewayOrigin: input.target.gatewayUrl,
      tenantId: input.target.tenantId,
      projectId: input.target.projectId,
      runId: input.target.runId,
      sessionId: input.target.sessionId,
      expectedActorId: input.expectedActorId,
    },
    expectedSelection: input.expectedSelection,
    apphost: { name: path.basename(cliPath), sha256: apphostHash, version },
    script: { name: path.basename(CASE_SCRIPT_PATH), sha256: scriptHash },
    commands: commandCount,
    generation,
    cleanup,
    ...(isCreditZero
      ? {
          numericMatrix: buildNumericMatrix({
            'credit-zero': status === 'passed' ? 'passed' : 'blocked',
            ...(status === 'passed' ? {} : { 'credit-zero:reason': failure ?? 'Zero-credit proof or cleanup did not complete.' }),
          }, { sourceCaseId: CREDIT_ZERO_CASE_ID }),
        }
      : {}),
    failure,
  };
  await persistResult(resultHandle, result);
  await resultHandle.close();
  console.log(JSON.stringify({ status, caseId: input.caseId, runId, evidence: outputPath, failure }));
  return status === 'passed' ? 0 : status === 'blocked' ? 2 : 1;
}

async function main(argv = process.argv.slice(2), env = process.env) {
  try {
    const options = parseArgs(argv);
    if (options.help) {
      console.log(USAGE);
      return 0;
    }
    return await runCase(options, env);
  } catch (error) {
    console.error(error.message);
    return 2;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = await main();
}
