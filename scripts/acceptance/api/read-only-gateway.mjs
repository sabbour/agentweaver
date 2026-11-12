#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, stat } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const CASE_ID = 'read-only-gateway';
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 4 * 1024 * 1024;
const COMMAND_TIMEOUT_MS = 60_000;
const TARGET_KEYS = ['gatewayUrl', 'brokerIssuer', 'clientId', 'apiAudience', 'tenantId'];
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

export function validateInput(value) {
  if (!exactKeys(value, [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'expectedActorId', 'target',
  ])
    || value.caseId !== CASE_ID
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || !nonEmpty(value.expectedActorId)
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))) {
    throw new Error('The case input does not match the read-only Gateway case schema.');
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

export function buildCommands(input) {
  const targetArgs = TARGET_OPTIONS.flatMap(([option, key]) => [option, input.target[key]]);
  return [
    { id: 'version', args: ['--version'] },
    { id: 'auth-status', args: ['auth', 'status', '--scope', 'api.read', ...targetArgs] },
    { id: 'api-operations', args: ['api', 'operations', ...targetArgs] },
    { id: 'api-describe', args: ['api', 'describe', 'getAuthorizationContext', ...targetArgs] },
    {
      id: 'rest-authorization-context',
      args: ['rest', ...targetArgs, '--scope', 'api.read', '--method', 'GET', '--url', '/api/v1/authorization/context'],
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

function requireActorContext(context, input, step) {
  if (context?.actorId !== input.expectedActorId || context?.tenantId !== input.target.tenantId) {
    throw new Error(`${step} returned an actor or tenant that does not match the case input.`);
  }
}

export function validateAuthStatus(status, input) {
  if (status?.isSignedIn !== true) return false;
  if (status.tenantId !== input.target.tenantId) {
    throw new Error('auth status returned a tenant that does not match the case input.');
  }
  return true;
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

async function runCase(options, env = process.env) {
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
  const resultPath = path.join(outputPath, 'result.json');
  const resultHandle = await open(resultPath, 'wx', 0o600);
  await resultHandle.writeFile('{"status":"incomplete"}\n', 'utf8');
  const startedAt = new Date().toISOString();
  let commandCount = 0;
  let status = 'failed';
  let failure = 'The case did not complete.';
  let version = null;
  try {
    for (const command of buildCommands(input)) {
      const record = runCommand(cliPath, command.args, env);
      await appendRedactedJsonLine(commandsPath, {
        caseId: CASE_ID,
        runId,
        stepId: command.id,
        executable: path.basename(cliPath),
        arguments: command.args,
        ...record,
      });
      commandCount += 1;
      requireSuccess(record, command.id);
      if (command.id === 'version') {
        version = record.stdout.trim();
        if (!version) throw new Error('The apphost did not report its version.');
      } else if (command.id === 'auth-status') {
        const auth = parseJson(record.stdout, command.id);
        if (!validateAuthStatus(auth, input)) {
          status = 'blocked';
          failure = 'Sign in through the supported CLI auth flow before running this case.';
          break;
        }
      } else if (command.id === 'api-operations') {
        const operations = parseJson(record.stdout, command.id);
        if (!Array.isArray(operations) || !operations.some(item => item.operationId === 'getAuthorizationContext')) {
          status = 'blocked';
          failure = 'The target does not expose getAuthorizationContext in its Gateway contract.';
          break;
        }
      } else if (command.id === 'api-describe') {
        const operation = parseJson(record.stdout, command.id);
        if (operation?.method !== 'GET' || operation?.path !== '/api/v1/authorization/context') {
          status = 'blocked';
          failure = 'The target Gateway contract does not match the expected authorization-context route.';
          break;
        }
      } else if (command.id === 'rest-authorization-context') {
        const response = parseJson(record.stdout, command.id);
        if (response?.status !== 200) throw new Error('The rest request did not return HTTP 200.');
        requireActorContext(response.body, input, command.id);
      }
    }
    if (failure === 'The case did not complete.') {
      status = 'passed';
      failure = null;
    }
  } catch (error) {
    failure = error.message;
  }
  const result = {
    schemaVersion: 1,
    caseId: CASE_ID,
    runId,
    scope: 'read-only CLI discovery and caller authorization context',
    status,
    startedAt,
    completedAt: new Date().toISOString(),
    sourceCommit: input.sourceCommit,
    targetCommit: input.targetCommit,
    target: {
      approvedNonProduction: input.approvedNonProduction,
      gatewayOrigin: input.target.gatewayUrl,
      tenantId: input.target.tenantId,
      expectedActorId: input.expectedActorId,
    },
    apphost: { name: path.basename(cliPath), sha256: apphostHash, version },
    script: { name: path.basename(CASE_SCRIPT_PATH), sha256: scriptHash },
    commands: commandCount,
    cleanup: { status: 'not-required', reason: 'The case makes no fixture or resource changes.' },
    failure,
  };
  await persistResult(resultHandle, result);
  await resultHandle.close();
  console.log(JSON.stringify({ status, caseId: CASE_ID, runId, evidence: outputPath, failure }));
  return status === 'passed' ? 0 : status === 'blocked' ? 2 : 1;
}

async function main(argv = process.argv.slice(2), env = process.env) {
  let options;
  try {
    options = parseArgs(argv);
    if (options.help) {
      console.log('Use --input <case.json> --output <new-evidence-directory> [--dry-run].');
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
