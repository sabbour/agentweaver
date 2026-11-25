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

const CASE_ID = 'task-model-completion';
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 8 * 1024 * 1024;
const COMMAND_TIMEOUT_MS = 60_000;
const DISPATCH_TIMEOUT_MS = 10 * 60_000;
const DISPATCH_POLL_INTERVAL_MS = 2_000;
const DISPATCH_GRACE_MS = 120_000;
const CLEANUP_TIMEOUT_MS = 120_000;
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
const REQUIRED_OPERATIONS = [
  'getAuthorizationContext',
  'getRunSelection',
  'readDecisions',
  'dispatchCoordinatorExecution',
  'readRunUsage',
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

function isUuid(value) {
  return typeof value === 'string'
    && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(value);
}

function isSha256(value) {
  return typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value);
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
    || value.maxChildren < 1
    || value.maxConcurrentChildren < 1
    || value.maxConcurrentChildren > value.maxChildren
    || value.maxWallTimeSeconds < 1
    || value.maxPromptTokens < 1024) {
    throw new Error('The expected run limits must be finite, valid Copilot limits.');
  }
  return { ...value };
}

export function validateInput(value) {
  const selectionKeys = [
    'projectConfigurationRevision', 'modelSelectionReference', 'modelId', 'maxRunUsd', 'runLimits',
  ];
  const taskKeys = [
    'taskId', 'taskRevision', 'claimId', 'workPlanId', 'workflowId', 'definitionRevision', 'associationId',
  ];
  if (!exactKeys(value, [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'approvedDisposableRun', 'expectedActorId',
    'expectedDecisionStateVersion', 'expectedSelection', 'expectedTask', 'target',
  ])
    || value.caseId !== CASE_ID
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || value.approvedDisposableRun !== true
    || !nonEmpty(value.expectedActorId)
    || !positiveInteger(value.expectedDecisionStateVersion)
    || !exactKeys(value.expectedSelection, selectionKeys)
    || !positiveInteger(value.expectedSelection.projectConfigurationRevision)
    || !nonEmpty(value.expectedSelection.modelSelectionReference, 256)
    || !nonEmpty(value.expectedSelection.modelId, 256)
    || typeof value.expectedSelection.maxRunUsd !== 'number'
    || !Number.isFinite(value.expectedSelection.maxRunUsd)
    || value.expectedSelection.maxRunUsd <= 0
    || value.expectedSelection.maxRunUsd > 5
    || !exactKeys(value.expectedTask, taskKeys)
    || !nonEmpty(value.expectedTask.taskId, 256)
    || !positiveInteger(value.expectedTask.taskRevision)
    || !isUuid(value.expectedTask.claimId)
    || !nonEmpty(value.expectedTask.workPlanId, 256)
    || !nonEmpty(value.expectedTask.workflowId, 256)
    || !nonEmpty(value.expectedTask.definitionRevision, 256)
    || !nonEmpty(value.expectedTask.associationId, 256)
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))
    || !nonEmpty(value.target.projectId, 256)
    || !nonEmpty(value.target.runId, 256)
    || !nonEmpty(value.target.sessionId, 256)
    || !exactKeys(value.expectedSelection.runLimits, RUN_LIMIT_KEYS)) {
    throw new Error('The case input does not match the task-model-completion case schema.');
  }
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
      modelId: value.expectedSelection.modelId,
      maxRunUsd: value.expectedSelection.maxRunUsd,
      runLimits: validateRunLimits(value.expectedSelection.runLimits),
    },
    expectedTask: { ...value.expectedTask },
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
  const path = [
    '--path', `projectId=${input.target.projectId}`,
    '--path', `runId=${input.target.runId}`,
    '--path', `sessionId=${input.target.sessionId}`,
  ];
  return [
    { id: 'version', args: ['--version'] },
    { id: 'help', args: ['--help'] },
    { id: 'api-operations', args: ['api', 'operations', ...target] },
    { id: 'api-describe-turn-digest', args: ['api', 'describe', TURN_READ_OPERATION, ...target] },
    { id: 'auth-status-run', args: ['auth', 'status', ...target, ...run] },
    { id: 'auth-status-project-writer', args: ['auth', 'status', ...target, '--scope', 'projects.admin'] },
    {
      id: 'api-invoke-authorization-context',
      args: ['api', 'invoke', 'getAuthorizationContext', ...target, ...run],
    },
    {
      id: 'api-invoke-run-selection',
      args: ['api', 'invoke', 'getRunSelection', ...target, ...path.slice(0, 4), ...run],
    },
    {
      id: 'api-invoke-decisions',
      args: ['api', 'invoke', 'readDecisions', ...target, ...path, ...run],
    },
  ];
}

