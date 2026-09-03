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

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(HERE, '..');
const TOOLS = path.join(ROOT, 'agent-driver-ui', 'tools.mjs');
const SESSIONS = path.join(ROOT, 'sessions');
const TRANSCRIPTS = path.join(ROOT, 'transcripts-ui');
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

test('runtime shutdown force-terminates an unresponsive worker and removes its resources', { timeout: 15_000 }, async () => {
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
    await stopSessionRuntime({ sessionsDirectory, sessionId, timeout: 100 });
    await waitForExit(child.pid);
    assert.equal(existsSync(runtime), false);
  } finally {
    if (isProcessAlive(child.pid)) process.kill(child.pid, 'SIGKILL');
    await rm(sessionsDirectory, { recursive: true, force: true });
  }
});
