import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { existsSync } from 'node:fs';
import { mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  isProcessAlive,
  readRuntimeState,
  runtimeDirectory,
  stopSessionRuntime,
  withSessionLock,
} from '../lib/session-runtime.mjs';
import { openBrowserSession } from '../lib/browser.mjs';
import { runSessionWorker } from '../agent-driver-ui/session-worker.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(HERE, '..');
const TOOLS = path.join(ROOT, 'agent-driver-ui', 'tools.mjs');
const SESSIONS = path.join(ROOT, 'sessions');
const TRANSCRIPTS = path.join(ROOT, 'transcripts-ui');
const CLOSE_FAILURE_WORKER = path.join(HERE, 'fixtures', 'session-worker-close-failure.mjs');
const FIXTURE_TEXT = 'Review https://docs.example.test/guide?token=secret-canary#section unchanged';

function fixtureHtml() {
  return `<!doctype html>
<html>
  <body>
    <main aria-label="Main content">
      <h1 id="state"></h1>
      <button data-testid="advance" type="button">Advance</button>
      <input data-testid="coordinator-composer" />
      <div data-testid="drag-source" style="position:absolute;left:20px;top:100px;width:40px;height:40px">Source</div>
      <div data-testid="drag-target" style="position:absolute;left:200px;top:100px;width:40px;height:40px">Target</div>
    </main>
    <script>
      const render = () => {
        const count = sessionStorage.getItem('fixture.count') || '0';
        const drag = sessionStorage.getItem('fixture.drag') || '0';
        const routeExact = location.pathname === '/projects' && location.search === '?tab=runs' && location.hash === '#active';
        const textExact = sessionStorage.getItem('fixture.textExact') || '0';
        document.querySelector('#state').textContent = location.pathname + ' count=' + count + ' drag=' + drag + ' routeExact=' + routeExact + ' textExact=' + textExact;
      };
      document.querySelector('[data-testid="coordinator-composer"]').addEventListener('input', (event) => {
        sessionStorage.setItem('fixture.textExact', event.target.value === ${JSON.stringify(FIXTURE_TEXT)} ? '1' : '0');
        render();
      });
      document.querySelector('[data-testid="advance"]').addEventListener('click', () => {
        const next = Number(sessionStorage.getItem('fixture.count') || '0') + 1;
        sessionStorage.setItem('fixture.count', String(next));
        render();
      });
      let dragging = false;
      document.querySelector('[data-testid="drag-source"]').addEventListener('pointerdown', () => {
        dragging = true;
      });
      document.querySelector('[data-testid="drag-target"]').addEventListener('pointerup', () => {
        if (!dragging) return;
        sessionStorage.setItem('fixture.drag', '1');
        dragging = false;
        render();
      });
      window.addEventListener('popstate', render);
      render();
    </script>
  </body>
</html>`;
}

async function listen(server) {
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  return server.address().port;
}

async function close(server) {
  await new Promise((resolve) => server.close(resolve));
}

function runCli(...args) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [TOOLS, ...args], {
      cwd: ROOT,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.once('error', reject);
    child.once('exit', (code) => {
      if (code !== 0) {
        reject(new Error(`CLI ${args.join(' ')} exited ${code}: ${stderr || stdout}`));
        return;
      }
      resolve({ pid: child.pid, stdout, stderr });
    });
  });
}

function parseJsonOutput(output) {
  const start = output.indexOf('{');
  assert.notEqual(start, -1, `expected JSON output, received: ${output}`);
  return JSON.parse(output.slice(start));
}

function visibleHeading(step) {
  return step.domSnapshot.find((element) => element.role === 'h1' && element.visible)?.name;
}

async function waitForExit(pid) {
  const deadline = Date.now() + 5_000;
  while (isProcessAlive(pid) && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  assert.equal(isProcessAlive(pid), false, `process ${pid} did not exit`);
}

async function directoryContains(directory, needle) {
  if (!existsSync(directory)) return false;
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      if (await directoryContains(entryPath, needle)) return true;
    } else if ((await readFile(entryPath)).includes(Buffer.from(needle))) {
      return true;
    }
  }
  return false;
}

