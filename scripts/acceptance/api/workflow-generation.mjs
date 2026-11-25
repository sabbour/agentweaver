#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, stat } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';
import {
  TURN_READ_OPERATION, TURN_READ_ROUTE, buildTurnDigestCommand, hasTurnDigestHelp, supportsTurnDigest,
} from './turn-digest.mjs';

const CASE_ID = 'workflow-generation';
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 8 * 1024 * 1024;
const COMMAND_TIMEOUT_MS = 60_000;
const JOB_DEADLINE_MS = 10 * 60_000;
const JOB_POLL_INTERVAL_MS = 2_000;
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

function validateRunLimits(value) {
  if (!exactKeys(value, RUN_LIMIT_KEYS)
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
  return { ...value };
}

export function validateInput(value) {
  if (!exactKeys(value, [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'approvedDisposableRun', 'expectedActorId',
    'expectedDecisionStateVersion', 'expectedSelection', 'prompt', 'target',
  ])
    || value.caseId !== CASE_ID
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || value.approvedDisposableRun !== true
    || !nonEmpty(value.expectedActorId)
    || !positiveInteger(value.expectedDecisionStateVersion)
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
    || !exactKeys(value.expectedSelection.runLimits, RUN_LIMIT_KEYS)) {
    throw new Error('The case input does not match the workflow-generation case schema.');
  }
  const target = {
    ...value.target,
    gatewayUrl: validateUrl(value.target.gatewayUrl, 'Gateway URL', true),
    brokerIssuer: validateUrl(value.target.brokerIssuer, 'Broker issuer', false),
  };
  return {
    caseId: CASE_ID,
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
      runLimits: validateRunLimits(value.expectedSelection.runLimits),
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
  return [
    { id: 'version', args: ['--version'] },
    { id: 'help', args: ['--help'], metadataOnly: true },
    { id: 'api-operations', args: ['api', 'operations', ...target] },
    {
      id: 'api-describe-turn-digest',
      args: ['api', 'describe', TURN_READ_OPERATION, ...target],
      metadataOnly: true,
    },
    { id: 'auth-status-run', args: ['auth', 'status', ...target, ...run] },
    { id: 'auth-status-project-writer', args: ['auth', 'status', ...target, '--scope', 'projects.admin'] },
    {
      id: 'api-invoke-authorization-context',
      args: ['api', 'invoke', 'getAuthorizationContext', ...target, ...run],
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

export function buildCleanupCommands(input, idempotencyKey) {
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
        '--body', JSON.stringify({ reason: 'workflow-generation acceptance cleanup' }),
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

export function validateCallerContext(context, input) {
  if (context?.actorId !== input.expectedActorId
    || context?.tenantId !== input.target.tenantId
    || context?.boundProjectId !== input.target.projectId
    || context?.boundRunId !== input.target.runId) {
    throw new Error('The current caller context does not match the expected actor, tenant, project, and run.');
  }
}

export function validateAcceptedSelection(selection, input) {
  const expected = input.expectedSelection;
  const limits = selection?.runLimits;
  const mismatches = [];
  if (selection?.projectId !== input.target.projectId || selection?.runId !== input.target.runId)
    mismatches.push('project or run identity');
  if (selection?.projectConfigurationRevision !== expected.projectConfigurationRevision)
    mismatches.push('project configuration revision');
  if (selection?.modelSelection?.reference !== expected.modelSelectionReference)
    mismatches.push('model selection');
  if (!limits || RUN_LIMIT_KEYS.some(key => limits[key] !== expected.runLimits[key]))
    mismatches.push('run limits');
  if (mismatches.length > 0) {
    throw new Error(`The accepted ${mismatches.join(', ')} do not match the case input.`);
  }
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
    return validateInput(JSON.parse(await readFile(inputPath, 'utf8')));
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

export async function runCase(options, env = process.env, dependencies = {}) {
  const input = await readJsonInput(path.resolve(options.inputPath));
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
      caseId: CASE_ID,
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
  await mkdir(outputPath, { recursive: false, mode: 0o700 });
  const commandsPath = path.join(outputPath, 'commands.jsonl');
  const commandsHandle = await open(commandsPath, 'wx', 0o600);
  await commandsHandle.close();
  const resultHandle = await open(path.join(outputPath, 'result.json'), 'wx', 0o600);
  await resultHandle.writeFile('{"status":"incomplete"}\n', 'utf8');
  const startedAt = new Date().toISOString();
  const generation = {
    status: 'not-started', jobId: null, artifactId: null, definitionSha256: null, storedTurn: null, failure: null,
  };
  const cleanup = { status: 'not-required', failure: null };
  let status = 'failed';
  let failure = null;
  let version = null;
  let runIdentityVerified = false;
  let selection = null;
  let commandCount = 0;
  const runCli = dependencies.runCommand ?? runCommand;
  const now = dependencies.now ?? Date.now;
  const sleep = dependencies.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)));

  async function logCommand(command, evidence) {
    await appendRedactedJsonLine(commandsPath, {
      caseId: CASE_ID,
      runId,
      stepId: command.id,
      executable: path.basename(cliPath),
      ...evidence,
    });
    commandCount += 1;
  }

  async function execute(command) {
    let record;
    try {
      record = runCli(cliPath, command.args, env, command.timeoutMs ?? COMMAND_TIMEOUT_MS);
    } catch {
      throw new Error(`${command.id} could not start.`);
    }
    await logCommand(command, command.metadataOnly
      ? processMetadata(record)
      : { arguments: safeArguments(command.args), ...record });
    requireSuccess(record, command.id);
    return record;
  }

  async function submitGeneration(command, attempt) {
    const record = runCli(cliPath, command.args, env, command.timeoutMs ?? COMMAND_TIMEOUT_MS);
    const queueFull = isQueueFull(record);
    await logCommand(command, {
      attempt,
      ...processMetadata(record),
      ...(queueFull ? { code: QUEUE_FULL } : {}),
    });
    if (attempt === 1 && queueFull) return null;
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
        if (REQUIRED_OPERATIONS.some(operation => !available.some(item => item?.operationId === operation))
          || !available.some(item => item?.operationId === TURN_READ_OPERATION
            && item.method === 'GET' && item.path === TURN_READ_ROUTE)) {
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
          validateCallerContext(apiResponse(record, command.id, 200), input);
        } catch (error) {
          throw new BlockedCaseError(error.message);
        }
        runIdentityVerified = true;
      } else if (command.id === 'api-invoke-run-selection') {
        selection = apiResponse(record, command.id, 200);
        try {
          validateAcceptedSelection(selection, input);
        } catch (error) {
          throw new BlockedCaseError(error.message);
        }
      }
    }

    generation.status = 'running';
    const submit = buildGenerateCommand(input, randomUUID());
    const envelope = await submitGeneration(submit, 1) ?? await submitGeneration(submit, 2);
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
  } catch (error) {
    generation.status = error instanceof BlockedCaseError ? 'blocked' : 'failed';
    generation.failure = error.message;
    failure = error.message;
  }

  if (runIdentityVerified && input.approvedDisposableRun) {
    cleanup.status = 'running';
    try {
      const [cancelCommand, verifyCommand] = buildCleanupCommands(input, randomUUID());
      const cancellation = await execute(cancelCommand).catch(() => null);
      const statusRecord = await execute(verifyCommand);
      const runStatus = apiResponse(statusRecord, verifyCommand.id, 200);
      if (runStatus.projectId !== input.target.projectId
        || runStatus.runId !== input.target.runId
        || runStatus.executionState !== 'cancelled') {
        throw new Error('The disposable run did not reach the cancelled state.');
      }
      if (cancellation !== null) {
        const response = apiResponse(cancellation, cancelCommand.id, 200);
        if (response.runId !== input.target.runId || response.state !== 'cancelled') {
          throw new Error('The run cancellation response did not confirm cancellation.');
        }
      }
      cleanup.status = 'passed';
    } catch (error) {
      cleanup.status = 'failed';
      cleanup.failure = error.message;
    }
  } else {
    cleanup.status = 'not-attempted';
    cleanup.failure = 'The run identity did not match the case input.';
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
    caseId: CASE_ID,
    runId,
    scope: 'workflow generation through the public Gateway and verified disposable-run cancellation',
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
    failure,
  };
  await persistResult(resultHandle, result);
  await resultHandle.close();
  console.log(JSON.stringify({ status, caseId: CASE_ID, runId, evidence: outputPath, failure }));
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
