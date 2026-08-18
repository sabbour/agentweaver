import assert from 'node:assert/strict';
import test from 'node:test';
import { cleanStaging, parseCleanupOptions } from '../clean-staging.mjs';

const fixture = {
  projectName: 'Agentweaver Demo S1 - Trailhead',
  safeProjectNamePatterns: ['^Agentweaver Demo S1 - Trailhead$'],
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
    /session "agentweaver-demo" is open/,
  );
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