test('separate CLI processes preserve page state, recover crashes, and isolate sessions', { timeout: 120_000 }, async () => {
  const html = fixtureHtml();
  const server = createServer((_request, response) => {
    response.writeHead(200, {
      connection: 'close',
      'content-length': Buffer.byteLength(html),
      'content-type': 'text/html; charset=utf-8',
    });
    response.end(html);
  });
  const port = await listen(server);
  const baseUrl = `http://127.0.0.1:${port}`;
  const secret = `secret-${randomUUID()}`;
  const authPath = path.join(HERE, `multi-process-${randomUUID()}.storageState.json`);
  const seedPath = `${authPath}.sessionStorage.json`;
  const sessionIds = [];

  await writeFile(authPath, JSON.stringify({
    cookies: [],
    origins: [{ origin: baseUrl, localStorage: [{ name: 'fixture.auth', value: secret }] }],
  }), { encoding: 'utf8', mode: 0o600 });
  await writeFile(seedPath, JSON.stringify({
    origin: baseUrl,
    entries: { 'fixture.token': secret },
  }), { encoding: 'utf8', mode: 0o600 });

  try {
    const initialized = await runCli('init', '--persona', 'priya', '--base-url', baseUrl, '--storage-state', authPath);
    const secondInitialized = await runCli('init', '--persona', 'priya', '--base-url', baseUrl, '--storage-state', authPath);
    const sessionId = parseJsonOutput(initialized.stdout).sessionId;
    const secondSessionId = parseJsonOutput(secondInitialized.stdout).sessionId;
    sessionIds.push(sessionId);
    sessionIds.push(secondSessionId);
    const originalWorker = await readRuntimeState(SESSIONS, sessionId);
    assert.equal(originalWorker.status, 'ready');
    assert.notEqual(initialized.pid, originalWorker.pid);

    const go = await runCli('goto', '--session', sessionId, '--path', '/projects?tab=runs#active');
    const typed = await runCli('type-coordinator', '--session', sessionId, '--text', FIXTURE_TEXT);
    await runCli('goto', '--session', secondSessionId, '--path', '/isolated');
    const click = await runCli('click', '--session', sessionId, '--test-id', 'advance');
    const drag = await runCli('drag', '--session', sessionId, '--from-test-id', 'drag-source', '--to-test-id', 'drag-target');
    const captured = await runCli('capture', '--session', sessionId, '--thought', 'initial-capture');
    assert.equal(new Set([go.pid, typed.pid, click.pid, drag.pid, captured.pid]).size, 5);

    const clickStep = parseJsonOutput(click.stdout);
    const dragStep = parseJsonOutput(drag.stdout);
    const captureStep = parseJsonOutput(captured.stdout);
    assert.equal(new URL(clickStep.url).pathname, '/projects');
    assert.equal(new URL(dragStep.url).pathname, '/projects');
    assert.deepEqual(dragStep.target.from.testId, 'drag-source');
    assert.equal(new URL(captureStep.url).pathname, '/projects');
    assert.match(visibleHeading(captureStep), /\/projects count=1 drag=1 routeExact=true textExact=1/);
    assert.equal(await directoryContains(runtimeDirectory(SESSIONS, sessionId), FIXTURE_TEXT), false);
    assert.equal(await directoryContains(runtimeDirectory(SESSIONS, sessionId), '?tab=runs#active'), false);

    const concurrent = await Promise.all([
      runCli('click', '--session', sessionId, '--test-id', 'advance', '--thought', 'concurrent-one'),
      runCli('click', '--session', sessionId, '--test-id', 'advance', '--thought', 'concurrent-two'),
    ]);
    const concurrentSteps = concurrent.map((result) => parseJsonOutput(result.stdout));
    assert.deepEqual(concurrentSteps.map((step) => step.id).sort((a, b) => a - b), [6, 7]);
    const afterConcurrent = parseJsonOutput((await runCli('capture', '--session', sessionId, '--thought', 'after-concurrent')).stdout);
    assert.equal(new URL(afterConcurrent.url).pathname, '/projects');
    assert.match(visibleHeading(afterConcurrent), /count=3/);

    process.kill(originalWorker.pid, 'SIGTERM');
    await waitForExit(originalWorker.pid);
    const recovered = parseJsonOutput((await runCli('capture', '--session', sessionId, '--thought', 'after-crash')).stdout);
    const recoveredWorker = await readRuntimeState(SESSIONS, sessionId);
    assert.notEqual(recoveredWorker.pid, originalWorker.pid);
    assert.equal(new URL(recovered.url).pathname, '/projects');
    assert.match(visibleHeading(recovered), /count=3/);

    const [isolatedResult, stillFirstResult] = await Promise.all([
      runCli('capture', '--session', secondSessionId, '--thought', 'isolated'),
      runCli('capture', '--session', sessionId, '--thought', 'first-still'),
    ]);
    const isolated = parseJsonOutput(isolatedResult.stdout);
    const stillFirst = parseJsonOutput(stillFirstResult.stdout);
    assert.equal(new URL(isolated.url).pathname, '/isolated');
    assert.equal(new URL(stillFirst.url).pathname, '/projects');

    const secondFinish = await runCli('finish', '--session', secondSessionId);
    const firstFinish = await runCli('finish', '--session', sessionId);
    assert.equal(parseJsonOutput(secondFinish.stdout).driver.pass, true);
    assert.equal(parseJsonOutput(firstFinish.stdout).driver.pass, true);
    assert.equal(firstFinish.stdout.includes(secret), false);
    assert.equal(firstFinish.stdout.includes('secret-canary'), false);
    assert.equal(existsSync(path.join(SESSIONS, `${sessionId}.json`)), false);
    assert.equal(existsSync(runtimeDirectory(SESSIONS, sessionId)), false);
    const archived = await readFile(path.join(TRANSCRIPTS, sessionId, 'result.json'), 'utf8');
    assert.equal(archived.includes(secret), false);
    assert.equal(archived.includes('secret-canary'), false);
    await rm(path.join(TRANSCRIPTS, sessionId), { recursive: true, force: true });
    await rm(path.join(TRANSCRIPTS, secondSessionId), { recursive: true, force: true });
    sessionIds.length = 0;
  } finally {
    for (const sessionId of sessionIds) {
      await stopSessionRuntime({ sessionsDirectory: SESSIONS, sessionId }).catch(() => {});
      await rm(path.join(SESSIONS, `${sessionId}.json`), { force: true });
      await rm(path.join(TRANSCRIPTS, sessionId), { recursive: true, force: true });
    }
    await rm(authPath, { force: true });
    await rm(seedPath, { force: true });
    await close(server);
  }
});

