import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { UiHarnessClient } from '../lib/client.mjs';

const origin = 'https://controlled-web.test';
const gateway = 'https://controlled-gateway.test';
const identity = { issuer: 'https://controlled-broker.test/', audience: 'gateway', actorId: 'controlled-actor' };
const binding = { projectId: 'controlled-project', runId: 'controlled-run' };
const root = '/api/v1/projects/controlled-project/runs/controlled-run';
const session = `${root}/coordination/sessions/controlled-session`;

function brokerToken(overrides = {}) {
  return ['eyJhbGciOiJub25lIn0', Buffer.from(JSON.stringify({
    iss: identity.issuer, aud: identity.audience, sub: identity.actorId,
    exp: Math.floor(Date.now() / 1000) + 3600, ...overrides,
  })).toString('base64url'), 'controlled-signature'].join('.');
}

function event(id, position) {
  return { eventId: id, position, identity: { ...binding, sessionId: 'controlled-session' },
    kind: 'message.accepted', objectReferences: [{ id: 'opaque-object-reference' }] };
}

// This page is a controlled adapter contract, not a browser or a live Gateway.
class ControlledPage extends EventEmitter {
  constructor() {
    super();
    this.location = `${origin}/`;
    this.waiters = [];
    this.actions = [];
    this.tenant = 'controlled-tenant';
    this.token = brokerToken();
    this.pendingGate = { requestId: 'controlled-request', kind: 'question',
      authorizedActorId: identity.actorId, fence: 7, allowedChoices: ['Continue'], allowsFreeform: true };
    this.blocker = { requestId: this.pendingGate.requestId, kind: 'awaitingInput',
      choices: ['Continue'], allowsFreeform: true };
    this.articleCount = 1;
    this.version = 4;
    this.fence = 7;
    this.statusCode = 200;
    this.accepted = true;
    this.ownerBody = null;
    this.key = 'controlled-idempotency-1';
    this.nodes = ['controlled-session', 'controlled-recipient'].map(sessionId => ({
      identity: { ...binding, sessionId }, lifecycle: 'active',
    }));
    this.pages = [
      { events: [event('event-1', 1)], hasMore: true, nextCursor: 'cursor-1' },
      { events: [event('event-1', 1), event('event-2', 2)], hasMore: false, nextCursor: 'cursor-2' },
    ];
  }

  url() { return this.location; }
  async goto(url) { this.location = url; }
  async reload() {
    let cursor;
    for (const page of this.pages) {
      const query = new URLSearchParams({ limit: '100', ...(cursor ? { cursor } : {}) });
      await this.reply(`${root}/events?${query}`, page);
      cursor = page.nextCursor;
    }
    await this.afterReload?.();
  }

  waitForResponse(predicate) {
    return new Promise((resolve, reject) => this.waiters.push({ predicate, resolve, reject }));
  }

  async reply(path, body, { method = 'GET', requestBody = null, status = 200 } = {}) {
    const request = {
      url: () => `${gateway}${path}`, method: () => method,
      allHeaders: async () => ({ authorization: `Bearer ${this.token}`, 'x-agentweaver-tenant': this.tenant }),
      postData: () => requestBody === null ? null : JSON.stringify(requestBody),
    };
    const response = {
      url: request.url, request: () => request, status: () => status,
      allHeaders: async () => ({ 'content-type': 'application/json', 'x-correlation-id': 'actual-controlled-trace' }),
      text: async () => JSON.stringify(body),
    };
    this.emit('response', response);
    for (const waiter of [...this.waiters]) {
      try {
        if (await waiter.predicate(response)) {
          this.waiters.splice(this.waiters.indexOf(waiter), 1);
          waiter.resolve(response);
        }
      } catch (error) {
        this.waiters.splice(this.waiters.indexOf(waiter), 1);
        waiter.reject(error);
      }
    }
  }

