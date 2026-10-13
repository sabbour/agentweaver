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

test('run identity provider receives the exact selected binding and creates origin-bound clients', async () => {
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
        return {
          token: jwt({ projectId: PROJECT_ID, runId: RUN_ID }),
          deniedToken: jwt({ actorId: 'denied-actor' }),
        };
      },
    }),
  });

  assert.equal(providerCalls, 1);
  assert.equal(identity.projectId, PROJECT_ID);
  assert.equal(identity.runId, RUN_ID);
  const clients = identity.createClients({
    deniedActor: { actorId: 'denied-actor', tenantId: 'other-tenant' },
  });
  assert.equal(clients.api.target.origin, new URL(ENVIRONMENT.baseUrl).origin);
  assert.equal(clients.api.tenantId, ENVIRONMENT.tenantId);
  assert.equal(clients.api.authProvider.actorId, ENVIRONMENT.actorId);
  assert.equal(clients.deniedApi.tenantId, 'other-tenant');
  assert.equal(clients.deniedApi.authProvider.actorId, 'denied-actor');

  const adapters = await createP1SurfaceAdapters({
    surface: 'api',
    environment: ENVIRONMENT,
    identity: { ...identity, ...clients },
    deniedActor: { actorId: 'denied-actor', tenantId: 'other-tenant' },
  });
  assert.equal(adapters.api, clients.api);
  assert.equal(adapters.deniedApi, clients.deniedApi);
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
        deniedToken: jwt({ actorId: 'denied-actor' }),
      }),
    }),
  }), /exact authorized actor and selected run/);
});

test('setup API rejects a token for a different selected actor', () => {
  assert.throws(() => createP1SetupApi({
    environment: ENVIRONMENT,
    setupToken: jwt({ actorId: 'another-actor' }),
  }), /selected issuer, audience, and actor/);
});