test('an abandoned lock owned by a dead process is recovered', async () => {
  const sessionsDirectory = path.join(HERE, `.sessions-${randomUUID()}`);
  const sessionId = randomUUID();
  const lockDirectory = path.join(runtimeDirectory(sessionsDirectory, sessionId), 'action.lock');
  await mkdir(lockDirectory, { recursive: true });
  await writeFile(path.join(lockDirectory, 'owner.json'), JSON.stringify({
    pid: 2_147_483_647,
    acquiredAt: new Date(0).toISOString(),
  }), 'utf8');
  try {
    const result = await withSessionLock(sessionsDirectory, sessionId, async () => 'recovered', {
      timeout: 1_000,
      staleAfter: 1,
    });
    assert.equal(result, 'recovered');
  } finally {
    await rm(sessionsDirectory, { recursive: true, force: true });
  }
});

test('runtime shutdown force-terminates an unresponsive worker and retains retry proof', { timeout: 15_000 }, async () => {
  const sessionsDirectory = path.join(HERE, `.sessions-${randomUUID()}`);
  const sessionId = randomUUID();
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  await mkdir(path.join(runtime, 'requests'), { recursive: true });
  await mkdir(path.join(runtime, 'responses'), { recursive: true });
  const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {
    windowsHide: true,
    stdio: 'ignore',
  });
  await new Promise((resolve, reject) => {
    child.once('spawn', resolve);
    child.once('error', reject);
  });
  await writeFile(path.join(runtime, 'state.json'), JSON.stringify({
    launchId: randomUUID(),
    pid: child.pid,
    status: 'ready',
    heartbeatAt: new Date().toISOString(),
  }), 'utf8');

  try {
    await assert.rejects(
      stopSessionRuntime({ sessionsDirectory, sessionId, timeout: 100 }),
      (error) => {
        assert(error instanceof AggregateError);
        assert.equal(error.errors.some((item) => item.code === 'BROWSER_CLOSE_UNPROVEN'), true);
        return true;
      },
    );
    await waitForExit(child.pid);
    assert.equal(existsSync(runtime), true);
    const retry = JSON.parse(await readFile(path.join(runtime, 'shutdown-retry.json'), 'utf8'));
    assert.equal(retry.browserClosed, false);
    assert.equal(retry.workerTerminated, true);
  } finally {
    if (isProcessAlive(child.pid)) process.kill(child.pid, 'SIGKILL');
    await rm(sessionsDirectory, { recursive: true, force: true });
  }
});

