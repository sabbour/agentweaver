import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import test from 'node:test';
import {
  cleanScenarioFixtures,
  isScenarioFixtureProject,
  resolveCapturePreflight,
  preflightFinalTake,
  validateFinalTake,
  validateScenarioFixture,
} from '../lib/preflight.mjs';

const fixture = {
  projectName: 'Agentweaver Demo — Trailhead Travel Studio',
  safeProjectNamePatterns: ['^Agentweaver Demo — Trailhead Travel Studio(?: - [0-9]{8}T[0-9]{6}Z)?$'],
};

const captureConfigWithLaterExternalArtifact = {
  schemaVersion: 1,
  beats: [
    {
      id: '0.0',
      captureMode: 'unauthenticated',
      steps: [{ type: 'waitFor', selector: 'Agentweaver sign-in affordance' }],
    },
    {
      id: '3.2',
      steps: [{ type: 'goto', url: '{{AGENTWEAVER_DEMO_GITHUB_TRIAGE_ISSUE_URL}}' }],
    },
  ],
  preflight: {
    externalArtifacts: [{
      beats: ['3.2'],
      environment: 'AGENTWEAVER_DEMO_GITHUB_TRIAGE_ISSUE_URL',
      host: 'github.com',
      instruction: 'Create the disposable issue before recording Beat 3.2.',
    }],
  },
};

test('capture preflight validates external artifacts only for selected beats', () => {
  const beatZero = resolveCapturePreflight(captureConfigWithLaterExternalArtifact, ['0.0'], {});
  assert.deepEqual(beatZero.beats[0], captureConfigWithLaterExternalArtifact.beats[0]);
  assert.equal(
    beatZero.beats[1].steps[0].url,
    '{{AGENTWEAVER_DEMO_GITHUB_TRIAGE_ISSUE_URL}}',
  );

  assert.throws(
    () => resolveCapturePreflight(
      captureConfigWithLaterExternalArtifact,
      captureConfigWithLaterExternalArtifact.beats.map((beat) => beat.id),
      {},
    ),
    /AGENTWEAVER_DEMO_GITHUB_TRIAGE_ISSUE_URL/,
  );
  assert.throws(
    () => resolveCapturePreflight(captureConfigWithLaterExternalArtifact, ['3.2'], {}),
    /AGENTWEAVER_DEMO_GITHUB_TRIAGE_ISSUE_URL/,
  );
});

test('fixture cleanup requires patterns constrained to the declared project name', () => {
  assert.throws(
    () => validateScenarioFixture({ ...fixture, safeProjectNamePatterns: ['Trailhead'] }),
    /must match only the declared fixture name/,
  );
  assert.throws(
    () => validateScenarioFixture({ ...fixture, projectName: 'Trailhead' }),
    /ownership marker/,
  );
  assert.throws(
    () => validateScenarioFixture({ ...fixture, safeProjectNamePatterns: ['^.*$'] }),
    /must match only the declared fixture name/,
  );
  assert.throws(
    () => validateScenarioFixture({ ...fixture, safeProjectNamePatterns: ['^Agentweaver Demo.*$'] }),
    /must match only the declared fixture name/,
  );
  assert.equal(isScenarioFixtureProject({ name: 'Agentweaver Demo — Trailhead Travel Studio' }, fixture), true);
  assert.equal(isScenarioFixtureProject({ name: 'Trailhead' }, fixture), false);
});

test('preflight deletes only safe-pattern projects and reports their associated sessions', async () => {
  let projects = [
    { project_id: 'safe-1', name: 'Agentweaver Demo — Trailhead Travel Studio' },
    { project_id: 'safe-2', name: 'Agentweaver Demo — Trailhead Travel Studio - 20260810T120000Z' },
    { project_id: 'user-1', name: 'Trailhead' },
    { project_id: 'user-2', name: 'Agentweaver Demo — Other Trailhead' },
  ];
  const deleted = [];
  const api = {
    async listAllProjects() {
      return projects;
    },
    async listAllProjectSessions(projectId) {
      return projectId === 'safe-1' ? [{ session_id: 'one' }, { session_id: 'two' }] : [];
    },
    async deleteProject(projectId) {
      deleted.push(projectId);
      projects = projects.filter((project) => project.project_id !== projectId);
    },
  };

  const result = await cleanScenarioFixtures({ fixture, baseUrl: 'https://staging.example' }, { api });
  assert.deepEqual(deleted, ['safe-1', 'safe-2']);
  assert.deepEqual(projects.map((project) => project.project_id), ['user-1', 'user-2']);
  assert.equal(result.discoveredProjectCount, 2);
  assert.equal(result.discoveredSessionCount, 2);
  assert.equal(result.remainingProjectCount, 0);
  assert.equal(result.clean, true);
});

