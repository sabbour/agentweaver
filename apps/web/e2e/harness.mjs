/**
 * Browser harness for Chief of Staff journeys. Serves the built web app from
 * `apps/web/dist`, fakes the Identity Broker (silent PKCE sign-in) and the
 * Gateway with Playwright route interception, and enforces the token rules the
 * owners enforce: Chief of Staff, notifications, and run-list reads need an
 * unbound token; run start needs a token bound to that run.
 */
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { createFakeGateway } from '../src/v1/__fixtures__/cosGateway.ts';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const BROKER = 'http://broker.test';
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml', '.json': 'application/json' };
const scopes = 'api.read api.write projects.admin';
/** Base64-encodes a value, matching how the deployed `env-config.js` stores settings. */
const encode = (value) => Buffer.from(value).toString('base64');

/** Serves dist with SPA fallback and a runtime config that points sign-in at the fake Broker. */
export async function startStaticServer() {
  // The app reads its settings from `env-config.js` at startup. Serving our own
  // copy points sign-in at the fake Broker instead of a real one.
  const config = `window.__AGENTWEAVER_CONFIG_BASE64__ = ${JSON.stringify({
    IDENTITY_BROKER_URL: encode(BROKER),
    OAUTH_CLIENT_ID: encode('agentweaver-web'),
    OAUTH_SCOPES: encode(scopes),
  })};`;
  const server = createServer(async (request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    if (path === '/env-config.js') {
      response.writeHead(200, { 'content-type': 'text/javascript' }).end(config);
      return;
    }
    const file = normalize(join(dist, decodeURIComponent(path)));
    // Only serve real files that stay inside dist. Paths without an extension
    // are client-side routes, so they get index.html (SPA fallback). This also
    // blocks `..` paths from reading files outside dist.
    const inside = file.startsWith(normalize(dist)) && extname(file);
    try {
      const target = inside ? file : join(dist, 'index.html');
      const body = await readFile(target);
      response.writeHead(200, { 'content-type': types[extname(target)] ?? 'application/octet-stream' }).end(body);
    } catch {
      response.writeHead(404).end();
    }
  });
  // Port 0 asks the OS for a free port, so parallel runs do not collide.
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return { origin: `http://127.0.0.1:${server.address().port}`, close: () => new Promise((resolve) => server.close(resolve)) };
}

/**
 * Builds a fake access token. The token text carries its run binding
 * (`tok.<project>.<run>`) so the fake Gateway can check it without a real
 * signature. An unbound token is `tok.unbound`.
 */
const tokenFor = (binding) => binding ? `tok.${binding.projectId}.${binding.runId}` : 'tok.unbound';
/** Reads the run binding back out of an Authorization header, or null for an unbound or missing token. */
function bindingOf(header) {
  const [, projectId, runId] = (header ?? '').replace(/^Bearer /, '').split('.');
  return projectId && projectId !== 'unbound' ? { projectId, runId } : null;
}

/**
 * Opens a fresh browser on the app with a faked Broker and Gateway.
 * `extra` adds Gateway handlers for one journey: `(method, path, url, binding) => Response | undefined`.
 * Requests that no handler answers are recorded in `unhandled` to help debug missing fakes.
 */
