import assert from 'node:assert/strict';
import test from 'node:test';
import {
  parseFinalTakePreflightOptions,
  runFinalTakePreflight,
} from '../final-take-preflight.mjs';

const captureConfig = {
  fixture: {
    projectName: 'Agentweaver Demo — Trailhead Travel Studio',
    safeProjectNamePatterns: ['^Agentweaver Demo — Trailhead Travel Studio$'],
  },
  finalTake: {
    id: 'trailhead-travel-studio',
    outputDirectory: 'recordings/final-takes/trailhead-travel-studio',
  },
  beats: [{ videoPath: 'recordings/final-takes/trailhead-travel-studio/1.1.webm' }],
};

test('final-take preflight requires a plan and refuses open recorder sessions', async () => {
  assert.throws(() => parseFinalTakePreflightOptions([]), /requires --plan/);
  await assert.rejects(
    runFinalTakePreflight({
      plan: 'plan.capture.json',
      baseUrl: 'https://staging.example',
      authRoot: '.auth',
    }, {
      listSessions: () => new Map([['agentweaver-demo', { status: 'open' }]]),
    }),
    /recorder session\(s\) "agentweaver-demo" are open/,
  );
});

test('final-take preflight checks the active plan without modifying its fixture or recordings', async () => {
  const result = await runFinalTakePreflight({
    plan: 'plan.capture.json',
    baseUrl: 'https://staging.example',
    authRoot: '.auth',
  }, {
    listSessions: () => new Map(),
    loadCaptureConfig: async () => captureConfig,
    api: { async listAllProjects() { return []; } },
    fileExists: async () => false,
  });
  assert.equal(result.clean, true);
  assert.equal(result.plannedVideoCount, 1);
});
