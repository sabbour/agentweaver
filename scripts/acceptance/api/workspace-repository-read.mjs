#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, stat } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const CASE_ID = 'workspace-repository-read';
const CASE_SCRIPT_PATH = fileURLToPath(import.meta.url);
const MAX_OUTPUT = 16 * 1024 * 1024;
export const COMMAND_TIMEOUT_MS = 60_000;
// One workspace command can wait for two browser flows (sign-in and repository selection) of 5 minutes each.
export const INTERACTIVE_COMMAND_TIMEOUT_MS = 660_000;
export const MAXIMUM_REF_PAGES = 100;
const USAGE = 'Use --input <case.json> --output <new-evidence-directory> --interactive [--dry-run].';
const SIGN_IN_REQUIRED = 'Sign in with agentweaver auth login --scope projects.admin and the target options before running this case.';
const APPROVAL_TIMEOUT = 'The browser approval did not complete in time.';
const BROKER_MESSAGES = [
  'The Identity Broker could not complete sign-in.',
  'Sign-in was canceled or access was denied.',
];
const WORKSPACE_OPERATIONS = {
  refs: 'listWorkspaceRepositoryRefs',
  tree: 'readWorkspaceRepositoryTree',
  file: 'readWorkspaceRepositoryFile',
};
const REQUIRED_OPERATIONS = ['getAuthorizationContext', 'getProjectConfiguration', ...Object.values(WORKSPACE_OPERATIONS)];
const TARGET_KEYS = ['gatewayUrl', 'brokerIssuer', 'clientId', 'apiAudience', 'tenantId'];
const FIXTURE_KEYS = ['projectId', 'repository', 'refKind', 'refName', 'commitSha', 'filePath', 'byteLength', 'sha256'];
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
    if (option === '--dry-run' || option === '--help' || option === '--interactive') {
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
    interactive: options['--interactive'] === true,
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

function validFixture(fixture) {
  return exactKeys(fixture, FIXTURE_KEYS)
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(fixture.projectId ?? '')
    && /^[A-Za-z0-9-]{1,39}\/[A-Za-z0-9._-]{1,100}$/.test(fixture.repository ?? '')
    && ['branch', 'tag'].includes(fixture.refKind)
    && nonEmpty(fixture.refName)
    && /^[a-f0-9]{40}$/i.test(fixture.commitSha ?? '')
    && nonEmpty(fixture.filePath)
    && !fixture.filePath.startsWith('/')
    && !fixture.filePath.includes('\\')
    && Number.isSafeInteger(fixture.byteLength)
    && fixture.byteLength >= 0
    && fixture.byteLength <= 1024 * 1024
    && /^[a-f0-9]{64}$/i.test(fixture.sha256 ?? '');
}

export function validateInput(value) {
  if (!exactKeys(value, [
    'caseId', 'sourceCommit', 'targetCommit', 'apphostSha256',
    'approvedNonProduction', 'expectedActorId', 'target', 'fixture',
  ])
    || value.caseId !== CASE_ID
    || !/^[a-f0-9]{40}$/i.test(value.sourceCommit ?? '')
    || !/^[a-f0-9]{40}$/i.test(value.targetCommit ?? '')
    || !/^[a-f0-9]{64}$/i.test(value.apphostSha256 ?? '')
    || value.approvedNonProduction !== true
    || !nonEmpty(value.expectedActorId)
    || !exactKeys(value.target, TARGET_KEYS)
    || TARGET_KEYS.some(key => !nonEmpty(value.target[key]))
    || !validFixture(value.fixture)) {
    throw new Error('The case input does not match the Workspace repository read case schema.');
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
    fixture: {
      ...value.fixture,
      projectId: value.fixture.projectId.toLowerCase(),
      commitSha: value.fixture.commitSha.toLowerCase(),
      sha256: value.fixture.sha256.toLowerCase(),
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

export function buildCommands(input, timeouts = {}) {
  const timeoutMs = timeouts.commandTimeoutMs ?? COMMAND_TIMEOUT_MS;
  const target = targetArgs(input);
  return [
    { id: 'version', args: ['--version'], timeoutMs },
    { id: 'api-operations', args: ['api', 'operations', ...target], timeoutMs },
    { id: 'auth-status', args: ['auth', 'status', '--scope', 'projects.admin', ...target], timeoutMs },
    {
      id: 'rest-authorization-context',
      args: ['rest', ...target, '--scope', 'projects.admin', '--method', 'GET', '--url', '/api/v1/authorization/context'],
      timeoutMs,
    },
  ];
}

export function buildWorkspaceCommand(input, command, options = {}) {
  const timeoutMs = options.interactiveTimeoutMs ?? INTERACTIVE_COMMAND_TIMEOUT_MS;
  const { projectId, commitSha, filePath } = input.fixture;
  const target = targetArgs(input);
  if (command === 'refs') {
    const cursor = options.cursor === undefined ? [] : ['--cursor', options.cursor];
    return {
      id: options.cursor === undefined ? 'workspace-refs' : `workspace-refs-${options.cursor}`,
      args: ['workspace', 'refs', '--project', projectId, '--interactive', ...cursor, ...target],
      timeoutMs,
      interactive: true,
    };
  }
  if (command === 'tree') {
    return {
      id: 'workspace-tree',
      args: ['workspace', 'tree', '--project', projectId, '--ref', commitSha, '--interactive', ...target],
      timeoutMs,
      interactive: true,
    };
  }
  if (command === 'file') {
    return {
      id: 'workspace-file',
      args: ['workspace', 'file', '--project', projectId, '--ref', commitSha, '--path', filePath, '--interactive', ...target],
      timeoutMs,
      interactive: true,
    };
  }
  throw new Error(`Unknown workspace command: ${command}`);
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

export function runCommand(cliPath, args, env, timeoutMs) {
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

class CaseOutcome extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function parseJson(text, step) {
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${step} did not return valid JSON.`);
  }
}

function requireSuccess(record, command) {
  if (command.interactive && record.timedOut) throw new CaseOutcome('blocked', APPROVAL_TIMEOUT);
  if (command.interactive && record.exitCode !== 0
    && BROKER_MESSAGES.some(message => record.stderr.includes(message))) {
    throw new CaseOutcome('blocked', `${command.id} stopped at the Identity Broker: ${BROKER_MESSAGES.find(message => record.stderr.includes(message))}`);
  }
  if (record.error || record.exitCode !== 0 || record.signal || record.timedOut) {
    throw new Error(`${command.id} did not complete successfully.`);
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

function requireWorkspaceDocument(document, command, input) {
  const operation = WORKSPACE_OPERATIONS[command];
  if (document?.status !== 'passed'
    || document.operation !== operation
    || document.selection?.version !== 2
    || document.selection?.repositoryFullName !== input.fixture.repository
    || document.result === null
    || typeof document.result !== 'object') {
    throw new Error(`workspace ${command} returned an unexpected result.`);
  }
  return document.result;
}

// Keep only the checked fields so file content and other response data never enter the evidence.
function summarize(command, result) {
  if (command === 'refs') {
    return {
      repository: result.repository,
      items: Array.isArray(result.items) ? result.items.length : null,
      nextCursor: result.nextCursor ?? null,
    };
  }
  if (command === 'tree') {
    return {
      repository: result.repository,
      commitSha: result.commitSha,
      items: Array.isArray(result.items) ? result.items.length : null,
    };
  }
  return {
    path: result.path,
    commitSha: result.commitSha,
    size: result.size,
    isBinary: result.isBinary,
    sha256: result.sha256,
    gitBlobSha1: result.gitBlobSha1,
  };
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

export async function runCase(options, env = process.env, overrides = {}) {
  const runner = overrides.runCommand ?? runCommand;
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
  if (!options.interactive) {
    throw new Error('This case opens browser approvals. Run it again with --interactive.');
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
  let observed = null;

  const execute = async (command, workspace) => {
    const record = runner(cliPath, command.args, childEnvironment(env), command.timeoutMs);
    let document = null;
    let summary = null;
    if (workspace && !record.timedOut && record.exitCode === 0) {
      try {
        document = JSON.parse(record.stdout);
      } catch {
        document = null;
      }
      summary = summarize(workspace, document?.result ?? {});
    }
    const { stdout, ...rest } = record;
    await appendRedactedJsonLine(commandsPath, {
      caseId: CASE_ID,
      runId,
      stepId: command.id,
      executable: path.basename(cliPath),
      arguments: command.args,
      timeoutMs: command.timeoutMs,
      ...rest,
      ...(workspace ? { stdoutSummary: summary } : { stdout }),
    });
    commandCount += 1;
    requireSuccess(record, command);
    return workspace ? requireWorkspaceDocument(document, workspace, input) : record.stdout;
  };

  try {
    for (const command of buildCommands(input, overrides)) {
      const stdout = await execute(command);
      if (command.id === 'version') {
        version = stdout.trim();
        if (!version) throw new Error('The apphost did not report its version.');
      } else if (command.id === 'api-operations') {
        const operations = parseJson(stdout, command.id);
        const missing = REQUIRED_OPERATIONS.filter(operation =>
          !Array.isArray(operations) || !operations.some(item => item?.operationId === operation));
        if (missing.length > 0) {
          throw new CaseOutcome('blocked', `The target Gateway contract does not expose ${missing.join(', ')}.`);
        }
      } else if (command.id === 'auth-status') {
        if (!validateAuthStatus(parseJson(stdout, command.id), input)) {
          throw new CaseOutcome('blocked', SIGN_IN_REQUIRED);
        }
      } else if (command.id === 'rest-authorization-context') {
        const response = parseJson(stdout, command.id);
        if (response?.status !== 200) throw new Error('The rest request did not return HTTP 200.');
        requireActorContext(response.body, input, command.id);
      }
    }

    const { fixture } = input;
    let cursor;
    const seenCursors = new Set();
    let reference = null;
    for (let page = 1; reference === null; page += 1) {
      if (page > MAXIMUM_REF_PAGES) throw new Error(`The ref was not found in ${MAXIMUM_REF_PAGES} pages.`);
      const refs = await execute(buildWorkspaceCommand(input, 'refs', { ...overrides, cursor }), 'refs');
      if (refs.repository !== fixture.repository || !Array.isArray(refs.items)) {
        throw new Error('workspace refs returned an unexpected repository page.');
      }
      reference = refs.items.find(item => item?.kind === fixture.refKind && item?.name === fixture.refName) ?? null;
      if (reference !== null) break;
      if (refs.nextCursor === undefined || refs.nextCursor === null) {
        throw new Error('The ref was not found in the repository.');
      }
      if (seenCursors.has(refs.nextCursor)) throw new Error('workspace refs repeated a page cursor.');
      seenCursors.add(refs.nextCursor);
      cursor = refs.nextCursor;
    }
    if (reference.commitSha?.toLowerCase() !== fixture.commitSha) {
      throw new Error('The ref does not point to the expected commit.');
    }

    const tree = await execute(buildWorkspaceCommand(input, 'tree', overrides), 'tree');
    if (tree.commitSha?.toLowerCase() !== fixture.commitSha || !Array.isArray(tree.items)) {
      throw new Error('workspace tree returned an unexpected commit.');
    }
    const entry = tree.items.find(item => item?.path === fixture.filePath);
    if (entry?.kind !== 'file' || entry.size !== fixture.byteLength || typeof entry.blobSha !== 'string') {
      throw new Error('workspace tree does not list the expected file.');
    }

    const file = await execute(buildWorkspaceCommand(input, 'file', overrides), 'file');
    if (file.path !== fixture.filePath
      || file.commitSha?.toLowerCase() !== fixture.commitSha
      || file.size !== fixture.byteLength
      || file.sha256 !== fixture.sha256
      || file.gitBlobSha1 !== entry.blobSha.toLowerCase()) {
      throw new Error('workspace file does not match the expected size, SHA-256, or blob SHA.');
    }
    observed = {
      refKind: reference.kind,
      refName: reference.name,
      commitSha: fixture.commitSha,
      filePath: file.path,
      byteLength: file.size,
      sha256: file.sha256,
      gitBlobSha1: file.gitBlobSha1,
    };
    status = 'passed';
    failure = null;
  } catch (error) {
    status = error instanceof CaseOutcome ? error.status : 'failed';
    failure = error.message;
  }
  const result = {
    schemaVersion: 1,
    caseId: CASE_ID,
    runId,
    scope: 'native CLI Workspace repository refs, tree, and file read with browser approval',
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
      projectId: input.fixture.projectId,
      repository: input.fixture.repository,
    },
    apphost: { name: path.basename(cliPath), sha256: apphostHash, version },
    script: { name: path.basename(CASE_SCRIPT_PATH), sha256: scriptHash },
    commands: commandCount,
    observed,
    cleanup: { status: 'not-required', reason: 'The case only reads the repository and makes no changes.' },
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
