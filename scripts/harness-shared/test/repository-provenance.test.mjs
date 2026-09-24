import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

import {
  markRepositoryScenarioRunning,
  preflightRepositoryScenario,
} from '../repository-provenance.mjs';

const revision = 'a'.repeat(40);
const valid = {
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

test('repository preflight returns complete running provenance', () => {
  const preflight = preflightRepositoryScenario(valid);
  assert.equal(preflight.ok, true);
  assert.deepEqual(markRepositoryScenarioRunning(preflight, {
    baseUrl: valid.baseUrl,
    orchestrationRunId: 'run-1',
  }), {
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

for (const [name, overrides, code] of [
  ['requested repository', { requestedRepository: null }, 'requested_repository_missing'],
  ['project', { project: null }, 'project_missing'],
  ['disposable ownership', { disposableProject: false }, 'project_not_disposable'],
  ['GitHub origin', { project: { ...valid.project, origin: 'blank' } }, 'project_not_repository_backed'],
  ['repository identity', { project: { ...valid.project, source_repository: null } }, 'repository_identity_missing'],
  ['matching repository identity', { project: { ...valid.project, source_repository: 'other/repo' } }, 'repository_identity_mismatch'],
  ['resolved revision', { workspaceRefs: { current_branch: 'dev', refs: [{ kind: 'base', branch: 'dev' }] } }, 'resolved_revision_missing'],
  ['workflow or Blueprint ID', { workflowId: null, blueprintId: null }, 'workflow_or_blueprint_missing'],
]) {
  test(`repository preflight fails actionably without ${name}`, () => {
    const result = preflightRepositoryScenario({ ...valid, ...overrides });
    assert.equal(result.ok, false);
    assert.equal(result.failure.code, code);
    assert.match(result.failure.message, /\S/);
    assert.match(result.failure.recovery, /\S/);
  });
}

test('repository scenario cannot report running without an orchestration', () => {
  const result = markRepositoryScenarioRunning(preflightRepositoryScenario(valid), {
    baseUrl: valid.baseUrl,
  });
  assert.equal(result.ok, false);
  assert.equal(result.failure.code, 'orchestration_missing');
  assert.match(result.failure.recovery, /Start the orchestration/i);
});

test('Harness contract requires repository preflight before completion execution', async () => {
  const contract = await readFile(new URL('../../../.github/agents/harness.agent.md', import.meta.url), 'utf8');
  assert.match(contract, /Preflight repository provenance before every completion-required repository\s+scenario/i);
  assert.match(contract, /Never substitute a blank\s+project/i);
  assert.match(contract, /do not treat[\s\S]*read-only discovery as execution/i);
  assert.match(contract, /project URL, exact repository identity, resolved revision, selected\s+workflow or Blueprint ID, and orchestration URL/i);
});