export function buildDispatchCommand(input, decisionStateVersion) {
  return {
    id: 'api-invoke-dispatch-task',
    timeoutMs: DISPATCH_TIMEOUT_MS,
    args: [
      'api', 'invoke', 'dispatchCoordinatorExecution',
      ...targetArgs(input),
      '--path', `projectId=${input.target.projectId}`,
      '--path', `runId=${input.target.runId}`,
      '--path', `sessionId=${input.target.sessionId}`,
      ...runBoundArgs(input),
      '--body', JSON.stringify({
        expectedStateVersion: decisionStateVersion,
        workPlanId: input.expectedTask.workPlanId,
      }),
    ],
  };
}

export function buildRunUsageCommand(input) {
  return {
    id: 'api-invoke-run-usage',
    args: [
      'api', 'invoke', 'readRunUsage',
      ...targetArgs(input),
      '--path', `projectId=${input.target.projectId}`,
      '--path', `runId=${input.target.runId}`,
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
        '--body', JSON.stringify({ reason: 'task-model-completion acceptance cleanup' }),
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

function requireSuccess(record, step) {
  if (record.error || record.exitCode !== 0 || record.signal || record.timedOut) {
    throw new Error(`${step} did not complete successfully.`);
  }
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
  const pin = selection?.modelSelection?.modelBindingPin;
  const limits = selection?.runLimits;
  if (selection?.projectId !== input.target.projectId
    || selection?.runId !== input.target.runId
    || selection?.projectConfigurationRevision !== expected.projectConfigurationRevision
    || selection?.modelSelection?.reference !== expected.modelSelectionReference
    || selection?.modelSelection?.sourceMode !== 'byok'
    || pin?.sourceMode !== 'byok'
    || pin?.modelSelectionReference !== expected.modelSelectionReference
    || pin?.modelId !== expected.modelId
    || !limits
    || RUN_LIMIT_KEYS.some(key => limits[key] !== expected.runLimits[key])) {
    throw new BlockedCaseError('The accepted project, run, BYOK model, or run limits do not match the case input.');
  }
  const matchingBudgets = limits.spendBudgets?.filter(budget =>
    budget.scope === 'run'
      && budget.meterSource === 'byok.tokens'
      && budget.unit === 'USD') ?? [];
  const hardLimit = matchingBudgets[0]?.hardLimit;
  if (matchingBudgets.length !== 1
    || !Number.isFinite(hardLimit)
    || hardLimit <= 0
    || hardLimit > expected.maxRunUsd
    || !Number.isFinite(matchingBudgets[0].reservePerDispatch)
    || matchingBudgets[0].reservePerDispatch <= 0) {
    throw new BlockedCaseError('The accepted run does not have the expected hard BYOK USD budget.');
  }
  return hardLimit;
}

export function validateDecisions(decisions, input) {
  if (decisions?.stateVersion !== input.expectedDecisionStateVersion || decisions?.canDispatch !== true) {
    throw new BlockedCaseError('The confirmed task plan is not dispatchable at the expected decision version.');
  }
  return decisions.stateVersion;
}

function terminalDispatchFailure(response, input) {
  const taskStartup = response.childRuntimeStartup?.[input.expectedTask.associationId];
  if (response.wallTimeLimitReached === true
    || response.failedDependencyIds?.length > 0
    || (response.unavailableExecutorStepIds?.length > 0 && taskStartup?.state !== 'pending')
    || Object.values(response.childRuntimeStartup ?? {}).some(startup =>
      startup?.state === 'blocked' || startup?.failureCode != null)) {
    throw new Error('The task dispatch is incomplete because the owner reported a terminal failure.');
  }
}

export function validateCompletedDispatch(response, input, expectedStateVersion) {
  const expected = input.expectedTask;
  if (response?.workPlanId !== expected.workPlanId
    || !Number.isSafeInteger(response.decisionStateVersion)
    || response.decisionStateVersion < expectedStateVersion
    || !Number.isSafeInteger(response.checkpointRevision)
    || response.checkpointRevision < 1
    || typeof response.isComplete !== 'boolean'
    || !Array.isArray(response.completionEvidence)) {
    throw new Error('The dispatch response does not match the expected confirmed work plan.');
  }
  terminalDispatchFailure(response, input);
  if (!response.isComplete) return null;

  const completion = response.taskCompletion;
  if (completion?.state !== 'completed'
    || completion.taskId !== expected.taskId
    || completion.taskRevision !== expected.taskRevision
    || completion.claimTaskRevision !== expected.taskRevision
    || completion.claimId?.toLowerCase() !== expected.claimId.toLowerCase()
    || completion.runId !== input.target.runId
    || completion.rootSessionId !== input.target.sessionId
    || completion.workPlanId !== expected.workPlanId
    || completion.workflowId !== expected.workflowId
    || completion.definitionRevision !== expected.definitionRevision
    || completion.associationId !== expected.associationId
    || completion.decisionStateVersion !== response.decisionStateVersion
    || completion.checkpointRevision !== response.checkpointRevision
    || !positiveInteger(completion.executionFence)
    || !nonEmpty(completion.childSessionId)
    || !nonEmpty(completion.checkpointId)
    || !isSha256(completion.taskContentSha256)
    || !isSha256(completion.acceptedSelectionHash)
    || !isSha256(completion.childOutputSha256)
    || !isSha256(completion.outputSetSha256)
    || !isSha256(completion.outputWitnessProofSha256)) {
    throw new Error('The owner did not return task-completion evidence for the expected claim and plan.');
  }
  const receipts = response.completionEvidence.filter(receipt =>
    receipt.workPlanId === expected.workPlanId
      && receipt.associationId === expected.associationId
      && receipt.childSessionId === completion.childSessionId);
  if (receipts.length !== 1) {
    throw new Error('The completed task does not have one matching child receipt.');
  }
  const receipt = receipts[0];
  const material = receipt.assistantMaterial;
  if (material?.kind !== 'turnContent'
    || !isUuid(material.eventId)
    || material.eventId === '00000000-0000-0000-0000-000000000000'
    || material.contentSha256 !== receipt.outputSha256
    || receipt.outputSha256 !== completion.childOutputSha256) {
    throw new Error('The child receipt does not identify the stored assistant reply.');
  }
  if (receipt.contractVersion !== 1
    || !isSha256(receipt.requestSha256)
    || !isSha256(receipt.promptSha256)
    || !isSha256(receipt.registrationSha256)
    || !isSha256(receipt.sourceSha256)
    || !positiveInteger(receipt.executionFence)
    || receipt.executionFence !== completion.executionFence
    || receipt.modelSelectionReference !== input.expectedSelection.modelSelectionReference
    || receipt.modelSourceMode !== 'byok'
    || receipt.modelId !== input.expectedSelection.modelId
    || receipt.acceptedSelectionHash !== completion.acceptedSelectionHash
    || !isSha256(receipt.workItemBindingSha256)
    || !Array.isArray(receipt.usageEventIds)
    || receipt.usageEventIds.length === 0
    || receipt.usageEventIds.some(eventId => !isUuid(eventId))) {
    throw new Error('The child receipt does not prove the expected BYOK model and task binding.');
  }
  return {
    taskId: completion.taskId,
    taskRevision: completion.taskRevision,
    claimId: completion.claimId,
    workPlanId: completion.workPlanId,
    workflowId: completion.workflowId,
    definitionRevision: completion.definitionRevision,
    associationId: completion.associationId,
    childSessionId: completion.childSessionId,
    assistantEventId: material.eventId.toLowerCase(),
    childOutputSha256: completion.childOutputSha256,
    acceptedSelectionHash: completion.acceptedSelectionHash,
    usageEventIds: receipt.usageEventIds,
    modelId: receipt.modelId,
    modelSourceMode: receipt.modelSourceMode,
  };
}

export function validateRunUsage(usage, input, completion, hardLimit = input.expectedSelection.maxRunUsd) {
  const expected = input.expectedSelection;
  const amounts = usage?.amounts?.filter(amount =>
    amount.meterSource === 'byok.tokens' && amount.unit === 'USD') ?? [];
  if (usage?.projectId !== input.target.projectId
    || usage?.runId !== input.target.runId
    || usage.isFullyPriced !== true
    || !Number.isSafeInteger(usage.events)
    || usage.events < completion.usageEventIds.length
    || amounts.length !== 1
    || !Number.isFinite(amounts[0].amount)
    || amounts[0].amount < 0
    || amounts[0].amount > hardLimit
    || amounts[0].pricedEvents < completion.usageEventIds.length
    || amounts[0].unpricedEvents !== 0) {
    throw new Error('The run usage is unpriced, incomplete, or above the accepted hard USD budget.');
  }
  return {
    events: usage.events,
    meterSource: amounts[0].meterSource,
    unit: amounts[0].unit,
    amount: amounts[0].amount,
    hardLimit,
    pricedEvents: amounts[0].pricedEvents,
    unpricedEvents: amounts[0].unpricedEvents,
  };
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
  const dispatch = { status: 'not-started', attempts: 0, task: null, storedTurn: null, usage: null, failure: null };
  const cleanup = { status: 'not-required', runtimeCleanup: null, failure: null };
  let status = 'failed';
  let failure = null;
  let version = null;
  let runIdentityVerified = false;
  let commandCount = 0;
  const runCli = dependencies.runCommand ?? runCommand;
  const now = dependencies.now ?? Date.now;
  const sleep = dependencies.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)));

  async function execute(command) {
    let record;
    try {
      record = runCli(cliPath, command.args, env, command.timeoutMs ?? COMMAND_TIMEOUT_MS);
    } catch {
      throw new Error(`${command.id} could not start.`);
    }
    await appendRedactedJsonLine(commandsPath, {
      caseId: CASE_ID,
      runId,
      stepId: command.id,
      executable: path.basename(cliPath),
      startedAt: record.startedAt,
      completedAt: record.completedAt,
      durationMs: record.durationMs,
      exitCode: record.exitCode,
      timedOut: record.timedOut,
    });
    commandCount += 1;
    requireSuccess(record, command.id);
    return record;
  }

  try {
    let selection = null;
    let acceptedBudgetHardLimit = null;
    let decisionStateVersion = null;
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
        const description = parseJson(record.stdout, command.id);
        if (!supportsTurnDigest(description)) {
          throw new BlockedCaseError('The Gateway turn read contract does not support this case.');
        }
      } else if (command.id === 'auth-status-run') {
        if (!validateAuthStatus(parseJson(record.stdout, command.id), input, true)) {
          throw new BlockedCaseError('Sign in with the run-bound projects.orchestrator identity before this case.');
        }
      } else if (command.id === 'auth-status-project-writer') {
        if (!validateAuthStatus(parseJson(record.stdout, command.id), input, false)) {
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
        try {
          selection = apiResponse(record, command.id, 200);
          acceptedBudgetHardLimit = validateAcceptedSelection(selection, input);
        } catch (error) {
          if (error instanceof BlockedCaseError) throw error;
          throw new BlockedCaseError(error.message);
        }
      } else if (command.id === 'api-invoke-decisions') {
        try {
          decisionStateVersion = validateDecisions(apiResponse(record, command.id, 200), input);
        } catch (error) {
          if (error instanceof BlockedCaseError) throw error;
          throw new BlockedCaseError(error.message);
        }
      }
    }

    dispatch.status = 'running';
    const deadline = now() + Math.min(
      30 * 60_000,
      input.expectedSelection.runLimits.maxWallTimeSeconds * 1000 + DISPATCH_GRACE_MS,
    );
    let completed = null;
    const command = buildDispatchCommand(input, decisionStateVersion);
    while (now() <= deadline) {
      const record = await execute(command);
      const response = apiResponse(record, 'dispatchCoordinatorExecution', 200);
      dispatch.attempts += 1;
      completed = validateCompletedDispatch(response, input, decisionStateVersion);
      if (completed !== null) break;
      await sleep(dependencies.pollIntervalMs ?? DISPATCH_POLL_INTERVAL_MS);
    }
    if (completed === null) throw new Error('The task dispatch did not complete before its deadline.');
    dispatch.task = completed;

    const turnCommand = buildTurnDigestCommand([...targetArgs(input), ...runBoundArgs(input)], {
      projectId: input.target.projectId,
      runId: input.target.runId,
      sessionId: completed.childSessionId,
      eventId: completed.assistantEventId,
    });
    const turn = parseJson((await execute(turnCommand)).stdout, turnCommand.id);
    if (turn?.projectId !== input.target.projectId
      || Object.keys(turn).length !== 5
      || turn.runId !== input.target.runId
      || turn.sessionId !== completed.childSessionId
      || turn.eventId !== completed.assistantEventId
      || turn.contentSha256 !== completed.childOutputSha256) {
      throw new Error('The stored assistant reply does not match the completed child receipt.');
    }
    dispatch.storedTurn = {
      projectId: turn.projectId,
      runId: turn.runId,
      sessionId: turn.sessionId,
      eventId: turn.eventId,
      contentSha256: turn.contentSha256,
    };

    const usageRecord = await execute(buildRunUsageCommand(input));
    dispatch.usage = validateRunUsage(
      apiResponse(usageRecord, 'readRunUsage', 200),
      input,
      completed,
      acceptedBudgetHardLimit,
    );
    dispatch.status = 'passed';
  } catch (error) {
    dispatch.status = error instanceof BlockedCaseError ? 'blocked' : 'failed';
    dispatch.failure = error.message;
    failure = error.message;
  }

  if (runIdentityVerified && input.approvedDisposableRun) {
    cleanup.status = 'running';
    try {
      const [cancelCommand, verifyCommand] = buildCleanupCommands(input, randomUUID());
      const cancellation = await execute(cancelCommand).catch(() => null);
      if (cancellation !== null) {
        const response = apiResponse(cancellation, cancelCommand.id, 200);
        if (response.runId !== input.target.runId || response.state !== 'cancelled') {
          throw new Error('The run cancellation response did not confirm cancellation.');
        }
      }
      const cleanupDeadline = now() + CLEANUP_TIMEOUT_MS;
      while (true) {
        const statusRecord = await execute(verifyCommand);
        const runStatus = apiResponse(statusRecord, verifyCommand.id, 200);
        if (runStatus.projectId !== input.target.projectId
          || runStatus.runId !== input.target.runId
          || runStatus.executionState !== 'cancelled') {
          throw new Error('The disposable run did not reach the cancelled state.');
        }
        const runtimeCleanup = runStatus.runtimeCleanup;
        if (!runtimeCleanup
          || !['pending', 'complete'].includes(runtimeCleanup.state)
          || !Number.isSafeInteger(runtimeCleanup.total)
          || !Number.isSafeInteger(runtimeCleanup.pending)
          || !Number.isSafeInteger(runtimeCleanup.complete)
          || runtimeCleanup.total < 0
          || runtimeCleanup.pending < 0
          || runtimeCleanup.complete < 0
          || runtimeCleanup.pending + runtimeCleanup.complete !== runtimeCleanup.total
          || (runtimeCleanup.state === 'complete') !== (runtimeCleanup.pending === 0)) {
          throw new Error('The run status did not confirm runtime cleanup.');
        }
        cleanup.runtimeCleanup = {
          state: runtimeCleanup.state,
          total: runtimeCleanup.total,
          pending: runtimeCleanup.pending,
          complete: runtimeCleanup.complete,
        };
        if (runtimeCleanup.state === 'complete') break;
        if (now() >= cleanupDeadline) {
          throw new Error('Runtime cleanup did not complete before its deadline.');
        }
        await sleep(dependencies.cleanupPollIntervalMs ?? DISPATCH_POLL_INTERVAL_MS);
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

  if (dispatch.status === 'passed' && cleanup.status === 'passed') {
    status = 'passed';
    failure = null;
  } else if (dispatch.status === 'blocked' && cleanup.status !== 'failed') {
    status = 'blocked';
  } else {
    status = 'failed';
    failure ??= cleanup.failure ?? dispatch.failure;
  }

  const result = {
    schemaVersion: 1,
    caseId: CASE_ID,
    runId,
    scope: 'one BYOK task dispatch through the public Gateway and disposable-run cleanup',
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
      taskId: input.expectedTask.taskId,
    },
    expectedSelection: input.expectedSelection,
    expectedTask: input.expectedTask,
    apphost: { name: path.basename(cliPath), sha256: apphostHash, version },
    script: { name: path.basename(CASE_SCRIPT_PATH), sha256: scriptHash },
    commands: commandCount,
    dispatch,
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