test('preflight fails closed when a safe-pattern project remains after delete', async () => {
  const project = { project_id: 'safe-1', name: fixture.projectName };
  const api = {
    async listAllProjects() {
      return [project];
    },
    async listAllProjectSessions() {
      return [];
    },
    async deleteProject() {},
  };
  await assert.rejects(
    cleanScenarioFixtures({ fixture, baseUrl: 'https://staging.example' }, { api }),
    /could not verify a clean state/,
  );
});

test('preflight refuses a fixture that requests deletion of every project', () => {
  assert.throws(
    () => validateScenarioFixture({ ...fixture, cleanAllProjects: true }),
    /not permitted/,
  );
});

test('Blueprint Beat 0 preflight does not require later GitHub-triage artifacts', async () => {
  const plan = JSON.parse(await fs.readFile(
    new URL('../plans/blueprint-demo.capture.json', import.meta.url),
    'utf8',
  ));

  assert.doesNotThrow(() => resolveCapturePreflight(plan, ['0.0'], {}));
  assert.throws(
    () => resolveCapturePreflight(plan, plan.beats.map((beat) => beat.id), {}),
    /AGENTWEAVER_DEMO_GITHUB_TRIAGE_ISSUE_URL/,
  );
  assert.throws(
    () => resolveCapturePreflight(plan, ['3.2'], {}),
    /AGENTWEAVER_DEMO_GITHUB_TRIAGE_ISSUE_URL/,
  );
});

const finalTakeConfig = {
  fixture,
  finalTake: {
    id: 'trailhead-travel-studio',
    outputDirectory: 'recordings/final-takes/trailhead-travel-studio',
  },
  beats: [
    { videoPath: 'recordings/final-takes/trailhead-travel-studio/1.1.webm' },
    { videoPath: 'recordings/final-takes/trailhead-travel-studio/1.2.webm' },
  ],
};

test('final-take validation keeps every planned recording in its isolated output directory', () => {
  assert.equal(validateFinalTake(finalTakeConfig).videoPaths.length, 2);
  assert.throws(
    () => validateFinalTake({
      ...finalTakeConfig,
      beats: [{ videoPath: 'recordings/blueprint-demo/old-dry-run.webm' }],
    }),
    /must stay inside finalTake.outputDirectory/,
  );
});

test('final-take preflight refuses old media and old state without deleting either', async () => {
  const existingProjects = [{ project_id: 'fixture-1', name: fixture.projectName }];
  const api = {
    async listAllProjects() {
      return existingProjects;
    },
  };
  await assert.rejects(
    preflightFinalTake(finalTakeConfig, {
      api,
      fileExists: async () => true,
    }),
    /planned output file\(s\) already exist.*never deletes recordings/,
  );

  await assert.rejects(
    preflightFinalTake(finalTakeConfig, {
      api,
      fileExists: async () => false,
    }),
    /declared fixture project\(s\) still exist/,
  );
  assert.deepEqual(existingProjects, [{ project_id: 'fixture-1', name: fixture.projectName }]);
});

test('final-take preflight accepts only new media with a clean plan-scoped fixture state', async () => {
  const result = await preflightFinalTake(finalTakeConfig, {
    api: { async listAllProjects() { return [{ project_id: 'other', name: 'Agentweaver Demo — Other Trailhead' }]; } },
    fileExists: async () => false,
  });
  assert.equal(result.finalTakeId, 'trailhead-travel-studio');
  assert.equal(result.plannedVideoCount, 2);
  assert.equal(result.clean, true);
});
