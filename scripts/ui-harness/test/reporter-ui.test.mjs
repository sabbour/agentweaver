import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { action, finish } from '../agent-driver-ui/tools.mjs';
import { computeDriverP0 } from '../lib/reporter-ui.mjs';
import { runtimeDirectory } from '../lib/session-runtime.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SESSIONS = path.join(HERE, '..', 'sessions');

test('only deterministic browser facts fail P0', () => {
  const result = computeDriverP0([{ id: 1, assertions: [{ required: true, observed: false, target: 'notification-type-badge' }], console: [{ type: 'error', text: 'boom' }], network: [{ userFacing: true, status: 500, method: 'GET', url: '/api/x' }] }]);
  assert.equal(result.pass, false);
  assert.deepEqual(result.failures.map((item) => item.kind), ['required-element-missing', 'console-error', 'user-facing-network-error']);
});

test('a recorded action failure fails P0 with its evidence', () => {
  const result = computeDriverP0([{
    id: 2,
    action: 'drag',
    outcome: 'failed',
    error: { message: 'drag target did not resolve' },
  }]);

  assert.equal(result.pass, false);
  assert.deepEqual(result.failures, [{
    kind: 'action-failed',
    turn: 2,
    evidence: 'drag target did not resolve',
  }]);
});

test('finish fails closed when capture evidence is only the authentication loading shell', async () => {
  const domSnapshot = JSON.parse(await readFile(path.join(HERE, 'fixtures', 'auth-loading-dom.json'), 'utf8'));
  const result = computeDriverP0([{
    id: 1,
    action: 'capture',
    url: 'https://agentweaver.example.staging/projects',
    domSnapshot,
  }]);

  assert.equal(result.pass, false);
  assert.deepEqual(result.failures.map((item) => item.kind), ['auth-loading-shell']);
});

test('finish fails closed when a capture command failed before evidence was recorded', () => {
  const result = computeDriverP0([], [{
    id: 1,
    action: 'capture',
    code: 'AUTH_EXPIRED',
    message: 'AUTH_EXPIRED: Loading sign-in options',
  }]);

  assert.equal(result.pass, false);
  assert.deepEqual(result.failures.map((item) => item.kind), ['no-evidence', 'command-failed']);
});

test('a failed capture command persists through finish', async () => {
  const sessionId = `test-${randomUUID()}`;
  const sessionFile = path.join(SESSIONS, `${sessionId}.json`);
  const failure = Object.assign(new Error('APP_NOT_READY: declared readiness target is not visible'), {
    code: 'APP_NOT_READY',
    readiness: { state: 'not-ready', reason: 'declared readiness target is not visible' },
  });

  await mkdir(SESSIONS, { recursive: true });
  await writeFile(sessionFile, JSON.stringify({
    id: sessionId,
    baseUrl: 'https://agentweaver.example.staging/',
    storageState: 'unused.storageState.json',
    persona: {
      id: 'priya',
      name: 'Priya',
      coreVersion: '1',
      adapterVersion: '1',
      text: 'Test persona',
    },
    steps: [],
    commandFailures: [],
    createdAt: new Date().toISOString(),
  }), 'utf8');

  try {
    await assert.rejects(
      action(
        { _: ['capture'], session: sessionId, path: '/projects', 'ready-test-id': 'projects-ready' },
        { dispatch: async () => { throw failure; } },
      ),
      (error) => error.code === 'APP_NOT_READY',
    );

    const stored = JSON.parse(await readFile(sessionFile, 'utf8'));
    assert.equal(stored.commandFailures.length, 1);
    assert.equal(stored.commandFailures[0].code, 'APP_NOT_READY');

    const output = [];
    const result = await finish(
      { session: sessionId, 'target-revision': 'test' },
      { write: (line) => output.push(line) },
    );

    assert.equal(result.driver.pass, false);
    assert.equal(result.preflight.surface, 'ui');
    assert.equal(result.preflight.cleanupResult, 'completed');
    assert.deepEqual(result.driver.failures.map((item) => item.kind), ['no-evidence', 'command-failed']);
    assert.match(output[0], /UI DRIVER P0 FAIL/);
  } finally {
    await rm(sessionFile, { force: true });
  }
});

