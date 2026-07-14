// test/runner-approvals.test.mjs — proves the scenario runner's poll loop DETECTS,
// JUDGES, and EXECUTES an approval gate (via a mock client + mock judge), and that
// it captures the full audit trail into evidence.approvalDecisions. No network.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { driveScenario } from '../lib/runner.mjs';

// A scripted mock of AgentweaverClient. Responds to each path the runner hits and
// emits a pending tool-approval gate on the events feed until it has been resolved.
function scriptedClient() {
  const calls = [];
  let outcomePolls = 0;
  let toolApproved = false;

  async function call(method, path, body) {
    const rec = { method, path, requestBody: body ?? null, status: 200, ok: true, responseBody: null };
    calls.push(rec);

    if (path === '/api/auth/github') {
      rec.responseBody = { status: 'signed_in', login: 'tester' };
    } else if (path === '/api/blueprints') {
      rec.responseBody = { blueprints: [{ id: 'software-delivery', name: 'SW' }] };
    } else if (path === '/api/projects' && method === 'POST') {
      rec.status = 201; rec.responseBody = { project_id: 'proj-1' };
    } else if (path.endsWith('/team')) {
      rec.responseBody = { members: [{ name: 'Coordinator' }, { name: 'Dev' }] };
    } else if (path.endsWith('/orchestrations') && method === 'POST') {
      rec.status = 201; rec.responseBody = { runId: 'run-1' };
    } else if (path === '/api/runs/run-1') {
      rec.responseBody = { status: 'in_progress' };
    } else if (path.endsWith('/outcome-spec')) {
      outcomePolls += 1;
      // Stay drafting until the gate is resolved so the loop actually reaches the
      // approval-driving step; then settle to end the run.
      rec.responseBody = toolApproved
        ? { status: 'awaiting_confirmation', desiredOutcome: 'done' }
        : { status: 'drafting', desiredOutcome: 'wip' };
    } else if (path.endsWith('/events')) {
      rec.responseBody = toolApproved
        ? [{ sequence: 1, type: 'run.started', payload: {} }]
        : [
            { sequence: 1, type: 'run.started', payload: {} },
            { sequence: 2, type: 'tool.approval_required', payload: { requestId: 'req-1', toolName: 'web_fetch', url: 'https://x' } },
          ];
    } else if (path.includes('/tool-approvals')) {
      toolApproved = true; // resolving the gate lets the spec settle next poll
      rec.responseBody = { run_id: 'run-1', request_id: body?.request_id, approved: true };
    } else {
      rec.responseBody = {};
    }
    return rec;
  }

  return {
    calls,
    call,
    get: (p) => call('GET', p),
    post: (p, b) => call('POST', p, b),
    put: (p, b) => call('PUT', p, b),
    del: (p) => call('DELETE', p),
  };
}

const scenario = {
  id: 'test-approval',
  title: 'Approval drive test',
  blueprintId: 'software-delivery',
  projectPrefix: 'test',
  buildGoal: () => 'do the thing',
};
const persona = { title: 'Tester', raw: '# Tester\nyou approve safe in-scope fetches', scenarios: [] };

test('driveScenario detects, judges, and executes an approval gate and records the audit trail', async () => {
  const client = scriptedClient();
  let judgedGate = null;
  const judge = async ({ gate, prompt }) => {
    judgedGate = gate;
    assert.match(prompt, /web_fetch/); // the driver packaged the gate evidence
    return { decision: 'approve', scope: 'once', reason: 'safe in-scope fetch', source: 'mock-judge' };
  };

  const result = await driveScenario(client, scenario, persona, {
    timeoutMs: 30_000,
    pollMs: 1,
    keep: true,
    driveApprovals: true,
    judge,
  });

  // The gate was judged and driven through the real endpoint.
  assert.ok(judgedGate && judgedGate.requestId === 'req-1');
  const drove = client.calls.find((c) => c.path === '/api/runs/run-1/tool-approvals');
  assert.ok(drove, 'expected a POST to tool-approvals');
  assert.deepEqual(drove.requestBody, { request_id: 'req-1', scope: 'once' });

  // Full audit trail captured.
  const decisions = result.evidence.approvalDecisions;
  assert.equal(decisions.length, 1);
  assert.equal(decisions[0].gate.requestId, 'req-1');
  assert.equal(decisions[0].judge.decision.decision, 'approve');
  assert.equal(decisions[0].executed, true);
  assert.equal(decisions[0].apiCall.path, '/api/runs/run-1/tool-approvals');

  // The run then settled (platform-correctness still green).
  assert.equal(result.evidence.outcomeSpecSettled ?? result.evidence.outcomeSpec?.status === 'awaiting_confirmation', true);
});

test('driveScenario with driveApprovals disabled never touches approval endpoints', async () => {
  const client = scriptedClient();
  const judge = async () => { throw new Error('judge must not be called when driveApprovals is off'); };
  const result = await driveScenario(client, scenario, persona, {
    timeoutMs: 200, pollMs: 1, keep: true, driveApprovals: false, judge,
  });
  assert.equal(client.calls.some((c) => c.path.includes('/tool-approvals')), false);
  assert.deepEqual(result.evidence.approvalDecisions, []);
});