  async refresh() {
    await this.reply(`${root}/coordination/status`, { ...binding, rootSessionId: 'controlled-session', executionFence: 7 });
    await this.reply(`${session}/status`, {
      identity: { ...binding, sessionId: 'controlled-session' }, executionFence: this.fence,
      blockers: [this.blocker],
    });
    await this.reply(`${session}/decisions`, {
      pendingGate: this.pendingGate, stateVersion: this.version, executionFence: this.fence,
    });
    await this.reply(`${session}/tree`, { rootSessionId: 'controlled-session', nodes: this.nodes });
    await this.afterRefresh?.();
  }

  getByRole(role, options) { return this.control(`${role}:${options.name}`); }
  getByLabel(name) { return this.control(`label:${name}`); }
  getByText(text) { return { exactText: text }; }
  locator(selector) { return this.control(selector); }

  control(name) {
    const page = this;
    return {
      getByRole(role, options) { return page.control(`${role}:${options.name}`); },
      getByLabel(label) { return page.getByLabel(label); },
      filter(filter) {
        if (filter.has?.exactText) page.actions.push(['exact-request', filter.has.exactText]);
        return this;
      },
      async count() { await page.afterDom?.(); return page.articleCount; },
      async waitFor() { await page.afterDom?.(); },
      async allTextContents() {
        await page.afterDom?.();
        if (page.dom) return page.dom;
        const events = new Map(page.pages.flatMap(item => item.events).map(item => [item.eventId, item]));
        return [...events.values()].sort((left, right) => right.position - left.position)
          .map(item => `${item.identity.sessionId} ${item.eventId} Position ${item.position}`);
      },
      async fill(value) {
        page.actions.push(['fill', name, value]); page.values ??= {}; page.values[name] = value;
        await page.afterDom?.();
      },
      async selectOption(value) { page.actions.push(['select', name, value]); page.values ??= {}; page.values[name] = value; },
      async click() {
        page.actions.push(['click', name]);
        if (name === 'button:Refresh owner snapshots') return page.refresh();
        if (name === 'button:Continue') { page.choice = 'Continue'; return; }
        const action = { 'button:Answer exact request': 'answer', 'button:Approve': 'approve', 'button:Deny': 'reject' }[name];
        if (action) {
          const body = { expectedStateVersion: page.sentVersion ?? page.version, idempotencyKey: page.key };
          if (action === 'answer') Object.assign(body, { choiceId: page.choice ?? null,
            freeformAnswer: page.choice ? null : page.values?.['label:Freeform answer'] ?? null });
          return page.reply(`${session}/decisions/gates/controlled-request/${action}`,
            page.ownerBody ?? { accepted: page.accepted, decisionId: 'actual-controlled-decision',
              executionFence: page.fence, stateVersion: page.version + 1, issues: [], token: 'credential-canary-ui' },
            { method: 'POST', requestBody: body, status: page.statusCode });
        }
        if (name === 'button:Send immediately') {
          return page.reply(`${session}/messages`,
            { ownerMessageId: 'actual-owner-message', recipientSessionId: 'controlled-recipient', status: 'accepted' },
            { method: 'POST', status: page.statusCode, requestBody: {
              recipientSessionId: page.values['label:To session'], idempotencyKey: page.key,
              deliveryMode: 'immediate', purpose: 'progress', kind: 'text',
              payload: { text: page.values['label:Message'].trim() },
            } });
        }
      },
    };
  }
}

async function fixture() {
  const page = new ControlledPage();
  const client = new UiHarnessClient({ page, baseUrl: `${origin}/`, gatewayUrl: `${gateway}/api/v1`,
    tenantId: page.tenant, ...identity });
  await client.openRun(binding);
  return { client, page };
}

const gateInput = { sessionId: 'controlled-session', requestId: 'controlled-request' };

