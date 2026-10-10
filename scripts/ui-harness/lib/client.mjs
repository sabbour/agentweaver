import { createBrokerAuthProvider } from '../../harness-shared/broker-auth.mjs';
import { redact } from '../../harness-shared/redaction.mjs';
import { validateNetworkTarget } from '../../harness-shared/target-guard.mjs';

const VIEWS = new Set(['Topology', 'Chat', 'Outcomes & approvals', 'Activity', 'Selection', 'Usage']);
const GATE_KINDS = new Map([
  ['awaitingInput', ['question']],
  ['awaitingOutcomeConfirmation', ['outcomeConfirmation']],
  ['awaitingPlanApproval', ['generatedWorkflowConfirmation', 'workPlanConfirmation', 'scopeChangeConfirmation']],
  ['awaitingApproval', ['approval']],
]);

function segment(value) {
  if (typeof value !== 'string' || !value.trim() || value === '.' || value === '..') {
    throw new Error('An exact owner identifier is required.');
  }
  return encodeURIComponent(value);
}

export class UiHarnessClient {
  constructor({ page, baseUrl, gatewayUrl, tenantId, issuer, audience, actorId }) {
    this.target = validateNetworkTarget(baseUrl, { exactPath: '/' });
    this.gateway = validateNetworkTarget(gatewayUrl, { exactPath: '/api/v1' });
    if (typeof page?.waitForResponse !== 'function' || typeof page?.on !== 'function'
      || validateNetworkTarget(page.url()).origin !== this.target.origin) {
      throw new Error('An already authorized, injected page on the exact web origin is required.');
    }
    if (![tenantId, issuer, audience, actorId].every(value => typeof value === 'string' && value.trim())) {
      throw new Error('Explicit broker identity and tenant metadata are required.');
    }
    this.page = page;
    this.tenantId = tenantId;
    this.identity = { issuer, audience, actorId };
    this.calls = [];
    this.responses = new WeakMap();
    this.pending = new Set();
    this.failures = [];
    this.idempotencyKeys = new Set();
    this.onResponse = response => {
      const pending = this.capture(response);
      this.pending.add(pending);
      pending.finally(() => this.pending.delete(pending));
    };
    this.onFailedRequest = request => {
      if (!this.matches(request.url())) return;
      const failure = redact({ path: new URL(request.url()).pathname, error: request.failure() });
      this.failures.push(failure);
      this.calls.push({ method: request.method(), ...failure });
    };
    page.on('response', this.onResponse);
    page.on('requestfailed', this.onFailedRequest);
  }

  matches(url) {
    const target = new URL(url);
    return target.origin === this.gateway.origin && target.pathname.startsWith('/api/v1/');
  }

  capture(response) {
    let original = response.request();
    while (original.redirectedFrom?.()) original = original.redirectedFrom();
    if (!this.matches(response.url()) && !this.matches(original.url())) return Promise.resolve(null);
    if (this.responses.has(response)) return this.responses.get(response);
    const promise = this.captureResponse(response);
    this.responses.set(response, promise);
    return promise;
  }

  async captureResponse(response) {
    const request = response.request();
    const url = new URL(request.url());
    const record = {
      method: request.method(), path: url.pathname,
      query: redact(Object.fromEntries(url.searchParams)), status: response.status(),
      requestBody: null, responseBody: null, traceId: null,
    };
    this.calls.push(record);
    try {
      if (!this.matches(request.url()) || request.redirectedFrom?.()) {
        throw new Error('The UI Gateway response followed a redirect; its target was not accepted as owner evidence.');
      }
      const headers = await request.allHeaders();
      if (headers['x-agentweaver-tenant'] !== this.tenantId) {
        throw new Error('The UI request used a different tenant selector.');
      }
      const authorization = headers.authorization;
      if (typeof authorization !== 'string' || !authorization.startsWith('Bearer ')) {
        throw new Error('The UI request did not use its broker bearer identity.');
      }
      const provider = createBrokerAuthProvider({
        target: this.gateway, token: authorization.slice(7), ...this.identity,
      });
      await provider.getAuthorization(url);
      const postData = request.postData();
      const requestBody = postData === null ? null : JSON.parse(postData);
      record.requestBody = redact(requestBody);
      const responseHeaders = await response.allHeaders();
      record.traceId = redact(['traceparent', 'request-id', 'x-request-id', 'x-correlation-id']
        .map(name => responseHeaders[name]).find(Boolean) ?? null);
      if (responseHeaders['content-type']?.includes('text/event-stream')) {
        record.stream = true;
      } else {
        const text = await response.text();
        const body = text && responseHeaders['content-type']?.includes('json') ? JSON.parse(text) : text;
        record.responseBody = redact(body);
        Object.defineProperty(record, 'transientResponseBody', { value: body });
      }
      Object.defineProperty(record, 'transientRequestBody', { value: requestBody });
    } catch (error) {
      record.error = redact(error);
      this.failures.push(record.error);
    }
    return record;
  }

