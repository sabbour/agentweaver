import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { closeSync, existsSync, openSync } from 'node:fs';
import { mkdir, readFile, readdir, rename, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertSessionId } from './session-store.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_WORKER = path.join(HERE, '..', 'agent-driver-ui', 'session-worker.mjs');
const HEARTBEAT_STALE_MS = 10_000;
const START_TIMEOUT_MS = 30_000;
const COMMAND_TIMEOUT_MS = 75_000;
const LOCK_TIMEOUT_MS = 120_000;
const LOCK_STALE_MS = 5 * 60_000;

const sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

export function runtimeDirectory(sessionsDirectory, sessionId) {
  return path.join(sessionsDirectory, `${assertSessionId(sessionId)}.runtime`);
}

function statePath(runtime) {
  return path.join(runtime, 'state.json');
}

function requestsDirectory(runtime) {
  return path.join(runtime, 'requests');
}

function responsesDirectory(runtime) {
  return path.join(runtime, 'responses');
}

async function writeJsonAtomic(file, value) {
  await mkdir(path.dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.${randomUUID()}.tmp`;
  await writeFile(temporary, JSON.stringify(value, null, 2), { encoding: 'utf8', mode: 0o600 });
  await rename(temporary, file);
}

async function readJson(file) {
  try {
    return JSON.parse(await readFile(file, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT' || error instanceof SyntaxError) return null;
    throw error;
  }
}

export function isProcessAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

async function terminateProcess(pid) {
  if (!isProcessAlive(pid)) return;
  try {
    process.kill(pid, 'SIGTERM');
  } catch {
    return;
  }
  const deadline = Date.now() + 3_000;
  while (isProcessAlive(pid) && Date.now() < deadline) await sleep(50);
  if (isProcessAlive(pid)) {
    try { process.kill(pid, 'SIGKILL'); } catch { /* already stopped */ }
  }
}

async function lockIsStale(lockDirectory, staleAfter) {
  const owner = await readJson(path.join(lockDirectory, 'owner.json'));
  if (owner && !isProcessAlive(owner.pid)) return true;
  try {
    const details = await stat(lockDirectory);
    return Date.now() - details.mtimeMs > staleAfter;
  } catch (error) {
    return error.code === 'ENOENT';
  }
}

export async function withSessionLock(
  sessionsDirectory,
  sessionId,
  callback,
  { timeout = LOCK_TIMEOUT_MS, staleAfter = LOCK_STALE_MS } = {},
) {
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  const lockDirectory = path.join(runtime, 'action.lock');
  const deadline = Date.now() + timeout;
  await mkdir(runtime, { recursive: true });

  while (true) {
    try {
      await mkdir(lockDirectory);
      await writeFile(
        path.join(lockDirectory, 'owner.json'),
        JSON.stringify({ pid: process.pid, acquiredAt: new Date().toISOString() }),
        { encoding: 'utf8', mode: 0o600 },
      );
      break;
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      if (await lockIsStale(lockDirectory, staleAfter)) {
        await rm(lockDirectory, { recursive: true, force: true });
        continue;
      }
      if (Date.now() >= deadline) throw new Error(`UI session ${sessionId} is busy`);
      await sleep(50);
    }
  }

  try {
    return await callback();
  } finally {
    await rm(lockDirectory, { recursive: true, force: true });
  }
}

async function runtimeIsReady(state) {
  if (!state || state.status !== 'ready' || !isProcessAlive(state.pid)) return false;
  const heartbeat = Date.parse(state.heartbeatAt ?? '');
  return Number.isFinite(heartbeat) && Date.now() - heartbeat <= HEARTBEAT_STALE_MS;
}

async function resetTransport(runtime) {
  await rm(requestsDirectory(runtime), { recursive: true, force: true });
  await rm(responsesDirectory(runtime), { recursive: true, force: true });
  await rm(statePath(runtime), { force: true });
  await mkdir(requestsDirectory(runtime), { recursive: true });
  await mkdir(responsesDirectory(runtime), { recursive: true });
}

export async function ensureSessionRuntime({
  sessionsDirectory,
  sessionId,
  guardOptions = {},
  workerPath = DEFAULT_WORKER,
  timeout = START_TIMEOUT_MS,
}) {
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  const startLock = path.join(runtime, 'start.lock');
  await mkdir(runtime, { recursive: true });

  return withDirectoryLock(startLock, async () => {
    const current = await readJson(statePath(runtime));
    if (await runtimeIsReady(current)) return current;
    if (current?.pid && isProcessAlive(current.pid)) await terminateProcess(current.pid);

    await resetTransport(runtime);
    const launchId = randomUUID();
    const workerArgs = [
      workerPath,
      '--session', sessionId,
      '--sessions-dir', sessionsDirectory,
      '--launch-id', launchId,
    ];
    const logPath = path.join(runtime, 'worker.log');
    const logHandle = openSync(logPath, 'w', 0o600);
    let child;
    try {
      child = spawn(process.execPath, workerArgs, {
        detached: true,
        stdio: ['ignore', 'ignore', logHandle],
        windowsHide: true,
      });
    } finally {
      closeSync(logHandle);
    }
    child.unref();

    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      const state = await readJson(statePath(runtime));
      if (state?.launchId === launchId && state.status === 'failed') {
        const error = new Error(state.error?.message ?? 'UI session worker failed to start');
        error.code = state.error?.code;
        throw error;
      }
      if (state?.launchId === launchId && await runtimeIsReady(state)) return state;
      if (!isProcessAlive(child.pid)) break;
      await sleep(50);
    }

    await terminateProcess(child.pid);
    const workerLog = await readFile(logPath, 'utf8').catch(() => '');
    const detail = workerLog.trim().split(/\r?\n/).slice(-8).join('\n');
    throw new Error(`UI session worker did not become ready within ${timeout}ms${detail ? `:\n${detail}` : ''}`);
  });
}

async function withDirectoryLock(lockDirectory, callback) {
  const deadline = Date.now() + START_TIMEOUT_MS;
  while (true) {
    try {
      await mkdir(lockDirectory);
      break;
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      if (await lockIsStale(lockDirectory, START_TIMEOUT_MS)) {
        await rm(lockDirectory, { recursive: true, force: true });
        continue;
      }
      if (Date.now() >= deadline) throw new Error('timed out waiting for UI session startup');
      await sleep(50);
    }
  }
  try {
    return await callback();
  } finally {
    await rm(lockDirectory, { recursive: true, force: true });
  }
}

async function waitForResponse(runtime, requestId, timeout) {
  const responseFile = path.join(responsesDirectory(runtime), `${requestId}.json`);
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const response = await readJson(responseFile);
    if (response) return response;
    const state = await readJson(statePath(runtime));
    if (state && state.status === 'failed') {
      const error = new Error(state.error?.message ?? 'UI session worker crashed');
      error.code = state.error?.code ?? 'SESSION_WORKER_CRASHED';
      throw error;
    }
    if (state?.pid && !isProcessAlive(state.pid)) {
      const workerLog = await readFile(path.join(runtime, 'worker.log'), 'utf8').catch(() => '');
      const detail = workerLog.trim().split(/\r?\n/).slice(-12).join('\n');
      const error = new Error(`UI session worker stopped before completing the command${detail ? `:\n${detail}` : ''}`);
      error.code = 'SESSION_WORKER_CRASHED';
      throw error;
    }
    await sleep(50);
  }
  const error = new Error(`UI session command timed out after ${timeout}ms`);
  error.code = 'SESSION_COMMAND_TIMEOUT';
  throw error;
}

export async function dispatchSessionCommand({
  sessionsDirectory,
  sessionId,
  request,
  guardOptions = {},
  timeout = COMMAND_TIMEOUT_MS,
}) {
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  await ensureSessionRuntime({ sessionsDirectory, sessionId, guardOptions });
  const requestId = randomUUID();
  const envelope = { ...request, requestId, requestedAt: new Date().toISOString() };
  await writeJsonAtomic(path.join(requestsDirectory(runtime), `${requestId}.json`), envelope);
  try {
    const response = await waitForResponse(runtime, requestId, timeout);
    return { ...response, requestId };
  } catch (error) {
    error.requestId = requestId;
    throw error;
  }
}

export async function acknowledgeSessionResponse(sessionsDirectory, sessionId, requestId) {
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  await rm(path.join(responsesDirectory(runtime), `${requestId}.json`), { force: true });
}

export async function reconcileSessionResponses(sessionsDirectory, sessionId) {
  const directory = responsesDirectory(runtimeDirectory(sessionsDirectory, sessionId));
  if (!existsSync(directory)) return [];
  const names = (await readdir(directory)).filter((name) => name.endsWith('.json')).sort();
  const responses = [];
  for (const name of names) {
    const response = await readJson(path.join(directory, name));
    if (response?.kind === 'action') responses.push(response);
  }
  return responses;
}

export async function stopSessionRuntime({ sessionsDirectory, sessionId, timeout = 15_000 }) {
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  const state = await readJson(statePath(runtime));
  if (state?.pid && isProcessAlive(state.pid)) {
    const requestId = randomUUID();
    await writeJsonAtomic(path.join(requestsDirectory(runtime), `${requestId}.json`), {
      kind: 'finish',
      requestId,
      requestedAt: new Date().toISOString(),
    });
    await waitForResponse(runtime, requestId, timeout);
    const deadline = Date.now() + timeout;
    while (isProcessAlive(state.pid) && Date.now() < deadline) await sleep(50);
    if (isProcessAlive(state.pid)) await terminateProcess(state.pid);
  }
  await rm(runtime, { recursive: true, force: true });
}

export async function readRuntimeState(sessionsDirectory, sessionId) {
  return readJson(statePath(runtimeDirectory(sessionsDirectory, sessionId)));
}
