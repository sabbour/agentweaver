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
const encode = (value) => Buffer.from(value).toString('base64');

/** Serves dist with SPA fallback and a runtime config that points sign-in at the fake Broker. */
export async function startStaticServer() {
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
    const inside = file.startsWith(normalize(dist)) && extname(file);
    try {
      const target = inside ? file : join(dist, 'index.html');
      const body = await readFile(target);
      response.writeHead(200, { 'content-type': types[extname(target)] ?? 'application/octet-stream' }).end(body);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return { origin: `http://127.0.0.1:${server.address().port}`, close: () => new Promise((resolve) => server.close(resolve)) };
}

const tokenFor = (binding) => binding ? `tok.${binding.projectId}.${binding.runId}` : 'tok.unbound';
function bindingOf(header) {
  const [, projectId, runId] = (header ?? '').replace(/^Bearer /, '').split('.');
  return projectId && projectId !== 'unbound' ? { projectId, runId } : null;
}

/** Extra Gateway handlers for one journey: `(method, path, url, binding) => Response | undefined`. */
export async function openApp({ seed = {}, extra, origin }) {
  const gateway = createFakeGateway(seed);
  const unhandled = [];
  const browser = await chromium.launch();
  const context = await browser.newContext();
  await context.addInitScript(() => window.sessionStorage.setItem('agentweaver.auth.signed-in-before', '1'));

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
      const token = form.get('code') ?? form.get('refresh_token') ?? 'tok.unbound';
      await route.fulfill({
        status: 200,
        headers: { 'access-control-allow-origin': origin, 'access-control-allow-credentials': 'true', 'content-type': 'application/json' },
        body: JSON.stringify({ access_token: token, token_type: 'Bearer', expires_in: 3600, refresh_token: token, scope: scopes }),
      });
      return;
    }
    await route.fulfill({ status: 404, headers: { 'access-control-allow-origin': origin }, body: '{}' });
  });

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
    const needsUnbound = path.startsWith('/chief-of-staff/') || path === '/notifications' || path.startsWith('/notifications/')
      || path === '/projects' || (method === 'GET' && /^\/projects\/[^/]+\/runs$/.test(path));
    if (needsUnbound && binding) return reply(json(403, { error: 'caller_binding_mismatch' }));
    const start = path.match(/^\/projects\/([^/]+)\/runs\/([^/]+)\/start$/);
    if (start && (binding?.projectId !== start[1] || binding?.runId !== start[2])) return reply(json(403, { error: 'caller_binding_mismatch' }));
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
    const custom = extra?.(method, path, url, binding, gateway);
    if (custom) return reply(await custom);
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