  async flush() {
    while (this.pending.size) await Promise.all([...this.pending]);
    if (this.failures.length) throw new Error('UI network evidence failed; inspect the redacted calls.');
  }

  runPath(...parts) {
    if (!this.binding) throw new Error('Open the exact project and run before taking an action.');
    return `/api/v1/projects/${segment(this.binding.projectId)}/runs/${segment(this.binding.runId)}`
      + (parts.length ? `/${parts.map(segment).join('/')}` : '');
  }

  async openRun({ projectId, runId }) {
    segment(projectId);
    segment(runId);
    this.binding = { projectId, runId };
    await this.page.goto(new URL(`/projects/${segment(projectId)}/runs/${segment(runId)}`, this.target).href,
      { waitUntil: 'domcontentloaded' });
    if (validateNetworkTarget(this.page.url()).origin !== this.target.origin) {
      throw new Error('The injected UI page left its selected web origin; no login was attempted.');
    }
  }

  requirePageScope() {
    const url = validateNetworkTarget(this.page.url());
    if (!this.binding || url.origin !== this.target.origin
      || url.pathname !== `/projects/${segment(this.binding.projectId)}/runs/${segment(this.binding.runId)}`) {
      throw new Error('The injected page is no longer on the exact selected project and run.');
    }
  }

  async selectView(name) {
    if (!VIEWS.has(name)) throw new Error('Unknown P1 run view.');
    this.requirePageScope();
    await this.page.getByRole('tab', { name, exact: true }).click();
  }

  waitFor(path, method = 'GET') {
    return this.page.waitForResponse(response => response.url() === `${this.gateway.origin}${path}`
      && response.request().method() === method, { timeout: 30000 });
  }

  async refresh(paths = []) {
    const runPath = this.runPath('coordination', 'status');
    const responses = [runPath, ...paths].map(path => this.waitFor(path));
    const records = (await Promise.all([
      ...responses, this.page.getByRole('button', { name: 'Refresh owner snapshots', exact: true }).click(),
    ])).slice(0, responses.length);
    const calls = await Promise.all(records.map(response => this.capture(response)));
    await this.flush();
    this.requirePageScope();
    if (calls.some(call => call.status !== 200)) throw new Error('Current owner snapshots are unavailable.');
    const run = calls[0].transientResponseBody;
    if (run?.projectId !== this.binding.projectId || run.runId !== this.binding.runId) {
      throw new Error('The run snapshot does not match the exact UI scope.');
    }
    return calls.map(call => call.transientResponseBody);
  }

