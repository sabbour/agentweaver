import test from 'node:test';
import assert from 'node:assert/strict';
import { acquireP1RunIdentity, createP1SetupApi, createP1SurfaceAdapters } from '../p1-runtime-adapters.mjs';

const ENVIRONMENT = {
  baseUrl: 'https://environment.controlled.test/',
  issuer: 'https://identity.controlled.test/',
  audience: 'agentweaver-controlled',
  actorId: 'controlled-actor',
  tenantId: 'controlled-tenant',
};
const PROJECT_ID = 'controlled-project';
const RUN_ID = '00000000-0000-4000-8000-000000000001';

function jwt({ actorId = ENVIRONMENT.actorId, projectId, runId } = {}) {
  return [
    'header',
    Buffer.from(JSON.stringify({
      iss: ENVIRONMENT.issuer,
      sub: actorId,
      aud: ENVIRONMENT.audience,
      exp: 2_000_000_000,
      ...(projectId ? { project_id: projectId } : {}),
      ...(runId ? { run_id: runId } : {}),
    })).toString('base64url'),
    'signature',
  ].join('.');
}

test('run identity provider receives the selected binding and creates one origin-bound client', async () => {
  let providerCalls = 0;
  const identity = await acquireP1RunIdentity({
    modulePath: 'authorized-identity-provider.mjs',
    environment: ENVIRONMENT,
    projectId: PROJECT_ID,
    runId: RUN_ID,
    importModule: async () => ({
      default: async binding => {
        providerCalls += 1;
        assert.equal(binding.projectId, PROJECT_ID);
        assert.equal(binding.runId, RUN_ID);
        assert.equal(Object.hasOwn(binding, 'setupToken'), false);
        return { token: jwt({ projectId: PROJECT_ID, runId: RUN_ID }) };
      },
    }),
  });

  assert.equal(providerCalls, 1);
  assert.equal(identity.projectId, PROJECT_ID);
  assert.equal(identity.runId, RUN_ID);
  const clients = identity.createClients();
  assert.equal(clients.api.target.origin, new URL(ENVIRONMENT.baseUrl).origin);
  assert.equal(clients.api.tenantId, ENVIRONMENT.tenantId);
  assert.equal(clients.api.authProvider.actorId, ENVIRONMENT.actorId);

  const adapters = await createP1SurfaceAdapters({
    surface: 'api',
    environment: ENVIRONMENT,
    identity: { ...identity, ...clients },
  });
  assert.equal(adapters.api, clients.api);
  assert.equal(adapters.mcp, undefined);
  assert.equal(adapters.ui, undefined);
});

test('run identity provider rejects a token for another project or run', async () => {
  await assert.rejects(acquireP1RunIdentity({
    modulePath: 'authorized-identity-provider.mjs',
    environment: ENVIRONMENT,
    projectId: PROJECT_ID,
    runId: RUN_ID,
    importModule: async () => ({
      default: async () => ({
        token: jwt({ projectId: PROJECT_ID, runId: '00000000-0000-4000-8000-000000000002' }),
      }),
    }),
  }), /exact authorized actor and selected run/);
});

test('run identity provider rejects extra credentials', async () => {
  await assert.rejects(acquireP1RunIdentity({
    modulePath: 'extra-credential-identity-provider.mjs',
    environment: ENVIRONMENT,
    projectId: PROJECT_ID,
    runId: RUN_ID,
    importModule: async () => ({
      default: async binding => {
        assert.equal(binding.projectId, PROJECT_ID);
        assert.equal(binding.runId, RUN_ID);
        return {
          token: jwt({ projectId: PROJECT_ID, runId: RUN_ID }),
          extraToken: 'unexpected',
        };
      },
    }),
  }), /only the run token/);
});

test('setup API rejects a token for a different selected actor', () => {
  assert.throws(() => createP1SetupApi({
    environment: ENVIRONMENT,
    setupToken: jwt({ actorId: 'another-actor' }),
  }), /selected issuer, audience, and actor/);
});

test('UI adapter accepts the built-in authorized page provider without loading a module', async () => {
  const page = {
    url: () => ENVIRONMENT.baseUrl,
    waitForResponse() {},
    on() {},
  };
  const api = { target: new URL(ENVIRONMENT.baseUrl), tenantId: ENVIRONMENT.tenantId };
  const identity = {
    projectId: PROJECT_ID,
    runId: RUN_ID,
    createClients: () => ({ api }),
  };
  let imports = 0;
  let providerInput;
  const adapters = await createP1SurfaceAdapters({
    surface: 'ui',
    environment: ENVIRONMENT,
    identity,
    pageProvider: async input => {
      providerInput = input;
      return page;
    },
    importModule: async () => {
      imports += 1;
      throw new Error('the built-in provider must not load a module');
    },
  });

  assert.equal(adapters.api, api);
  assert.equal(adapters.ui.page, page);
  assert.equal(imports, 0);
  assert.deepEqual(providerInput, {
    baseUrl: ENVIRONMENT.baseUrl,
    actorId: ENVIRONMENT.actorId,
    tenantId: ENVIRONMENT.tenantId,
    issuer: ENVIRONMENT.issuer,
    audience: ENVIRONMENT.audience,
    projectId: PROJECT_ID,
    runId: RUN_ID,
  });
});
