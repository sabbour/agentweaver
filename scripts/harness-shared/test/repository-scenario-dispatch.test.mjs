import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  markRepositoryScenarioDispatchRunning,
  prepareRepositoryScenarioDispatch,
} from '../repository-scenario-dispatch.mjs';
import { validateVerdict } from '../../harness-judge/verdict-schema.mjs';

const revision = 'a'.repeat(40);
const metadata = {
  batchId: 'batch-1',
  scenarioId: 'completion-1',
  inputSeed: 'seed-1',
  adapterVersion: 'adapter-1',
  personaCoreVersion: 'persona-1',
  targetRevision: revision,
  surface: 'api',
  runId: null,
  timestamp: '2026-09-23T20:00:00.000Z',
  persona: 'Oracle',
};
const repository = {
  baseUrl: 'https://agentweaver.example.test',
  requestedRepository: 'sabbour/agentweaver',
  disposableProject: true,
  project: {
    project_id: 'project-1',
    origin: 'github',
    source_repository: 'sabbour/agentweaver',
    default_branch: 'dev',
  },
  workspaceRefs: {
    current_branch: 'dev',
    refs: [{ kind: 'base', branch: 'dev', revision }],
  },
  workflowId: 'workflow-advanced',
};

test('dispatch boundary persists a schema-valid setup verdict and stops before persona dispatch', async () => {
  const writes = [];
  const result = await prepareRepositoryScenarioDispatch({
    metadata,
    repository: {
      ...repository,
      project: { ...repository.project, source_repository: 'other/repository' },
    },
    verdictPath: 'verdicts/setup.json',
  }, {
    writeJson: async (file, verdict) => writes.push({ file, verdict }),
  });

  assert.equal(result.action, 'stop');
  assert.equal(result.status, 'setup-failed');
  assert.equal(writes.length, 1);
  assert.equal(writes[0].file, 'verdicts/setup.json');
  assert.equal(validateVerdict(writes[0].verdict).ok, true);
  assert.match(writes[0].verdict.findings[0].title, /repository_identity_mismatch/);
});

test('dispatch boundary emits running only with marked repository and orchestration evidence', async () => {
  const prepared = await prepareRepositoryScenarioDispatch({
    metadata,
    repository,
    verdictPath: 'verdicts/setup.json',
  });
  assert.equal(prepared.action, 'create-orchestration');
  assert.equal(prepared.status, 'ready');

  const result = await markRepositoryScenarioDispatchRunning({
    metadata,
    repositoryPreflight: prepared.repositoryPreflight,
    baseUrl: repository.baseUrl,
    orchestrationRunId: 'run-1',
    verdictPath: 'verdicts/setup.json',
  });

  assert.equal(result.action, 'dispatch-persona');
  assert.equal(result.status, 'running');
  assert.deepEqual(result.repositoryProvenance, {
    status: 'running',
    projectId: 'project-1',
    projectUrl: 'https://agentweaver.example.test/projects/project-1',
    repositoryIdentity: 'sabbour/agentweaver',
    resolvedRevision: revision,
    workflowOrBlueprintId: 'workflow-advanced',
    orchestrationRunId: 'run-1',
    orchestrationUrl: 'https://agentweaver.example.test/projects/project-1/orchestrations/run-1',
  });
});

test('dispatch boundary treats a missing orchestration as setup failure, not running', async () => {
  const writes = [];
  const prepared = await prepareRepositoryScenarioDispatch({
    metadata,
    repository,
    verdictPath: 'verdicts/setup.json',
  });
  const result = await markRepositoryScenarioDispatchRunning({
    metadata,
    repositoryPreflight: prepared.repositoryPreflight,
    baseUrl: repository.baseUrl,
    verdictPath: 'verdicts/setup.json',
  }, {
    writeJson: async (_file, verdict) => writes.push(verdict),
  });

  assert.equal(result.action, 'stop');
  assert.equal(result.status, 'setup-failed');
  assert.equal(validateVerdict(writes[0]).ok, true);
  assert.match(writes[0].findings[0].title, /orchestration_missing/);
});

test('running phase cannot bypass the repository preflight', async () => {
  const writes = [];
  const result = await markRepositoryScenarioDispatchRunning({
    metadata,
    baseUrl: repository.baseUrl,
    orchestrationRunId: 'run-1',
    verdictPath: 'verdicts/setup.json',
  }, {
    writeJson: async (_file, verdict) => writes.push(verdict),
  });

  assert.equal(result.action, 'stop');
  assert.equal(result.status, 'setup-failed');
  assert.equal(validateVerdict(writes[0]).ok, true);
  assert.match(writes[0].findings[0].title, /repository_preflight_missing/);
});

test('executable dispatch gate stops before orchestration and persists its verdict', async () => {
  const artifactDir = path.resolve('artifacts', `repository-dispatch-test-${process.pid}`);
  const requestPath = path.join(artifactDir, 'request.json');
  const verdictPath = path.join(artifactDir, 'verdict.json');
  const script = fileURLToPath(new URL('../repository-scenario-dispatch.mjs', import.meta.url));
  await mkdir(artifactDir, { recursive: true });
  try {
    await writeFile(requestPath, JSON.stringify({
      metadata,
      repository: {
        ...repository,
        workspaceRefs: { current_branch: 'dev', refs: [] },
      },
      verdictPath,
    }), 'utf8');

    const outcome = spawnSync(process.execPath, [script, '--input', requestPath], {
      cwd: path.resolve('.'),
      encoding: 'utf8',
    });
    assert.equal(outcome.status, 1);
    assert.equal(outcome.stderr, '');
    const result = JSON.parse(outcome.stdout);
    assert.equal(result.action, 'stop');
    assert.equal(result.status, 'setup-failed');
    assert.equal(validateVerdict(JSON.parse(await readFile(verdictPath, 'utf8'))).ok, true);
  } finally {
    await rm(artifactDir, { recursive: true, force: true });
  }
});
