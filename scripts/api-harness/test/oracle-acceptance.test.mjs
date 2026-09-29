import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { EventDeltas, cleanupOwnedPreviews, runOracleAcceptance } from '../lib/oracle-acceptance.mjs';
import { parseOracleArgs } from '../run-oracle-acceptance.mjs';
import { verifyRenderedPreview } from '../../harness-shared/preview-browser.mjs';

test('CLI accepts named phase budgets, rejects invalid values and preserves target files', () => {
  const args = parseOracleArgs(['--budget', 'revisionProvisioning=11', '--target-files', 'src/App.tsx,src/index.css']);
  assert.equal(args.budgets.revisionProvisioning, 11);
  assert.deepEqual(args.targetFiles, ['src/App.tsx', 'src/index.css']);
  assert.throws(() => parseOracleArgs(['--budget', 'unknown=3']), /Invalid --budget/);
  assert.throws(() => parseOracleArgs(['--budget', 'initialPreview=0']), /Invalid --budget/);
});

test('event delta cursor drains pages and advances independently per run', async () => {
  const paths = [];
  const deltas = new EventDeltas(async (_, url) => {
    paths.push(url);
    const after = Number(new URL(url, 'https://example.test').searchParams.get('after'));
    return { status: 200, body: after === 0 ? [{ sequence: 1, type: 'started' }, { sequence: 2, type: 'working' }] : after === 2 ? [{ sequence: 3, type: 'done' }] : [] };
  }, { limit: 2 });
  assert.equal((await deltas.poll('parent')).length, 3);
  assert.equal((await deltas.poll('parent')).length, 0);
  assert.match(paths[2], /after=3/);
  assert.equal(paths.length, 3);
  assert.equal((await deltas.poll('child')).length, 3);
  assert.match(paths[3], /child\/events\?after=0/);
});

test('preview gate requires HTTP 200, matching application text and no browser errors', async () => {
  const entries = {};
  const listeners = {};
  const open = async () => ({
    page: {
      on: (event, handler) => { listeners[event] = handler; },
      goto: async () => ({ status: () => 200 }),
      locator: () => ({ innerText: async () => 'An assembled app with revised text' }),
      title: async () => 'Example app',
    },
    close: async () => { entries.closed = true; },
  });
  const valid = await verifyRenderedPreview('https://preview.example.test/', 'assembled app', { open });
  assert.equal(valid.ready, true);
  assert.equal(entries.closed, true);
  const missing = await verifyRenderedPreview('https://preview.example.test/', 'unseen', { open });
  assert.equal(missing.ready, false);
  const noisyOpen = async () => {
    const session = await open();
    const goto = session.page.goto;
    session.page.goto = async () => {
      listeners.pageerror(new Error('crashed'));
      listeners.console({ type: () => 'error', text: () => 'failed script' });
      listeners.requestfailed({ failure: () => ({ errorText: 'net::ERR_FAILED' }) });
      listeners.response({ status: () => 500, request: () => ({ resourceType: () => 'script' }) });
      return goto();
    };
    return session;
  };
  const broken = await verifyRenderedPreview('https://preview.example.test/', 'assembled app', { open: noisyOpen });
  assert.equal(broken.ready, false);
  assert.equal(broken.errors.length, 4);
});

test('cleanup deletes only owned IDs and verifies they disappeared', async () => {
  const calls = [];
  const outcomes = await cleanupOwnedPreviews(async (method, url) => {
    calls.push(`${method} ${url}`);
    return method === 'DELETE' ? { status: 200 } : { status: 200, body: [{ session_id: 'unrelated' }] };
  }, [{ runId: 'parent', sessionId: 'owned' }]);
  assert.deepEqual(outcomes.map((o) => o.deleted), [true]);
  assert.equal(calls.length, 2);
  assert.ok(calls.every((entry) => !entry.includes('unrelated')));
  const stillThere = await cleanupOwnedPreviews(async (method) => method === 'DELETE'
    ? { status: 200 } : { status: 200, body: [{ session_id: 'owned' }] },
  [{ runId: 'parent', sessionId: 'owned' }]);
  assert.equal(stillThere[0].deleted, false);
});

