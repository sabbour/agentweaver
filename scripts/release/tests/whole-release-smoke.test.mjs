import assert from 'node:assert/strict';
import { test } from 'node:test';
import { P1_RUN_SCOPES } from '../../harness-shared/p1-signin.mjs';
import {
  prepareGatewaySmokeContext, runGatewaySmokeChecks,
} from '../whole-release.mjs';

const issuer = 'https://agentweaver.smoke-test.aksapp.io/';
const audience = issuer;
const webOrigin = 'https://portal.smoke-test.aksapp.io';
const gatewayRuntimeConfig = {
  Gateway__WebOrigin: webOrigin,
  Identity__Issuer: issuer,
  Identity__Audience: audience,
};
const token = [
  'header',
  Buffer.from(JSON.stringify({
    iss: issuer,
    sub: 'signed-in-actor',
    aud: audience,
    exp: Math.floor(Date.now() / 1000) + 600,
    scope: P1_RUN_SCOPES.join(' '),
  })).toString('base64url'),
  'signature',
].join('.');

function encode(value) {
  return Buffer.from(value).toString('base64');
}

function runtimeConfigScript() {
  return `window.__AGENTWEAVER_CONFIG_BASE64__ = ${JSON.stringify({
    GATEWAY_URL: encode('/api/v1'),
    IDENTITY_BROKER_URL: encode(issuer),
    IDENTITY_BROKER_ISSUER: encode(issuer),
  })};\n`;
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function openApiDocument() {
  return {
    openapi: '3.1.0',
    paths: {
      '/api/v1/projects': { get: { operationId: 'listProjects' } },
      '/api/v1/priorities': { get: { operationId: 'listChiefOfStaffPriorities' } },
      '/api/v1/projects/{projectId}/runs': {
        get: {
          operationId: 'listProjectRuns',
          parameters: [{ in: 'path', name: 'projectId', required: true }],
        },
      },
      '/api/v1/projects/{projectId}/metrics': {
        get: {
          operationId: 'readProjectMetrics',
          parameters: [{ in: 'path', name: 'projectId', required: true }],
        },
      },
    },
  };
}

function makeFetch({ contextActorId = 'signed-in-actor', projects } = {}) {
  const requests = [];
  const fetchImpl = async (input, init = {}) => {
    const url = new URL(input);
    requests.push({ url, init });
    if (url.pathname === '/env-config.js') {
      return new Response(runtimeConfigScript(), {
        status: 200, headers: { 'content-type': 'application/javascript' },
      });
    }
    if (url.pathname === '/api/v1/authorization/context') {
      return jsonResponse({
        contractVersion: 1,
        issuer,
        actorId: contextActorId,
        tenantId: 'signed-in-tenant',
        membershipRevision: 3,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [],
      });
    }
    if (url.pathname === '/openapi/v1.json') return jsonResponse(openApiDocument());
    if (url.pathname === '/api/v1/projects') {
      return jsonResponse(projects ?? [
        { projectId: 'zeta', name: 'Zeta', state: 'active', revision: 1 },
        { projectId: 'archived', name: 'Archived', state: 'archived', revision: 1 },
        { projectId: 'alpha', name: 'Alpha', state: 'active', revision: 1 },
      ]);
    }
    if (url.pathname === '/api/v1/priorities'
      || url.pathname.endsWith('/runs') || url.pathname.endsWith('/metrics')) {
      return jsonResponse([]);
    }
    return jsonResponse({ detail: 'unexpected test request' }, 404);
  };
  return { fetchImpl, requests };
}

function baseOptions(overrides = {}) {
  return {
    gatewayTokenEnv: 'TEST_GATEWAY_TOKEN',
    ...overrides,
  };
}

test('automatic P1 sign-in derives actor, tenant, and active project from live Gateway context', async () => {
  const { fetchImpl, requests } = makeFetch();
  const context = { disposable: true };
  let browserStarts = 0;
  const prepared = await prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions(),
    env: {},
    fetchImpl,
    loadChromium: async () => ({ launchPersistentContext() {} }),
    withBrowserContext: async ({ chromium }, useContext) => {
      browserStarts += 1;
      assert.ok(chromium);
      return useContext(context);
    },
    authorize: async input => {
      assert.equal(input.context, context);
      assert.deepEqual(input.scopes, P1_RUN_SCOPES);
      assert.equal(input.actorId, undefined);
      assert.equal(input.environment.issuer, issuer);
      assert.equal(input.environment.audience, audience);
      return token;
    },
  });

  assert.equal(browserStarts, 1);
  assert.equal(prepared.projectId, 'alpha');
  assert.equal(Object.hasOwn(prepared, 'token'), false);
  const contextRequest = requests.find(request => request.url.pathname === '/api/v1/authorization/context');
  assert.equal(contextRequest.init.headers.Authorization, `Bearer ${token}`);
  assert.equal(contextRequest.init.headers['X-Agentweaver-Tenant'], undefined);
  const projectRequest = requests.find(request => request.url.pathname === '/api/v1/projects');
  assert.equal(projectRequest.init.headers.get('X-Agentweaver-Tenant'), 'signed-in-tenant');
  assert.equal(projectRequest.init.headers.get('Authorization'), `Bearer ${token}`);

  const checks = await runGatewaySmokeChecks(prepared);
  assert.deepEqual(checks.map(check => check.operationId), [
    'listProjects', 'listChiefOfStaffPriorities', 'listProjectRuns', 'readProjectMetrics',
  ]);
  assert.equal(requests.filter(request => request.url.pathname === '/openapi/v1.json').length, 2);
  assert.equal(requests.filter(request => request.url.pathname === '/api/v1/projects').length, 2);
});