export async function openApp({ seed = {}, extra, origin }) {
  const gateway = createFakeGateway(seed);
  const unhandled = [];
  const browser = await chromium.launch();
  const context = await browser.newContext();
  // This flag tells the app the user signed in before, so it tries a silent
  // sign-in instead of showing the sign-in screen.
  await context.addInitScript(() => window.sessionStorage.setItem('agentweaver.auth.signed-in-before', '1'));

  // Fake Broker. Authorize redirects straight back with a "code" that is the
  // token itself, and the token endpoint echoes it. The code keeps the
  // project/run binding the app asked for, so bound and unbound sign-ins differ.
  await context.route(`${BROKER}/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname === '/connect/authorize') {
      const callback = new URL(url.searchParams.get('redirect_uri'));
      const binding = url.searchParams.get('project_id') ? { projectId: url.searchParams.get('project_id'), runId: url.searchParams.get('run_id') } : null;
      callback.searchParams.set('code', tokenFor(binding));
      callback.searchParams.set('state', url.searchParams.get('state'));
      await route.fulfill({ status: 302, headers: { location: callback.toString() } });
      return;
    }
    if (url.pathname === '/connect/token') {
      const form = new URLSearchParams(request.postData() ?? '');
      // Refresh requests send the old token as `refresh_token`; reuse it so the binding is kept.
      const token = form.get('code') ?? form.get('refresh_token') ?? 'tok.unbound';
      // The app calls the token endpoint with fetch from another origin, so it needs CORS headers.
      await route.fulfill({
        status: 200,
        headers: { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true', 'content-type': 'application/json' },
        body: JSON.stringify({ access_token: token, token_type: 'Bearer', expires_in: 3600, refresh_token: token, scope: scopes }),
      });
      return;
    }
    await route.fulfill({ status: 404, headers: { 'access-control-allow-origin': origin }, body: '{}' });
  });

  // Fake Gateway. Checks token binding first, then answers authorization,
  // project reads, journey-specific routes, and finally the shared fake.
  await context.route(`${origin}/api/v1/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const path = url.pathname.replace(/^\/api\/v1/, '');
    const binding = bindingOf(request.headers().authorization);
    const reply = async (response) => route.fulfill({
      status: response.status,
      headers: Object.fromEntries(response.headers.entries()),
      body: response.status === 204 ? '' : await response.text(),
    });
    const json = (status, body) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
    // Same rule as the real owners: caller-wide reads (Chief of Staff,
    // notifications, project list, run list) refuse a run-bound token. This
    // catches the app sending the wrong token for a request.
    const needsUnbound = path.startsWith('/chief-of-staff/') || path === '/notifications' || path.startsWith('/notifications/')
      || path === '/projects' || (method === 'GET' && /^\/projects\/[^/]+\/(?:runs|source-control\/(?:issues|pull-requests))$/.test(path));
    if (needsUnbound && binding) return reply(json(403, { error: 'caller_binding_mismatch' }));
    // Starting a run needs a token bound to exactly that project and run.
    const start = path.match(/^\/projects\/([^/]+)\/runs\/([^/]+)\/start$/);
    if (start && (binding?.projectId !== start[1] || binding?.runId !== start[2])) return reply(json(403, { error: 'caller_binding_mismatch' }));
    // Grant full project permissions on every seeded project, and report the
    // token's binding so the app can tell which token it holds.
    if (path === '/authorization/context') {
      return reply(json(200, {
        contractVersion: 1, issuer: `${BROKER}/`, actorId: 'me', tenantId: 'tenant-1', membershipRevision: 1,
        boundProjectId: binding?.projectId ?? null, boundRunId: binding?.runId ?? null,
        effectiveAuthority: gateway.state.projects.map((project) => ({
          resourceType: 'project', resourceId: project.projectId,
          permissions: ['readProjects', 'writeProjects', 'readRunSelection', 'acceptRunSelection'].map((permission) => ({ permission, roleRevision: 1 })),
        })),
      }));
    }
    const projectRead = method === 'GET' && path.match(/^\/projects\/([^/]+)$/);
    if (projectRead) {
      const project = gateway.state.projects.find((item) => item.projectId === decodeURIComponent(projectRead[1]));
      return reply(project ? json(200, project) : json(404, { error: 'project_not_found' }));
    }
    // Journey-specific handlers win over the shared fake. Record their
    // requests too, so tests can assert on every request in one list.
    const custom = extra?.(method, path, Object.assign(url, { body: request.postData() ?? null }), binding, gateway);
    if (custom) {
      const raw = request.postData();
      gateway.requests.push({ method, url: new URL(request.url()), headers: new Headers(request.headers()), body: raw ? JSON.parse(raw) : undefined });
      return reply(await custom);
    }
    // Everything else goes to the shared in-memory fake Gateway.
    const response = await gateway.fetch(url.toString(), {
      method,
      headers: request.headers(),
      body: request.postData() ?? undefined,
    });
    if (response.status === 404) unhandled.push(`${method} ${path}`);
    return reply(response);
  });

  const page = await context.newPage();
  return {
    page,
    gateway,
    unhandled,
    async close() { await browser.close(); },
  };
}

/**
 * Fakes the bound run endpoints the run page reads, in the shapes from
 * `src/v1/contracts.ts`: owner status, a coordinator root with one child work session,
 * session status with the pending gate as a blocker, decision state, a journal of `turn`
 * events with their text from `readSessionTurnContent`, gate actions, and addressed messages. Every route needs a token bound to this run.
 * Mutate `room.decisions` to change or replace the gate. Writes land in `room.writes`.
 */
