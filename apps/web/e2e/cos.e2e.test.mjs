/**
 * Chief of Staff browser journeys against the built web app with a faked
 * Broker and Gateway: starting a chat, editing Priorities, the Flow board,
 * permission loss, stale gates, and follow-up messages.
 * Run `npm run build` first, then `npm run e2e`.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { startStaticServer, openApp, runRoom } from './harness.mjs';
import { notification, priority, project, run } from '../src/v1/__fixtures__/cosGateway.ts';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });

async function shot(name, locator) {
  const directory = process.env.AGENTWEAVER_EVIDENCE_DIR;
  if (!directory) return;
  // Evidence stays outside the repository and uses only the journey's test data.
  await mkdir(directory, { recursive: true });
  await locator.screenshot({ path: join(directory, `${name}.png`) });
}

/** Returns the recorded Gateway requests with this method whose path matches the pattern. */
const writes = (app, method, pattern) => app.gateway.requests.filter((request) => request.method === method && pattern.test(request.url.pathname));

test('new chat: one create for a double send, and the chat survives reload', async () => {
  // Two projects, so the test also proves the run is created in the chosen one only.
  const app = await openApp({ origin: server.origin, seed: { projects: [project('p1', 'Payments'), project('p2', 'Search')] } });
  try {
    const { page } = app;
    await page.goto(`${server.origin}/overview`);
    await page.getByLabel('Project', { exact: true }).selectOption('p2');
    await page.getByLabel('Message', { exact: true }).fill('Fix the flaky search test');
    await shot('pr-new-chat-gateway-adapter', page.getByLabel('Message', { exact: true }).locator('xpath=ancestor::form'));
    const send = page.getByRole('button', { name: 'Send' });
    // A double click simulates an impatient user. The app must still create one run.
    await send.dblclick();
    await page.waitForURL(/\/projects\/p2\/runs\/run-1$/);
    const creates = writes(app, 'POST', /^\/api\/v1\/projects\/p2\/runs$/);
    assert.equal(creates.length, 1, 'one create request');
    assert.equal(writes(app, 'POST', /^\/api\/v1\/projects\/p1\/runs$/).length, 0);
    // Start needs a run-bound token. Chief of Staff writes need the unbound one.
    const starts = writes(app, 'POST', /^\/api\/v1\/projects\/p2\/runs\/run-1\/start$/);
    assert.equal(starts.length, 1, 'one start');
    assert.equal(starts[0].headers.get('authorization'), 'Bearer tok.p2.run-1', 'start uses the run-bound token');
    for (const request of writes(app, 'POST', /^\/api\/v1\/chief-of-staff\//)) {
      assert.equal(request.headers.get('authorization'), 'Bearer tok.unbound');
    }

    // Navigating back reloads the app. The chat must come from the server, and
    // the reload must not repeat the create.
    await page.goto(`${server.origin}/overview`);
    await page.getByRole('list', { name: 'My conversations' }).getByText('Fix the flaky search test').waitFor();
    assert.equal(writes(app, 'POST', /^\/api\/v1\/projects\/p2\/runs$/).length, 1, 'reload does not create again');
    assert.equal(app.gateway.state.conversations.length, 1);
  } finally {
    await app.close();
  }
});

test('priorities: create, checklist, link a run, reorder, and a revision conflict', async () => {
  const app = await openApp({
    origin: server.origin,
    seed: { runs: { p1: [run('r-2', 'active', 'Profile the API')] }, priorities: [priority('pr-1', 'Hire two engineers')] },
  });
  try {
    const { page } = app;
    // Rows are labelled "Priority <rank>: <outcome>". Match any rank because reordering changes it.
    const item = (outcome) => page.getByRole('listitem', { name: new RegExp(`^Priority \\d+: ${outcome}$`) });
    await page.goto(`${server.origin}/overview`);
    const form = page.getByRole('form', { name: 'Add a Priority' });
    await form.getByLabel('Outcome').fill('Cut latency');
    await form.getByRole('button', { name: 'Add Priority' }).click();
    const row = item('Cut latency');
    await row.waitFor();

    const checklist = row.getByRole('form', { name: 'Add checklist item to Cut latency' });
    await checklist.getByLabel('New checklist item').fill('Profile the API');
    await checklist.getByRole('button', { name: 'Add item' }).click();
    await row.getByRole('checkbox', { name: 'Profile the API' }).waitFor();

    // Link the seeded run. The row may show the run id or its title, so accept either.
    await row.getByRole('button', { name: 'Add link to Cut latency' }).click();
    const linkForm = row.getByRole('form', { name: 'Add link to Cut latency' });
    await linkForm.getByLabel('Run').selectOption('r-2');
    await linkForm.getByRole('button', { name: 'Add link' }).click();
    await row.getByText('Run r-2').or(row.getByText('Profile the API', { exact: false })).first().waitFor();
    assert.deepEqual(
      app.gateway.state.priorities.find((value) => value.outcome === 'Cut latency').links.map(({ kind, projectId, runId }) => ({ kind, projectId, runId })),
      [{ kind: 'run', projectId: 'p1', runId: 'r-2' }],
    );
    await shot('pr-priority-run-link-new-gateway-adapter', row);

    // The new Priority starts last. Moving it up must persist the new order on the server.
    await row.getByRole('button', { name: 'Move up: Cut latency' }).click();
    await page.getByRole('listitem', { name: 'Priority 1: Cut latency' }).waitFor();
    assert.deepEqual(app.gateway.state.priorities.map((value) => value.outcome), ['Cut latency', 'Hire two engineers']);

    const other = item('Hire two engineers');
    await other.getByRole('button', { name: 'Edit: Hire two engineers' }).click();
    const edit = other.getByRole('form', { name: 'Edit Priority: Hire two engineers' });
    await edit.getByLabel('Notes').fill('Backend first');
    // Simulate another tab saving first: change the server copy and bump its
    // revision while the form is open. Save must then fail with a conflict.
    app.gateway.state.priorities = app.gateway.state.priorities.map((value) =>
      value.outcome === 'Hire two engineers' ? { ...value, notes: 'Changed elsewhere', revision: value.revision + 1 } : value);
    await edit.getByRole('button', { name: 'Save' }).click();
    assert.equal(await other.getByRole('alert').innerText(), 'This Priority changed. Review the current values and try again.');
    // The user's draft is kept so they do not lose their typing.
    assert.equal(await other.getByLabel('Notes').inputValue(), 'Backend first');

    // Priorities are planning data only. No edit above may start, change, or decide run work.
    const execution = app.gateway.requests.filter((request) =>
      /\/runs(\/|$)/.test(request.url.pathname) && request.method !== 'GET' || /\/(decisions|gates|approve|reject|answer)/.test(request.url.pathname));
    assert.deepEqual(execution.map((request) => `${request.method} ${request.url.pathname}`), [], 'Priority edits call no run or gate endpoint');
  } finally {
    await app.close();
  }
});

test('flow: groups by state, keeps dismissed gates, hides promotions, and pins the gate version', async () => {
  // One run per run state, so every Flow column and every Done label appears.
  // n1 is a dismissed gate that Flow must still show. n2 is a backlog
  // promotion, which is not a question and must not appear as Needs input.
  const app = await openApp({
    origin: server.origin,
    seed: {
      runs: {
        p1: [
          run('r-idle', 'idle', 'Draft the plan'),
          run('r-blocked', 'blocked', 'Pick a branch'),
          run('r-active', 'active', 'Write tests'),
          run('r-done', 'completed', 'Ship docs'),
          run('r-failed', 'failed', 'Build image'),
          run('r-cancelled', 'cancelled', 'Old idea'),
          run('r-unknown', 'indeterminate', 'Lost worker'),
        ],
      },
      notifications: [
        notification('n1', { dismissed: true }),
        notification('n2', {
          source: 'backlogPromoted', promotionId: 'promo-1', taskCount: 3,
          requestId: null, kind: null, gateKind: null, stateVersion: null, prompt: 'Promoted 3 tasks',
        }),
      ],
    },
  });
  try {
    const { page } = app;
    await page.goto(`${server.origin}/projects/p1/flow`);
    // The dismissed gate still shows on Flow, because the run is still blocked on it.
    await page.getByText('Which branch?').waitFor();
    assert.equal(await page.getByText('Promoted 3 tasks').count(), 0, 'promotions are not Needs input');
    const region = (name) => page.getByRole('region', { name, exact: true });
    await region('Open').getByRole('link', { name: 'Open run Draft the plan' }).waitFor();
    // A blocked run is still in progress, so it belongs in Working.
    await region('Working').getByRole('link', { name: 'Open run Write tests' }).waitFor();
    await region('Working').getByRole('link', { name: 'Open run Pick a branch' }).waitFor();
    // All finished states share Done, each with its own label.
    const done = region('Done');
    for (const label of ['Done', 'Failed', 'Cancelled', 'Outcome unknown']) await done.getByText(label, { exact: true }).first().waitFor();
    // No pull request data is seeded, so the app must say "Unavailable" and never guess a state.
    assert.equal(await page.getByText('Pull request: Unavailable', { exact: true }).count(), 3);
    // The Flow run groups do not infer pull request state from execution state.
    assert.equal(await page.getByText(/Merged|Pull request: Open/).count(), 0);
    // The bell reads the caller inbox without dismissed items; Flow reads this project with them.
    await page.getByRole('button', { name: /^Notifications/ }).waitFor();
    const reads = app.gateway.requests.filter((value) => value.url.pathname === '/api/v1/notifications');
    for (const request of reads) {
      const flow = request.url.searchParams.get('projectId') === 'p1';
      assert.equal(request.url.searchParams.get('includeDismissed') === 'true', flow);
      assert.equal(request.headers.get('authorization'), 'Bearer tok.unbound');
    }
    assert.ok(reads.some((request) => request.url.searchParams.get('projectId') === 'p1'));
    assert.ok(reads.some((request) => request.url.searchParams.get('projectId') === null));
    await shot('pr-flow-gateway-adapter', page.locator('main'));

    // The decision view rereads this pinned version before acting; DecisionView.test.tsx covers the no-POST path.
    // Opening the gate must carry its request id and state version in the URL,
    // so the decision view acts on exactly the version the user saw.
    await page.getByRole('listitem').filter({ hasText: 'Which branch?' }).first().click();
    await page.waitForURL(/\/projects\/p1\/runs\/r-blocked\?/);
    const url = new URL(page.url());
    assert.equal(url.searchParams.get('requestId'), 'req-n1');
    assert.equal(url.searchParams.get('stateVersion'), '4');
  } finally {
    await app.close();
  }
});

test('permission loss: unavailable conversations and links show no project content', async () => {
  // The server marks items "unavailable" and blanks their project fields when
  // the caller lost access. The app must not fill the gap from cached data.
  const app = await openApp({
    origin: server.origin,
    seed: {
      conversations: [{
        conversationId: 'conv-gone', access: 'unavailable', projectId: null, runId: null, rootSessionId: null,
        title: null, requestState: null, runState: null, createdAt: '2026-10-20T12:00:00.000Z',
      }],
      priorities: [priority('pr-1', 'Cut latency', {
        links: [{ linkId: 'l1', kind: 'run', access: 'unavailable', projectId: null, runId: null, state: null, createdAt: '2026-10-20T12:00:00.000Z' }],
      })],
    },
  });
  try {
    const { page } = app;
    await page.goto(`${server.origin}/overview`);
    const list = page.getByRole('list', { name: 'My conversations' });
    await list.getByText('Unavailable').first().waitFor();
    // No open button and no project name: the user cannot reach or learn about the project.
    assert.equal(await list.getByRole('button').count(), 0);
    assert.equal(await list.getByText('Payments').count(), 0);
    const row = page.getByRole('listitem', { name: 'Priority 1: Cut latency' });
    await row.getByText('Unavailable').first().waitFor();
    assert.equal(await row.getByText(/p1|Payments/).count(), 0);
  } finally {
    await app.close();
  }
});

test('stale gate: the decision view rereads the gate and sends nothing when its version changed', async () => {
  // The notification and the run room agree on version 4 at first, as when the user opens the gate.
  const room = runRoom({ projectId: 'p1', runId: 'r-blocked', gate: { requestId: 'req-n1', stateVersion: 4, prompt: 'Deploy to staging?' } });
  const app = await openApp({
    origin: server.origin,
    seed: {
      runs: { p1: [run('r-blocked', 'blocked', 'Deploy')] },
      notifications: [notification('n1', {
        sessionId: room.rootSessionId, kind: 'awaitingApproval', gateKind: 'approval', prompt: 'Deploy to staging?', stateVersion: 4,
      })],
    },
    extra: (method, path, url, binding) => room.handle(method, path, url, binding),
  });
  try {
    const { page } = app;
    await page.goto(`${server.origin}/projects/p1/flow`);
    const needsInput = page.getByRole('region', { name: 'Needs input', exact: true });
    await needsInput.getByText('Deploy to staging?').click();
    await page.waitForURL(/\/projects\/p1\/runs\/r-blocked\?view=approvals&requestId=req-n1&stateVersion=4$/);
    const approve = page.getByTestId('approval-gate-approve-btn');
    await approve.waitFor();
    // Wait for the bell too, so the page has finished loading before the gate changes.
    await page.getByRole('button', { name: /^Notifications/ }).waitFor();

    // Another actor changes the gate after the page loaded: same request, newer state version.
    room.decisions = { ...room.decisions, stateVersion: 6 };
    // Count decision reads so far, to check that clicking Approve causes a fresh read.
    const readsBefore = app.gateway.requests.filter((request) => /\/decisions$/.test(request.url.pathname)).length;
    await approve.click();
    await page.getByText('This request changed since you opened it. Nothing was sent. Review it and try again.').waitFor();
    const rereads = app.gateway.requests.filter((request) => /\/decisions$/.test(request.url.pathname)).slice(readsBefore);
    assert.ok(rereads.length >= 1, 'the decision view reread the gate');
    // Rereads are run reads, so they must use the run-bound token.
    for (const request of rereads) assert.equal(request.headers.get('authorization'), 'Bearer tok.p1.r-blocked');
    assert.deepEqual(room.writes, [], 'no decision was sent');
    assert.equal(writes(app, 'POST', /\/decisions\/gates\//).length, 0);
  } finally {
    await app.close();
  }
});

test('follow-up: a chat reopens from Sessions with its messages and sends one message per draft', async () => {
  // The run id matches the one the fake Gateway gives the first created run,
  // so the chat started below lands in this room.
  const room = runRoom({
    projectId: 'p2', runId: 'run-1',
    turns: [
      { role: 'user', text: 'Fix the flaky search test' },
      { role: 'assistant', text: 'The retry budget is too low. I raised it to three.' },
    ],
  });
  const app = await openApp({
    origin: server.origin,
    seed: { projects: [project('p1', 'Payments'), project('p2', 'Search')] },
    extra: (method, path, url, binding) => room.handle(method, path, url, binding),
  });
  try {
    const { page } = app;
    await page.goto(`${server.origin}/overview`);
    await page.getByRole('heading', { name: 'Overview' }).waitFor();
    await page.getByLabel('Project', { exact: true }).selectOption('p2');
    await page.getByLabel('Message', { exact: true }).fill('Fix the flaky search test');
    await page.getByRole('button', { name: 'Send' }).click();
    await page.waitForURL(/\/projects\/p2\/runs\/run-1$/);

    // Reopen the chat from the Sessions list, as a user returning later would.
    await page.goto(`${server.origin}/sessions`);
    await page.getByRole('button', { name: 'Open Fix the flaky search test' }).click();
    await page.waitForURL(/\/projects\/p2\/runs\/run-1$/);
    const history = page.getByRole('list', { name: 'Messages' });
    await history.getByText('The retry budget is too low. I raised it to three.').waitFor();
    await history.getByText('Fix the flaky search test').waitFor();
    await shot('pr-conversation-transcript-gateway-adapter', history);
    // Message text comes from per-turn reads, one per turn, with the run-bound token.
    const turnReads = app.gateway.requests.filter((request) => /\/sessions\/[^/]+\/turns\/[^/]+$/.test(request.url.pathname));
    assert.ok(turnReads.length >= 2);
    for (const request of turnReads) assert.equal(request.headers.get('authorization'), 'Bearer tok.p2.run-1');
    await page.getByLabel('Message', { exact: true }).fill('Also check the retry budget');
    // A double click must still send one message for this one draft.
    await page.getByRole('button', { name: 'Send' }).dblclick();
    await page.getByText('Message sent. It shows in the activity list when the agent receives it.').waitFor();

    const messages = writes(app, 'POST', /\/projects\/p2\/runs\/run-1\/coordination\/sessions\/[^/]+\/messages$/);
    assert.equal(messages.length, 1, 'one message for one draft');
    assert.equal(messages[0].headers.get('authorization'), 'Bearer tok.p2.run-1');
    const { body } = messages[0];
    // The message goes to the coordinator root and carries an idempotency key,
    // so a retry of the same draft cannot deliver it twice.
    assert.equal(body.recipientSessionId, room.rootSessionId);
    assert.equal(body.payload.text, 'Also check the retry budget');
    assert.ok(body.idempotencyKey);
    assert.equal(writes(app, 'POST', /^\/api\/v1\/projects\/p2\/runs$/).length, 1, 'follow-up creates no new run');
    assert.equal(app.gateway.state.conversations.length, 1);

    // Permission loss: the owner refuses turn reads, so no message text renders.
    // Reload so the page cannot reuse text it already loaded.
    room.revoked = true;
    await page.reload();
    await page.getByText('You can no longer read this run. Messages are hidden.').waitFor();
    assert.equal(await page.getByText('The retry budget is too low. I raised it to three.').count(), 0);
    assert.equal(await page.getByRole('list', { name: 'Messages' }).count(), 0);
  } finally {
    await app.close();
  }
});
