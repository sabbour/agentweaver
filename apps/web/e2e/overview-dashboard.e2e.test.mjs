/**
 * Tests the mounted v1 Overview and project dashboard against the public Gateway routes.
 * Build the Web app before running this browser test.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openApp, startStaticServer } from './harness.mjs';
import { project, run } from '../src/v1/__fixtures__/cosGateway.ts';

let server;
const evidence = process.env.AGENTWEAVER_EVIDENCE_DIR;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });

async function capture(name, page, heading) {
  if (!evidence) return;
  await mkdir(evidence, { recursive: true });
  await page.locator('.aw-shell-scroll').evaluate((container, targetHeading) => {
    if (!targetHeading) {
      container.scrollTo(0, 0);
      return;
    }
    const target = [...container.querySelectorAll('h1, h2, h3, h4')]
      .find((element) => element.textContent?.trim() === targetHeading);
    if (!target) throw new Error(`Could not find heading "${targetHeading}" in the page content.`);
    container.scrollTop += target.getBoundingClientRect().top - container.getBoundingClientRect().top - 24;
  }, heading ?? null);
  await page.screenshot({ path: join(evidence, name) });
}

/** Supplies project detail reads that the shared fixture does not own. */
function projectReads(method, path) {
  if (method === 'GET' && path === '/projects/p1/configuration')
    return Response.json({
      projectId: 'p1', revision: 3, updatedByActorId: 'me', createdAt: '2026-10-20T12:00:00Z',
      configuration: {
        modelSelection: null, providerOverrides: [], orderedProviderOverrides: [],
        agentCharters: [], casting: [], blueprintWorkflowReferences: [], skills: [], runLimits: {},
      },
    });
  // The selected project's metrics and usage are separate reads for the same date range.
  if (method === 'GET' && path === '/projects/p1/metrics')
    return Response.json({
      projectId: 'p1', from: '2026-09-20T00:00:00Z', to: '2026-10-20T00:00:00Z',
      activeRuns: 1, blockedRuns: 0, runsInRange: 1, runsTotal: 1,
      completedRuns: 0, succeededRuns: 0,
      series: [{ date: '2026-10-19', created: 1, completed: 0 }],
    });
  if (method === 'GET' && path === '/projects/p1/usage')
    return Response.json({
      projectId: 'p1', from: '2026-09-20T00:00:00Z', to: '2026-10-20T00:00:00Z',
      usageByModel: [{
        id: 'gpt-5', requestCount: 1, inputTokens: 10, outputTokens: 20, cachedTokens: 0,
        cacheWriteTokens: 0, reasoningTokens: 0, avgDurationMs: 45000, runs: 1,
        unpricedRequestCount: 0, cost: [{ priceUnit: 'AIC', disposition: 'estimate', amount: 1 }],
      }],
      usageByAgent: [{
        id: 'Ada', requestCount: 1, inputTokens: 10, outputTokens: 20, cachedTokens: 0,
        cacheWriteTokens: 0, reasoningTokens: 0, avgDurationMs: 45000, runs: 1,
        unpricedRequestCount: 0, cost: [{ priceUnit: 'AIC', disposition: 'estimate', amount: 1 }],
      }],
    });
  if (method === 'GET' && path === '/projects/p1/backlog')
    return Response.json({ projectId: 'p1', revision: 1, tasks: [], dependencies: [] });
  return undefined;
}