for (const failure of ['context', 'browser']) {
  test(`worker propagates ${failure}.close failure, terminates, and retains sanitized retry state`, { timeout: 15_000 }, async () => {
    const sessionsDirectory = path.join(HERE, `.sessions-${randomUUID()}`);
    const sessionId = randomUUID();
    const runtime = runtimeDirectory(sessionsDirectory, sessionId);
    const marker = path.join(sessionsDirectory, `${failure}.marker`);
    const secret = `secret-${randomUUID()}`;
    await mkdir(sessionsDirectory, { recursive: true });
    await writeFile(path.join(sessionsDirectory, `${sessionId}.json`), JSON.stringify({
      id: sessionId,
      baseUrl: 'https://agentweaver.example.staging/',
      storageState: 'unused.storageState.json',
    }), 'utf8');
    const child = spawn(process.execPath, [
      CLOSE_FAILURE_WORKER,
      '--session', sessionId,
      '--sessions-dir', sessionsDirectory,
      '--launch-id', randomUUID(),
    ], {
      env: {
        ...process.env,
        AGENTWEAVER_CLOSE_FAILURE: failure,
        AGENTWEAVER_CLOSE_MARKER: marker,
      },
      windowsHide: true,
      stdio: 'ignore',
    });
    await new Promise((resolve, reject) => {
      child.once('spawn', resolve);
      child.once('error', reject);
    });

    try {
      const readyDeadline = Date.now() + 5_000;
      let state;
      while (Date.now() < readyDeadline) {
        state = await readRuntimeState(sessionsDirectory, sessionId);
        if (state?.status === 'ready') break;
        await new Promise((resolve) => setTimeout(resolve, 50));
      }
      assert.equal(state?.status, 'ready');
      await writeFile(path.join(runtime, 'recovery.json'), JSON.stringify({ token: secret }), 'utf8');
      await writeFile(path.join(runtime, 'recovery.storageState.json'), JSON.stringify({ token: secret }), 'utf8');

      await assert.rejects(
        stopSessionRuntime({ sessionsDirectory, sessionId, timeout: 2_000 }),
        (error) => {
          assert(error instanceof AggregateError);
          const closure = error.errors.find((item) => item instanceof AggregateError);
          assert.equal(
            closure?.errors.some((item) => item.message === `fixture ${failure} close failed`),
            true,
          );
          assert.equal(error.errors.some((item) => item.code === 'BROWSER_CLOSE_UNPROVEN'), true);
          return true;
        },
      );
      await waitForExit(child.pid);
      assert.equal(existsSync(path.join(sessionsDirectory, `${sessionId}.json`)), true);
      assert.deepEqual((await readFile(marker, 'utf8')).trim().split(/\r?\n/), ['page', 'context', 'browser']);
      const retry = JSON.parse(await readFile(path.join(runtime, 'shutdown-retry.json'), 'utf8'));
      assert.equal(retry.browserClosed, false);
      assert.equal(retry.workerTerminated, true);
      assert.equal(retry.errors.some((item) => item.message === `fixture ${failure} close failed`), true);
      assert.equal(await directoryContains(runtime, secret), false);
      assert.deepEqual((await readdir(runtime)).sort(), ['shutdown-retry.json']);
    } finally {
      if (isProcessAlive(child.pid)) process.kill(child.pid, 'SIGKILL');
      await rm(sessionsDirectory, { recursive: true, force: true });
    }
  });
}

