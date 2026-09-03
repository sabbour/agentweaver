#!/usr/bin/env node
import {
  constants as cryptoConstants,
  createDecipheriv,
  generateKeyPairSync,
  privateDecrypt,
  randomUUID,
} from 'node:crypto';
import { existsSync } from 'node:fs';
import { chmod, mkdir, readFile, readdir, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { attachPageCapture } from '../lib/evidence.mjs';
import { openBrowserSession } from '../lib/browser.mjs';
import { executeUiAction } from '../lib/ui-actions.mjs';
import { loadSession } from '../lib/session-store.mjs';
import { runtimeDirectory } from '../lib/session-runtime.mjs';
import { redact, sanitizeUrl } from '../../harness-shared/redaction.mjs';

const HEARTBEAT_INTERVAL_MS = 1_000;
const POLL_INTERVAL_MS = 50;
const IDLE_TIMEOUT_MS = 30 * 60_000;
const sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

function parseArgs(argv) {
  const out = {};
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (!arg.startsWith('--')) continue;
    if (argv[index + 1] && !argv[index + 1].startsWith('--')) out[arg.slice(2)] = argv[++index];
    else out[arg.slice(2)] = true;
  }
  return out;
}

async function writeJsonAtomic(file, value) {
  await mkdir(path.dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.${randomUUID()}.tmp`;
  await writeFile(temporary, JSON.stringify(value, null, 2), { encoding: 'utf8', mode: 0o600 });
  await rename(temporary, file);
}

function openActionArgs(sealed, privateKey) {
  if (sealed?.algorithm !== 'rsa-oaep-sha256+aes-256-gcm') {
    throw new Error('UI action arguments were not sealed for this worker');
  }
  const key = privateDecrypt({
    key: privateKey,
    oaepHash: 'sha256',
    padding: cryptoConstants.RSA_PKCS1_OAEP_PADDING,
  }, Buffer.from(sealed.encryptedKey, 'base64'));
  const decipher = createDecipheriv('aes-256-gcm', key, Buffer.from(sealed.iv, 'base64'));
  decipher.setAuthTag(Buffer.from(sealed.authTag, 'base64'));
  return JSON.parse(Buffer.concat([
    decipher.update(Buffer.from(sealed.ciphertext, 'base64')),
    decipher.final(),
  ]).toString('utf8'));
}

function flattenErrors(error) {
  return error instanceof AggregateError
    ? error.errors.flatMap((item) => flattenErrors(item))
    : [error];
}

function combineErrors(errors, message) {
  const present = errors.filter(Boolean);
  if (present.length === 0) return null;
  if (present.length === 1) return present[0];
  return new AggregateError(present, message, { cause: present[0] });
}

function errorEvidence(error, fallbackCode) {
  return redact({
    code: error?.code ?? fallbackCode,
    message: String(error?.message ?? error),
    errors: error instanceof AggregateError
      ? flattenErrors(error).map((item) => ({
          code: item?.code ?? fallbackCode,
          message: String(item?.message ?? item),
        }))
      : undefined,
  });
}

async function loadRecovery(runtime) {
  const file = path.join(runtime, 'recovery.json');
  if (!existsSync(file)) return null;
  try {
    return JSON.parse(await readFile(file, 'utf8'));
  } catch {
    return null;
  }
}

async function saveRecovery(runtime, browserRuntime) {
  const origin = await browserRuntime.page.evaluate(() => window.location.origin);
  const entries = await browserRuntime.page.evaluate(() => ({ ...window.sessionStorage }));
  const storageState = path.join(runtime, 'recovery.storageState.json');
  await browserRuntime.context.storageState({ path: storageState });
  await chmod(storageState, 0o600).catch(() => {});
  await writeJsonAtomic(path.join(runtime, 'recovery.json'), {
    lastUrl: sanitizeUrl(browserRuntime.page.url()),
    sessionStorageSeed: { origin, entries },
    updatedAt: new Date().toISOString(),
  });
}

async function nextRequest(requests) {
  const names = (await readdir(requests)).filter((name) => name.endsWith('.json')).sort();
  if (names.length === 0) return null;
  const file = path.join(requests, names[0]);
  try {
    const request = JSON.parse(await readFile(file, 'utf8'));
    await rm(file, { force: true });
    return request;
  } catch (error) {
    await rm(file, { force: true });
    throw error;
  }
}

export async function runSessionWorker({
  argv = process.argv.slice(2),
  openBrowserSessionImpl = openBrowserSession,
  processImpl = process,
} = {}) {
  const args = parseArgs(argv);
  const sessionId = args.session;
  const sessionsDirectory = path.resolve(args['sessions-dir']);
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  const requests = path.join(runtime, 'requests');
  const responses = path.join(runtime, 'responses');
  const stateFile = path.join(runtime, 'state.json');
  const launchId = args['launch-id'];
  let browserRuntime;
  let heartbeat;
  let stopping = false;
  let finishRequest;
  let workerError;
  let browserOpenAttempted = false;
  let startupTermination;
  let lastActivity = Date.now();
  let stateWrite = Promise.resolve();
  const { publicKey, privateKey } = generateKeyPairSync('rsa', {
    modulusLength: 2048,
    publicKeyEncoding: { type: 'spki', format: 'pem' },
    privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
  });

  const writeState = (status, extra = {}) => {
    const state = {
      launchId,
      pid: processImpl.pid,
      status,
      heartbeatAt: new Date().toISOString(),
      argumentPublicKey: publicKey,
      ...extra,
    };
    stateWrite = stateWrite.catch(() => {}).then(() => writeFile(
      stateFile,
      JSON.stringify(redact(state), null, 2),
      { encoding: 'utf8', mode: 0o600 },
    ));
    return stateWrite;
  };

  const requestStop = () => { stopping = true; };
  processImpl.on('SIGINT', requestStop);
  processImpl.on('SIGTERM', requestStop);

  try {
    await mkdir(requests, { recursive: true });
    await mkdir(responses, { recursive: true });
    await writeState('starting');
    const session = await loadSession(sessionsDirectory, sessionId);
    const recovery = await loadRecovery(runtime);
    const recoveredStorageState = path.join(runtime, 'recovery.storageState.json');
    browserOpenAttempted = true;
    try {
      browserRuntime = await openBrowserSessionImpl({
        baseUrl: session.baseUrl,
        storageState: existsSync(recoveredStorageState) ? recoveredStorageState : session.storageState,
        sessionStorageSeed: recovery?.sessionStorageSeed ?? null,
        headless: true,
      });
    } catch (error) {
      startupTermination = error?.termination;
      throw error;
    }
    const capture = attachPageCapture(browserRuntime.page);
    if (recovery?.lastUrl && recovery.lastUrl !== 'about:blank') {
      const destination = new URL(recovery.lastUrl);
      if (destination.origin === new URL(session.baseUrl).origin) await browserRuntime.goto(destination.toString());
    }
    await writeState('ready', { recovered: Boolean(recovery) });
    heartbeat = setInterval(() => {
      writeState('ready').catch(() => { stopping = true; });
    }, HEARTBEAT_INTERVAL_MS);
    heartbeat.unref();

    while (!stopping) {
      const request = await nextRequest(requests);
      if (!request) {
        if (Date.now() - lastActivity >= IDLE_TIMEOUT_MS) break;
        await sleep(POLL_INTERVAL_MS);
        continue;
      }
      lastActivity = Date.now();
      if (request.kind === 'finish') {
        finishRequest = request;
        stopping = true;
        continue;
      }

      try {
        const liveArgs = openActionArgs(request.sealedArgs, privateKey);
        const step = await executeUiAction({
          runtime: browserRuntime,
          capture,
          session,
          args: liveArgs,
          eventId: request.eventId,
          transcriptDirectory: path.join(path.dirname(sessionsDirectory), 'transcripts-ui', sessionId),
        });
        await saveRecovery(runtime, browserRuntime);
        await writeJsonAtomic(path.join(responses, `${request.requestId}.json`), {
          kind: 'action',
          requestId: request.requestId,
          ok: true,
          action: request.args._[0],
          eventId: request.eventId,
          step,
          completedAt: new Date().toISOString(),
        });
      } catch (error) {
        await writeJsonAtomic(path.join(responses, `${request.requestId}.json`), {
          kind: 'action',
          requestId: request.requestId,
          ok: false,
          action: request.args._[0],
          eventId: request.eventId,
          step: error.evidenceStep ?? null,
          error: redact({
            code: error.code ?? 'COMMAND_FAILED',
            message: String(error.message ?? error),
            readiness: error.readiness ?? null,
          }),
          completedAt: new Date().toISOString(),
        });
      }
    }
  } catch (error) {
    workerError = error;
    processImpl.exitCode = 2;
  } finally {
    if (heartbeat) clearInterval(heartbeat);
    processImpl.off('SIGINT', requestStop);
    processImpl.off('SIGTERM', requestStop);

    let closeError;
    let closeTermination;
    if (browserRuntime) {
      try {
        closeTermination = await browserRuntime.close();
      } catch (error) {
        closeError = error;
        closeTermination = error?.termination;
        processImpl.exitCode = 2;
      }
    }

    const shutdownError = combineErrors([workerError, closeError], 'UI session worker shutdown failed');
    const startupClosureProven = startupTermination?.browserClosureProven === true
      && startupTermination?.browserClosed === true;
    const closeClosureProven = browserRuntime
      ? closeTermination == null
        ? !closeError
        : closeTermination.browserClosureProven === true && closeTermination.browserClosed === true
      : false;
    const browserClosureProven = browserRuntime
      ? closeClosureProven
      : browserOpenAttempted ? startupClosureProven : true;
    const termination = {
      browserLaunchAttempted: browserOpenAttempted,
      browserLaunched: browserRuntime
        ? true
        : startupTermination?.browserLaunched === true,
      browserCloseAttempted: browserRuntime
        ? true
        : startupTermination?.browserCloseAttempted === true,
      browserClosed: browserClosureProven,
      browserClosureProven,
      workerExitExpected: true,
      workerTerminated: false,
    };
    let responseError;
    if (finishRequest) {
      try {
        await writeJsonAtomic(path.join(responses, `${finishRequest.requestId}.json`), {
          kind: 'finish',
          requestId: finishRequest.requestId,
          ok: !shutdownError,
          termination,
          error: shutdownError ? errorEvidence(
            shutdownError,
            closeError ? 'BROWSER_CLOSE_FAILED' : 'SESSION_WORKER_FAILED',
          ) : undefined,
          completedAt: new Date().toISOString(),
        });
      } catch (error) {
        responseError = error;
        processImpl.exitCode = 2;
      }
    }

    const reportedError = combineErrors([shutdownError, responseError], 'UI session worker shutdown reporting failed');
    let stateError;
    try {
      await writeState(reportedError ? 'failed' : 'exiting', {
        termination,
        ...(reportedError
          ? { error: errorEvidence(reportedError, closeError ? 'BROWSER_CLOSE_FAILED' : 'SESSION_WORKER_FAILED') }
          : {}),
      });
    } catch (error) {
      stateError = error;
      processImpl.exitCode = 2;
    }

    const finalError = combineErrors([reportedError, stateError], 'UI session worker shutdown failed');
    if (finalError) throw finalError;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  runSessionWorker().catch((error) => {
    console.error(redact(String(error?.message ?? error)));
    process.exitCode = 2;
  });
}
