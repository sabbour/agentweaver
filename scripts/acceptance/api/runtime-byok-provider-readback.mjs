#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, stat } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const CASE_ID = 'runtime-byok-provider-readback';
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 4 * 1024 * 1024;
export const COMMAND_TIMEOUT_MS = 60_000;
export const DEFAULT_PAGE_SIZE = 50;
export const MAXIMUM_PAGES = 100;
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const CURSOR_RE = /^[A-Za-z0-9_-]{1,256}={0,2}$/;

const TARGET_KEYS = ['gatewayUrl', 'brokerIssuer', 'clientId', 'apiAudience', 'tenantId'];
const EXPECTED_REQUEST_KEYS = [
  'requestId', 'tenantId', 'scope', 'projectId', 'providerId', 'providerRevision', 'host', 'port',
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

export const ALLOWLISTED_ERROR_CODES = Object.freeze({
  invalidArguments: 'invalid_arguments',
  invalidInput: 'invalid_input',
  invalidCliPath: 'invalid_cli_path',
  apphostMissing: 'apphost_missing',
  apphostHashMismatch: 'apphost_hash_mismatch',
  outputExists: 'output_exists',
  commandSpawnFailed: 'command_spawn_failed',
  commandTimeout: 'command_timeout',
  commandSignal: 'command_signal',
  commandFailed: 'command_failed',
  malformedJson: 'malformed_json',
  malformedResponse: 'malformed_response',
  authRequired: 'auth_required',
  tenantMismatch: 'tenant_mismatch',
  actorMismatch: 'actor_mismatch',
  operationUnavailable: 'operation_unavailable',
  authContextHttpError: 'auth_context_http_error',
  authContextBound: 'auth_context_bound',
  platformAdminRequired: 'platform_admin_required',
  egressListForbidden: 'egress_list_forbidden',
  egressListUnavailable: 'egress_list_unavailable',
  egressListBadRequest: 'egress_list_bad_request',
  egressListHttpError: 'egress_list_http_error',
  malformedCursor: 'malformed_cursor',
  repeatedCursor: 'repeated_cursor',
  pageLimitExceeded: 'page_limit_exceeded',
  duplicateRequest: 'duplicate_request',
  requestMismatch: 'request_mismatch',
  requestNotFound: 'request_not_found',
  unexpectedError: 'unexpected_error',
});

const KNOWN_ERROR_CODES = new Set(Object.values(ALLOWLISTED_ERROR_CODES));

export class CaseError extends Error {
  constructor(status, errorCode, message) {
    super(message ?? errorCode);
    this.status = status;
    this.errorCode = KNOWN_ERROR_CODES.has(errorCode)
      ? errorCode
      : ALLOWLISTED_ERROR_CODES.unexpectedError;
  }
}

export function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const option = argv[index];
    if (option === '--dry-run' || option === '--help') {
      if (options[option] !== undefined) {
        throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidArguments);
      }
      options[option] = true;
      continue;
    }
    if (!['--input', '--output'].includes(option)
      || typeof argv[index + 1] !== 'string'
      || argv[index + 1].startsWith('--')
      || options[option] !== undefined) {
      throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidArguments);
    }
    options[option] = argv[++index];
  }
  if (!options['--help'] && (!options['--input'] || !options['--output'])) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidArguments);
  }
  return {
    help: options['--help'] === true,
    dryRun: options['--dry-run'] === true,
    inputPath: options['--input'],
    outputPath: options['--output'],
  };
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function exactKeys(value, expected) {
  return isPlainObject(value)
    && Object.keys(value).length === expected.length
    && expected.every(key => Object.hasOwn(value, key));
}

function nonEmpty(value, maxLength = 512) {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= maxLength;
}

function validateUrl(value, originOnly) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidInput);
  }
  const localHttp = url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  if ((!localHttp && url.protocol !== 'https:')
    || url.username
    || url.password
    || url.search
    || url.hash) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidInput);
  }
  if (originOnly && url.pathname !== '/') {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidInput);
  }
  return originOnly ? url.origin : value;
}