  async gate({ sessionId, requestId, action, answer }) {
    if (!['answer', 'approve', 'reject'].includes(action)) throw new Error('Unknown exact-gate action.');
    segment(requestId);
    await this.selectView('Outcomes & approvals');
    const base = this.runPath('coordination', 'sessions', sessionId);
    const [run, status, decisions] = await this.refresh([`${base}/status`, `${base}/decisions`]);
    const blocker = status?.blockers?.find(item => item.requestId === requestId);
    const gate = decisions?.pendingGate;
    if (status?.identity?.projectId !== this.binding.projectId
      || status.identity.runId !== this.binding.runId || status.identity.sessionId !== sessionId
      || !blocker || gate?.requestId !== requestId || !GATE_KINDS.get(blocker.kind)?.includes(gate.kind)
      || gate.authorizedActorId !== this.identity.actorId || gate.fence !== run.executionFence
      || decisions.executionFence !== run.executionFence || status.executionFence !== run.executionFence
      || !Number.isSafeInteger(decisions.stateVersion) || decisions.stateVersion < 0) {
      throw new Error('Current owner snapshots do not authorize this exact gate, actor, and fence.');
    }
    const article = this.page.locator('article.v1-record')
      .filter({ has: this.page.getByText(requestId, { exact: true }) });
    if (await article.count() !== 1) throw new Error('The exact pending request is missing or ambiguous in the UI.');
    let expectedAnswer;
    let button;
    if (action === 'answer') {
      if (blocker.kind !== 'awaitingInput' || typeof answer !== 'string' || !answer.trim()) {
        throw new Error('This action requires a current input gate and a nonempty answer.');
      }
      if (blocker.choices.includes(answer) && gate.allowedChoices.includes(answer)) {
        await article.getByRole('button', { name: answer, exact: true }).click();
        expectedAnswer = { choiceId: answer, freeformAnswer: null };
      } else if (blocker.allowsFreeform && gate.allowsFreeform && !blocker.choices.includes(answer)) {
        await article.getByLabel('Freeform answer', { exact: true }).fill(answer);
        expectedAnswer = { choiceId: null, freeformAnswer: answer };
      } else {
        throw new Error('The exact gate does not allow this answer.');
      }
      button = article.getByRole('button', { name: 'Answer exact request', exact: true });
    } else {
      if (blocker.kind === 'awaitingInput') throw new Error('An input gate cannot be approved or rejected.');
      button = article.getByRole('region', { name: 'Approval required', exact: true })
        .getByRole('button', { name: action === 'approve' ? 'Approve' : 'Deny', exact: true });
    }
    const path = `${base}/decisions/gates/${segment(requestId)}/${action}`;
    this.requirePageScope();
    const [response] = await Promise.all([this.waitFor(path, 'POST'), button.click()]);
    const call = await this.capture(response);
    await this.flush();
    const body = call.transientRequestBody;
    const expected = { expectedStateVersion: decisions.stateVersion, idempotencyKey: body?.idempotencyKey,
      ...expectedAnswer };
    const reusedKey = this.idempotencyKeys.has(body?.idempotencyKey);
    if (typeof body?.idempotencyKey === 'string') this.idempotencyKeys.add(body.idempotencyKey);
    if (typeof body?.idempotencyKey !== 'string' || !body.idempotencyKey.trim()
      || reusedKey
      || JSON.stringify(Object.keys(body).sort()) !== JSON.stringify(Object.keys(expected).sort())
      || Object.keys(expected).some(key => body[key] !== expected[key])) {
      throw new Error('The UI did not send the exact state-version, answer, and fresh idempotency contract.');
    }
    const receipt = call.transientResponseBody;
    if (call.status >= 200 && call.status < 300 && (typeof receipt?.accepted !== 'boolean'
      || typeof receipt.decisionId !== 'string' || !receipt.decisionId.trim()
      || !Number.isSafeInteger(receipt.stateVersion) || receipt.stateVersion < 0
      || !Number.isSafeInteger(receipt.executionFence) || receipt.executionFence < 1
      || !Array.isArray(receipt.issues) || receipt.issues.some(issue =>
        typeof issue?.code !== 'string' || typeof issue.path !== 'string' || typeof issue.message !== 'string'))) {
      throw new Error('The UI action response does not contain the actual owner decision-receipt contract.');
    }
    if (call.status >= 200 && call.status < 300 && (receipt.executionFence !== run.executionFence
      || receipt.stateVersion !== decisions.stateVersion + 1)) {
      throw new Error('The owner decision receipt does not match the exact fence and next state version.');
    }
    return call;
  }

  async sendMessage({ rootSessionId, senderSessionId, recipientSessionId, text }) {
    if (senderSessionId === recipientSessionId || typeof text !== 'string' || !text.trim()) {
      throw new Error('An addressed message needs different existing sessions and nonempty text.');
    }
    await this.selectView('Chat');
    const [run, tree] = await this.refresh([
      this.runPath('coordination', 'sessions', rootSessionId, 'tree'),
    ]);
    if (run.rootSessionId !== rootSessionId || tree?.rootSessionId !== rootSessionId
      || !Array.isArray(tree.nodes) || tree.nodes.some(node =>
        node.identity?.projectId !== this.binding.projectId || node.identity.runId !== this.binding.runId)) {
      throw new Error('The current session tree does not match the selected run.');
    }
    for (const id of [senderSessionId, recipientSessionId]) {
      if (tree.nodes.filter(node => node.identity.sessionId === id && node.lifecycle === 'active').length !== 1) {
        throw new Error('Both addressed sessions must already exist and be active in the owner tree.');
      }
    }
    await this.page.getByLabel('From session', { exact: true }).selectOption(senderSessionId);
    await this.page.getByLabel('To session', { exact: true }).selectOption(recipientSessionId);
    await this.page.getByLabel('Message', { exact: true }).fill(text);
    const path = this.runPath('coordination', 'sessions', senderSessionId, 'messages');
    this.requirePageScope();
    const [response] = await Promise.all([
      this.waitFor(path, 'POST'), this.page.getByRole('button', { name: 'Send immediately', exact: true }).click(),
    ]);
    const call = await this.capture(response);
    await this.flush();
    const body = call.transientRequestBody;
    const reusedKey = this.idempotencyKeys.has(body?.idempotencyKey);
    if (typeof body?.idempotencyKey === 'string') this.idempotencyKeys.add(body.idempotencyKey);
    if (body?.recipientSessionId !== recipientSessionId || body.deliveryMode !== 'immediate'
      || body.purpose !== 'progress' || body.kind !== 'text' || body.payload?.text !== text.trim()
      || typeof body.idempotencyKey !== 'string' || !body.idempotencyKey.trim()
      || reusedKey || Object.keys(body).sort().join(',') !== 'deliveryMode,idempotencyKey,kind,payload,purpose,recipientSessionId'
      || Object.keys(body.payload).join(',') !== 'text') {
      throw new Error('The UI did not send the exact addressed-message contract.');
    }
    if (call.status >= 200 && call.status < 300 && (!call.transientResponseBody?.ownerMessageId
      || typeof call.transientResponseBody.ownerMessageId !== 'string'
      || typeof call.transientResponseBody.status !== 'string'
      || call.transientResponseBody.recipientSessionId !== recipientSessionId)) {
      throw new Error('The owner did not return an addressed-message receipt.');
    }
    return call;
  }