test('uses the current exact gate and actual owner receipt, not browser approval text', async () => {
  const { client, page } = await fixture();
  try {
    const call = await client.gate({ ...gateInput, action: 'answer', answer: 'Continue' });
    assert.equal(call.traceId, 'actual-controlled-trace');
    assert.equal(call.transientRequestBody.expectedStateVersion, 4);
    assert.equal(call.requestBody.choiceId, 'Continue');
    assert.equal(call.responseBody.accepted, true);
    assert.equal(call.responseBody.token, '[REDACTED]');
    assert.doesNotMatch(JSON.stringify(client.calls), /credential-canary-ui|controlled-signature/);
    assert.equal(Object.keys(call).includes('transientResponseBody'), false);
    assert.ok(page.actions.some(item => item[0] === 'exact-request' && item[1] === gateInput.requestId));
  } finally {
    await client.close();
  }
  assert.equal(page.listenerCount('response'), 0);
  assert.equal(page.listenerCount('requestfailed'), 0);
  assert.equal(page.url(), `${origin}/projects/controlled-project/runs/controlled-run`);
});

test('freeform answers, all current approval-kind mappings, and Deny use their exact body and action', async () => {
  const { client, page } = await fixture();
  try {
    assert.deepEqual((await client.gate({ ...gateInput, action: 'answer', answer: 'Bounded answer' })).requestBody,
      { expectedStateVersion: 4, idempotencyKey: page.key, choiceId: null, freeformAnswer: 'Bounded answer' });
    for (const [index, [blockerKind, kind]] of [
      ['awaitingOutcomeConfirmation', 'outcomeConfirmation'],
      ['awaitingPlanApproval', 'generatedWorkflowConfirmation'],
      ['awaitingPlanApproval', 'workPlanConfirmation'],
      ['awaitingPlanApproval', 'scopeChangeConfirmation'],
      ['awaitingApproval', 'approval'],
    ].entries()) {
      page.blocker.kind = blockerKind;
      page.pendingGate.kind = kind;
      page.key = `controlled-idempotency-${index + 2}`;
      const call = await client.gate({ ...gateInput, action: index === 4 ? 'reject' : 'approve' });
      assert.equal(call.path.endsWith(index === 4 ? '/reject' : '/approve'), true);
      assert.deepEqual(Object.keys(call.requestBody).sort(), ['expectedStateVersion', 'idempotencyKey']);
    }
  } finally { await client.close(); }
});

test('missing/stale gates, different actor/fence, ambiguous DOM and disallowed answers never submit a POST', async () => {
  for (const mutate of [
    page => { page.pendingGate.requestId = 'replacement-request'; },
    page => { page.pendingGate.authorizedActorId = 'foreign-actor'; },
    page => { page.fence = 8; },
    page => { page.articleCount = 2; },
    page => { page.blocker.allowsFreeform = false; page.pendingGate.allowedChoices = []; },
  ]) {
    const { client, page } = await fixture();
    mutate(page);
    try {
      await assert.rejects(client.gate({ ...gateInput, action: 'answer', answer: 'Continue' }));
      assert.equal(client.calls.some(call => call.method === 'POST'), false);
    } finally { await client.close(); }
  }
});

test('actual 409 and owner accepted=false are denials, not completed gates; stale UI version fails explicitly', async () => {
  const { client, page } = await fixture();
  try {
    page.statusCode = 409;
    page.ownerBody = { code: 'gate_state_changed' };
    assert.equal((await client.gate({ ...gateInput, action: 'answer', answer: 'Continue' })).status, 409);
    page.statusCode = 200;
    page.key = 'controlled-idempotency-2';
    page.ownerBody = { accepted: false, decisionId: 'actual-controlled-decision', executionFence: 7,
      stateVersion: 5, issues: [{ code: 'denied', path: 'request', message: 'The owner denied this request.' }] };
    assert.equal((await client.gate({ ...gateInput, action: 'answer', answer: 'Continue' })).responseBody.accepted, false);
    page.key = 'controlled-idempotency-3';
    page.sentVersion = 3;
    await assert.rejects(client.gate({ ...gateInput, action: 'answer', answer: 'Continue' }), /exact state-version/);
    page.sentVersion = undefined;
    page.key = 'controlled-idempotency-2';
    await assert.rejects(client.gate({ ...gateInput, action: 'answer', answer: 'Continue' }), /fresh idempotency/);
  } finally { await client.close(); }
});