function validExpectedRequest(request) {
  if (!exactKeys(request, EXPECTED_REQUEST_KEYS)
    || !UUID_RE.test(request.requestId ?? '')
    || !nonEmpty(request.tenantId, 256)
    || !['platform', 'project'].includes(request.scope)
    || !nonEmpty(request.providerId, 128)
    || !Number.isSafeInteger(request.providerRevision)
    || request.providerRevision < 1
    || !nonEmpty(request.host, 255)
    || /\s/.test(request.host)
    || !Number.isSafeInteger(request.port)
    || request.port < 1
    || request.port > 65535) {
    return false;
  }
  if (request.scope === 'platform') return request.projectId === null;
  return nonEmpty(request.projectId, 32);
}

export function validateInput(value) {
  if (!exactKeys(value, [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'expectedActorId', 'target', 'expectedRequest',
  ])
    || value.caseId !== CASE_ID
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || !nonEmpty(value.expectedActorId, 256)
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))
    || !validExpectedRequest(value.expectedRequest)) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidInput);
  }
  return {
    caseId: CASE_ID,
    sourceCommit: value.sourceCommit.toLowerCase(),
    targetCommit: value.targetCommit.toLowerCase(),
    apphostSha256: value.apphostSha256.toLowerCase(),
    approvedNonProduction: true,
    expectedActorId: value.expectedActorId,
    target: {
      ...value.target,
      gatewayUrl: validateUrl(value.target.gatewayUrl, true),
      brokerIssuer: validateUrl(value.target.brokerIssuer, false),
    },
    expectedRequest: {
      ...value.expectedRequest,
      requestId: value.expectedRequest.requestId.toLowerCase(),
    },
  };
}

export function resolveCliPath(value, platform = process.platform) {
  if (typeof value !== 'string' || !path.isAbsolute(value)) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidCliPath);
  }
  const expectedName = platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  if (path.basename(value).toLowerCase() !== expectedName) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidCliPath);
  }
  return path.resolve(value);
}

function targetArgs(input) {
  return TARGET_OPTIONS.flatMap(([option, key]) => [option, input.target[key]]);
}

export function buildCommands(input, timeouts = {}) {
  const timeoutMs = timeouts.commandTimeoutMs ?? COMMAND_TIMEOUT_MS;
  const target = targetArgs(input);
  return [
    { id: 'version', args: ['--version'], timeoutMs },
    { id: 'api-operations', args: ['api', 'operations', ...target], timeoutMs },
    { id: 'auth-status', args: ['auth', 'status', '--scope', 'projects.admin', ...target], timeoutMs },
    {
      id: 'api-invoke-authorization-context',
      args: ['api', 'invoke', 'getAuthorizationContext', '--scope', 'projects.admin', ...target],
      timeoutMs,
    },
  ];
}

export function buildListEgressCommand(input, page, options = {}) {
  const timeoutMs = options.commandTimeoutMs ?? COMMAND_TIMEOUT_MS;
  const pageSize = options.pageSize ?? DEFAULT_PAGE_SIZE;
  const queryArgs = ['--query', 'state=requested', '--query', `pageSize=${pageSize}`];
  if (options.cursor !== null && options.cursor !== undefined) {
    queryArgs.push('--query', `cursor=${options.cursor}`);
  }
  return {
    id: `api-invoke-list-egress-page-${page}`,
    args: [
      'api', 'invoke', 'listPlatformEgressDestinations',
      '--scope', 'projects.admin',
      ...targetArgs(input),
      ...queryArgs,
    ],
    timeoutMs,
  };
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

export function runCommand(cliPath, args, env, timeoutMs = COMMAND_TIMEOUT_MS) {
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
    spawnError: Boolean(result.error && result.error.code !== 'ETIMEDOUT'),
  };
}

function classifyProcessFailure(record) {
  if (record.timedOut) return ALLOWLISTED_ERROR_CODES.commandTimeout;
  if (record.spawnError) return ALLOWLISTED_ERROR_CODES.commandSpawnFailed;
  if (record.signal) return ALLOWLISTED_ERROR_CODES.commandSignal;
  return null;
}

function tryParseHttpEnvelope(text) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }
  if (!isPlainObject(parsed) || !Number.isSafeInteger(parsed.status)) {
    return null;
  }
  return parsed;
}

function parseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedJson);
  }
}

export function validateAuthStatus(status, input) {
  if (!isPlainObject(status)) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
  }
  if (status.isSignedIn !== true) {
    throw new CaseError('blocked', ALLOWLISTED_ERROR_CODES.authRequired);
  }
  if (status.tenantId !== input.target.tenantId) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.tenantMismatch);
  }
  if (status.actorId !== null && status.actorId !== undefined && status.actorId !== input.expectedActorId) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.actorMismatch);
  }
}

export function validateAuthorizationContext(context, input) {
  if (!isPlainObject(context)) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
  }
  if (context.actorId !== input.expectedActorId) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.actorMismatch);
  }
  if (context.tenantId !== input.target.tenantId) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.tenantMismatch);
  }
  if (context.boundProjectId !== null || context.boundRunId !== null) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.authContextBound);
  }
  const hasPlatformRead = Array.isArray(context.effectiveAuthority)
    && context.effectiveAuthority.some(resource =>
      resource?.resourceType === 'platform'
      && resource.resourceId === 'default'
      && Array.isArray(resource.permissions)
      && resource.permissions.some(grant =>
        grant?.permission === 'readPlatformRuntimeDefaults'
        && Number.isSafeInteger(grant.roleRevision)
        && grant.roleRevision > 0));
  if (!hasPlatformRead) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.platformAdminRequired);
  }
}

function matchDestination(item, expected) {
  if (!isPlainObject(item)
    || typeof item.destinationId !== 'string'
    || !UUID_RE.test(item.destinationId)
    || item.tenantId !== expected.tenantId
    || item.scope !== expected.scope
    || item.projectId !== expected.projectId
    || item.providerId !== expected.providerId
    || item.providerRevision !== expected.providerRevision
    || item.host !== expected.host
    || item.port !== expected.port
    || item.state !== 'requested'
    || item.origin !== 'modelProvider'
    || typeof item.requestedAt !== 'string'
    || Number.isNaN(Date.parse(item.requestedAt))) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.requestMismatch);
  }
  return {
    destinationId: item.destinationId.toLowerCase(),
    requestId: item.requestId.toLowerCase(),
    tenantId: item.tenantId,
    scope: item.scope,
    projectId: item.projectId,
    providerId: item.providerId,
    providerRevision: item.providerRevision,
    host: item.host,
    port: item.port,
    state: item.state,
    origin: item.origin,
    requestedAt: item.requestedAt,
  };
}

function validateCursor(cursor, seenCursors) {
  if (cursor === null) return null;
  if (typeof cursor !== 'string' || cursor.length > 256 || !CURSOR_RE.test(cursor)) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedCursor);
  }
  if (seenCursors.has(cursor)) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.repeatedCursor);
  }
  seenCursors.add(cursor);
  return cursor;
}

function mapEgressListHttpStatus(httpStatus) {
  if (httpStatus === 403) return ALLOWLISTED_ERROR_CODES.egressListForbidden;
  if (httpStatus === 503) return ALLOWLISTED_ERROR_CODES.egressListUnavailable;
  if (httpStatus === 400) return ALLOWLISTED_ERROR_CODES.egressListBadRequest;
  return ALLOWLISTED_ERROR_CODES.egressListHttpError;
}

async function readJsonInput(inputPath) {
  let value;
  try {
    value = JSON.parse(await readFile(inputPath, 'utf8'));
  } catch {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.invalidInput);
  }
  return validateInput(value);
}

async function requireNewOutputDirectory(outputPath) {
  const resolved = path.resolve(outputPath);
  try {
    await stat(resolved);
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.outputExists);
  } catch (error) {
    if (error instanceof CaseError) throw error;
    if (error.code !== 'ENOENT') {
      throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.outputExists);
    }
  }
  return resolved;
}

async function persistResult(handle, result) {
  await handle.truncate(0);
  await handle.write(serializeRedactedJsonLine(result) + '\n', 0, 'utf8');
  await handle.sync();
}