test('a completed worker response is reconciled after the invoking CLI process disappears', async () => {
  const sessionId = `test-${randomUUID()}`;
  const sessionFile = path.join(SESSIONS, `${sessionId}.json`);
  const responses = path.join(runtimeDirectory(SESSIONS, sessionId), 'responses');
  const orphanRequestId = randomUUID();
  const readyStep = (id, intent) => ({
    id,
    action: 'click',
    intent,
    url: 'https://agentweaver.example.staging/projects',
    domSnapshot: [{ role: 'main', name: 'Main content', visible: true }],
    console: [],
    network: [],
  });

  await mkdir(responses, { recursive: true });
  await writeFile(sessionFile, JSON.stringify({
    id: sessionId,
    baseUrl: 'https://agentweaver.example.staging/',
    storageState: 'unused.storageState.json',
    persona: {
      id: 'priya',
      name: 'Priya',
      coreVersion: '1',
      adapterVersion: '1',
      text: 'Test persona',
    },
    steps: [],
    commandFailures: [],
    processedRequestIds: [],
    createdAt: new Date().toISOString(),
  }), 'utf8');
  await writeFile(path.join(responses, `${orphanRequestId}.json`), JSON.stringify({
    kind: 'action',
    requestId: orphanRequestId,
    ok: true,
    eventId: 1,
    action: 'click',
    step: readyStep(1, 'orphaned'),
    completedAt: new Date().toISOString(),
  }), 'utf8');

  try {
    await action(
      { _: ['click'], session: sessionId, 'test-id': 'next' },
      {
        dispatch: async () => ({
          kind: 'action',
          requestId: randomUUID(),
          ok: true,
          eventId: 2,
          action: 'click',
          step: readyStep(2, 'current'),
          completedAt: new Date().toISOString(),
        }),
        write: () => {},
      },
    );

    const stored = JSON.parse(await readFile(sessionFile, 'utf8'));
    assert.deepEqual(stored.steps.map((step) => step.intent), ['orphaned', 'current']);
    assert.equal(stored.processedRequestIds.includes(orphanRequestId), true);
    assert.equal(await readFile(path.join(responses, `${orphanRequestId}.json`), 'utf8').catch(() => null), null);
  } finally {
    await rm(sessionFile, { force: true });
    await rm(runtimeDirectory(SESSIONS, sessionId), { recursive: true, force: true });
  }
});

test('finish cleans runtime and session before surfacing evidence write failure', async () => {
  const sessionId = `test-${randomUUID()}`;
  const sessionFile = path.join(SESSIONS, `${sessionId}.json`);
  await mkdir(SESSIONS, { recursive: true });
  await writeFile(sessionFile, JSON.stringify({
    id: sessionId,
    baseUrl: 'https://agentweaver.example.staging/',
    storageState: 'unused.storageState.json',
    persona: { id: 'priya', name: 'Priya', coreVersion: '1', adapterVersion: '1', text: 'Test persona' },
    steps: [],
    commandFailures: [],
  }), 'utf8');
  let runtimeStopped = false;
  let sessionRemoved = false;
  try {
    await assert.rejects(
      finish({ session: sessionId }, {
        write: () => {},
        stopRuntime: async () => {
          runtimeStopped = true;
          throw new Error('browser close failed');
        },
        removeStoredSession: async () => { sessionRemoved = true; },
        mkdirImpl: async () => {},
        writeFileImpl: async () => { throw new Error('artifact disk failure'); },
      }),
      (error) => {
        assert.equal(error.message, 'artifact disk failure');
        assert.deepEqual(error.cleanupErrors, [
          'browser/runtime cleanup failed: browser close failed',
        ]);
        return true;
      },
    );
    assert.equal(runtimeStopped, true);
    assert.equal(sessionRemoved, true);
  } finally {
    await rm(sessionFile, { force: true });
    await rm(runtimeDirectory(SESSIONS, sessionId), { recursive: true, force: true });
  }
});

test('finish preserves a primary failure and separately records all cleanup failures', async () => {
  const sessionId = `test-${randomUUID()}`;
  const sessionFile = path.join(SESSIONS, `${sessionId}.json`);
  await mkdir(SESSIONS, { recursive: true });
  await writeFile(sessionFile, JSON.stringify({
    id: sessionId,
    baseUrl: 'https://agentweaver.example.staging/',
    storageState: 'unused.storageState.json',
    persona: null,
    steps: [],
    commandFailures: [],
  }), 'utf8');
  try {
    await assert.rejects(
      finish({ session: sessionId }, {
        stopRuntime: async () => { throw new Error('browser close failed'); },
        removeStoredSession: async () => { throw new Error('session remove failed'); },
      }),
      (error) => {
        assert.match(error.message, /Cannot read properties|null/);
        assert.deepEqual(error.cleanupErrors, [
          'browser/runtime cleanup failed: browser close failed',
          'session cleanup failed: session remove failed',
        ]);
        return true;
      },
    );
  } finally {
    await rm(sessionFile, { force: true });
    await rm(runtimeDirectory(SESSIONS, sessionId), { recursive: true, force: true });
  }
});
