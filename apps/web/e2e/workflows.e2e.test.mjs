/**
 * Mounted v1 Workflows states and local historical-source comparison.
 * Build Web first. All service responses are local Playwright fixtures.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer as createViteServer } from 'vite';
import { chromium } from 'playwright';
import { openApp, startStaticServer } from './harness.mjs';
import { project } from '../src/v1/__fixtures__/cosGateway.ts';

let server;
const evidence = process.env.AGENTWEAVER_EVIDENCE_DIR;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });
const configuration = {
  projectId: 'p1', revision: 7, updatedByActorId: 'me', createdAt: '2026-10-20T12:00:00Z',
  configuration: {
    defaultWorkflowId: 'issue-to-merge',
    blueprintWorkflowReferences: [
      { blueprintId: 'engineer', workflowId: 'issue-to-merge' },
      { blueprintId: 'reviewer', workflowId: 'review-and-verify' },
      { blueprintId: 'release-manager', workflowId: 'review-and-verify' },
    ],
    agentCharters: [], providerOverrides: [], orderedProviderOverrides: [], casting: [], skills: [], runLimits: {},
  },
};

async function capture(page, name) {
  if (!evidence) return;
  await mkdir(evidence, { recursive: true });
  await page.screenshot({ path: join(evidence, `${name}.png`) });
}

test('Workflows: grouped cards, keyboard details, narrow layout, reset, and read recovery', async () => {
  let state = 'loaded';
  let revision = structuredClone(configuration);
  let releaseRead;
  const writes = [];
  const app = await openApp({
    origin: server.origin,
    seed: { projects: [project('p1', 'Payments')] },
    extra: (method, path, url) => {
      if (path !== '/projects/p1/configuration') return undefined;
      if (method === 'PUT') {
        const body = JSON.parse(url.body);
        assert.equal(body.expectedRevision, revision.revision);
        assert.equal(body.configuration.defaultWorkflowId, null);
        assert.deepEqual(body.configuration.blueprintWorkflowReferences, revision.configuration.blueprintWorkflowReferences);
        writes.push(body);
        revision = { ...revision, revision: revision.revision + 1, configuration: body.configuration };
        return Response.json(revision);
      }
      assert.equal(method, 'GET');
      if (state === 'loading') return new Promise((resolve) => { releaseRead = () => resolve(Response.json(revision)); });
      if (state === 'denied') return Response.json({ error: 'project_read_denied' }, { status: 403 });
      if (state === 'error') return Response.json({ detail: 'Configuration is down' }, { status: 500 });
      if (state === 'empty') return Response.json({ ...revision, configuration: { ...revision.configuration, defaultWorkflowId: null, blueprintWorkflowReferences: [] } });
      return Response.json(revision);
    },
  });
  try {
    const { page } = app;
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(`${server.origin}/projects/p1/workflows`);
    await page.getByText('Configuration revision 7').waitFor();
    const active = page.getByRole('list', { name: 'Active workflow', exact: true });
    await active.getByText('issue-to-merge', { exact: true }).waitFor();
    const referenced = page.getByRole('list', { name: 'Referenced workflows', exact: true });
    assert.equal(await referenced.getByRole('listitem').count(), 1);
    const toggle = referenced.getByRole('button', { name: 'View blueprints for review-and-verify' });
    // Reach the disclosure from the preceding active-card control using the normal tab order.
    await active.getByRole('button', { name: 'View blueprints for issue-to-merge' }).focus();
    await page.keyboard.press('Tab');
    assert.equal(await toggle.evaluate((element) => element === document.activeElement), true);
    await page.keyboard.press('Enter');
    assert.equal(await toggle.getAttribute('aria-expanded'), 'true');
    const assignments = page.getByRole('list', { name: 'Blueprints using review-and-verify' });
    await assignments.getByText('reviewer', { exact: true }).waitFor();
    await assignments.getByText('release-manager', { exact: true }).waitFor();
    await capture(page, 'workflows-v1-after');
    await page.keyboard.press('Space');
    assert.equal(await toggle.getAttribute('aria-expanded'), 'false');
    assert.equal(await toggle.evaluate((element) => element === document.activeElement), true);

    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Collapse navigation' }).click();
    await page.locator('.aw-shell-scroll').evaluate((element) => element.scrollTo(0, 0));
    await capture(page, 'workflows-v1-after-narrow');
    // Long service-supplied IDs must wrap, including the expanded inline details.
    const longId = 'workflow-with-an-unbroken-identifier-'.repeat(4);
    revision.configuration.blueprintWorkflowReferences.push({ blueprintId: 'blueprint-'.repeat(16), workflowId: longId });
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    const longToggle = page.getByRole('button', { name: `View blueprints for ${longId}` });
    await longToggle.click();
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no document overflow');
    const lists = await page.getByRole('list', { name: /Active workflow|Referenced workflows|Blueprints using/ }).all();
    for (const list of lists) {
      assert.ok(await list.evaluate((element) => element.scrollWidth <= element.clientWidth), 'workflow content wraps');
      const bounds = await list.boundingBox();
      assert.ok(bounds && bounds.x >= 0 && bounds.x + bounds.width <= 390, 'workflow card fits the visible content area');
    }
    await capture(page, 'workflows-v1-long-ids-narrow');
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.getByRole('button', { name: 'Expand navigation' }).click();
    await page.getByRole('button', { name: 'Use platform default' }).click();
    await page.getByText('None set. Runs use the platform default.').waitFor();
    assert.equal(writes.length, 1);
    await page.getByRole('list', { name: 'Referenced workflows', exact: true }).getByText('issue-to-merge', { exact: true }).waitFor();

    for (const next of ['denied', 'error', 'empty', 'loading']) {
      state = next;
      await page.reload();
      if (next === 'denied' || next === 'error') {
        await page.getByRole('alert').filter({ hasText: next === 'denied' ? 'You do not have permission' : 'Configuration is down' }).waitFor();
        assert.equal(await page.getByRole('list', { name: 'Referenced workflows', exact: true }).count(), 0);
      } else if (next === 'empty') {
        await page.getByText('No other workflow references', { exact: true }).waitFor();
        assert.equal(await page.getByRole('button', { name: /View blueprints/ }).count(), 0);
      } else {
        await page.getByRole('status', { name: 'Loading workflow references' }).waitFor();
      }
      await capture(page, `workflows-v1-${next}`);
      if (next === 'loading') {
        releaseRead();
        await page.getByText('Configuration revision 8').waitFor();
      }
    }
    assert.equal(await page.getByRole('button', { name: /New workflow|Edit visually|Run now|View graph|Sync|Set as default/ }).count(), 0);
    assert.deepEqual(app.unhandled, []);
    const allowed = new Set(['/projects', '/projects/p1/configuration', '/projects/p1/runs', '/notifications']);
    assert.deepEqual(app.gateway.requests.filter(({ url }) => !allowed.has(url.pathname.replace(/^\/api\/v1/, '')))
      .map(({ method, url }) => `${method} ${url.pathname}`), [], 'only supported project/shell routes');
  } finally { await app.close(); }
});

test('historical Workflows source fixture: real retained page, not a historical deployment', async () => {
  const vite = await createViteServer({
    root: fileURLToPath(new URL('../', import.meta.url)),
    server: { host: '127.0.0.1', port: 0, strictPort: false }, logLevel: 'error',
  });
  await vite.listen();
  const origin = `http://127.0.0.1:${vite.httpServer.address().port}`;
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    // Force old-client requests into this local interceptor instead of its localhost default.
    await page.addInitScript(() => { window.__AGENTWEAVER_CONFIG__ = { API_URL: '' }; });
    const unknown = [];
    await page.route(`${origin}/api/**`, async (route) => {
      const path = new URL(route.request().url()).pathname;
      let body;
      if (path === '/api/projects/p1') body = { id: 'p1', name: 'Payments' };
      else if (path === '/api/ai/execution-context') body = {
        ai_required: false, operation: 'workflow.run', phase: 'prepared', execution_key: 'fixture', expires_at: null, effective_model_provider: null,
      };
      else if (path === '/api/projects/p1/workflows') body = {
        default_workflow_id: 'issue-to-merge',
        workflows: [
          { id: 'issue-to-merge', name: 'Issue to merge', description: 'Implement and review a repository issue.', source: '.agentweaver/workflows/issue-to-merge.yaml', valid: true, is_default: true, is_built_in: false },
          { id: 'review-and-verify', name: 'Review and verify', description: 'Review a completed change and verify the outcome.', source: '.agentweaver/workflows/review-and-verify.yaml', valid: true, is_default: false, is_built_in: false },
        ],
      };
      else unknown.push(path);
      await route.fulfill({ status: body ? 200 : 404, json: body ?? {} });
    });
    await page.goto(`${origin}/e2e/workflows-reference.html`);
    await page.getByRole('heading', { name: 'Available workflows', exact: true }).waitFor();
    await page.getByText('Review and verify', { exact: true }).waitFor();
    await capture(page, 'workflows-0x-source-fixture');
    assert.deepEqual(unknown, []);
  } finally { await browser.close(); await vite.close(); }
});