test('a supplied token and explicit overrides skip browser sign-in and require a listed active project', async () => {
  const { fetchImpl, requests } = makeFetch();
  const prepared = await prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions({
      gatewayUrl: 'https://gateway.override.test/',
      tenantId: 'signed-in-tenant',
      issuer,
      audience,
      actorId: 'signed-in-actor',
      projectId: 'zeta',
    }),
    env: { TEST_GATEWAY_TOKEN: token },
    fetchImpl,
    loadChromium: async () => { throw new Error('browser must not start'); },
  });

  assert.equal(prepared.projectId, 'zeta');
  assert.ok(requests.some(request => request.url.origin === 'https://gateway.override.test'));
  const contextRequest = requests.find(request => request.url.pathname === '/api/v1/authorization/context');
  assert.equal(contextRequest.init.headers['X-Agentweaver-Tenant'], 'signed-in-tenant');
  assert.equal(JSON.stringify(prepared).includes(token), false);
});

test('live configuration or authorization mismatch fails before browser sign-in or project discovery', async () => {
  const requests = [];
  let browserStarts = 0;
  await assert.rejects(prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions(),
    env: {},
    fetchImpl: async input => {
      requests.push(new URL(input));
      return new Response('unavailable', { status: 503 });
    },
    loadChromium: async () => {
      browserStarts += 1;
      return {};
    },
  }), /live Web runtime configuration returned HTTP 503/);
  assert.equal(browserStarts, 0);
  assert.deepEqual(requests.map(url => url.pathname), ['/env-config.js']);

  const { fetchImpl: mismatchedFetch, requests: mismatchedRequests } = makeFetch({
    contextActorId: 'different-actor',
  });
  await assert.rejects(prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions(),
    env: { TEST_GATEWAY_TOKEN: token },
    fetchImpl: mismatchedFetch,
  }), /invalid authorization context/);
  assert.equal(mismatchedRequests.some(request => request.url.pathname === '/api/v1/projects'), false);
});

test('identity setup fails closed when the signed-in actor has no active projects', async () => {
  const { fetchImpl } = makeFetch({
    projects: [{ projectId: 'archived', name: 'Archived', state: 'archived', revision: 1 }],
  });
  await assert.rejects(prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions(),
    env: { TEST_GATEWAY_TOKEN: token },
    fetchImpl,
  }), /no active project/);
});

test('post-rollout smoke checks rediscover OpenAPI and repeat the project request', async t => {
  for (const failedPath of ['/openapi/v1.json', '/api/v1/projects']) {
    await t.test(`${failedPath} failure`, async () => {
      const base = makeFetch();
      let afterPreparation = false;
      const fetchImpl = async (input, init) => {
        const url = new URL(input);
        if (afterPreparation && url.pathname === failedPath) {
          return jsonResponse({ detail: 'post-rollout service unavailable' }, 503);
        }
        return base.fetchImpl(input, init);
      };
      const prepared = await prepareGatewaySmokeContext({
        gatewayRuntimeConfig,
        options: baseOptions(),
        env: { TEST_GATEWAY_TOKEN: token },
        fetchImpl,
      });
      afterPreparation = true;
      await assert.rejects(runGatewaySmokeChecks(prepared), /returned HTTP 503|OpenAPI document/);
    });
  }
});
