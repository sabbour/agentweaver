import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { AcceptanceFailure, DEFAULT_BUDGETS, EventDeltas, cleanupOwnedPreviews, createAcceptanceTransport, runOracleAcceptance } from '../lib/oracle-acceptance.mjs';
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
        return { status: 200, body: { status: 'in_progress', project_id: 'project' } };
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
        return { status: 200, body: { status: 'in_progress', project_id: 'project', failureReason: 'waiting for planning' } };
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

async function driveReviewFixture({
  missingInitialId = false, staleCorrectedId = false, missingExecutionKey = false,
  unchangedContent = false, advanceBrowserMs = 0, correctedBudget = 5,
  rejectReviewHeader = false, staleDecision = false, advancePreviewRequestMs = 0,
  transientRunReads = 0, thrownRunReads = 0, skipInitialRunRead = false,
  advanceThrownRunMs = 0, planningBudget = 6,
} = {}) {
  const directory = await mkdtemp(path.join(process.cwd(), '.oracle-acceptance-test-'));
  try {
    let revised = false;
    let approved = false;
    let now = 0;
    const opened = [];
    const deleted = [];
    const decisions = [];
    const active = new Set();
    let remainingTransientRunReads = transientRunReads;
    let remainingThrownRunReads = thrownRunReads;
    let runReadAttempts = 0;
    const browser = async (url, expected) => {
      opened.push([url, expected]);
      if (revised) now += advanceBrowserMs;
      return { ready: true, status: 200, bodySha256: revised ? 'revised' : 'original', expectedTextMatched: true, errors: [] };
    };
    const request = async (method, url, body, options) => {
      if (url === '/api/version') return { status: 200, body: { version: 'test' } };
      if (url === '/openapi/v1.json') return { status: 200, body: { paths: {} } };
      if (url === '/api/auth/session') return { status: 200, body: { authenticated: true } };
      if (url === '/api/ai/execution-context') return { status: 200, body: missingExecutionKey ? {} : { execution_key: `key-${decisions.length + 1}` } };
      if (url === '/api/projects/project') return { status: 200, body: {} };
      if (url.endsWith('/work-plan')) return { status: 200, body: { status: approved ? 'complete' : 'in_review' } };
      if (url.endsWith('/children')) return { status: 200, body: [{ childRunId: 'first' }, ...(revised ? [{ childRunId: 'second' }] : [])] };
      if (url.includes('/events?')) {
        const after = Number(new URL(url, 'https://example.test').searchParams.get('after'));
        if (url.includes('/parent/')) return { status: 200, body: [
          { sequence: 1, type: 'coordinator.assembly_review_requested', payload: missingInitialId ? {} : { outputRevisionId: 'revision-1' } },
          ...(revised ? [{ sequence: 2, type: 'coordinator.assembly_review_requested', payload: { outputRevisionId: staleCorrectedId ? 'revision-1' : 'revision-2' } }] : []),
        ].filter((event) => event.sequence > after) };
        return { status: 200, body: (url.includes('/first/') || url.includes('/second/')) && after === 0
          ? [{ sequence: 1, type: 'sandbox.execution_pod.bound', payload: {} }] : [] };
      }
      if (url.endsWith('/assembly/files')) return { status: 200, body: [{ path: 'index.html', status: 'modified' }] };
      if (url.includes('/output-revisions/revision-')) {
        const id = url.split('/').at(-1);
        return { status: 200, body: { revision_id: id, manifest_incomplete: false, tree_content_sha256: id === 'revision-1' || unchangedContent ? 'content-original' : 'content-revised' } };
      }
      if (url.endsWith('/assembly/review')) {
        decisions.push({ body, headers: options?.headers });
        if (rejectReviewHeader || !options?.headers?.['If-Model-Provider-Key']) {
          return { status: 400, body: { error: 'ai_execution_context_required' } };
        }
        if (staleDecision || body.output_revision_id !== (revised ? 'revision-2' : 'revision-1')
          || options.headers['If-Model-Provider-Key'] !== `key-${decisions.length}`) {
          return { status: 409, body: { error: 'stale_output_revision' } };
        }
        if (body.request_changes) revised = true;
        else approved = true;
        return { status: 200, body: {} };
      }
      if (url.endsWith('/sandbox/port-forward') && method === 'POST') {
        const session_id = revised ? 'second-preview' : 'first-preview';
        active.add(session_id);
        if (revised) now += advancePreviewRequestMs;
        return { status: 200, body: { session_id, preview_url: `https://${revised ? 'revised' : 'initial'}.example.test` } };
      }
      if (method === 'DELETE') { deleted.push(url); active.delete(url.split('/').at(-1)); return { status: 200 }; }
      if (url.endsWith('/sandbox/port-forward')) return { status: 200, body: [...active].map((session_id) => ({ session_id })) };
      if (url.endsWith('/output-revisions')) return { status: 200, body: [
        { revision_id: 'revision-1' }, ...(revised ? [{ revision_id: 'revision-2' }] : []),
      ] };
      if (url === '/api/runs/parent') {
        runReadAttempts++;
        if (remainingThrownRunReads > 0 && (!skipInitialRunRead || runReadAttempts > 1)) {
          remainingThrownRunReads--;
          now += advanceThrownRunMs;
          throw new AcceptanceFailure('GET /api/runs/parent: timed out', 'request_timeout');
        }
        if (remainingTransientRunReads > 0) {
          remainingTransientRunReads--;
          return { status: 0, body: { error: 'transport_error', message: 'fetch failed' } };
        }
      }
      return { status: 200, body: { status: approved ? 'completed' : 'in_progress', project_id: 'project' } };
    };
    const result = await runOracleAcceptance({
      request, runId: 'parent', expectedText: 'original', correctedText: 'fixed',
      feedback: 'The initial app is missing a visible feature.', targetFiles: ['index.html'],
      browser, transcriptPath: path.join(directory, 'trace.jsonl'), resultPath: path.join(directory, 'result.json'),
      budgets: { ...DEFAULT_BUDGETS, planning: planningBudget, correctedPreview: correctedBudget },
      clock: () => now, pause: async (ms) => { now += ms; },
    });
    return { result, opened, decisions, deleted, runReadAttempts };
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

test('both preview gates pin fresh parent revisions and execution headers; metadata list can be identical', async () => {
  const { result, opened, decisions, deleted } = await driveReviewFixture();
  assert.equal(result.verdict, 'pass');
  assert.equal(result.projectId, 'project');
  assert.deepEqual(opened.map((o) => o[1]), ['original', 'fixed']);
  assert.deepEqual(result.decisions.map((o) => o.decision), ['request_changes', 'approve']);
  assert.deepEqual(decisions.map((o) => o.body.output_revision_id), ['revision-1', 'revision-2']);
  assert.deepEqual(decisions.map((o) => o.headers['If-Model-Provider-Key']), ['key-1', 'key-2']);
  assert.deepEqual([result.initialRevision.contentIdentity, result.correctedRevision.contentIdentity], ['content-original', 'content-revised']);
  assert.equal(result.cleanup.length, 2);
  assert.equal(deleted.length, 2);
});

test('server rejection of missing review header or stale output revision fails closed', async () => {
  for (const options of [{ rejectReviewHeader: true }, { staleDecision: true }]) {
    const { result, decisions } = await driveReviewFixture(options);
    assert.equal(result.verdict, 'fail');
    assert.equal(decisions.length, 1);
    assert.equal(result.decisions.length, 0);
    assert.match(result.error.message, /assembly review: HTTP (400|409)/);
  }
});

test('missing initial review revision or execution key fails before submitting an unpinned decision', async () => {
  for (const options of [{ missingInitialId: true }, { missingExecutionKey: true }]) {
    const { result, decisions } = await driveReviewFixture(options);
    assert.equal(result.verdict, 'fail');
    assert.equal(decisions.length, 0);
    assert.match(result.error.message, /output revision event|execution key unavailable/);
  }
});

test('stale corrected revision and unchanged revision content fail before approval', async () => {
  for (const options of [{ staleCorrectedId: true }, { unchangedContent: true }]) {
    const { result, decisions } = await driveReviewFixture({ ...options, correctedBudget: 0.001 });
    assert.equal(result.verdict, 'fail');
    assert.deepEqual(decisions.map((entry) => entry.body.request_changes), [true]);
    assert.match(result.error.message, /Phase correctedPreview|unchanged artifact content/);
  }
});

test('corrected browser success after the shared deadline is rejected and preserves one timing entry', async () => {
  const { result, decisions } = await driveReviewFixture({ advanceBrowserMs: 61, correctedBudget: 0.001 });
  assert.equal(result.verdict, 'fail');
  assert.equal(result.error.code, 'phase_timeout');
  assert.equal(result.phase, 'correctedPreview');
  assert.equal(result.phaseTimingsMs.correctedPreview, 61);
  assert.equal(decisions.length, 1);
});

test('late preview publication is failed and its returned session is still cleaned up', async () => {
  const { result, decisions, deleted } = await driveReviewFixture({
    advancePreviewRequestMs: 61, correctedBudget: 0.001,
  });
  assert.equal(result.verdict, 'fail');
  assert.equal(result.error.code, 'phase_timeout');
  assert.equal(result.phase, 'correctedPreview');
  assert.equal(decisions.length, 1);
  assert.equal(deleted.length, 2);
  assert.ok(result.cleanup.every((entry) => entry.deleted));
});

test('idempotent polling retries bounded transient transport failures', async () => {
  const recovered = await driveReviewFixture({ transientRunReads: 2 });
  assert.equal(recovered.result.verdict, 'pass');

  const exhausted = await driveReviewFixture({ transientRunReads: 3 });
  assert.equal(exhausted.result.verdict, 'fail');
  assert.equal(exhausted.result.error.code, 'transport_error');
  assert.match(exhausted.result.error.message, /GET \/api\/runs\/parent: fetch failed/);
});

test('thrown GET request timeout recovers without changing preview or review checks', async () => {
  const { result, opened, decisions, runReadAttempts } = await driveReviewFixture({ thrownRunReads: 2 });
  assert.equal(result.verdict, 'pass');
  assert.equal(runReadAttempts, (await driveReviewFixture()).runReadAttempts + 2);
  assert.deepEqual(opened.map((entry) => entry[1]), ['original', 'fixed']);
  assert.deepEqual(decisions.map((entry) => entry.body.output_revision_id), ['revision-1', 'revision-2']);
});

async function runPreflightFailure({ method = 'GET', error }) {
  const directory = await mkdtemp(path.join(process.cwd(), '.oracle-acceptance-test-'));
  try {
    let attempts = 0;
    let pauses = 0;
    const result = await runOracleAcceptance({
      projectId: 'project', goal: 'test',
      transcriptPath: path.join(directory, 'trace.jsonl'), resultPath: path.join(directory, 'result.json'),
      pause: async () => { pauses++; },
      request: async (requestMethod, url) => {
        if (requestMethod === method && url === (method === 'GET' ? '/api/version' : '/api/ai/execution-context')) {
          attempts++;
          throw error;
        }
        if (url === '/api/version') return { status: 200, body: {} };
        if (url === '/openapi/v1.json') return { status: 200, body: { paths: {} } };
        if (url === '/api/auth/session') return { status: 200, body: { authenticated: true } };
        if (url === '/api/projects/project') return { status: 200, body: {} };
        throw new Error(`Unexpected request: ${requestMethod} ${url}`);
      },
    });
    return { result, attempts, pauses };
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

test('thrown transient GET errors stop after three attempts and retain the final code', async () => {
  for (const code of ['request_timeout', 'transport_error']) {
    const { result, attempts, pauses } = await runPreflightFailure({
      error: new AcceptanceFailure('GET /api/version: temporary read failure', code),
    });
    assert.equal(result.verdict, 'fail');
    assert.equal(result.error.code, code);
    assert.match(result.error.message, /temporary read failure/);
    assert.equal(attempts, 3);
    assert.equal(pauses, 2);
  }
});

test('thrown POST failure and unexpected GET error never retry', async () => {
  const post = await runPreflightFailure({
    method: 'POST', error: new AcceptanceFailure('POST /api/ai/execution-context: timed out', 'request_timeout'),
  });
  assert.equal(post.result.error.code, 'request_timeout');
  assert.equal(post.attempts, 1);
  assert.equal(post.pauses, 0);

  const unexpected = await runPreflightFailure({ error: new Error('unexpected parsing error') });
  assert.equal(unexpected.result.error.code, 'acceptance_failed');
  assert.match(unexpected.result.error.message, /unexpected parsing error/);
  assert.equal(unexpected.attempts, 1);
  assert.equal(unexpected.pauses, 0);

  for (const code of ['phase_timeout', 'request_cancelled']) {
    const failure = await runPreflightFailure({ error: new AcceptanceFailure('GET /api/version: stopped', code) });
    assert.equal(failure.result.error.code, code);
    assert.equal(failure.attempts, 1);
    assert.equal(failure.pauses, 0);
  }
});

test('expired phase budget prevents another GET after a thrown timeout', async () => {
  const { result, runReadAttempts } = await driveReviewFixture({
    thrownRunReads: 2, skipInitialRunRead: true, advanceThrownRunMs: 61, planningBudget: 0.001,
  });
  assert.equal(result.verdict, 'fail');
  assert.equal(result.error.code, 'phase_timeout');
  assert.equal(result.phase, 'planning');
  assert.equal(runReadAttempts, 2);
});

test('explicit transport cancellation is not classified as a retryable timeout', async () => {
  const directory = await mkdtemp(path.join(process.cwd(), '.oracle-acceptance-test-'));
  try {
    const abort = new AbortController();
    abort.abort(new Error('cancelled by caller'));
    const request = createAcceptanceTransport({
      call: async () => { throw abort.signal.reason; },
    }, path.join(directory, 'trace.jsonl'));
    await assert.rejects(
      request('GET', '/api/runs/parent', undefined, { signal: abort.signal }),
      (error) => error.code === 'request_cancelled',
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
