import assert from 'node:assert/strict';
import test from 'node:test';
import { cleanStaging, parseCleanupOptions } from '../clean-staging.mjs';

const fixture = {
  projectName: 'Agentweaver Demo — Trailhead Travel Studio',
  safeProjectNamePatterns: ['^Agentweaver Demo — Trailhead Travel Studio$'],
};
const captureConfig = {
  fixture,
  beats: [{ startUrl: 'https://staging.example/overview' }],
};

test('staging cleanup requires an explicit destructive acknowledgement and active plan', () => {
  assert.throws(() => parseCleanupOptions(['--plan', 'demo.capture.json']), /destructive/);
  assert.throws(
    () => parseCleanupOptions(['--confirm-demo-cleanup']),
    /requires --plan/,
  );
});

test('staging cleanup refuses to run while its recording session is open', async () => {
  await assert.rejects(
    cleanStaging({
      confirmed: true,
      plan: 'demo.capture.json',
      baseUrl: 'https://staging.example',
      authRoot: '.auth',
      session: 'agentweaver-demo',
    }, {
      loadCaptureConfig: async () => captureConfig,
      listSessions: () => new Map([['agentweaver-demo', { status: 'open' }]]),
    }),
    /recorder session\(s\) "agentweaver-demo" are open/,
  );
});

test('staging cleanup refuses any open recorder session despite a different --session', async () => {
  await assert.rejects(
    cleanStaging({
      confirmed: true,
      plan: 'demo.capture.json',
      baseUrl: 'https://staging.example',
      authRoot: '.auth',
      session: 'other-session',
    }, {
      loadCaptureConfig: async () => captureConfig,
      listSessions: () => new Map([['agentweaver-demo', { status: 'open' }]]),
    }),
    /recorder session\(s\) "agentweaver-demo" are open/,
  );
});

test('staging cleanup accepts a plan with continuation beats that omit startUrl', async () => {
  const mixedConfig = {
    fixture,
    beats: [
      { startUrl: 'https://staging.example/overview' },
      { id: 'continuation-beat' }, // no startUrl — cross-beat continuity
    ],
  };
  let received;
  await cleanStaging({
    confirmed: true,
    plan: 'demo.capture.json',
    baseUrl: 'https://staging.example',
    authRoot: '.auth',
    session: 'agentweaver-demo',
  }, {
    loadCaptureConfig: async () => mixedConfig,
    listSessions: () => new Map(),
    createApiFromSession: async () => ({ listAllProjects: async () => [] }),
    cleanFixtures: async (options) => { received = options; return { discoveredProjectCount: 0 }; },
  });
  assert.ok(received, 'cleanFixtures should have been called');
});

test('staging cleanup refuses a plan that does not target its staging base URL', async () => {
  await assert.rejects(
    cleanStaging({
      confirmed: true,
      plan: 'demo.capture.json',
      baseUrl: 'https://other-staging.example',
      authRoot: '.auth',
      session: 'agentweaver-demo',
    }, {
      loadCaptureConfig: async () => captureConfig,
    }),
    /must target exactly the configured staging base URL/,
  );
});

test('staging cleanup passes only the active plan fixture to fixture cleanup', async () => {
  let received;
  const result = await cleanStaging({
    confirmed: true,
    plan: 'demo.capture.json',
    baseUrl: 'https://staging.example',
    authRoot: '.auth',
    session: 'agentweaver-demo',
  }, {
    loadCaptureConfig: async () => captureConfig,
    listSessions: () => new Map(),
    createApiFromSession: async () => ({ listAllProjects: async () => [] }),
    cleanFixtures: async (options) => {
      received = options;
      return { discoveredProjectCount: 0 };
    },
  });

  assert.deepEqual(received.fixture.projectName, fixture.projectName);
  assert.deepEqual(received.fixture.safeProjectNamePatterns, fixture.safeProjectNamePatterns);
  assert.equal(result.discoveredProjectCount, 0);
});
