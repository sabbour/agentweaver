import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { navigateForAppEvidence } from '../agent-driver-ui/tools.mjs';
import { classifyAppReadiness, waitForAppReadiness } from '../lib/readiness.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));

async function fixture(name) {
  return JSON.parse(await readFile(path.join(HERE, 'fixtures', name), 'utf8'));
}

function fakePage(snapshots, url = 'https://agentweaver.example.staging/projects') {
  let index = 0;
  return {
    url: () => url,
    snapshot: async () => snapshots[Math.min(index, snapshots.length - 1)],
    waitForTimeout: async () => { index += 1; },
  };
}

test('capture readiness rejects a persistent authentication loading shell', async () => {
  const page = fakePage([await fixture('auth-loading-dom.json')]);

  await assert.rejects(
    waitForAppReadiness(page, { timeout: 5, pollInterval: 1, snapshotPage: (candidate) => candidate.snapshot() }),
    (error) => error.code === 'AUTH_EXPIRED' && error.readiness?.state === 'auth-loading',
  );
});

test('capture navigation waits for readiness after DOM content loads', async () => {
  const page = fakePage([await fixture('auth-loading-dom.json')]);
  const destinations = [];
  const runtime = {
    page,
    goto: async (destination) => { destinations.push(destination); },
  };

  await assert.rejects(
    navigateForAppEvidence(runtime, '/projects', {
      timeout: 5,
      pollInterval: 1,
      snapshotPage: (candidate) => candidate.snapshot(),
    }),
    (error) => error.code === 'AUTH_EXPIRED',
  );
  assert.deepEqual(destinations, ['/projects']);
});

test('capture readiness permits a legitimately slow authenticated app load', async () => {
  const loading = await fixture('auth-loading-dom.json');
  const ready = await fixture('app-shell-dom.json');
  const page = fakePage([loading, loading, ready]);

  const result = await waitForAppReadiness(page, {
    timeout: 100,
    pollInterval: 1,
    snapshotPage: (candidate) => candidate.snapshot(),
  });

  assert.equal(result.state, 'ready');
  assert.deepEqual(result.target, { role: 'main', name: 'Main content' });
});

test('default capture readiness accepts the authenticated app shell', async () => {
  const result = classifyAppReadiness({
    url: 'https://agentweaver.example.staging/projects',
    domSnapshot: await fixture('app-shell-dom.json'),
  });

  assert.equal(result.state, 'ready');
  assert.deepEqual(result.target, { role: 'main', name: 'Main content' });
});

test('capture readiness treats a visible sign-in prompt as expired authentication', async () => {
  const result = classifyAppReadiness({
    url: 'https://agentweaver.example.staging/projects',
    domSnapshot: await fixture('sign-in-dom.json'),
  });

  assert.equal(result.state, 'auth-required');
});

test('explicit capture readiness cannot pass merely because the app shell is visible', async () => {
  const result = classifyAppReadiness({
    url: 'https://agentweaver.example.staging/custom',
    domSnapshot: await fixture('app-shell-dom.json'),
    target: { testId: 'custom-ready' },
  });

  assert.equal(result.state, 'not-ready');
  assert.equal(result.reason, 'declared readiness target is not visible');
});

test('explicit capture readiness waits for its semantic target after the shell appears', async () => {
  const shell = await fixture('app-shell-dom.json');
  const target = { testId: 'custom-ready', role: 'div', name: null, visible: true };
  const page = fakePage([shell, shell, [...shell, target]]);

  const result = await waitForAppReadiness(page, {
    timeout: 100,
    pollInterval: 1,
    target: { testId: 'custom-ready' },
    snapshotPage: (candidate) => candidate.snapshot(),
  });

  assert.equal(result.state, 'ready');
  assert.deepEqual(result.target, { testId: 'custom-ready' });
});

test('authentication loading fails closed even when shell and explicit target are visible', async () => {
  const domSnapshot = [
    ...await fixture('app-shell-dom.json'),
    ...await fixture('auth-loading-dom.json'),
    { testId: 'custom-ready', role: 'div', name: null, visible: true },
  ];
  const result = classifyAppReadiness({
    url: 'https://agentweaver.example.staging/custom',
    domSnapshot,
    target: { testId: 'custom-ready' },
  });

  assert.equal(result.state, 'auth-loading');
});
