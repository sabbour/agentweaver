/**
 * Configured-repository Workspace journey against the built app and local owner fixtures.
 * No live Identity, Gateway, or GitHub services are used.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { startStaticServer, openApp } from './harness.mjs';
import { project } from '../src/v1/__fixtures__/cosGateway.ts';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });

const commitSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const json = (status, body) => new Response(JSON.stringify(body), {
  status,
  headers: { 'content-type': 'application/json' },
});

test('Workspace: browse a pinned file and fit the retained reader at 390 pixels', async () => {
  const projectConfiguration = {
    projectId: 'p1',
    revision: 1,
    updatedByActorId: 'me',
    createdAt: '2026-10-20T12:00:00Z',
    configuration: {
      defaultWorkflowId: null,
      blueprintWorkflowReferences: [],
      agentCharters: [],
      sourceControl: {
        repository: { owner: 'octo', name: 'repo' },
        authMode: 'githubApp',
        appConnectionId: 'connection-1',
      },
    },
  };
  const app = await openApp({
    origin: server.origin,
    seed: { projects: [project('p1', 'Workspace fixture')] },
    extra: (method, path, url, binding) => {
      if (method === 'GET' && path === '/projects/p1/configuration')
        return json(200, projectConfiguration);
      if (method === 'GET' && path === '/projects/p1/source-control/pull-requests')
        return json(200, {
          projectId: 'p1',
          coverage: { recordedSince: '2026-10-20T12:00:00Z', includesEarlierPullRequests: false },
          items: [],
          nextCursor: null,
        });
      if (method === 'GET' && path === '/projects/p1/source-control/issues')
        return json(200, { projectId: 'p1', items: [], nextCursor: null });
      if (path === '/projects/p1/source-control/refs') {
        assert.equal(method, 'GET');
        assert.equal(binding, null, 'configured-repository reads use the unbound project caller');
        return json(200, {
          repository: 'octo/repo',
          defaultBranch: 'main',
          items: [{ name: 'main', kind: 'branch', commitSha, isDefault: true }],
          nextCursor: null,
        });
      }
      if (path === '/projects/p1/source-control/tree') {
        assert.equal(method, 'GET');
        assert.equal(url.searchParams.get('ref'), commitSha);
        assert.equal(binding, null);
        return json(200, {
          repository: 'octo/repo',
          reference: commitSha,
          commitSha,
          items: [
            { path: 'src', kind: 'directory', size: null, blobSha: null },
            { path: 'src/nested', kind: 'directory', size: null, blobSha: null },
            { path: 'src/nested/readme.txt', kind: 'file', size: 22, blobSha: 'b'.repeat(40) },
          ],
        });
      }
      if (path === '/projects/p1/source-control/files/src/nested/readme.txt') {
        assert.equal(method, 'GET');
        assert.equal(url.searchParams.get('ref'), commitSha);
        assert.equal(binding, null);
        return json(200, {
          repository: 'octo/repo',
          path: 'src/nested/readme.txt',
          commitSha,
          content: 'workspace browser text',
          isBinary: false,
          language: 'plaintext',
          size: 22,
        });
      }
      return undefined;
    },
  });

  const brokerRequests = [];
  await app.page.route('http://broker.test/auth/github/repo-app/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    brokerRequests.push({ method: request.method(), path: url.pathname });
    const cors = {
      'access-control-allow-origin': server.origin,
      'access-control-allow-credentials': 'true',
    };
    if (request.method() === 'OPTIONS') {
      await route.fulfill({
        status: 204,
        headers: {
          ...cors,
          'access-control-allow-methods': 'GET, POST, OPTIONS',
          'access-control-allow-headers': 'accept, content-type, x-csrf-token',
        },
      });
      return;
    }
    if (request.method() === 'GET' && url.pathname.endsWith('/csrf')) {
      await route.fulfill({ status: 200, headers: { ...cors, 'content-type': 'application/json' }, body: JSON.stringify({ csrf_token: 'fixture-csrf' }) });
      return;
    }
    if (request.method() === 'GET' && url.pathname.endsWith('/repositories')) {
      await route.fulfill({
        status: 200,
        headers: { ...cors, 'content-type': 'application/json' },
        body: JSON.stringify({
          connectionId: 'connection-1',
          connectionRevision: 1,
          githubLogin: 'octo',
          repositories: [{
            installationId: 7,
            repositoryId: 11,
            fullName: 'octo/repo',
            ownerLogin: 'octo',
            isPrivate: true,
            defaultBranch: 'main',
          }],
        }),
      });
      return;
    }
    if (request.method() === 'POST' && url.pathname.endsWith('/selection')) {
      assert.deepEqual(JSON.parse(request.postData()), { installationId: 7, repositoryId: 11 });
      await route.fulfill({
        status: 200,
        headers: { ...cors, 'content-type': 'application/json' },
        body: JSON.stringify({
          code: 'selection-code',
          connectionId: 'connection-1',
          connectionRevision: 1,
          installationId: 7,
          repositoryId: 11,
          repositoryFullName: 'octo/repo',
        }),
      });
      return;
    }
    await route.fulfill({ status: 404, headers: { ...cors, 'content-type': 'application/json' }, body: '{}' });
  });

  try {
    const { page } = app;
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(`${server.origin}/projects/p1/workspace`);
    await page.getByRole('combobox', { name: 'Repository ref' }).waitFor();
    await page.getByRole('button', { name: 'nested' }).click();
    await page.getByRole('button', { name: 'readme.txt' }).click();
    await page.getByText('workspace browser text', { exact: true }).waitFor();

    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Collapse navigation' }).click();
    const browser = page.getByLabel('Configured repository browser');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no mobile horizontal overflow');
    const bounds = await browser.boundingBox();
    assert.ok(bounds && bounds.x >= 0 && bounds.x + bounds.width <= 390, 'repository browser fits the visible content area');
    await page.getByText('workspace browser text', { exact: true }).waitFor();

    const workspaceReads = app.gateway.requests.filter(({ url }) =>
      url.pathname.includes('/source-control/refs') ||
      url.pathname.includes('/source-control/tree') ||
      url.pathname.includes('/source-control/files/'));
    assert.deepEqual(workspaceReads.map(({ method }) => method), ['GET', 'GET', 'GET']);
    for (const request of workspaceReads) {
      assert.equal(request.headers.get('authorization'), 'Bearer tok.unbound');
      assert.equal(request.headers.get('x-agentweaver-tenant'), 'tenant-1');
      assert.equal(request.headers.get('x-agentweaver-team-sync-selection'), 'selection-code');
    }
    assert.equal(workspaceReads[1].url.searchParams.get('ref'), commitSha);
    assert.equal(workspaceReads[2].url.searchParams.get('ref'), commitSha);
    assert.deepEqual(app.gateway.writes(), [], 'the Workspace reader performs no writes');
    assert.ok(brokerRequests.some(({ path }) => path.endsWith('/repositories')));
    assert.ok(brokerRequests.some(({ path }) => path.endsWith('/selection')));
    assert.deepEqual(app.unhandled, []);
  } finally {
    await app.close();
  }
});