test('pre-existing preview is never adopted or deleted', async () => {
  const directory = await mkdtemp(path.join(process.cwd(), '.oracle-acceptance-test-'));
  try {
    const deleted = [];
    const result = await runOracleAcceptance({
      transcriptPath: path.join(directory, 'trace.jsonl'), resultPath: path.join(directory, 'result.json'),
      runId: 'parent', expectedText: 'app', correctedText: 'fixed', feedback: 'needs change',
      targetFiles: ['index.html'],
      request: async (method, url) => {
        if (method === 'DELETE') deleted.push(url);
        if (url === '/api/version') return { status: 200, body: {} };
        if (url === '/openapi/v1.json') return { status: 200, body: { paths: {} } };
        if (url === '/api/auth/session') return { status: 200, body: { authenticated: true } };
        if (url.endsWith('/work-plan')) return { status: 200, body: { status: 'in_review' } };
        if (url.endsWith('/children')) return { status: 200, body: [{ childRunId: 'child' }] };
        if (url.includes('/events?')) return { status: 200, body: url.includes('/child/') && url.includes('after=0')
          ? [{ sequence: 1, type: 'sandbox.execution_pod.bound', payload: {} }] : [] };
        if (url.endsWith('/assembly/files')) return { status: 200, body: [{ path: 'index.html' }] };
        if (url.endsWith('/sandbox/port-forward')) return { status: 200, body: [{ session_id: 'someone-else' }] };
        return { status: 200, body: { status: 'in_progress' } };
      },
    });
    assert.equal(result.verdict, 'fail');
    assert.match(result.error.message, /pre-existing preview/);
    assert.deepEqual(deleted, []);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('timeout fails closed with phase, identifiers, last events, diagnostic and persisted paths', async () => {
  const directory = await mkdtemp(path.join(process.cwd(), '.oracle-acceptance-test-'));
  try {
    const transcriptPath = path.join(directory, 'trace.jsonl');
    const resultPath = path.join(directory, 'result.json');
    let now = 0;
    const result = await runOracleAcceptance({
      transcriptPath, resultPath, runId: 'parent', projectId: 'project',
      budgets: { planning: 0.001 }, pollMs: 50,
      clock: () => now, pause: async (ms) => { now += ms; },
      request: async (method, url) => {
        if (url === '/api/version') return { status: 200, body: { version: 'test' } };
        if (url === '/openapi/v1.json') return { status: 200, body: { paths: {} } };
        if (url === '/api/auth/session') return { status: 200, body: { authenticated: true } };
        if (url === '/api/projects/project') return { status: 200, body: { id: 'project' } };
        if (url.endsWith('/work-plan')) return { status: 404, body: {} };
        if (url.endsWith('/children')) return { status: 200, body: [] };
        if (url.includes('/events?')) return { status: 200, body: [{ sequence: 1, type: 'run.started', payload: {} }].filter((e) => url.includes('after=0')) };
        return { status: 200, body: { status: 'in_progress', failureReason: 'waiting for planning' } };
      },
    });
    assert.equal(result.verdict, 'fail');
    assert.equal(result.error.code, 'phase_timeout');
    assert.equal(result.phase, 'planning');
    assert.equal(result.parentRunId, 'parent');
    assert.equal(result.lastEvents[0].type, 'run.started');
    assert.equal(result.terminalDiagnostic, 'waiting for planning');
    assert.equal(result.resultPath, resultPath);
    assert.equal((JSON.parse(await readFile(resultPath, 'utf8'))).error.code, 'phase_timeout');
    assert.equal((await readFile(transcriptPath, 'utf8')).trim().split('\n').length, 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('both preview gates require distinct revised artifacts before approval', async () => {
  const directory = await mkdtemp(path.join(process.cwd(), '.oracle-acceptance-test-'));
  try {
    let revised = false;
    let approved = false;
    const opened = [];
    const deleted = [];
    const active = new Set();
    const browser = async (url, expected) => {
      opened.push([url, expected]);
      return { ready: true, status: 200, bodySha256: revised ? 'revised' : 'original', expectedTextMatched: true, errors: [] };
    };
    const request = async (method, url, body) => {
      if (url === '/api/version') return { status: 200, body: { version: 'test' } };
      if (url === '/openapi/v1.json') return { status: 200, body: { paths: {} } };
      if (url === '/api/auth/session') return { status: 200, body: { authenticated: true } };
      if (url === '/api/projects/project') return { status: 200, body: {} };
      if (url.endsWith('/work-plan')) return { status: 200, body: { status: approved ? 'completed' : revised ? 'in_review' : 'in_review' } };
      if (url.endsWith('/children')) return { status: 200, body: [{ childRunId: 'first' }, ...(revised ? [{ childRunId: 'second' }] : [])] };
      if (url.includes('/events?')) return { status: 200, body: (url.includes('/first/') || url.includes('/second/')) && url.includes('after=0')
        ? [{ sequence: 1, type: 'sandbox.execution_pod.bound', payload: {} }] : [] };
      if (url.endsWith('/assembly/files')) return { status: 200, body: [{ path: 'index.html', diff: revised ? 'new' : 'old' }] };
      if (url.endsWith('/assembly/review')) {
        if (body.request_changes) revised = true;
        else approved = true;
        return { status: 200, body: {} };
      }
      if (url.endsWith('/sandbox/port-forward') && method === 'POST') {
        const session_id = revised ? 'second-preview' : 'first-preview';
        active.add(session_id);
        return { status: 200, body: { session_id, preview_url: `https://${revised ? 'revised' : 'initial'}.example.test` } };
      }
      if (method === 'DELETE') { deleted.push(url); active.delete(url.split('/').at(-1)); return { status: 200 }; }
      if (url.endsWith('/sandbox/port-forward')) return { status: 200, body: [...active].map((session_id) => ({ session_id })) };
      if (url.endsWith('/output-revisions')) return { status: 200, body: [{ revision_id: 'revision-2' }] };
      return { status: 200, body: { status: approved ? 'completed' : 'in_progress' } };
    };
    const result = await runOracleAcceptance({
      request, projectId: 'project', runId: 'parent', expectedText: 'original', correctedText: 'fixed',
      feedback: 'The initial app is missing a visible feature.', targetFiles: ['index.html'],
      browser, transcriptPath: path.join(directory, 'trace.jsonl'), resultPath: path.join(directory, 'result.json'),
    });
    assert.equal(result.verdict, 'pass');
    assert.deepEqual(opened.map((o) => o[1]), ['original', 'fixed']);
    assert.deepEqual(result.decisions.map((o) => o.decision), ['request_changes', 'approve']);
    assert.equal(result.cleanup.length, 2);
    assert.equal(deleted.length, 2);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