test('wrong tenant, actor and expired broker metadata fail without retaining credentials', async () => {
  for (const mutate of [
    page => { page.tenant = 'foreign-tenant'; },
    page => { page.token = brokerToken({ sub: 'foreign-actor' }); },
    page => { page.token = brokerToken({ exp: 0 }); },
  ]) {
    const { client, page } = await fixture();
    mutate(page);
    await assert.rejects(client.gate({ ...gateInput, action: 'answer', answer: 'Continue' }), /network evidence failed/);
    assert.equal(client.calls.some(call => call.method === 'POST'), false);
    assert.doesNotMatch(JSON.stringify(client.calls), /controlled-signature/);
    await assert.rejects(client.close(), /network evidence failed/);
    assert.equal(page.listenerCount('response'), 0);
  }
});

test('messages use two existing active owner sessions and preserve accepted-not-delivered evidence', async () => {
  const { client, page } = await fixture();
  const input = { rootSessionId: 'controlled-session', senderSessionId: 'controlled-session',
    recipientSessionId: 'controlled-recipient', text: ' Bounded message ' };
  try {
    const call = await client.sendMessage(input);
    assert.equal(call.requestBody.payload.text, 'Bounded message');
    assert.equal(call.responseBody.status, 'accepted');
    assert.equal(call.responseBody.ownerMessageId, 'actual-owner-message');
    page.nodes[1].lifecycle = 'completed';
    const posts = client.calls.filter(call => call.method === 'POST').length;
    await assert.rejects(client.sendMessage(input), /already exist and be active/);
    assert.equal(client.calls.filter(call => call.method === 'POST').length, posts);
    await assert.rejects(client.sendMessage({ ...input, recipientSessionId: input.senderSessionId }), /different/);
  } finally { await client.close(); }
});

test('replay correlates actual cursor pages with deduplicated reverse DOM order, without inventing transcript content', async () => {
  const { client } = await fixture();
  try {
    const proof = await client.replayJournal();
    assert.deepEqual(proof.events.map(item => item.eventId), ['event-2', 'event-1']);
    assert.equal(proof.nextCursor, 'cursor-2');
    assert.equal(proof.transcriptContentAvailable, false);
  } finally { await client.close(); }
});

test('foreign/conflicting replay events, repeated cursors, and wrong DOM order fail explicitly', async () => {
  for (const mutate of [
    page => { page.pages[1].events[1].identity.runId = 'foreign-run'; },
    page => { page.pages[1].events[0].kind = 'changed-event'; },
    page => { page.pages[1].nextCursor = 'cursor-1'; },
    page => { page.dom = ['controlled-session event-1 Position 1', 'controlled-session event-2 Position 2']; },
    page => { page.pages[1].events[1].position = 1; },
  ]) {
    const { client, page } = await fixture();
    mutate(page);
    try { await assert.rejects(client.replayJournal()); }
    finally { await client.close(); }
  }
});

test('the adapter rejects a foreign borrowed page and records request transport failures', async () => {
  const page = new ControlledPage();
  page.location = 'https://foreign.test/';
  assert.throws(() => new UiHarnessClient({ page, baseUrl: `${origin}/`, gatewayUrl: `${gateway}/api/v1`,
    tenantId: page.tenant, ...identity }), /injected page/);
  const { client, page: authorized } = await fixture();
  authorized.emit('requestfailed', { url: () => `${gateway}${root}/events/live`,
    method: () => 'GET', failure: () => ({ errorText: 'controlled connection failure' }) });
  await assert.rejects(client.flush(), /network evidence failed/);
  assert.match(JSON.stringify(client.calls), /controlled connection failure/);
  await assert.rejects(client.close(), /network evidence failed/);
});

