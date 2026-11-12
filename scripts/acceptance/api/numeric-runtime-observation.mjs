#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, stat, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const CASE_ID = 'numeric-runtime-observation';
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

export function validateInput(value) {
  if (!exactKeys(value, [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'expectedActorId', 'expectedMaxToolCalls', 'target',
  ])
    || value.caseId !== CASE_ID
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || !nonEmpty(value.expectedActorId)
    || !Number.isInteger(value.expectedMaxToolCalls)
    || value.expectedMaxToolCalls < 1
    || value.expectedMaxToolCalls > 10_000
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
  return {
    caseId: CASE_ID,
    sourceCommit: value.sourceCommit.toLowerCase(),
    targetCommit: value.targetCommit.toLowerCase(),
    apphostSha256: value.apphostSha256.toLowerCase(),
    approvedNonProduction: true,
    expectedActorId: value.expectedActorId,
    expectedMaxToolCalls: value.expectedMaxToolCalls,
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
    { id: 'auth-status', args: ['auth', 'status', '--scope', 'projects.orchestrator', ...unbound] },
    { id: 'api-operations', args: ['api', 'operations', ...unbound] },
    {
      id: 'api-describe-runtime-observation',
      args: ['api', 'describe', 'readRuntimeObservationV2', ...unbound],
    },
    {
      id: 'rest-authorization-context',
      args: [
        'rest', '--method', 'GET', '--url', '/api/v1/authorization/context',
        '--scope', 'api.read', ...unbound,
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

export function childEnvironment(env) {
  const childEnv = { ...env };
  const excluded = new Set([...TARGET_ENV, 'AGENTWEAVER_CLI_PATH']);
  for (const key of Object.keys(childEnv)) {
    if (excluded.has(key.toUpperCase())) delete childEnv[key];
  }
  return childEnv;
}

function matchesToolLimitRejection(fact, sessionId) {
  return fact?.kind === 'ToolPermissionRejected'
    && fact.limitKind === 'ToolCalls'
    && fact.stage === 'toolPermission'
    && fact.reasonCode === TOOL_LIMIT_DENIAL
    && fact.sessionId === sessionId
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(fact.attemptId ?? '')
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(fact.dispatchId ?? '')
    && fact.ownerDecision?.state === 'Available'
    && fact.toolPermission?.state === 'Available'
    && fact.nativeNoSend?.state === 'Unavailable'
    && fact.toolExecution?.state === 'Unavailable';
}

export function validateObservation(input, pages, exact, restExact) {
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
  if (pages.some(page => page.history?.complete !== true
    || !Array.isArray(page.history?.items)
    || !Object.hasOwn(page.history, 'nextCursor')
    || (page.history.nextCursor !== null && typeof page.history.nextCursor !== 'string')
    || page.history?.snapshotId !== firstHistory.snapshotId
    || page.history?.highWatermark !== firstHistory.highWatermark)) {
    throw new Error('The history is incomplete, malformed, or its pages do not share one snapshot.');
  }

  const factsById = new Map();
  for (const page of pages) {
    for (const fact of page.history.items ?? []) {
      if (factsById.has(fact.factId)) throw new Error('The paged history contains a duplicate fact.');
      factsById.set(fact.factId, fact);
    }
  }
  const facts = [...factsById.values()];
  const rejections = facts.filter(fact => matchesToolLimitRejection(fact, expected.sessionId));
  if (rejections.length === 0) {
    throw new Error('The selected session has no retained numeric tool-permission rejection.');
  }
  const rejection = rejections[0];
  if (!facts.some(fact => fact.kind === 'ToolInvocationReserved'
    && fact.limitKind === 'ToolCalls'
    && fact.sessionId === expected.sessionId
    && fact.attemptId !== rejection.attemptId)) {
    throw new Error('The history does not show a separate admitted tool reservation.');
  }
  const total = first.totals?.toolSlotsReserved;
  if (total?.state !== 'Available' || total.value !== input.expectedMaxToolCalls) {
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
      caseId: CASE_ID,
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
    caseId: CASE_ID,
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
    const context = restBody(contextRecord, 'rest-authorization-context');
    if (context.actorId !== input.expectedActorId || context.tenantId !== input.target.tenantId) {
      throw new Error('The Gateway authorization context does not match the case actor and tenant.');
    }
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

    const facts = pages.flatMap(page => page.history.items ?? []);
    const rejection = facts.find(fact => matchesToolLimitRejection(fact, input.target.sessionId));
    if (!rejection?.attemptId) throw new Error('The selected session has no retained numeric tool-permission rejection.');

    const exactResponse = await execute(
      'observation-exact-attempt',
      observationArgs(input, { attemptId: rejection.attemptId }),
    );
    const exact = apiResponseBody(exactResponse, 'observation-exact-attempt');
    const restUrl = new URL(
      `/api/v1/projects/${input.target.projectId}/runs/${input.target.runId}/coordination/sessions/${input.target.sessionId}/runtime-observation/v2`,
      input.target.gatewayUrl,
    );
    restUrl.searchParams.set('attemptId', rejection.attemptId);
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