test('partial startup with unproven browser closure retains sanitized retry metadata', async () => {
  const sessionsDirectory = path.join(HERE, `.sessions-${randomUUID()}`);
  const sessionId = randomUUID();
  const runtime = runtimeDirectory(sessionsDirectory, sessionId);
  const sessionFile = path.join(sessionsDirectory, `${sessionId}.json`);
  const secret = 'secret-canary-startup-cleanup';
  const calls = [];
  const page = {
    close: async () => { calls.push('page'); },
  };
  const context = {
    addInitScript: async () => {},
    newPage: async () => page,
    route: async () => { throw new Error(`routing failed token=${secret}`); },
    close: async () => { calls.push('context'); },
  };
  const browser = {
    newContext: async () => context,
    close: async () => {
      calls.push('browser');
      throw new Error(`browser cleanup failed secret=${secret}`);
    },
  };
  const processImpl = {
    pid: 2_147_483_647,
    exitCode: 0,
    on: () => {},
    off: () => {},
  };

  await mkdir(sessionsDirectory, { recursive: true });
  await writeFile(sessionFile, JSON.stringify({
    id: sessionId,
    baseUrl: 'https://agentweaver.example.com',
    storageState: 'fixture.storageState.json',
  }), 'utf8');
  try {
    await assert.rejects(
      runSessionWorker({
        argv: [
          '--session', sessionId,
          '--sessions-dir', sessionsDirectory,
          '--launch-id', randomUUID(),
        ],
        processImpl,
        openBrowserSessionImpl: (options) => openBrowserSession(options, {
          chromium: { launch: async () => browser },
          loadStorageStateForOriginImpl: async () => ({ cookies: [], origins: [] }),
          loadSessionStorageSeedImpl: async () => null,
        }),
      }),
      (error) => {
        assert(error instanceof AggregateError);
        assert.equal(error.errors.some((item) => item.message.includes('routing failed')), true);
        assert.equal(error.errors.some((item) => item.message.includes('browser cleanup failed')), true);
        return true;
      },
    );
    assert.deepEqual(calls, ['page', 'context', 'browser']);

    const failedState = await readRuntimeState(sessionsDirectory, sessionId);
    assert.equal(failedState.status, 'failed');
    assert.equal(failedState.termination.browserCloseAttempted, true);
    assert.equal(failedState.termination.browserClosed, false);
    assert.equal(failedState.termination.browserClosureProven, false);
    assert.equal(JSON.stringify(failedState).includes(secret), false);

    await assert.rejects(
      stopSessionRuntime({ sessionsDirectory, sessionId, timeout: 100 }),
      (error) => {
        assert.equal(
          error instanceof AggregateError
            ? error.errors.some((item) => item.code === 'BROWSER_CLOSE_UNPROVEN')
            : error.code === 'BROWSER_CLOSE_UNPROVEN',
          true,
        );
        return true;
      },
    );
    assert.equal(existsSync(sessionFile), true);
    const retry = JSON.parse(await readFile(path.join(runtime, 'shutdown-retry.json'), 'utf8'));
    assert.equal(retry.browserClosed, false);
    assert.equal(retry.browserClosureProven, false);
    assert.equal(retry.errors.some((item) => item.message.includes('routing failed')), true);
    assert.equal(retry.errors.some((item) => item.message.includes('browser cleanup failed')), true);
    assert.equal(retry.errors.some((item) => item.code === 'BROWSER_CLOSE_UNPROVEN'), true);
    assert.equal(JSON.stringify(retry).includes(secret), false);
    assert.deepEqual((await readdir(runtime)).sort(), ['shutdown-retry.json']);
  } finally {
    await rm(sessionsDirectory, { recursive: true, force: true });
  }
});