export async function runCase(options, env = process.env, overrides = {}) {
  const runner = overrides.runCommand ?? runCommand;
  const maxPages = overrides.maxPages ?? MAXIMUM_PAGES;
  const pageSize = overrides.pageSize ?? DEFAULT_PAGE_SIZE;
  const input = await readJsonInput(path.resolve(options.inputPath));
  const cliPath = resolveCliPath(env.AGENTWEAVER_CLI_PATH);
  const outputPath = await requireNewOutputDirectory(options.outputPath);
  const apphostStat = await stat(cliPath).catch(() => null);
  if (!apphostStat?.isFile()) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.apphostMissing);
  }
  const apphostHash = await sha256(cliPath);
  if (input.apphostSha256 !== apphostHash) {
    throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.apphostHashMismatch);
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
  const resultPath = path.join(outputPath, 'result.json');
  const resultHandle = await open(resultPath, 'wx', 0o600);
  await resultHandle.writeFile('{"status":"incomplete"}\n', 'utf8');
  const startedAt = new Date().toISOString();

  let commandCount = 0;
  let pagesRead = 0;
  let status = 'failed';
  let errorCode = ALLOWLISTED_ERROR_CODES.unexpectedError;
  let matchedDestination = null;

  const execute = async (command, evaluator, { allowExitOneEnvelope = false } = {}) => {
    const record = runner(cliPath, command.args, childEnvironment(env), command.timeoutMs);
    commandCount += 1;
    let stepStatus = 'passed';
    let stepErrorCode = classifyProcessFailure(record);
    let httpStatus = null;
    let pageSummary = null;
    let outcome = null;

    if (stepErrorCode === null && record.exitCode !== 0) {
      const parsedEnvelope = allowExitOneEnvelope && record.exitCode === 1
        ? tryParseHttpEnvelope(record.stdout)
        : null;
      if (parsedEnvelope === null || parsedEnvelope.status === 200) {
        stepErrorCode = ALLOWLISTED_ERROR_CODES.commandFailed;
      }
    }

    if (stepErrorCode !== null) {
      stepStatus = 'failed';
      outcome = new CaseError('failed', stepErrorCode);
    } else {
      try {
        const evaluated = evaluator(record.stdout);
        httpStatus = evaluated?.httpStatus ?? null;
        pageSummary = evaluated?.pageSummary ?? null;
      } catch (error) {
        const mapped = error instanceof CaseError
          ? error
          : new CaseError('failed', ALLOWLISTED_ERROR_CODES.unexpectedError);
        stepStatus = mapped.status;
        stepErrorCode = mapped.errorCode;
        if (error?.httpStatus !== undefined) httpStatus = error.httpStatus;
        outcome = mapped;
      }
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
      signal: record.signal,
      timedOut: record.timedOut,
      status: stepStatus,
      httpStatus,
      errorCode: stepErrorCode,
      ...(pageSummary !== null ? { pageSummary } : {}),
    });

    if (outcome !== null) throw outcome;
  };

  try {
    for (const command of buildCommands(input, overrides)) {
      await execute(command, (stdout) => {
        if (command.id === 'version') {
          if (!nonEmpty(stdout)) {
            throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
          }
          return null;
        }
        if (command.id === 'api-operations') {
          const operations = parseJson(stdout);
          if (!Array.isArray(operations)) {
            throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
          }
          const hasAuth = operations.some(item => item?.operationId === 'getAuthorizationContext');
          const hasList = operations.some(item => item?.operationId === 'listPlatformEgressDestinations');
          if (!hasAuth || !hasList) {
            throw new CaseError('blocked', ALLOWLISTED_ERROR_CODES.operationUnavailable);
          }
          return null;
        }
        if (command.id === 'auth-status') {
          validateAuthStatus(parseJson(stdout), input);
          return null;
        }
        if (command.id === 'api-invoke-authorization-context') {
          const envelope = parseJson(stdout);
          if (!isPlainObject(envelope) || !Number.isSafeInteger(envelope.status)) {
            throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
          }
          if (envelope.status !== 200) {
            const err = new CaseError('failed', ALLOWLISTED_ERROR_CODES.authContextHttpError);
            err.httpStatus = envelope.status;
            throw err;
          }
          validateAuthorizationContext(envelope.body, input);
          return { httpStatus: envelope.status };
        }
        return null;
      }, { allowExitOneEnvelope: command.id === 'api-invoke-authorization-context' });
    }

    const seenCursors = new Set();
    let cursor = null;
    for (let page = 1; ; page += 1) {
      const command = buildListEgressCommand(input, page, {
        commandTimeoutMs: overrides.commandTimeoutMs,
        pageSize,
        cursor,
      });
      let nextCursor = null;
      await execute(command, (stdout) => {
        const envelope = parseJson(stdout);
        if (!isPlainObject(envelope) || !Number.isSafeInteger(envelope.status)) {
          throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
        }
        if (envelope.status !== 200) {
          const err = new CaseError('failed', mapEgressListHttpStatus(envelope.status));
          err.httpStatus = envelope.status;
          throw err;
        }
        const body = envelope.body;
        if (!isPlainObject(body)
          || !Array.isArray(body.destinations)
          || !Object.hasOwn(body, 'nextCursor')) {
          throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
        }

        pagesRead += 1;
        let matchedOnPage = false;
        for (const item of body.destinations) {
          if (!isPlainObject(item) || typeof item.requestId !== 'string') {
            throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.malformedResponse);
          }
          if (item.requestId.toLowerCase() === input.expectedRequest.requestId) {
            if (matchedDestination !== null) {
              throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.duplicateRequest);
            }
            matchedDestination = matchDestination(item, input.expectedRequest);
            matchedOnPage = true;
          }
        }

        nextCursor = validateCursor(body.nextCursor, seenCursors);
        if (nextCursor !== null && page >= maxPages) {
          throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.pageLimitExceeded);
        }

        return {
          httpStatus: envelope.status,
          pageSummary: {
            page,
            itemCount: body.destinations.length,
            hasNextCursor: nextCursor !== null,
            matched: matchedOnPage,
          },
        };
      }, { allowExitOneEnvelope: true });

      if (nextCursor === null) break;
      cursor = nextCursor;
    }

    if (matchedDestination === null) {
      throw new CaseError('failed', ALLOWLISTED_ERROR_CODES.requestNotFound);
    }

    status = 'passed';
    errorCode = null;
  } catch (error) {
    const mapped = error instanceof CaseError
      ? error
      : new CaseError('failed', ALLOWLISTED_ERROR_CODES.unexpectedError);
    status = mapped.status;
    errorCode = mapped.errorCode;
  }

  const result = {
    schemaVersion: 1,
    caseId: CASE_ID,
    runId,
    scope: 'read-only pending BYOK model-provider host request readback',
    status,
    errorCode,
    startedAt,
    completedAt: new Date().toISOString(),
    sourceCommit: input.sourceCommit,
    targetCommit: input.targetCommit,
    apphost: path.basename(cliPath),
    apphostSha256: apphostHash,
    scriptSha256: scriptHash,
    target: {
      approvedNonProduction: input.approvedNonProduction,
      gatewayOrigin: input.target.gatewayUrl,
      tenantId: input.target.tenantId,
      expectedActorId: input.expectedActorId,
    },
    commandCount,
    pagesRead,
    matchedDestination,
  };
  await persistResult(resultHandle, result);
  await resultHandle.close();

  console.log(JSON.stringify({
    status,
    caseId: CASE_ID,
    runId,
    errorCode,
    commandCount,
    pagesRead,
  }));

  return status === 'passed' ? 0 : status === 'blocked' ? 2 : 1;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    console.log('Use --input <case.json> --output <new-evidence-directory> [--dry-run].');
    return 0;
  }
  return runCase(options);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().then(code => {
    process.exitCode = code;
  }).catch(error => {
    const mapped = error instanceof CaseError
      ? error
      : new CaseError('failed', ALLOWLISTED_ERROR_CODES.unexpectedError);
    console.error(JSON.stringify({
      status: mapped.status,
      caseId: CASE_ID,
      errorCode: mapped.errorCode,
    }));
    process.exitCode = mapped.status === 'blocked' ? 2 : 1;
  });
}
