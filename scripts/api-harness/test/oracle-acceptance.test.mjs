import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { AcceptanceFailure, DEFAULT_BUDGETS, EventDeltas, cleanupOwnedPreviews, createAcceptanceTransport, runOracleAcceptance, selectCurrentAutomaticPreview } from '../lib/oracle-acceptance.mjs';
import { parseOracleArgs } from '../run-oracle-acceptance.mjs';
import { verifyRenderedPreview } from '../../harness-shared/preview-browser.mjs';

test('CLI accepts named phase budgets, rejects invalid values and preserves target files', () => {
  const args = parseOracleArgs(['--budget', 'revisionProvisioning=11', '--target-files', 'src/App.tsx,src/index.css']);
  assert.equal(args.budgets.revisionProvisioning, 11);
  assert.deepEqual(args.targetFiles, ['src/App.tsx', 'src/index.css']);
  assert.throws(() => parseOracleArgs(['--budget', 'unknown=3']), /Invalid --budget/);
  assert.throws(() => parseOracleArgs(['--budget', 'initialPreview=0']), /Invalid --budget/);
  assert.throws(() => parseOracleArgs(['--port', '3000']), /Unknown option --port/);
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

test('a listed preview with no current review evidence is never adopted or deleted', async () => {
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
        if (url.endsWith('/pending-approvals')) return { status: 200, body: { run_id: 'parent', count: 0, approvals: [] } };
        if (url.includes('/events?')) return { status: 200, body: url.includes('/child/') && url.includes('after=0')
          ? [{ sequence: 1, type: 'sandbox.execution_pod.bound', payload: {} }] : [] };
        if (url.endsWith('/assembly/files')) return { status: 200, body: [{ path: 'index.html' }] };
        if (url.endsWith('/sandbox/port-forward')) return { status: 200, body: [{ session_id: 'someone-else' }] };
        return { status: 200, body: { status: 'in_progress', project_id: 'project' } };
      },
    });
    assert.equal(result.verdict, 'fail');
    assert.match(result.error.message, /no output revision event/);
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
        if (url.endsWith('/pending-approvals')) return { status: 200, body: { run_id: 'parent', count: 0, approvals: [] } };
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
  unchangedContent = false, advanceBrowserMs = 0, correctedBudget = 5, initialBudget = 5,
  rejectReviewHeader = false, staleDecision = false, advancePreviewReadMs = 0,
  transientRunReads = 0, thrownRunReads = 0, skipInitialRunRead = false,
  advanceThrownRunMs = 0, planningBudget = 6,
  approvals = [], approveShell = false, approvalConflict = null, pendingBody = null,
  previewCase = null, previewReadTimeouts = 0, sourceCase = null, physicalBuildChildEnded = false,
} = {}) {
  const directory = await mkdtemp(path.join(process.cwd(), '.oracle-acceptance-test-'));
  try {
    let revised = false;
    let approved = false;
    let now = 0;
    const opened = [];
    const deleted = [];
    const decisions = [];
    const approvalPosts = [];
    const previewPosts = [];
    let pendingReads = 0;
    let remainingTransientRunReads = transientRunReads;
    let remainingThrownRunReads = thrownRunReads;
    let previewReads = 0;
    const tree = () => revised ? 'tree-revised' : 'tree-original';
    const previewSession = () => ({
      session_id: revised ? 'second-preview' : 'first-preview',
      preview_url: `https://${revised ? 'revised' : 'initial'}.example.test`,
      preview_runner_session_id: revised ? 'runner-second' : 'runner-first',
      pod_name: 'parent-pod', target_port: 8235, local_port: 0,
    });
    const readyEvent = (second) => ({
      sequence: second ? 5 : 2, type: previewCase === 'failed' ? 'sandbox.preview_failed' : 'sandbox.preview_ready',
      payload: {
        run_id: 'parent', work_plan_id: 42,
        tree_hash: previewCase === 'stale' || (second && previewCase === 'correctedStale')
          ? 'old-tree' : second ? 'tree-revised' : 'tree-original',
        source: previewCase === 'manual' ? 'preview-api' : 'preview-step',
        target_port: previewCase === 'port' ? 3000 : 8235,
        pod_name: previewCase === 'pod' ? 'other-pod' : 'parent-pod',
        session_id: second ? 'second-preview' : 'first-preview',
        preview_runner_session_id: previewCase === 'runner' ? 'other-runner' : second ? 'runner-second' : 'runner-first',
        preview_url: previewCase === 'url' ? 'https://other.example.test'
          : `https://${second ? 'revised' : 'initial'}.example.test`,
      },
    });
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
      if (url.endsWith('/work-plan')) return { status: 200, body: {
        workPlanId: 42, coordinatorRunId: 'parent', status: approved ? 'complete' : 'in_review',
      } };
      if (url.endsWith('/children')) return { status: 200, body: [{ childRunId: 'first' }, ...(revised ? [{ childRunId: 'second' }] : [])] };
      if (url.endsWith('/pending-approvals')) {
        pendingReads++;
        const current = approvals.filter((entry) => (!entry.onRevision || revised)
          && !(entry.resolved || entry.expired || (entry.approved !== false && approvalPosts.some((post) =>
            post.url === `/api/runs/${entry.action_run_id}/shell-approvals` && post.body.command_hash === entry.request_id)))
          && !(approvalConflict === 'resolved' && approvalPosts.length));
        return { status: 200, body: typeof pendingBody === 'function' ? pendingBody(pendingReads)
          : pendingBody ?? { run_id: 'parent', count: current.length, approvals: current } };
      }
      if (url.includes('/events?')) {
        const after = Number(new URL(url, 'https://example.test').searchParams.get('after'));
        if (url.includes('/parent/')) return { status: 200, body: [
          { sequence: 1, type: previewCase === 'unbound' ? 'sandbox.execution_pod.unbound'
            : 'sandbox.execution_pod.bound', payload: { podName: 'parent-pod' } },
          readyEvent(false),
          { sequence: 3, type: 'coordinator.assembly_build_test_completed', payload: { workPlanId: 42, treeHash: 'tree-original' } },
          { sequence: 4, type: 'coordinator.assembly_review_requested', payload: {
            workPlanId: 42, treeHash: 'tree-original', outputRevisionId: missingInitialId ? undefined : 'revision-1',
          } },
          ...(revised ? [
            readyEvent(true),
            { sequence: 6, type: 'coordinator.assembly_build_test_completed', payload: { workPlanId: 42, treeHash: 'tree-revised' } },
            { sequence: 7, type: 'coordinator.assembly_review_requested', payload: {
              workPlanId: 42, treeHash: 'tree-revised', outputRevisionId: staleCorrectedId ? 'revision-1' : 'revision-2',
            } },
          ] : []),
        ].filter((event) => event.sequence > after) };
        const owner = url.includes('/first/') ? 'first' : url.includes('/second/') ? 'second' : null;
        return { status: 200, body: owner && after === 0
          ? [{ sequence: 1, type: 'sandbox.execution_pod.bound', payload: {} },
            ...approvals.filter((entry) => entry.owning_run_id === owner).map((entry, index) => ({
              sequence: index + 2, type: 'shell.approval_required',
              payload: { commandHash: entry.request_id, ...(entry.expired ? { expiresAt: '2020-01-01T00:00:00Z' } : {}) },
            }))] : [] };
      }
      if (url.endsWith('/shell-approvals')) {
        approvalPosts.push({ url, body });
        if (approvalConflict === 'timeout') throw new AcceptanceFailure('POST shell approval timed out', 'request_timeout');
        return approvalConflict ? { status: 409, body: { error: approvalConflict === 'other' ? 'Unrelated conflict.' : 'Run is not active.' } }
          : { status: 200, body: { approved: true } };
      }
      if (url.endsWith('/assembly/files')) return { status: 200, body: [{ path: 'index.html', status: 'modified' }] };
      if (url.includes('/output-revisions/revision-') && url.includes('/files/')) {
        const id = url.split('/')[5];
        const bytes = Buffer.from(id === 'revision-1' ? 'original application' : 'fixed application');
        return { status: 200, body: {
          revision_id: id, path: 'index.html', sha256: createHash('sha256').update(bytes).digest('hex'),
          content_base64: sourceCase === 'tampered' || (sourceCase === 'correctedTampered' && id === 'revision-2')
            ? Buffer.from('tampered application').toString('base64') : bytes.toString('base64'),
        } };
      }
      if (url.includes('/output-revisions/revision-')) {
        const id = url.split('/').at(-1);
        const bytes = Buffer.from(id === 'revision-1' ? 'original application' : 'fixed application');
        return { status: 200, body: {
          revision_id: id, manifest_incomplete: false, work_plan_id: 42,
          tree_hash: id === 'revision-1' ? 'tree-original' : 'tree-revised',
          tree_content_sha256: id === 'revision-1' || unchangedContent ? 'content-original' : 'content-revised',
          files: [{ path: 'index.html', size: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') }],
        } };
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
        previewPosts.push({ url, body });
        throw new Error('Oracle must not create a preview');
      }
      if (method === 'DELETE') { deleted.push(url); throw new Error('Oracle must not delete an automatic preview'); }
      if (url.endsWith('/sandbox/port-forward')) {
        previewReads++;
        if (previewReadTimeouts-- > 0) throw new AcceptanceFailure('GET preview timed out', 'request_timeout');
        if (revised) now += advancePreviewReadMs;
        return { status: 200, body: previewCase === 'missing' ? [] : previewCase === 'ambiguous'
          ? [previewSession(), { ...previewSession(), session_id: 'other-session' }]
          : previewCase === 'foreign' ? [{ ...previewSession(), session_id: 'foreign' }] : [previewSession()] };
      }
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
      if (physicalBuildChildEnded && url === '/api/runs/first') {
        return { status: 200, body: {
          status: 'assemble_ready', ended_at: '2026-10-02T00:43:05Z', parent_run_id: 'parent',
          sandbox: { backend: 'kata-exec-sidecar', pod_name: 'historic-child-pod',
            current_binding: { state: 'unavailable', reason: 'active_lease_missing' } },
        } };
      }
      return { status: 200, body: {
        status: approved ? 'completed' : 'in_progress', project_id: 'project', lifecycle_generation: 1,
        sandbox: { backend: 'kata-exec-sidecar', claim_name: 'historic-claim', pod_name: 'historic-pod',
          current_binding: { state: previewCase === 'lost' ? 'unavailable' : 'verified', run_id: 'parent',
            provisioner: 'kubernetes-sandbox-claim', claim_name: 'agent-parent',
            claim_uid: 'claim-uid', pod_name: 'parent-pod', pod_uid: 'pod-uid',
            namespace: 'agentweaver', lifecycle_generation: 1, assembly_attempt: '4',
            source_repository: '/repository', source_ref: 'branch',
            source_base_commit: 'commit', source_tree: tree(), source_worktree: '/worktree' } },
      } };
    };
    const result = await runOracleAcceptance({
      request, runId: 'parent', expectedText: 'original', correctedText: 'fixed',
      feedback: 'The initial app is missing a visible feature.', targetFiles: ['index.html'],
      browser, transcriptPath: path.join(directory, 'trace.jsonl'), resultPath: path.join(directory, 'result.json'),
      budgets: { ...DEFAULT_BUDGETS, planning: planningBudget, initialPreview: initialBudget, correctedPreview: correctedBudget },
      clock: () => now, pause: async (ms) => { now += ms; },
      approveShell,
    });
    return { result, opened, decisions, deleted, runReadAttempts, approvalPosts, pendingReads, previewPosts, previewReads };
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

const shellApproval = (owner = 'first', hash = 'command-hash') => ({
  root_run_id: 'parent', owning_run_id: owner, action_run_id: owner,
  request_id: hash, tool_name: 'run_command', is_shell: true,
});

test('attaching at review keeps completed-child shell history but does not approve it', async () => {
  const { result, approvalPosts, opened } = await driveReviewFixture({
    approvals: [{ ...shellApproval(), resolved: true }],
  });
  assert.equal(result.verdict, 'pass');
  assert.equal(approvalPosts.length, 0);
  assert.deepEqual(opened.map((entry) => entry[1]), ['original', 'fixed']);
  assert.ok(result.lastEvents.some((event) => event.type === 'shell.approval_required'));
});

test('resolved and expired historical approvals require no opt-in or POST', async () => {
  for (const approveShell of [false, true]) {
    const { result, approvalPosts, pendingReads } = await driveReviewFixture({
      approveShell, approvals: [{ ...shellApproval('first', 'resolved'), resolved: true },
        { ...shellApproval('first', 'expired'), expired: true }],
    });
    assert.equal(result.verdict, 'pass');
    assert.equal(approvalPosts.length, 0);
    assert.ok(pendingReads > 0);
  }
});

test('owned actionable shell approval requires opt-in and uses its action run and hash', async () => {
  const approval = shellApproval();
  const denied = await driveReviewFixture({ approvals: [approval] });
  assert.equal(denied.result.verdict, 'fail');
  assert.match(denied.result.error.message, /Shell approval required/);
  assert.equal(denied.approvalPosts.length, 0);
  const allowed = await driveReviewFixture({ approvals: [approval], approveShell: true });
  assert.equal(allowed.result.verdict, 'pass');
  assert.deepEqual(allowed.approvalPosts, [{ url: '/api/runs/first/shell-approvals', body: { command_hash: 'command-hash' } }]);
});

test('new revision child shell request is handled after attachment', async () => {
  const { result, approvalPosts, opened } = await driveReviewFixture({
    approveShell: true,
    approvals: [{ ...shellApproval(), resolved: true }, { ...shellApproval('second', 'new-hash'), onRevision: true }],
  });
  assert.equal(result.verdict, 'pass');
  assert.deepEqual(approvalPosts, [{ url: '/api/runs/second/shell-approvals', body: { command_hash: 'new-hash' } }]);
  assert.deepEqual(opened.map((entry) => entry[1]), ['original', 'fixed']);
});

test('synthetic coordinator stream uses the parent action run, and unrelated non-shell approvals do not trigger writes', async () => {
  const synthetic = { ...shellApproval('parent-coordinator-decompose', 'synthetic-hash'), action_run_id: 'parent' };
  const nonShell = { ...shellApproval('first', 'tool-id'), is_shell: false, tool_name: 'read_file' };
  const { result, approvalPosts } = await driveReviewFixture({
    approveShell: true, approvals: [synthetic, nonShell],
  });
  assert.equal(result.verdict, 'pass');
  assert.deepEqual(approvalPosts, [{ url: '/api/runs/parent/shell-approvals', body: { command_hash: 'synthetic-hash' } }]);
});

test('unrelated, malformed, or inconsistently classified pending approvals fail closed', async () => {
  const cases = [
    { approvals: [shellApproval('other')] },
    { approvals: [{ ...shellApproval(), root_run_id: 'other' }] },
    { approvals: [{ ...shellApproval(), action_run_id: 'other' }] },
    { approvals: [{ ...shellApproval(), request_id: '' }] },
    { approvals: [{ ...shellApproval(), tool_name: 'some_other_tool' }] },
    { approvals: [{ ...shellApproval(), is_shell: false }] },
    { approvals: [shellApproval(), shellApproval()] },
    { pendingBody: { run_id: 'parent', count: 1, approvals: [] } },
    { pendingBody: { run_id: 'other', count: 0, approvals: [] } },
    { pendingBody: { count: 0, approvals: [] } },
    { pendingBody: { run_id: 'parent', count: 1, approvals: null } },
  ];
  for (const options of cases) {
    const { result, approvalPosts } = await driveReviewFixture({ ...options, approveShell: true });
    assert.equal(result.verdict, 'fail', JSON.stringify(options));
    assert.match(result.error.message, /pending approval|pending shell/i);
    assert.equal(approvalPosts.length, 0);
  }
});

test('409 resolution race reconciles with one read, but pending or unrelated conflicts fail', async () => {
  const approval = shellApproval();
  const resolved = await driveReviewFixture({ approvals: [approval], approveShell: true, approvalConflict: 'resolved' });
  assert.equal(resolved.result.verdict, 'pass');
  assert.equal(resolved.approvalPosts.length, 1);
  assert.deepEqual(resolved.result.shellApprovalReconciliations.map((entry) => entry.requestId), ['command-hash']);
  assert.match(resolved.result.shellApprovalReconciliations[0].reason, /confirmed absent/);

  for (const approvalConflict of ['pending', 'other']) {
    const { result, approvalPosts } = await driveReviewFixture({
      approvals: [{ ...approval, approved: false }], approveShell: true, approvalConflict,
    });
    assert.equal(result.verdict, 'fail');
    assert.equal(approvalPosts.length, 1);
    assert.match(result.error.message, /conflict|HTTP 409/i);
  }
  const timeout = await driveReviewFixture({
    approvals: [{ ...approval, approved: false }], approveShell: true, approvalConflict: 'timeout',
  });
  assert.equal(timeout.result.verdict, 'fail');
  assert.equal(timeout.result.error.code, 'request_timeout');
  assert.equal(timeout.approvalPosts.length, 1);
  const malformed = await driveReviewFixture({
    approvals: [approval], approveShell: true, approvalConflict: 'resolved',
    pendingBody: (read) => read === 1
      ? { run_id: 'parent', count: 1, approvals: [approval] }
      : { run_id: 'parent', count: 0 },
  });
  assert.equal(malformed.result.verdict, 'fail');
  assert.equal(malformed.approvalPosts.length, 1);
  assert.match(malformed.result.error.message, /Invalid pending approvals response/);
});

test('both preview gates pin fresh parent revisions and execution headers; metadata list can be identical', async () => {
  const { result, opened, decisions, deleted, previewPosts } = await driveReviewFixture();
  assert.equal(result.verdict, 'pass');
  assert.deepEqual(previewPosts, []);
  assert.equal(result.projectId, 'project');
  assert.deepEqual(opened.map((o) => o[1]), ['original', 'fixed']);
  assert.deepEqual(result.decisions.map((o) => o.decision), ['request_changes', 'approve']);
  assert.deepEqual(decisions.map((o) => o.body.output_revision_id), ['revision-1', 'revision-2']);
  assert.deepEqual(decisions.map((o) => o.headers['If-Model-Provider-Key']), ['key-1', 'key-2']);
  assert.deepEqual([result.initialRevision.contentIdentity, result.correctedRevision.contentIdentity], ['content-original', 'content-revised']);
  assert.deepEqual(result.previews.map((entry) => entry.targetPort), [8235, 8235]);
  assert.deepEqual(result.cleanup, []);
  assert.deepEqual(deleted, []);
});

test('stale, manual, foreign, ambiguous, mismatched and failed previews are not adopted or deleted', async () => {
  for (const previewCase of ['stale', 'manual', 'foreign', 'ambiguous', 'port', 'pod', 'runner', 'url', 'failed', 'lost', 'unbound']) {
    const { result, previewPosts, opened, deleted, decisions } = await driveReviewFixture({ previewCase });
    assert.equal(result.verdict, 'fail', previewCase);
    assert.equal(result.phase, 'initialPreview', previewCase);
    assert.match(result.error.message, /preview|claim-bound/i, previewCase);
    assert.deepEqual([previewPosts, opened, deleted, decisions], [[], [], [], []], previewCase);
  }
});

test('same-tree recovery selects only the latest terminal event and its live session', async () => {
  const session = (id) => ({
    session_id: id, preview_runner_session_id: `runner-${id}`, pod_name: 'parent-pod',
    target_port: 8235, preview_url: `https://${id}.example.test`,
  });
  const ready = (id, sequence, type = 'sandbox.preview_ready') => ({
    sequence, type,
    payload: {
      run_id: 'parent', work_plan_id: 42, tree_hash: 'tree-original', source: 'preview-step',
      session_id: id, preview_runner_session_id: `runner-${id}`, pod_name: 'parent-pod',
      target_port: 8235, preview_url: `https://${id}.example.test`,
    },
  });
  const select = async (history, sessions) => {
    const events = [
      { sequence: 1, type: 'sandbox.execution_pod.bound', payload: { podName: 'parent-pod' } },
      ...history,
      { sequence: 5, type: 'coordinator.assembly_build_test_completed', payload: { workPlanId: 42, treeHash: 'tree-original' } },
      { sequence: 6, type: 'coordinator.assembly_review_requested', payload: {
        workPlanId: 42, treeHash: 'tree-original', outputRevisionId: 'revision-1',
      } },
    ];
    const deltas = new EventDeltas(async () => ({ status: 200, body: events }));
    await deltas.poll('parent');
    return () => selectCurrentAutomaticPreview({
      runId: 'parent', plan: { workPlanId: 42, coordinatorRunId: 'parent', status: 'in_review' },
      run: { lifecycle_generation: 1, sandbox: { backend: 'kata-exec-sidecar', pod_name: 'historic-pod', current_binding: {
        state: 'verified', run_id: 'parent', provisioner: 'kubernetes-sandbox-claim', claim_name: 'agent-parent',
        claim_uid: 'claim-uid', pod_name: 'parent-pod', pod_uid: 'pod-uid',
        namespace: 'agentweaver', lifecycle_generation: 1, assembly_attempt: '4',
        source_repository: '/repository', source_ref: 'branch', source_base_commit: 'commit',
        source_tree: 'tree-original', source_worktree: '/worktree',
      } } },
      revision: { tree_hash: 'tree-original', work_plan_id: 42 }, deltas, sessions,
    });
  };
  for (const previous of ['sandbox.preview_ready', 'sandbox.preview_failed']) {
    const current = await select([ready('old', 2, previous), ready('new', 3)], [session('new')]);
    assert.deepEqual(current(), { sessionId: 'new', previewUrl: 'https://new.example.test', targetPort: 8235 });
  }
  for (const latest of ['sandbox.preview_failed', 'sandbox.preview_skipped_not_applicable']) {
    const current = await select([ready('old', 2), ready('new', 3, latest)], [session('old')]);
    assert.throws(current, /Current automatic preview failed or was skipped/);
  }
  const stale = await select([ready('old', 2), ready('new', 3)], [session('old')]);
  assert.throws(stale, /does not match the current automatic preview event/);
  const foreign = await select([ready('old', 2), ready('new', 3)], [session('foreign')]);
  assert.throws(foreign, /does not match the current automatic preview event/);
  const unknown = await select([], [session('unknown')]);
  assert.throws(unknown, /no current automatic preview-ready event/);
});

test('current binding rejects historical pods and missing provenance without changing preview fences', async () => {
  const { result } = await driveReviewFixture();
  assert.equal(result.verdict, 'pass');
  for (const bindingCase of ['missing', 'unavailable', 'foreignRun', 'claimUid', 'podUid', 'sourceTree', 'attempt', 'generation', 'rotatedGeneration']) {
    const base = {
      state: 'verified', run_id: 'parent', provisioner: 'kubernetes-sandbox-claim', claim_name: 'agent-parent',
      claim_uid: 'claim-uid', pod_name: 'parent-pod', pod_uid: 'pod-uid',
      namespace: 'agentweaver', lifecycle_generation: 1, assembly_attempt: '4',
      source_repository: '/repository', source_ref: 'branch', source_base_commit: 'commit',
      source_tree: 'tree-original', source_worktree: '/worktree',
    };
    if (bindingCase === 'unavailable') base.state = 'unavailable';
    if (bindingCase === 'foreignRun') base.run_id = 'physical-child';
    if (bindingCase === 'claimUid') base.claim_uid = '';
    if (bindingCase === 'podUid') base.pod_uid = '';
    if (bindingCase === 'sourceTree') base.source_tree = 'old-tree';
    if (bindingCase === 'attempt') base.assembly_attempt = '';
    if (bindingCase === 'generation') base.lifecycle_generation = 0;
    const deltas = new EventDeltas(async () => ({ status: 200, body: [
      { sequence: 1, type: 'sandbox.execution_pod.bound', payload: { podName: 'parent-pod' } },
      { sequence: 2, type: 'sandbox.preview_ready', payload: {
        run_id: 'parent', work_plan_id: 42, tree_hash: 'tree-original', source: 'preview-step',
        session_id: 'preview', preview_runner_session_id: 'runner', pod_name: 'parent-pod',
        target_port: 8235, preview_url: 'https://preview.example.test',
      } },
      { sequence: 3, type: 'coordinator.assembly_build_test_completed', payload: {
        workPlanId: 42, treeHash: 'tree-original',
      } },
      { sequence: 4, type: 'coordinator.assembly_review_requested', payload: {
        workPlanId: 42, treeHash: 'tree-original', outputRevisionId: 'revision',
      } },
    ] }));
    await deltas.poll('parent');
    assert.throws(() => selectCurrentAutomaticPreview({
      runId: 'parent', plan: { workPlanId: 42, coordinatorRunId: 'parent', status: 'in_review' },
      run: { lifecycle_generation: bindingCase === 'rotatedGeneration' ? 2 : 1,
        sandbox: { backend: 'kata-exec-sidecar', pod_name: 'historic-pod',
        current_binding: bindingCase === 'missing' ? undefined : base } },
      revision: { tree_hash: 'tree-original', work_plan_id: 42 }, deltas,
      sessions: [{ session_id: 'preview', preview_runner_session_id: 'runner',
        pod_name: 'parent-pod', target_port: 8235, preview_url: 'https://preview.example.test' }],
    }), /verified claim and source binding/, bindingCase);
  }
});

test('reviewer selects the logical parent preview while a physical build child has ended', async () => {
  const { result, opened, decisions } = await driveReviewFixture({ physicalBuildChildEnded: true });
  assert.equal(result.verdict, 'pass');
  assert.equal(opened.length, 2);
  assert.equal(decisions.length, 2);
});

test('corrected tree freshness and both revisions source bytes are enforced', async () => {
  const stale = await driveReviewFixture({ previewCase: 'correctedStale' });
  assert.equal(stale.result.verdict, 'fail');
  assert.equal(stale.result.phase, 'correctedPreview');
  assert.deepEqual(stale.decisions.map((d) => d.body.request_changes), [true]);
  assert.deepEqual(stale.deleted, []);
  for (const sourceCase of ['tampered', 'correctedTampered']) {
    const { result, decisions, opened, previewPosts } = await driveReviewFixture({ sourceCase });
    assert.equal(result.verdict, 'fail');
    assert.match(result.error.message, /source hash or size mismatch/);
    assert.equal(decisions.length, sourceCase === 'tampered' ? 0 : 1);
    assert.equal(opened.length, sourceCase === 'tampered' ? 0 : 1);
    assert.deepEqual(previewPosts, []);
  }
});

test('missing automatic preview times out without a POST; GET timeout retries only reads', async () => {
  const missing = await driveReviewFixture({ previewCase: 'missing', initialBudget: 0.001 });
  assert.equal(missing.result.verdict, 'fail');
  assert.equal(missing.result.error.code, 'phase_timeout');
  assert.deepEqual(missing.previewPosts, []);
  const recovered = await driveReviewFixture({ previewReadTimeouts: 2 });
  assert.equal(recovered.result.verdict, 'pass');
  assert.ok(recovered.previewReads >= 4);
  assert.deepEqual(recovered.previewPosts, []);
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

test('late preview read fails without claiming or deleting the automatic route', async () => {
  const { result, decisions, deleted } = await driveReviewFixture({
    advancePreviewReadMs: 61, correctedBudget: 0.001,
  });
  assert.equal(result.verdict, 'fail');
  assert.equal(result.error.code, 'phase_timeout');
  assert.equal(result.phase, 'correctedPreview');
  assert.equal(decisions.length, 1);
  assert.deepEqual(deleted, []);
  assert.deepEqual(result.cleanup, []);
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