test('Overview and project dashboard make only versioned, existing Gateway reads', async () => {
  const paymentsProject = {
    ...project('p1', 'Payments'),
    effectiveRoles: [{ resourceType: 'project', resourceId: 'p1', role: 'owner' }],
  };
  const app = await openApp({
    origin: server.origin,
    seed: { projects: [paymentsProject], runs: { p1: [run('r1', 'active', 'Process payments')] } },
    extra: projectReads,
  });

  const requests = [];
  const failed = [];
  app.page.setViewportSize({ width: 1440, height: 1000 });
  app.page.on('request', (request) => {
    const url = new URL(request.url());
    // Catch requests outside the Gateway interceptor as well: localhost and old /api return the SPA.
    if (url.pathname.startsWith('/api/') || url.hostname === 'localhost')
      requests.push({ method: request.method(), path: url.pathname, host: url.host });
  });

  app.page.on('response', (response) => {
    if (response.url().includes('/api/') && !response.ok()) failed.push(`${response.status()} ${response.url()}`);
  });
  try {
    // Wait for the project metrics and activity feed; both are independent of the project list.
    await app.page.goto(`${server.origin}/overview`);
    await app.page.getByLabel('Project metric summary').waitFor();
    await app.page.getByRole('heading', { name: 'Activity feed' }).waitFor();
    await app.page.getByRole('link', { name: 'Open run Process payments' }).waitFor();
    await capture('overview-v1-top.png', app.page);
    await capture('overview-v1-operations.png', app.page, 'Operations');
    await capture('overview-v1-activity.png', app.page, 'Activity feed');
    const chiefOfStaffLink = app.page.getByRole('link', { name: 'Chief of Staff' });
    assert.equal(await chiefOfStaffLink.getAttribute('href'), '/overview#chief-of-staff');
    await chiefOfStaffLink.click();
    await app.page.waitForURL('**/overview#chief-of-staff');
    await app.page.getByRole('heading', { name: 'Chief of Staff' }).waitFor();
    const overview = new Set(requests.map(({ path }) => path));
    for (const path of [
      '/api/v1/authorization/context', '/api/v1/projects', '/api/v1/projects/p1/runs',
      '/api/v1/projects/p1/configuration', '/api/v1/projects/p1/backlog',
      '/api/v1/projects/p1/metrics', '/api/v1/projects/p1/usage',
      '/api/v1/notifications', '/api/v1/chief-of-staff/priorities',
      '/api/v1/chief-of-staff/conversations',
    ]) assert.ok(overview.has(path), `Overview should read ${path}`);
    assert.ok(!overview.has('/api/v1/overview'), 'the legacy global overview route is not published');

    // Navigation mounts the project summary, metrics, usage and shared shell.
    await app.page.goto(`${server.origin}/projects/p1`);
    await app.page.getByText('Live project health is unavailable. This status uses run counts only.').waitFor();
    await app.page.getByText('Runs, last 7 days').waitFor();
    await capture('dashboard-v1-top.png', app.page);
    await capture('dashboard-v1-models.png', app.page, 'Model and agent metrics');
    const dashboard = new Set(requests.map(({ path }) => path));
    for (const path of [
      '/api/v1/projects/p1', '/api/v1/projects/p1/metrics', '/api/v1/projects/p1/usage',
    ]) assert.ok(dashboard.has(path), `Dashboard should read ${path}`);
    assert.ok(!dashboard.has('/api/v1/projects/p1/dashboard'), 'the legacy dashboard route is not published');
    assert.deepEqual(failed, [], 'no page or shell request may fail');
    assert.deepEqual(app.unhandled, [], 'no unknown Gateway route may be requested');
    // Exact approved paths cover page panels, Chief of Staff, the shell, and its notification bell.
    const published = new Set([
      '/api/v1/authorization/context', '/api/v1/projects', '/api/v1/projects/p1',
      '/api/v1/projects/p1/configuration', '/api/v1/projects/p1/backlog', '/api/v1/projects/p1/runs',
      '/api/v1/projects/p1/metrics', '/api/v1/projects/p1/usage',
      '/api/v1/notifications', '/api/v1/chief-of-staff/priorities',
      '/api/v1/chief-of-staff/conversations',
    ]);
    assert.deepEqual(requests.filter(({ method, path, host }) =>
      method !== 'GET' || !published.has(path) || host !== new URL(server.origin).host), []);
  } finally {
    await app.close();
  }
});

test('a deployed build with no Gateway URL shows a startup error without making requests', async () => {
  const missing = await startStaticServer({ includeGateway: false });
  const app = await openApp({ origin: missing.origin });
  try {
    // The module import rejects before mounting either page; the bootstrap must render the reason.
    await app.page.goto(`${missing.origin}/overview`);
    const alert = app.page.getByRole('alert');
    await alert.getByRole('heading', { name: 'Gateway configuration is unavailable' }).waitFor();
    assert.match(await alert.textContent(), /Gateway URL is missing from the web configuration/);
    assert.deepEqual(app.gateway.requests, []);
  } finally {
    await app.close();
    await missing.close();
  }
});