test('a user navigation to a different run stops actions; redirected responses remain failed evidence', async () => {
  const { client, page } = await fixture();
  page.location = `${origin}/projects/controlled-project/runs/other-run`;
  await assert.rejects(client.gate({ ...gateInput, action: 'answer', answer: 'Continue' }), /exact selected/);
  assert.deepEqual(page.actions, []);
  const original = { url: () => `${gateway}${root}/coordination/status`, redirectedFrom: () => null };
  const redirected = { url: () => 'https://outside.test/redirected', method: () => 'GET',
    redirectedFrom: () => original };
  const record = await client.capture({
    url: redirected.url, status: () => 200, request: () => redirected,
  });
  assert.match(record.error.message, /redirect/);
  await assert.rejects(client.close(), /network evidence failed/);
  assert.equal(page.listenerCount('response'), 0);
});

test('a success-shaped acceptance flag without the actual typed owner receipt fails explicitly', async () => {
  for (const body of [
    { accepted: true },
    { accepted: false, decisionId: 'decision', executionFence: 7, stateVersion: 4, issues: [{ code: 'denied' }] },
    { accepted: true, decisionId: 'decision', executionFence: 7, stateVersion: -1, issues: [] },
  ]) {
    const { client, page } = await fixture();
    page.ownerBody = body;
    try {
      await assert.rejects(client.gate({ ...gateInput, action: 'answer', answer: 'Continue' }), /decision-receipt contract/);
      assert.deepEqual(client.calls.find(call => call.method === 'POST').responseBody, body);
    } finally { await client.close(); }
  }
});

test('gate receipts must match the snapshot fence and next state version, including owner denials', async () => {
  for (const accepted of [true, false]) {
    for (const mismatch of [
      { executionFence: 8, stateVersion: 5 },
      { executionFence: 7, stateVersion: 3 },
      { executionFence: 7, stateVersion: 4 },
      { executionFence: 7, stateVersion: 6 },
    ]) {
      const { client, page } = await fixture();
      page.ownerBody = { accepted, decisionId: 'actual-controlled-decision', issues: [], ...mismatch };
      try {
        await assert.rejects(client.gate({ ...gateInput, action: 'answer', answer: 'Continue' }),
          /exact fence and next state version/);
        assert.deepEqual(client.calls.find(call => call.method === 'POST').responseBody, page.ownerBody);
      } finally { await client.close(); }
    }
  }
});

test('navigation during snapshot or DOM waits stops gate and message actions before their final POST', async () => {
  for (const hook of ['afterRefresh', 'afterDom']) {
    for (const action of ['gate', 'message']) {
      const { client, page } = await fixture();
      page[hook] = () => { page.location = `${origin}/projects/controlled-project/runs/other-run`; };
      try {
        const operation = action === 'gate'
          ? client.gate({ ...gateInput, action: 'answer', answer: 'Continue' })
          : client.sendMessage({ rootSessionId: 'controlled-session', senderSessionId: 'controlled-session',
            recipientSessionId: 'controlled-recipient', text: 'Bounded message' });
        await assert.rejects(operation, /exact selected project and run/);
        assert.equal(client.calls.some(call => call.method === 'POST'), false);
        assert.equal(page.actions.some(item => item[0] === 'click'
          && ['button:Answer exact request', 'button:Send immediately'].includes(item[1])), false);
      } finally { await client.close(); }
    }
  }
});

test('navigation during replay reload or DOM waits cannot return old-run journal proof', async () => {
  for (const hook of ['afterReload', 'afterDom']) {
    const { client, page } = await fixture();
    page[hook] = () => { page.location = `${origin}/projects/controlled-project/runs/other-run`; };
    try {
      await assert.rejects(client.replayJournal(), /exact selected project and run/);
      assert.equal(client.calls.some(call => call.method === 'POST'), false);
    } finally { await client.close(); }
  }
});
