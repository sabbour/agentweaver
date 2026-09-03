#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { existsSync } from 'node:fs';
import { chmod, mkdir, readFile, readdir, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
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

async function main() {
  const args = parseArgs(process.argv.slice(2));
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
  let lastActivity = Date.now();
  let stateWrite = Promise.resolve();

  const writeState = (status, extra = {}) => {
    const state = {
      launchId,
      pid: process.pid,
      status,
      heartbeatAt: new Date().toISOString(),
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
  process.on('SIGINT', requestStop);
  process.on('SIGTERM', requestStop);

  try {
    await mkdir(requests, { recursive: true });
    await mkdir(responses, { recursive: true });
    await writeState('starting');
    const session = await loadSession(sessionsDirectory, sessionId);
    const recovery = await loadRecovery(runtime);
    const recoveredStorageState = path.join(runtime, 'recovery.storageState.json');
    browserRuntime = await openBrowserSession({
      baseUrl: session.baseUrl,
      storageState: existsSync(recoveredStorageState) ? recoveredStorageState : session.storageState,
      sessionStorageSeed: recovery?.sessionStorageSeed ?? null,
      headless: true,
    });
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
        await writeJsonAtomic(path.join(responses, `${request.requestId}.json`), {
          kind: 'finish',
          requestId: request.requestId,
          ok: true,
          completedAt: new Date().toISOString(),
        });
        stopping = true;
        continue;
      }

      try {
        const step = await executeUiAction({
          runtime: browserRuntime,
          capture,
          session,
          args: request.args,
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
    await writeState('failed', {
      error: redact({ code: error.code ?? 'SESSION_WORKER_FAILED', message: String(error.message ?? error) }),
    }).catch(() => {});
    process.exitCode = 2;
  } finally {
    if (heartbeat) clearInterval(heartbeat);
    if (browserRuntime) await browserRuntime.close().catch(() => {});
    if (!process.exitCode) await writeState('stopped').catch(() => {});
  }
}

main().catch((error) => {
  console.error(redact(String(error?.message ?? error)));
  process.exitCode = 2;
});
