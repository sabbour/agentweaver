import assert from 'node:assert/strict';
import test from 'node:test';
import { createArtifactMetadata } from './stage-docs-artifact.mjs';

const validEnvironment = {
  DOCS_REPOSITORY: 'sabbour/agentweaver',
  DOCS_BRANCH: 'v1',
  DOCS_EVENT: 'push',
  DOCS_RUN_ID: '12345',
  DOCS_RUN_ATTEMPT: '2',
  DOCS_SOURCE_SHA: 'a'.repeat(40),
};

test('binds docs artifact metadata to repository, push, workflow, run, and source', () => {
  assert.deepEqual(createArtifactMetadata(validEnvironment), {
    schemaVersion: 1,
    repository: 'sabbour/agentweaver',
    branch: 'v1',
    event: 'push',
    workflow: 'Agentweaver v1 Docs CI',
    workflowPath: '.github/workflows/v1-docs-ci.yml',
    runId: 12345,
    runAttempt: 2,
    sourceSha: 'a'.repeat(40),
    artifactName: `v1-docs-${'a'.repeat(40)}-2`,
  });
});

test('refuses metadata that cannot describe an admitted v1 push artifact', () => {
  for (const [key, value] of [
    ['DOCS_BRANCH', 'feature'],
    ['DOCS_EVENT', 'pull_request'],
    ['DOCS_RUN_ID', '0'],
    ['DOCS_RUN_ATTEMPT', 'not-a-number'],
    ['DOCS_SOURCE_SHA', 'short'],
  ]) {
    assert.throws(() => createArtifactMetadata({ ...validEnvironment, [key]: value }), new RegExp(key));
  }
});