export function runRoom({ projectId, runId, gate = null, turns = [] }) {
  // One coordinator root with one child work session is the smallest tree the run page shows.
  const rootSessionId = `${runId}-root`;
  const childSessionId = `${runId}-work`;
  const createdAt = '2026-10-20T12:00:00.000Z';
  const identity = (sessionId) => ({ projectId, runId, sessionId });
  const node = (sessionId, kind, parentSessionId) => ({
    identity: identity(sessionId), parentSessionId, rootSessionId, kind, detached: false,
    lifecycle: 'active', executionFence: 1, logicalTurnOrdinal: 1, stateVersion: 1, createdAt, archivedAt: null,
  });
  const room = {
    rootSessionId,
    childSessionId,
    writes: [],
    /** Set to true to answer turn reads with 403, as after losing project read access. */
    revoked: false,
    // Each turn gets a stable event id. Turns default to the root session.
    turns: turns.map((value, index) => ({ eventId: `turn-${index + 1}`, sessionId: rootSessionId, ...value })),
    // The gate's state version is the value the app pins. Tests bump it to simulate another actor.
    decisions: {
      stateVersion: gate?.stateVersion ?? 1, executionFence: 1, outcomeConfirmed: true, workflowConfirmed: true,
      canDecompose: false, canDispatch: false,
      pendingGate: gate ? {
        requestId: gate.requestId, kind: 'approval', subjectId: 'deploy', authorizedActorId: 'me', fence: 1,
        allowedChoices: [], allowsFreeform: false, prompt: gate.prompt,
      } : null,
    },
  };
  const status = (sessionId, kind, parentSessionId) => {
    // Only the root holds the gate. While a gate is pending the root is idle
    // and blocked; otherwise it is busy and the run is active.
    const pending = sessionId === rootSessionId ? room.decisions.pendingGate : null;
    return {
      ...node(sessionId, kind, parentSessionId), activity: pending ? 'idle' : 'busy', activityUnavailableCode: null,
      blockers: pending ? [{ kind: 'awaitingApproval', requestId: pending.requestId, choices: [], allowsFreeform: false, prompt: pending.prompt }] : [],
      runtimeEffectsState: 'none', runtimeEffectsUnavailableCode: '',
      interruptionIntent: { state: 'none' },
      runExecution: { state: pending ? 'blocked' : 'active', stateVersion: room.decisions.stateVersion },
    };
  };
  const json = (code, body) => new Response(JSON.stringify(body), { status: code, headers: { 'content-type': 'application/json' } });
  const prefix = `/projects/${projectId}/runs/${runId}`;
  room.handle = (method, path, url, binding) => {
    // Not a route for this run: return undefined so the shared fake answers it.
    if (!path.startsWith(`${prefix}/`)) return undefined;
    // Every run route needs a token bound to this exact run, like the real owner.
    if (binding?.projectId !== projectId || binding?.runId !== runId) return json(403, { error: 'caller_binding_mismatch' });
    const rest = path.slice(prefix.length);
    // The journal only holds content references. The text is read separately per turn below.
    if (method === 'GET' && rest === '/events') return json(200, {
      events: room.turns.map((value, index) => ({
        schemaVersion: 1, eventVersion: 1, eventId: value.eventId, identity: identity(value.sessionId), position: index + 1,
        occurredAt: createdAt, kind: 'turn', payload: { kind: 'turn', role: value.role, content: { reference: `content-${index + 1}` } },
        objectReferences: [],
      })),
      nextCursor: null, hasMore: false,
    });
    const turnRead = method === 'GET' && rest.match(/^\/sessions\/([^/]+)\/turns\/([^/]+)$/);
    if (turnRead) {
      // After revocation the owner refuses turn text, so the page must hide messages.
      if (room.revoked) return json(403, { error: 'project_read_permission_denied' });
      const found = room.turns.find((value) => value.sessionId === decodeURIComponent(turnRead[1]) && value.eventId === decodeURIComponent(turnRead[2]));
      return found
        ? json(200, { eventId: found.eventId, identity: identity(found.sessionId), text: found.text })
        : json(404, { error: 'turn_content_not_found' });
    }
    // An empty live stream: the tests drive changes by mutating `room`, not by pushing events.
    if (method === 'GET' && rest === '/events/live') return new Response('', { status: 200, headers: { 'content-type': 'text/event-stream' } });
    if (method === 'GET' && rest === '/coordination/status') return json(200, {
      projectId, runId, rootSessionId, executionFence: 1, logicalTurnOrdinal: 1,
      executionState: room.decisions.pendingGate ? 'blocked' : 'active', stateVersion: room.decisions.stateVersion,
    });
    if (method === 'GET' && rest === `/coordination/sessions/${rootSessionId}/tree`) return json(200, {
      rootSessionId, nodes: [node(rootSessionId, 'coordinator', null), node(childSessionId, 'childWork', rootSessionId)],
    });
    if (method === 'GET' && rest === `/coordination/sessions/${rootSessionId}/status`) return json(200, status(rootSessionId, 'coordinator', null));
    if (method === 'GET' && rest === `/coordination/sessions/${childSessionId}/status`) return json(200, status(childSessionId, 'childWork', rootSessionId));
    if (method === 'GET' && /^\/coordination\/sessions\/[^/]+\/decisions$/.test(rest)) return json(200, room.decisions);
    if (method === 'POST') {
      // Record every write so tests can prove that nothing was sent.
      room.writes.push({ method, path: rest, body: url.body });
      const gateAction = rest.match(/\/decisions\/gates\/([^/]+)\/(approve|reject|answer)$/);
      if (gateAction) {
        // Any gate action resolves the gate and advances the state version.
        room.decisions = { ...room.decisions, stateVersion: room.decisions.stateVersion + 1, pendingGate: null };
        return json(200, { decisionId: 'decision-1', stateVersion: room.decisions.stateVersion, accepted: true, executionFence: 1, issues: [] });
      }
      if (/\/coordination\/sessions\/[^/]+\/messages$/.test(rest)) {
        return json(200, { ownerMessageId: `message-${room.writes.length}`, recipientSessionId: rootSessionId, status: 'accepted' });
      }
    }
    return undefined;
  };
  return room;
}