  async replayJournal() {
    await this.selectView('Activity');
    const start = this.calls.length;
    const path = this.runPath('events');
    const lastPage = this.page.waitForResponse(async response => {
      const url = new URL(response.url());
      if (url.origin !== this.gateway.origin || url.pathname !== path
        || response.request().method() !== 'GET') return false;
      const call = await this.capture(response);
      if (call.error || call.status !== 200) throw new Error('The UI replay request failed.');
      return call.transientResponseBody?.hasMore === false;
    }, { timeout: 30000 });
    await Promise.all([lastPage, this.page.reload({ waitUntil: 'domcontentloaded' })]);
    await this.flush();
    const pages = this.calls.slice(start).filter(call => call.method === 'GET' && call.path === path);
    const events = new Map();
    const positions = new Map();
    const cursors = new Set();
    let cursor = null;
    for (const [index, call] of pages.entries()) {
      const page = call.transientResponseBody;
      if ((call.query.cursor ?? null) !== cursor || !Array.isArray(page?.events)
        || typeof page.hasMore !== 'boolean' || page.hasMore !== (index < pages.length - 1)
        || page.nextCursor != null && (typeof page.nextCursor !== 'string' || !page.nextCursor)) {
        throw new Error('The actual UI replay pages do not form a complete advancing cursor chain.');
      }
      if (page.nextCursor) {
        if (cursors.has(page.nextCursor)) throw new Error('The actual UI replay cursor repeated.');
        cursors.add(page.nextCursor);
      }
      if (page.hasMore && !page.nextCursor) throw new Error('The UI replay did not advance its cursor.');
      cursor = page.nextCursor ?? cursor;
      for (const event of page.events) {
        if (typeof event.eventId !== 'string' || !event.eventId
          || event.identity?.projectId !== this.binding.projectId || event.identity.runId !== this.binding.runId
          || typeof event.identity.sessionId !== 'string' || !event.identity.sessionId
          || !Number.isSafeInteger(event.position) || event.position < 1
          || events.has(event.eventId) && JSON.stringify(events.get(event.eventId)) !== JSON.stringify(event)
          || positions.has(event.position) && positions.get(event.position) !== event.eventId) {
          throw new Error('The replay contains a foreign scope, invalid position, or conflicting event identity.');
        }
        events.set(event.eventId, event);
        positions.set(event.position, event.eventId);
      }
    }
    const ordered = [...events.values()].sort((left, right) => right.position - left.position);
    if (ordered.length) {
      await this.page.locator('ol.v1-event-list code').filter({ hasText: ordered[0].eventId }).waitFor();
      const visible = await this.page.locator('ol.v1-event-list > li').allTextContents();
      let previousIndex = -1;
      for (const event of ordered) {
        const index = visible.findIndex(text => text.includes(event.eventId)
          && text.includes(event.identity.sessionId) && new RegExp(`\\bPosition ${event.position}\\b`).test(text));
        if (index <= previousIndex || visible.filter(text => text.includes(event.eventId)).length !== 1) {
          throw new Error('The UI did not render each committed replay event once in reverse journal order.');
        }
        previousIndex = index;
      }
    }
    this.requirePageScope();
    return redact({ source: 'Gateway replay DTOs and current journal DOM', events: ordered,
      nextCursor: cursor, transcriptContentAvailable: false });
  }

  async close() {
    this.page.off('response', this.onResponse);
    this.page.off('requestfailed', this.onFailedRequest);
    await this.flush();
  }
}
