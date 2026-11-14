/**
 * Checks GitHub project creation against the built app and fixture Broker/Gateway.
 * Build the Web app before running this browser test.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { openApp, startStaticServer } from './harness.mjs';
import { project } from '../src/v1/__fixtures__/cosGateway.ts';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });

const json = (status, body) => Response.json(body, { status });

test('creates a GitHub project with the exact Identity binding and reads the saved configuration', async () => {
  const identityConnectionId = 'identity-connection-1';
  const initialConfiguration = {
    modelSelection: null,
    providerOverrides: [],
    orderedProviderOverrides: [],
    agentCharters: [{ agentId: 'agent-ada', name: 'Ada', role: 'Engineer', charter: 'Build the project.' }],
    casting: [],
    blueprintWorkflowReferences: [],
    skills: [],
    runLimits: {},
    sourceControl: {
      repository: { owner: 'old', name: 'repository' },
      authMode: 'secret',
      apiSecretReference: 'fixture/api-secret:v1',
      checkoutSecretReference: 'fixture/checkout-secret:v2',
      webhookSecretReference: 'fixture/webhook-secret:v3',
    },
  };
  const expectedConfiguration = {
    ...initialConfiguration,
    sourceControl: {
      repository: { owner: 'octo', name: 'agentweaver' },
      authMode: 'githubApp',
      identityConnectionId,
      webhookSecretReference: 'fixture/webhook-secret:v3',
    },
  };
  let saved;
  const app = await openApp({
    origin: server.origin,
    seed: { projects: [] },
    extra(method, path, url, _binding, gateway) {
      if (method === 'POST' && path === '/projects') {
        const request = JSON.parse(url.body);
        assert.deepEqual(request, { name: 'From GitHub' });
        const created = project('p9', 'From GitHub');
        gateway.state.projects.push(created);
        return json(201, created);
      }
      if (method === 'GET' && path === '/projects/p9/configuration')
        return json(200, saved ?? {
          projectId: 'p9',
          revision: 7,
          updatedByActorId: 'me',
          createdAt: '2026-11-14T00:00:00Z',
          configuration: initialConfiguration,
        });
      if (method === 'PUT' && path === '/projects/p9/configuration') {
        const request = JSON.parse(url.body);
        const sourceControl = request.configuration?.sourceControl;
        if (request.expectedRevision !== 7 ||
            sourceControl?.identityConnectionId !== identityConnectionId ||
            Object.hasOwn(sourceControl, 'apiSecretReference') ||
            Object.hasOwn(sourceControl, 'checkoutSecretReference'))
          return json(400, { error: 'invalid_project_source_control' });
        saved = {
          projectId: 'p9',
          revision: 8,
          updatedByActorId: 'me',
          createdAt: '2026-11-14T00:00:00Z',
          configuration: request.configuration,
        };
        return json(200, saved);
      }
      return undefined;
    },
  });
  const status = {
    state: 'connected',
    localReadiness: 'access_token_available',
    connectionId: identityConnectionId,
    connectionRevision: 4,
    githubLogin: 'octo',
    accessTokenExpiresAt: '2026-11-14T01:00:00Z',
    updatedAt: '2026-11-14T00:00:00Z',
  };
  await app.page.route('http://broker.test/auth/github/repo-app/status', (route) => route.fulfill({
    status: 200,
    headers: {
      'access-control-allow-origin': server.origin,
      'access-control-allow-credentials': 'true',
      'content-type': 'application/json',
    },
    body: JSON.stringify(status),
  }));

  try {
    await app.page.goto(`${server.origin}/projects`);
    await app.page.getByRole('button', { name: 'Create project' }).click();
    const dialog = app.page.getByRole('dialog');
    await dialog.getByLabel('Project name').fill('From GitHub');
    await dialog.getByRole('radio', { name: 'GitHub repository' }).check();
    await dialog.getByRole('textbox', { name: 'Repository' }).fill('https://github.com/octo/agentweaver.git');
    await dialog.getByText('identity-connection-1').waitFor();
    await dialog.getByRole('button', { name: 'Create project' }).click();
    await app.page.getByText('Project From GitHub was created. It uses octo/agentweaver.').waitFor();

    const update = app.gateway.requests.filter((request) =>
      request.method === 'PUT' && request.url.pathname === '/api/v1/projects/p9/configuration');
    assert.equal(update.length, 1);
    assert.equal(update[0].headers.get('X-Agentweaver-Tenant'), 'tenant-1');
    assert.deepEqual(update[0].body, {
      expectedRevision: 7,
      configuration: expectedConfiguration,
    });

    const read = await app.page.evaluate(async () => {
      const response = await fetch('/api/v1/projects/p9/configuration', {
        headers: {
          Authorization: 'Bearer tok.unbound',
          'X-Agentweaver-Tenant': 'tenant-1',
        },
      });
      return { status: response.status, body: await response.json() };
    });
    assert.equal(read.status, 200);
    assert.equal(read.body.revision, 8);
    assert.deepEqual(read.body.configuration, expectedConfiguration);
    assert.deepEqual(app.unhandled, []);
  } finally {
    await app.close();
  }
});
