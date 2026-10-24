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

function gatewayApiRequests(requests) {
  return requests.filter(request => request.url.pathname !== '/env-config.js');
}

test('automatic P1 sign-in validates the token before rollout and defers Gateway reads to smoke', async () => {
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
  assert.equal(prepared.actorId, 'signed-in-actor');
  assert.equal(Object.hasOwn(prepared, 'token'), false);
  assert.equal(JSON.stringify(prepared).includes(token), false);
  assert.deepEqual(gatewayApiRequests(requests), []);

  const checks = await runGatewaySmokeChecks(prepared);
  assert.deepEqual(checks.map(check => check.operationId), [
    'listProjects', 'listChiefOfStaffPriorities', 'listProjectRuns', 'readProjectMetrics',
  ]);
  const contextRequest = requests.find(request => request.url.pathname === '/api/v1/authorization/context');
  assert.equal(contextRequest.init.headers.Authorization, `Bearer ${token}`);
  assert.equal(contextRequest.init.headers['X-Agentweaver-Tenant'], undefined);
  const projectRequest = requests.find(request => request.url.pathname === '/api/v1/projects');
  assert.equal(projectRequest.init.headers.get('X-Agentweaver-Tenant'), 'signed-in-tenant');
  assert.equal(projectRequest.init.headers.get('Authorization'), `Bearer ${token}`);
  assert.ok(requests.some(request => request.url.pathname === '/api/v1/projects/alpha/runs'));
  assert.equal(requests.filter(request => request.url.pathname === '/openapi/v1.json').length, 1);
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
  assert.equal(JSON.stringify(prepared).includes(token), false);
  await runGatewaySmokeChecks(prepared);
  assert.ok(requests.some(request => request.url.origin === 'https://gateway.override.test'));
  assert.ok(requests.some(request => request.url.pathname === '/api/v1/projects/zeta/runs'));
  const contextRequest = requests.find(request => request.url.pathname === '/api/v1/authorization/context');
  assert.equal(contextRequest.init.headers['X-Agentweaver-Tenant'], 'signed-in-tenant');
});

test('live configuration or token mismatch fails before browser sign-in or any Gateway request', async () => {
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

  const { fetchImpl, requests: tokenRequests } = makeFetch();
  await assert.rejects(prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions({ actorId: 'different-actor' }),
    env: { TEST_GATEWAY_TOKEN: token },
    fetchImpl,
  }));
  assert.deepEqual(gatewayApiRequests(tokenRequests), []);
});

test('an authorization context mismatch fails the smoke before project discovery', async () => {
  const { fetchImpl, requests } = makeFetch({ contextActorId: 'different-actor' });
  const prepared = await prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions(),
    env: { TEST_GATEWAY_TOKEN: token },
    fetchImpl,
  });
  await assert.rejects(runGatewaySmokeChecks(prepared), /invalid authorization context/);
  assert.equal(requests.some(request => request.url.pathname === '/api/v1/projects'), false);
});

test('a Gateway authorization outage does not block preparation and is checked after rollout', async () => {
  const base = makeFetch();
  let gatewayDown = true;
  const fetchImpl = async (input, init) => {
    const url = new URL(input);
    if (gatewayDown && url.pathname.startsWith('/api/')) {
      return jsonResponse({ detail: 'upstream timeout' }, 504);
    }
    return base.fetchImpl(input, init);
  };
  const prepared = await prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions(),
    env: { TEST_GATEWAY_TOKEN: token },
    fetchImpl,
  });
  assert.deepEqual(gatewayApiRequests(base.requests), []);

  await assert.rejects(runGatewaySmokeChecks(prepared), /authorization context returned HTTP 504/);
  gatewayDown = false;
  const checks = await runGatewaySmokeChecks(prepared);
  assert.equal(checks.length, 4);
});

test('identity setup fails closed when the signed-in actor has no active projects', async () => {
  const { fetchImpl } = makeFetch({
    projects: [{ projectId: 'archived', name: 'Archived', state: 'archived', revision: 1 }],
  });
  const prepared = await prepareGatewaySmokeContext({
    gatewayRuntimeConfig,
    options: baseOptions(),
    env: { TEST_GATEWAY_TOKEN: token },
    fetchImpl,
  });
  await assert.rejects(runGatewaySmokeChecks(prepared), /no active project/);
});

test('post-rollout smoke checks fail when discovery or the project request fails', async t => {
  for (const failedPath of ['/openapi/v1.json', '/api/v1/projects']) {
    await t.test(`${failedPath} failure`, async () => {
      const base = makeFetch();
      const fetchImpl = async (input, init) => {
        const url = new URL(input);
        if (url.pathname === failedPath) {
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
      await assert.rejects(runGatewaySmokeChecks(prepared), /returned HTTP 503|OpenAPI document/);
    });
  }
});

test('smoke checks reject an incomplete preparation', async () => {
  await assert.rejects(runGatewaySmokeChecks({ projectId: 'alpha' }), /context is incomplete/);
});
